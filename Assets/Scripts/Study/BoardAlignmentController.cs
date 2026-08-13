using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class BoardAlignmentController : MonoBehaviour
{
    private const string SpatialAnchorKey = "VHard.BoardAlignmentAnchor";
    private const int IndexTipBoneIndex = 10;
    private const float FiducialDistanceToleranceMeters = 0.15f;
    private const float FiducialHeightToleranceMeters = 0.10f;
    private const double SpatialAnchorLocalizationTimeoutSeconds = 10.0;

    [SerializeField] private SceneConfiguror sceneConfiguror;
    [SerializeField] private Transform boardMotionRoot;
    [SerializeField] private float boardBaseHeightAboveFloorMeters;
    [SerializeField] private float boardBaseDistanceAheadOfOriginMeters =
        BoardStandoffPolicy.DefaultBoardBaseDistanceMeters;
    [SerializeField] private float boardCenterLateralOffsetMeters;

    private MoonBoardStudyCatalog catalog;
    private OVRSpatialAnchor spatialAnchor;
    private Vector3 initialLocalPosition;
    private Quaternion initialLocalRotation;
    private Vector3 firstFiducial;
    private CalibrationStage calibrationStage;
    private bool pinchArmed;
    private bool isAligned;
    private bool isSpatiallyAnchored;
    private bool isLoadingSpatialAnchor;
    private bool isSavingSpatialAnchor;
    private string spatialAnchorUuid = string.Empty;
    private int recenterEpoch;
    private string statusMessage = "Board alignment is optional and has not been run.";

    public bool IsCalibrating => calibrationStage != CalibrationStage.None;
    public bool IsBusy => IsCalibrating || isLoadingSpatialAnchor || isSavingSpatialAnchor;
    public bool IsAligned => isAligned;
    public string StatusMessage => statusMessage;

    private enum CalibrationStage
    {
        None,
        FirstFiducial,
        SecondFiducial,
    }

    private void Awake()
    {
        SeatBoardBaseOnTrackingFloor();
        SeatBoardBaseAheadOfTrackingOrigin();
        initialLocalPosition = transform.localPosition;
        initialLocalRotation = transform.localRotation;
        ResolveReferences();
    }

    private void Start()
    {
        LoadSavedSpatialAnchor();
    }

    private void OnEnable()
    {
        OVRManager.TrackingOriginChangePending += HandleTrackingOriginChange;
    }

    private void OnDisable()
    {
        OVRManager.TrackingOriginChangePending -= HandleTrackingOriginChange;
    }

    public void SetCatalog(MoonBoardStudyCatalog studyCatalog)
    {
        catalog = studyCatalog;
        ResolveReferences();
    }

    public bool BeginCalibration(out string error)
    {
        ResolveReferences();
        if (catalog == null)
        {
            error = "MoonBoard catalog is unavailable.";
            return false;
        }
        if (!catalog.TryValidate(out error))
        {
            return false;
        }
        if (sceneConfiguror == null || boardMotionRoot == null)
        {
            error = "Board alignment references are unavailable.";
            return false;
        }
        if (isLoadingSpatialAnchor || isSavingSpatialAnchor)
        {
            error = "Wait for the current spatial-anchor operation to finish.";
            return false;
        }
        if (!ApproximatelyOne(transform.parent != null ? transform.parent.lossyScale : Vector3.one))
        {
            error = "Board alignment parent must remain at unit scale.";
            return false;
        }

        RemoveRuntimeAnchorComponent();
        PlayerPrefs.DeleteKey(SpatialAnchorKey);
        PlayerPrefs.Save();
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        calibrationStage = CalibrationStage.FirstFiducial;
        pinchArmed = false;
        statusMessage = "Release, then pinch the physical A3 fiducial.";
        error = string.Empty;
        return true;
    }

    public void CancelCalibration()
    {
        calibrationStage = CalibrationStage.None;
        pinchArmed = false;
        statusMessage = "Board alignment cancelled.";
    }

    public bool ClearAlignment()
    {
        if (IsBusy)
        {
            statusMessage = "Wait for spatial-anchor persistence to finish before clearing alignment.";
            return false;
        }
        calibrationStage = CalibrationStage.None;
        RemoveRuntimeAnchorComponent();
        PlayerPrefs.DeleteKey(SpatialAnchorKey);
        PlayerPrefs.Save();
        transform.SetLocalPositionAndRotation(initialLocalPosition, initialLocalRotation);
        transform.localScale = Vector3.one;
        isAligned = false;
        isSpatiallyAnchored = false;
        spatialAnchorUuid = string.Empty;
        statusMessage = "Board alignment cleared.";
        return true;
    }

    public BoardAlignmentSnapshot GetSnapshot()
    {
        return new BoardAlignmentSnapshot
        {
            isAligned = isAligned,
            isSpatiallyAnchored = isSpatiallyAnchored,
            spatialAnchorUuid = spatialAnchorUuid,
            recenterEpoch = recenterEpoch,
            position = transform.position,
            rotation = transform.rotation,
        };
    }

    private void Update()
    {
        if (calibrationStage == CalibrationStage.None)
        {
            return;
        }

        bool isPinching = TryGetIndexPinchPoint(out Vector3 pinchPoint);
        if (!pinchArmed)
        {
            pinchArmed = !isPinching;
            return;
        }
        if (!isPinching)
        {
            return;
        }

        pinchArmed = false;
        if (calibrationStage == CalibrationStage.FirstFiducial)
        {
            firstFiducial = pinchPoint;
            calibrationStage = CalibrationStage.SecondFiducial;
            statusMessage = "A3 captured. Release, then pinch the physical K3 fiducial.";
            return;
        }

        if (TryApplyRigidAlignment(firstFiducial, pinchPoint, out string error))
        {
            calibrationStage = CalibrationStage.None;
            statusMessage = "Board aligned. Saving the spatial anchor...";
            SaveSpatialAnchor();
        }
        else
        {
            calibrationStage = CalibrationStage.FirstFiducial;
            statusMessage = error + " Release, then retry A3.";
        }
    }

    private bool TryApplyRigidAlignment(Vector3 measuredA3, Vector3 measuredK3, out string error)
    {
        Vector3 measuredSpan = measuredK3 - measuredA3;
        Vector3 horizontalSpan = Vector3.ProjectOnPlane(measuredSpan, Vector3.up);
        float expectedDistance = Vector3.Distance(
            catalog.GetBoardLocalPosition("A3"),
            catalog.GetBoardLocalPosition("K3"));
        if (Mathf.Abs(measuredSpan.magnitude - expectedDistance) > FiducialDistanceToleranceMeters ||
            Mathf.Abs(measuredSpan.y) > FiducialHeightToleranceMeters ||
            horizontalSpan.sqrMagnitude < 0.01f)
        {
            error = $"Fiducials measured {measuredSpan.magnitude:F2} m apart; expected {expectedDistance:F2} m.";
            return false;
        }

        Quaternion worldRotation = Quaternion.FromToRotation(Vector3.right, horizontalSpan.normalized);
        Vector3 localA3 = catalog.GetBoardLocalPosition("A3");
        Vector3 worldPosition = measuredA3 - worldRotation * localA3;
        if (transform.parent == null)
        {
            transform.SetPositionAndRotation(worldPosition, worldRotation);
        }
        else
        {
            transform.localPosition = transform.parent.InverseTransformPoint(worldPosition);
            transform.localRotation = Quaternion.Inverse(transform.parent.rotation) * worldRotation;
        }
        transform.localScale = Vector3.one;
        isAligned = true;
        isSpatiallyAnchored = false;
        spatialAnchorUuid = string.Empty;
        error = string.Empty;
        return true;
    }

    private bool TryGetIndexPinchPoint(out Vector3 point)
    {
        point = Vector3.zero;
        return TryGetIndexPinchPoint(sceneConfiguror?.leftHandOVRSkeleton, out point) ||
               TryGetIndexPinchPoint(sceneConfiguror?.rightHandOVRSkeleton, out point);
    }

    private static bool TryGetIndexPinchPoint(OVRSkeleton skeleton, out Vector3 point)
    {
        point = Vector3.zero;
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count <= IndexTipBoneIndex)
        {
            return false;
        }
        OVRHand hand = skeleton.GetComponent<OVRHand>();
        if (hand == null || !hand.IsTracked || !hand.IsDataHighConfidence ||
            !hand.GetFingerIsPinching(OVRHand.HandFinger.Index))
        {
            return false;
        }
        point = skeleton.Bones[IndexTipBoneIndex].Transform.position;
        return true;
    }

    private async void SaveSpatialAnchor()
    {
        isSavingSpatialAnchor = true;
        try
        {
#if UNITY_ANDROID
            if (Application.isEditor)
            {
                await System.Threading.Tasks.Task.Yield();
                statusMessage = "Board aligned. Spatial-anchor persistence requires a Quest build.";
                return;
            }
            OVRSpatialAnchor anchor = gameObject.AddComponent<OVRSpatialAnchor>();
            spatialAnchor = anchor;
            if (!await anchor.WhenCreatedAsync())
            {
                statusMessage = "Board aligned, but spatial-anchor creation failed.";
                return;
            }
            var result = await anchor.SaveAnchorAsync();
            if (!result.Success)
            {
                statusMessage = "Board aligned, but spatial-anchor save failed: " + result.Status + ".";
                return;
            }
            spatialAnchorUuid = anchor.Uuid.ToString();
            PlayerPrefs.SetString(SpatialAnchorKey, spatialAnchorUuid);
            PlayerPrefs.Save();
            isSpatiallyAnchored = true;
            statusMessage = "Board aligned and spatially anchored.";
#else
            await System.Threading.Tasks.Task.Yield();
            statusMessage = "Board aligned. Spatial-anchor persistence requires a Quest build.";
#endif
        }
        finally
        {
            isSavingSpatialAnchor = false;
        }
    }

    private async void LoadSavedSpatialAnchor()
    {
#if UNITY_ANDROID
        if (Application.isEditor)
        {
            await System.Threading.Tasks.Task.Yield();
            return;
        }
        string savedUuid = PlayerPrefs.GetString(SpatialAnchorKey, string.Empty);
        if (!Guid.TryParse(savedUuid, out Guid uuid))
        {
            return;
        }
        isLoadingSpatialAnchor = true;
        try
        {
            List<OVRSpatialAnchor.UnboundAnchor> anchors = new();
            var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new[] { uuid }, anchors);
            if (!result.Success || anchors.Count != 1 ||
                !await anchors[0].LocalizeAsync(SpatialAnchorLocalizationTimeoutSeconds))
            {
                statusMessage = "Saved board anchor could not be localized; recalibrate before the block.";
                isAligned = false;
                isSpatiallyAnchored = false;
                PlayerPrefs.DeleteKey(SpatialAnchorKey);
                PlayerPrefs.Save();
                return;
            }
            spatialAnchor = gameObject.AddComponent<OVRSpatialAnchor>();
            anchors[0].BindTo(spatialAnchor);
            spatialAnchorUuid = uuid.ToString();
            isAligned = true;
            isSpatiallyAnchored = true;
            statusMessage = "Saved board spatial anchor localized.";
        }
        finally
        {
            isLoadingSpatialAnchor = false;
        }
#else
        await System.Threading.Tasks.Task.Yield();
#endif
    }

    private void HandleTrackingOriginChange(OVRManager.TrackingOrigin origin, OVRPose? pose)
    {
        recenterEpoch++;
        if (spatialAnchor != null && spatialAnchor.Localized)
        {
            statusMessage = "Tracking origin changed; board remains spatially anchored.";
            return;
        }
        isAligned = false;
        isSpatiallyAnchored = false;
        statusMessage = "Tracking origin changed; recalibrate the board before the next block.";
    }

    // OVRManager tracks from a floor-level origin, so world y = 0 is the floor the participant
    // is standing on, and the Moonboard child is authored with its kicker base at local y = 0.
    // Seating this root on that plane reproduces what an A3/K3 calibration resolves to, so the
    // uncalibrated pose no longer leaves the participant floating above the reconstructed floor.
    private void SeatBoardBaseOnTrackingFloor()
    {
        Vector3 worldPosition = transform.position;
        transform.position = new Vector3(
            worldPosition.x,
            boardBaseHeightAboveFloorMeters,
            worldPosition.z);
    }

    // The same origin also fixes where the participant is standing horizontally, and neither
    // horizontal axis was ever measured against them in the authored A3-fiducial frame. The
    // 40-degree face is not a fixed distance away: it closes on the origin by tan(40 degrees) per
    // metre of height above the kicker, so seating the vertical kicker plane at the policy standoff
    // decides both how much of the board a standing participant can reach for a ground rehearsal
    // and how much room they have under the overhang. Squaring the board's centre column onto the
    // origin then keeps the reach that standoff buys symmetric across both hands; the offset stays
    // tunable because the physical bay may not let the participant stand on the centre line.
    private void SeatBoardBaseAheadOfTrackingOrigin()
    {
        Vector3 worldPosition = transform.position;
        transform.position = new Vector3(
            boardCenterLateralOffsetMeters,
            worldPosition.y,
            boardBaseDistanceAheadOfOriginMeters);
    }

    private void ResolveReferences()
    {
        sceneConfiguror ??= FindAnyObjectByType<SceneConfiguror>();
        if (boardMotionRoot == null)
        {
            boardMotionRoot = transform.Find("Moonboard");
        }
    }

    private void RemoveRuntimeAnchorComponent()
    {
        if (spatialAnchor != null)
        {
            Destroy(spatialAnchor);
            spatialAnchor = null;
        }
        isSpatiallyAnchored = false;
    }

    private static bool ApproximatelyOne(Vector3 scale)
    {
        return Mathf.Abs(scale.x - 1f) < 0.001f &&
               Mathf.Abs(scale.y - 1f) < 0.001f &&
               Mathf.Abs(scale.z - 1f) < 0.001f;
    }
}
