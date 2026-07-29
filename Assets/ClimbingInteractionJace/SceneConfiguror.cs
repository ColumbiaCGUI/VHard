using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using Debug = UnityEngine.Debug;

public enum GameMode
{
    Basic = 0, // Only shader
    Grip = 1, // JACE: In development, fixed-grip movement mode
    Ghost = 2,
}

public enum RoutesLoadState
{
    Loading,
    Ready,
    Failed,
}

public enum HoldAffordancesLoadState
{
    Loading,
    Ready,
    Failed,
}

public class SceneConfiguror : MonoBehaviour
{
    private const int TrackedBoneCount = 26;
    private const string StudyHoldsLayerName = "StudyHolds";
    private const string StudyGhostHoldsLayerName = "StudyGhostHolds";
    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };
    private static readonly OVRHand.HandFinger[] TrackedFingers =
    {
        OVRHand.HandFinger.Thumb,
        OVRHand.HandFinger.Index,
        OVRHand.HandFinger.Middle,
        OVRHand.HandFinger.Ring,
        OVRHand.HandFinger.Pinky,
    };
    [Header("Action Recorder")]
    public ActionRecorder actionRecorder;
    [Header("HighlightCircle")]
    public GameObject highlightCirclePrefab;
    private List<GameObject> activeHighlightCircles = new();
    private Material highlightCircleMaterial;

    [Header("Route Cues")]
    [SerializeField] private RouteCuePresentation baselineRouteCuePresentation =
        RouteCuePresentation.PhysicalBoardLeds;

    [Header("Minimap Settings")]
    public Camera mainCamera;     // assign your main/world camera here
    //public Camera minimapCamera;  // assign your minimap camera here [Update 7/7/2026: we don't need a minimap anymore. CAROLINE]
    public string indicatorLayerName = "HighlightCircle"; // assign your highlight circle layer here
    private int indicatorLayer;

    [Header("Scene References")]
    public GameObject environment;
    public GameObject holdsParentGameObject;
    public Dictionary<string, GameObject> holdsDictionary;
    public List<string> activeRouteHoldsNamesList;
    public List<GameObject> activeHoldsList;
    private Light examinationHeadlamp;
    private int studyHoldsLayer = -1;
    private int studyGhostHoldsLayer = -1;
    private static readonly string[] SupplementalSceneryNameMarkers =
    {
        "water", "ocean", "terrain", "scenery", "landscape", "skybox",
    };
    private readonly Dictionary<GameObject, bool> supplementalSceneryActiveStates = new();
    private bool studyEnvironmentHidden;
    // The inspector mainCamera reference is a disabled legacy camera; the participant renders
    // through the OVR rig eye anchors, so background suppression must cover every live camera.
    private readonly Dictionary<Camera, (CameraClearFlags flags, Color background)>
        studyEnvironmentCameraStates = new();

    [Header("Hands References")]
    public GameObject centerEyeAnchor;
    public OVRSkeleton leftHandOVRSkeleton;
    public OVRSkeleton rightHandOVRSkeleton;

    [Header("Other Player's Avatar")]
    public bool shouldOtherPlayerHandsBeActive;
    public NetworkedHands networkedHands;
    public AvatarHand otherPlayerLeftHand;
    public AvatarHand otherPlayerRightHand;

    [Header("Hands State")]
    public int numBonesPerHand;
    public Vector3 centerEyePosition;
    public List<Vector3> leftHandBonePositions = new List<Vector3>();
    public List<Vector3> rightHandBonePositions = new List<Vector3>();
    public List<Quaternion> leftHandBoneQuaternions = new List<Quaternion>();
    public List<Quaternion> rightHandBoneQuaternions = new List<Quaternion>();

    [Header("Climb Settings")]
    public GameMode gameMode;
    public string currentRouteName = string.Empty;
    public GhostHoldController ghostHoldController;

    // Ad-hoc routes loaded from StreamingAssets/routes.json (e.g. MoonBoard benchmarks
    // converted via tools/moonboard_to_routes.py). Built-in study routes always win;
    // RouteLibrary rejects any file that tries to shadow them.
    private static readonly string[] BuiltInRouteNames =
    {
        "DEATH STAR", "TO JUG, OR NOT TO JUG...",
    };
    private readonly Dictionary<string, RouteDefinition> jsonRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> jsonRouteNames = new();
    public RouteDefinition ActiveRouteDefinition { get; private set; }
    public RoutesLoadState RoutesJsonLoadState { get; private set; } = RoutesLoadState.Loading;
    public string RoutesLoadFailureReason { get; private set; } = string.Empty;
    public string RoutesJsonSha256 { get; private set; }
    public HoldAffordancesLoadState HoldAffordancesState { get; private set; } =
        HoldAffordancesLoadState.Loading;
    public string HoldAffordancesFailureReason { get; private set; } = string.Empty;

    [Header("Interaction Settings")]
    public float interactionColorMaxDistanceOverride;
    public bool disableInactiveHolds;
    public float inactiveHoldAlpha;
    public float activeHoldAlpha;

    [Header("Interaction State")]
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;
    public int HoverContactEpoch { get; private set; }

    [Header("Interaction Compute Shader Settings")]
    public ComputeShader distanceToClosestBoneComputeShader;
    public int kernelHandle;

    [Header("Grip Settings")]
    public GameObject moonBoardEnv;
    public float gripFingertipRange;
    public GameObject leftHandGripStatusDisplayHelper;
    public GameObject rightHandGripStatusDisplayHelper;
    public GripScoreConfig gripScoreConfig;
    [Range(1, 4)] public int defaultMinFingers = 3;
    [Range(0f, 1f)] public float gripFlexionEngageThreshold = 0.55f;
    [Range(0f, 1f)] public float gripFlexionReleaseThreshold = 0.35f;
    [Min(0f)] public float gripReleaseGraceSeconds = 0.15f;
    [Min(0f)] public float gripTrackingFreezeSeconds = 0.25f;
    [Min(0.01f)] public float gripFrozenTimeoutSeconds = 2f;
    [Min(0.01f)] public float gripOneEuroMinCutoff = 1f;
    [Min(0f)] public float gripOneEuroBeta = 0.007f;
    [Min(0.01f)] public float gripMaximumAcceleration = 12f;

    [Header("Grip State")]
    public float[] leftHandBoneToHoldMinDistances;
    public float[] rightHandBoneToHoldMinDistances;
    public bool isGripLocomotionActive;
    public bool leftHandIsGripping;
    public bool rightHandIsGripping;
    public int perFingerContactMask = -1;
    public float currentGripScore = -1f;
    public int leftFingerContactMask;
    public int rightFingerContactMask;
    public float leftHandGripScore;
    public float rightHandGripScore;
    public IReadOnlyList<float> LeftFingerCurls => leftFingerCurls;
    public IReadOnlyList<float> RightFingerCurls => rightFingerCurls;
    public event Action<string, Hand, GameObject, string> GripEngagementRecorded;
    public Vector3 leftHandGripStartPosition;
    public Vector3 leftHandGripLastPosition;
    public Vector3 rightHandGripStartPosition;
    public Vector3 rightHandGripLastPosition;
    public List<Vector3> leftHandGripStartPose;
    public List<Vector3> leftHandGripCurrentPose;
    public List<Vector3> rightHandGripStartPose;
    public List<Vector3> rightHandGripCurrentPose;
    private GripContactPipeline gripContactPipeline;
    private Vector3 initialMoonBoardLocalPosition;
    private Quaternion initialMoonBoardLocalRotation;
    private Vector3 initialMoonBoardLocalScale;
    private bool hasInitialMoonBoardTransform;
    private OVRHand leftTrackedHand;
    private OVRHand rightTrackedHand;
    private bool leftTrackingValid;
    private bool rightTrackingValid;
    private Hand? gripLocomotionHand;
    private bool gripRecoveryAttempted;
    private bool debugForceGripReadbackFailures;
    private bool studyFeedbackVisible = true;
    public bool IsGripFeedbackDegraded { get; private set; }
    public string GripFeedbackDegradedUtc { get; private set; }
    public bool IsDegradedGripAcquisitionActive { get; private set; }
    public string DegradedGripAcquisitionFailureReason { get; private set; } = string.Empty;
    private MoonBoardStudyCatalog routeCatalog;
    private HoldAffordanceCatalog holdAffordanceCatalog;
    private GripLatchStateMachine leftGripLatch;
    private GripLatchStateMachine rightGripLatch;
    private GripLocomotionFilter leftLocomotionFilter;
    private GripLocomotionFilter rightLocomotionFilter;
    private GameObject leftLatchedHold;
    private GameObject rightLatchedHold;
    private readonly float[] leftFingerCurls = new float[FingerCurlEstimator.FingerCount];
    private readonly float[] rightFingerCurls = new float[FingerCurlEstimator.FingerCount];
    private readonly GripAcquisitionSample leftGripAcquisitionSample = new();
    private readonly GripAcquisitionSample rightGripAcquisitionSample = new();
    private readonly bool[] leftFingerConfidence = new bool[FingerCurlEstimator.FingerCount];
    private readonly bool[] rightFingerConfidence = new bool[FingerCurlEstimator.FingerCount];
    private readonly OverlapContactResolver<GameObject> leftHoverContacts = new();
    private readonly OverlapContactResolver<GameObject> rightHoverContacts = new();
    private readonly Dictionary<Mesh, DegradedGripContactGeometry> degradedGripGeometry = new();
    private readonly HashSet<int> reportedDegradedGripGeometryFailures = new();
    private readonly float[] leftDegradedGripDistances =
        new float[GripEngagementGate.RequiredBoneDistanceCount];
    private readonly float[] rightDegradedGripDistances =
        new float[GripEngagementGate.RequiredBoneDistanceCount];
    private bool leftLegacyFiveTipContact;
    private bool rightLegacyFiveTipContact;
    private string holdsDictionaryError = string.Empty;
    private MaterialPropertyBlock holdProperties;
    private MaterialPropertyBlock HoldProperties => holdProperties ??= new MaterialPropertyBlock();

    private void Awake()
    {
        InitializeGripFacades();
        EnsureRuntimeControllers();
        EnsureExaminationHeadlamp();
    }

    void Start()
    {
            // 1) layer lookup
        indicatorLayer = LayerMask.NameToLayer(indicatorLayerName);
        Debug.Log($"[SC] indicatorLayerName='{indicatorLayerName}' → layerIndex={indicatorLayer}");
        if (indicatorLayer < 0)
        {
            Debug.LogError($"[SC] Layer '{indicatorLayerName}' not found! Check Project Settings > Tags & Layers.");
        }

        // Route halos are a world-space board cue and must be visible in the main camera.
        if (indicatorLayer >= 0 && mainCamera != null)
        {
            int mask = 1 << indicatorLayer;
            Debug.Log($"[SC] mainCamera mask before:    {mainCamera.cullingMask:X8}");
            mainCamera.cullingMask |= mask;
            Debug.Log($"[SC] mainCamera mask after:     {mainCamera.cullingMask:X8}");
        }

        UnityEngine.Debug.Log("SceneConfiguror initializing.");
        CacheMoonBoardTransform();

        // Add all the children of the holds parent to the holds dictionary, to be accessed using the string [A-K][1-18]
        // Jace: Note that the holds are currently named [A-K][1-18].[001/002/003]
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        StartCoroutine(LoadRoutesJson());
        StartCoroutine(LoadHoldAffordances());

        EnsureRuntimeControllers();
        ghostHoldController.Initialize(this);
        SetGameMode(gameMode);
    }

    void TraverseBones(GameObject rootBone, List<GameObject> bones)
    {
        bones.Add(rootBone);
        foreach (Transform child in rootBone.transform)
        {
            TraverseBones(child.gameObject, bones);
        }
    }

    void Update()
    {
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        gripContactPipeline?.Update(Time.unscaledTime);
        HandleGripPipelineHealth();
        centerEyePosition = centerEyeAnchor.transform.position;

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            SetInteractionVisual(
                leftHandInteractingClimbingHold,
                gripContactPipeline == null && !IsGripFeedbackDegraded,
                interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            SetInteractionVisual(
                rightHandInteractingClimbingHold,
                gripContactPipeline == null && !IsGripFeedbackDegraded,
                interactionColorMaxDistanceOverride);
        }

        leftTrackingValid = IsHandTrackingValid(leftHandOVRSkeleton, ref leftTrackedHand);
        rightTrackingValid = IsHandTrackingValid(rightHandOVRSkeleton, ref rightTrackedHand);
        numBonesPerHand = leftTrackingValid || rightTrackingValid ? TrackedBoneCount : 0;
        if (leftTrackingValid)
        {
            CopyHandBones(leftHandOVRSkeleton, leftHandBonePositions, leftHandBoneQuaternions, TrackedBoneCount);
            UpdateFingerCurls(Hand.Left, leftFingerCurls);
        }
        if (rightTrackingValid)
        {
            CopyHandBones(rightHandOVRSkeleton, rightHandBonePositions, rightHandBoneQuaternions, TrackedBoneCount);
            UpdateFingerCurls(Hand.Right, rightFingerCurls);
        }
        EnsureGripDistanceArrays(TrackedBoneCount);
        gripContactPipeline?.Process(
            leftHandInteractingClimbingHold,
            rightHandInteractingClimbingHold,
            leftHandBonePositions,
            rightHandBonePositions,
            leftFingerCurls,
            rightFingerCurls,
            leftTrackingValid,
            rightTrackingValid);

        // Grip state is shared by in-context and detached examination. Only Grip mode may move the board.
        if (gameMode == GameMode.Grip)
        {
            UpdateGripMode();
        }
        else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                 ghostHoldController.CurrentGhost != null)
        {
            UpdateGripMode(false);
        }

        if (!leftTrackingValid && !rightTrackingValid)
        {
            return;
        }

        // Update networked hands
        if (!shouldOtherPlayerHandsBeActive)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else if (networkedHands == null)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else if (networkedHands.leftHandJointPositionsNetworked.Count != numBonesPerHand
             || networkedHands.rightHandJointPositionsNetworked.Count != numBonesPerHand
             || networkedHands.leftHandJointQuaternionNetworked.Count != numBonesPerHand
             || networkedHands.rightHandJointQuaternionNetworked.Count != numBonesPerHand)
        {
            otherPlayerLeftHand.gameObject.SetActive(false);
            otherPlayerRightHand.gameObject.SetActive(false);
        }
        else
        {
            otherPlayerLeftHand.gameObject.SetActive(true);
            otherPlayerRightHand.gameObject.SetActive(true);
            for (int i = 0; i < numBonesPerHand; i++)
            {
                otherPlayerLeftHand.joints[i].transform.position = networkedHands.leftHandJointPositionsNetworked[i] + environment.transform.position;
                otherPlayerRightHand.joints[i].transform.position = networkedHands.rightHandJointPositionsNetworked[i] + environment.transform.position;
                otherPlayerLeftHand.joints[i].transform.rotation = networkedHands.leftHandJointQuaternionNetworked[i];
                otherPlayerRightHand.joints[i].transform.rotation = networkedHands.rightHandJointQuaternionNetworked[i];
            }
        }

    }

    private static void CopyHandBones(
        OVRSkeleton skeleton,
        List<Vector3> positions,
        List<Quaternion> rotations,
        int count)
    {
        positions.Clear();
        rotations.Clear();
        if (positions.Capacity < count)
        {
            positions.Capacity = count;
        }
        if (rotations.Capacity < count)
        {
            rotations.Capacity = count;
        }

        for (int i = 0; i < count; i++)
        {
            Transform bone = skeleton.Bones[i].Transform;
            positions.Add(bone.position);
            rotations.Add(bone.rotation);
        }
    }

    private void EnsureGripDistanceArrays(int count)
    {
        if (leftHandBoneToHoldMinDistances == null || leftHandBoneToHoldMinDistances.Length != count)
        {
            leftHandBoneToHoldMinDistances = new float[count];
            Array.Fill(leftHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
        if (rightHandBoneToHoldMinDistances == null || rightHandBoneToHoldMinDistances.Length != count)
        {
            rightHandBoneToHoldMinDistances = new float[count];
            Array.Fill(rightHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
    }

    private void ResetHandDistances(int hand)
    {
        float[] distances = hand == 0
            ? leftHandBoneToHoldMinDistances
            : rightHandBoneToHoldMinDistances;
        if (distances != null)
        {
            Array.Fill(distances, float.PositiveInfinity);
        }
    }

    public void UpdateGripMode(bool allowLocomotion = true)
    {
        InitializeGripFacades();
        float now = Time.unscaledTime;
        UpdateHandGripLatch(Hand.Left, now);
        UpdateHandGripLatch(Hand.Right, now);
        UpdateLegacyGripTelemetry(Hand.Left);
        UpdateLegacyGripTelemetry(Hand.Right);
        leftHandIsGripping = leftGripLatch.IsEngaged;
        rightHandIsGripping = rightGripLatch.IsEngaged;

        if (!allowLocomotion)
        {
            StopGripLocomotion();
            return;
        }

        GripLocomotionDriver driver = GripLocomotionPolicy.SelectDriver(
            leftGripLatch.Phase,
            leftTrackingValid,
            rightGripLatch.Phase,
            rightTrackingValid);
        if (driver == GripLocomotionDriver.None)
        {
            StopGripLocomotion();
            return;
        }

        Hand drivingHand = driver == GripLocomotionDriver.Left ? Hand.Left : Hand.Right;
        if (!isGripLocomotionActive || gripLocomotionHand != drivingHand)
        {
            StopGripLocomotion();
            StartGripLocomotion(drivingHand, now);
        }

        Vector3 movement;
        if (drivingHand == Hand.Left)
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(leftHandBonePositions);
            movement = AdvanceGripLocomotion(Hand.Left, wristPosition, now);
            leftHandGripLastPosition = wristPosition;
        }
        else
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(rightHandBonePositions);
            movement = AdvanceGripLocomotion(Hand.Right, wristPosition, now);
            rightHandGripLastPosition = wristPosition;
        }
        if (moonBoardEnv != null)
        {
            moonBoardEnv.transform.position += movement;
        }
    }

    private void UpdateHandGripLatch(Hand hand, float now)
    {
        bool trackingValid = hand == Hand.Left ? leftTrackingValid : rightTrackingValid;
        GameObject candidate = hand == Hand.Left
            ? leftHandInteractingClimbingHold
            : rightHandInteractingClimbingHold;
        float[] curls = hand == Hand.Left ? leftFingerCurls : rightFingerCurls;
        GripAcquisitionSample acquisitionSample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        GripLatchStateMachine latch = hand == Hand.Left ? leftGripLatch : rightGripLatch;

        int minFingers = ResolveMinFingers(candidate);
        int candidateHoldId = candidate != null ? candidate.GetInstanceID() : 0;
        bool canEvaluateAcquisition = latch.Phase == GripLatchPhase.Free &&
                                      candidate != null &&
                                      trackingValid &&
                                      HoldAffordancesState == HoldAffordancesLoadState.Ready;
        bool useDegradedCpu = DegradedGripContactAcquisition.ShouldUseCpu(
            IsDegradedGripAcquisitionActive,
            GetGripAcquisitionContext());
        bool acquisitionReady = false;
        int highFlexedContactMask = 0;
        if (canEvaluateAcquisition && useDegradedCpu)
        {
            acquisitionReady = TryBuildDegradedGripContactMask(
                hand,
                candidate,
                curls,
                out highFlexedContactMask);
        }
        else if (canEvaluateAcquisition && !IsGripFeedbackDegraded && acquisitionSample.IsValid)
        {
            acquisitionReady = true;
            highFlexedContactMask = acquisitionSample.ConsumeFlexedContactMask(
                candidateHoldId,
                curls,
                gripFlexionEngageThreshold,
                gripFingertipRange,
                now);
        }
        int lowFlexedMask = GripEngagementGate.BuildFlexedMask(curls, gripFlexionReleaseThreshold);
        GripLatchTransition transition = latch.Update(
            now,
            trackingValid,
            acquisitionReady,
            acquisitionReady ? candidateHoldId : 0,
            minFingers,
            highFlexedContactMask,
            lowFlexedMask);

        HandleGripLatchTransition(hand, candidate, minFingers, transition, now, trackingValid);
    }

    private GripAcquisitionContext GetGripAcquisitionContext()
    {
        return gameMode switch
        {
            GameMode.Grip => GripAcquisitionContext.WallGrip,
            GameMode.Ghost => GripAcquisitionContext.DetachedInspection,
            _ => GripAcquisitionContext.None,
        };
    }

    private bool TryBuildDegradedGripContactMask(
        Hand hand,
        GameObject hold,
        IReadOnlyList<float> curls,
        out int contactMask)
    {
        contactMask = 0;
        if (!TryGetDegradedGripGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error))
        {
            RecordDegradedGripGeometryFailure(hand, hold, error);
            return false;
        }

        List<Vector3> positions = hand == Hand.Left
            ? leftHandBonePositions
            : rightHandBonePositions;
        float[] distances = hand == Hand.Left
            ? leftDegradedGripDistances
            : rightDegradedGripDistances;
        if (!DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                hold,
                geometry,
                positions,
                distances,
                out error))
        {
            RecordDegradedGripGeometryFailure(hand, hold, error);
            return false;
        }

        contactMask = GripEngagementGate.BuildFlexedContactMask(
            curls,
            distances,
            gripFlexionEngageThreshold,
            gripFingertipRange);
        return true;
    }

    private void UpdateFingerCurls(Hand hand, float[] curls)
    {
        OVRHand trackedHand = hand == Hand.Left ? leftTrackedHand : rightTrackedHand;
        bool[] confidence = hand == Hand.Left ? leftFingerConfidence : rightFingerConfidence;
        List<Quaternion> rotations = hand == Hand.Left
            ? leftHandBoneQuaternions
            : rightHandBoneQuaternions;
        for (int finger = 0; finger < TrackedFingers.Length; finger++)
        {
            confidence[finger] = trackedHand.GetFingerConfidence(TrackedFingers[finger]) ==
                                 OVRHand.TrackingConfidence.High;
        }
        FingerCurlEstimator.Update(rotations, confidence, curls);
    }

    private void HandleGripLatchTransition(
        Hand hand,
        GameObject candidate,
        int minFingers,
        GripLatchTransition transition,
        float now,
        bool trackingValid)
    {
        GameObject latchedHold = hand == Hand.Left ? leftLatchedHold : rightLatchedHold;
        if (transition.Kind == GripLatchTransitionKind.Latched)
        {
            latchedHold = candidate;
            SetLatchedHold(hand, latchedHold);
            PublishGripEngagement(
                "GripLatched",
                hand,
                latchedHold,
                "min_fingers=" + minFingers);
        }
        else if (transition.Kind == GripLatchTransitionKind.Frozen)
        {
            PublishGripEngagement("GripFrozen", hand, latchedHold, "tracking_lost");
        }
        else if (transition.Kind == GripLatchTransitionKind.Released)
        {
            CompleteGripLocomotion(hand);
            PublishGripEngagement(
                "GripReleased",
                hand,
                latchedHold,
                transition.ReleaseReason.ToRecorderValue());
            SetLatchedHold(hand, null);
            InvalidateGripAcquisitionSample(hand);
        }

        if (transition.ResetAnchor && trackingValid)
        {
            ResetGripAnchor(hand, now);
        }
    }

    private void StartGripLocomotion(Hand hand, float now)
    {
        ResetGripAnchor(hand, now);
        gripLocomotionHand = hand;
        isGripLocomotionActive = true;
        actionRecorder?.Record(
            "LocomotionStart",
            hand == Hand.Left ? "Left" : "Right",
            hand == Hand.Left ? leftLatchedHold : rightLatchedHold,
            "one-hand grip locomotion started");
    }

    private Vector3 AdvanceGripLocomotion(
        Hand hand,
        Vector3 wristPosition,
        float now)
    {
        GripLocomotionFilter filter = hand == Hand.Left
            ? leftLocomotionFilter
            : rightLocomotionFilter;
        Vector3 movement = filter.Update(wristPosition, now);
        if (filter.LastDiscontinuityReason != GripLocomotionDiscontinuityReason.None)
        {
            Debug.LogWarning("[SceneConfiguror] " +
                             (hand == Hand.Left ? "Left" : "Right") +
                             " grip locomotion re-anchored after " +
                             filter.LastDiscontinuityReason + ".");
        }
        return movement;
    }

    private void CompleteGripLocomotion(Hand hand)
    {
        if (!isGripLocomotionActive || gripLocomotionHand != hand)
        {
            return;
        }

        GripLocomotionFilter filter = hand == Hand.Left
            ? leftLocomotionFilter
            : rightLocomotionFilter;
        filter.Complete();
    }

    private void ResetGripAnchor(Hand hand, float now)
    {
        List<Vector3> positions = hand == Hand.Left ? leftHandBonePositions : rightHandBonePositions;
        if (positions == null || positions.Count <= GripLocomotionAnchor.OpenXrWristBoneIndex)
        {
            return;
        }

        Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(positions);
        if (hand == Hand.Left)
        {
            leftHandGripStartPosition = wristPosition;
            leftHandGripLastPosition = wristPosition;
            leftLocomotionFilter.Reset(wristPosition, now);
            if (leftLocomotionFilter.LastDiscontinuityReason !=
                GripLocomotionDiscontinuityReason.None)
            {
                Debug.LogError("[SceneConfiguror] Left grip anchor rejected: " +
                               leftLocomotionFilter.LastDiscontinuityReason + ".");
            }
        }
        else
        {
            rightHandGripStartPosition = wristPosition;
            rightHandGripLastPosition = wristPosition;
            rightLocomotionFilter.Reset(wristPosition, now);
            if (rightLocomotionFilter.LastDiscontinuityReason !=
                GripLocomotionDiscontinuityReason.None)
            {
                Debug.LogError("[SceneConfiguror] Right grip anchor rejected: " +
                               rightLocomotionFilter.LastDiscontinuityReason + ".");
            }
        }
    }

    private void UpdateLegacyGripTelemetry(Hand hand)
    {
        GameObject hold = hand == Hand.Left
            ? leftHandInteractingClimbingHold
            : rightHandInteractingClimbingHold;
        bool trackingValid = hand == Hand.Left ? leftTrackingValid : rightTrackingValid;
        bool isFiveTipContact = trackingValid && hold != null &&
                                CheckIfHandIsGrippingHold((int)hand, hold);
        bool wasFiveTipContact = hand == Hand.Left
            ? leftLegacyFiveTipContact
            : rightLegacyFiveTipContact;
        if (isFiveTipContact && !wasFiveTipContact)
        {
            actionRecorder?.Record(
                "GripStart",
                hand == Hand.Left ? "Left" : "Right",
                hold,
                "legacy_all_five_tips");
        }
        if (hand == Hand.Left)
        {
            leftLegacyFiveTipContact = isFiveTipContact;
        }
        else
        {
            rightLegacyFiveTipContact = isFiveTipContact;
        }
    }

    private void SetLatchedHold(Hand hand, GameObject hold)
    {
        if (hand == Hand.Left)
        {
            leftLatchedHold = hold;
        }
        else
        {
            rightLatchedHold = hold;
        }
    }

    private void PublishGripEngagement(string action, Hand hand, GameObject hold, string details)
    {
        GripEngagementRecorded?.Invoke(action, hand, hold, details);
        actionRecorder?.Record(action, hand == Hand.Left ? "Left" : "Right", hold, details);
    }

    internal void PublishGripAcquisitionSample(
        Hand hand,
        int holdId,
        IReadOnlyList<float> curls,
        IReadOnlyList<float> distances,
        float sampledAt)
    {
        GripAcquisitionSample sample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        sample.Publish(holdId, curls, distances, sampledAt);
    }

    internal void InvalidateGripAcquisitionSample(Hand hand)
    {
        GripAcquisitionSample sample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        sample.Invalidate();
    }

    public bool CheckIfHandIsGrippingHold(int handIndex, GameObject climbingHold)
    {
        // If we don't have access to the expected hand bone positions, return false.
        // NOTE: The expected hand bone positions refer to the standard number and ordering of hand bone positions as returned from OVRSkeleton.Bones

        // Clarify left or right hand
        float[] handBoneToHoldMinDistances = null;
        if (handIndex == 0)
        {
            handBoneToHoldMinDistances = leftHandBoneToHoldMinDistances;
        }
        else if (handIndex == 1)
        {
            handBoneToHoldMinDistances = rightHandBoneToHoldMinDistances;
        }

        if (handBoneToHoldMinDistances == null || handBoneToHoldMinDistances.Length <= 25)
        {
            return false;
        }

        // Keep the legacy all-five-fingertips threshold as telemetry only. Locomotion consumes
        // GripLatchStateMachine instead.
        // Currently, the following will check if each fingertip is close to the hold. (When using Meta SDK v71's new OpenXR Hand Skeleton, bone indices 5, 10, 15, 20, 25 for thumb, pointer, middle, ring, and little fingertips respectively)
        bool isGripping = true;
        foreach (int boneIndex in FingertipBoneIndices)
        {
            if (handBoneToHoldMinDistances[boneIndex] > gripFingertipRange)
            {
                isGripping = false;
                break;
            }
        }

        return isGripping;
    }
    public bool AreHandPosesApproximatelyEqual(List<Vector3> pose1, List<Vector3> pose2, float threshold)
    {
        if (pose1.Count != pose2.Count)
        {
            return false;
        }
        for (int i = 2; i < pose1.Count; i++) // Skip bones 0 (palm) and 1 (wrist)
        {
            if (Vector3.Distance(pose1[i], pose2[i]) > threshold)
            {
                return false;
            }
        }
        return true;
    }

    public OVRSkeleton GetOVRSkeletonFromHandIndex(int handIndex)
    {
        if (handIndex == 0)
        {
            return leftHandOVRSkeleton;
        }
        else if (handIndex == 1)
        {
            return rightHandOVRSkeleton;
        }
        else
        {
            UnityEngine.Debug.LogError("Hand index " + handIndex + " not found!");
            return null;
        }
    }
    public void HandHoverEnter(int hand, GameObject hoveredGameObject)
    {
        GameObject hold = ResolveEligibleHoverHold(hoveredGameObject);
        if (hold == null)
        {
            return;
        }

        GetHoverResolver(hand)?.Enter(hold);
        RefreshHandHoverTarget(hand);
    }

    public void HandHoverExit(int hand, GameObject hoveredGameObject)
    {
        GameObject hold = ResolveCanonicalHoverHold(hoveredGameObject);
        if (hold == null)
        {
            return;
        }

        GetHoverResolver(hand)?.Exit(hold);
        RefreshHandHoverTarget(hand);
    }

    private OverlapContactResolver<GameObject> GetHoverResolver(int hand)
    {
        if (hand == 0)
        {
            return leftHoverContacts;
        }
        if (hand == 1)
        {
            return rightHoverContacts;
        }
        Debug.LogError("Hand index " + hand + " not found.");
        return null;
    }

    private GameObject ResolveEligibleHoverHold(GameObject candidate)
    {
        GameObject hold = ResolveCanonicalHoverHold(candidate);
        if (hold == null || gameMode == GameMode.Basic)
        {
            return null;
        }
        bool isGhost = IsGhostHold(hold);
        if ((gameMode == GameMode.Ghost && !isGhost) || (gameMode == GameMode.Grip && isGhost))
        {
            return null;
        }
        return isGhost || IsActiveRouteHold(hold) ? hold : null;
    }

    private GameObject ResolveCanonicalHoverHold(GameObject candidate)
    {
        if (candidate == null)
        {
            return null;
        }
        if (IsGhostHold(candidate))
        {
            return ghostHoldController.CurrentGhost;
        }

        GameObject activeHold = GetActiveRouteHold(candidate);
        if (activeHold != null)
        {
            return activeHold;
        }
        for (Transform current = candidate.transform; current != null; current = current.parent)
        {
            if (holdsParentGameObject != null && current.parent == holdsParentGameObject.transform)
            {
                return current.gameObject;
            }
        }
        return null;
    }

    private void RefreshHandHoverTarget(int hand, string exitDetails = "")
    {
        OverlapContactResolver<GameObject> resolver = GetHoverResolver(hand);
        if (resolver == null)
        {
            return;
        }
        GameObject previous = hand == 0
            ? leftHandInteractingClimbingHold
            : rightHandInteractingClimbingHold;
        GameObject current = resolver.Current;
        if (previous == current)
        {
            return;
        }

        Hand handSide = hand == 0 ? Hand.Left : Hand.Right;
        InvalidateGripAcquisitionSample(handSide);
        gripContactPipeline?.NotifyTargetDiscontinuity(handSide);

        GameObject otherHandTarget = hand == 0
            ? rightHandInteractingClimbingHold
            : leftHandInteractingClimbingHold;
        if (previous != null)
        {
            actionRecorder?.Record(
                "HoverExit",
                hand == 0 ? "Left" : "Right",
                previous,
                exitDetails);
            if (otherHandTarget != previous)
            {
                SetInteractionVisual(previous, false);
            }
        }

        if (hand == 0)
        {
            leftHandInteractingClimbingHold = current;
        }
        else
        {
            rightHandInteractingClimbingHold = current;
        }
        ResetHandDistances(hand);

        if (current != null)
        {
            actionRecorder?.Record("HoverEnter", hand == 0 ? "Left" : "Right", current);
            SetInteractionVisual(current, gripContactPipeline == null && !IsGripFeedbackDegraded);
        }
    }

    public void SetUpRouteByName(string routeName)
    {
        EnsureHoldsDictionary();
        UnityEngine.Debug.Log("Requested route by name: " + routeName);
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            UnityEngine.Debug.LogError("Route name " + routeName + " not found!");
            return;
        }

        ResetInteractionState();
        degradedGripGeometry.Clear();
        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        if (routeName != "[PREVIEW ALL (SHADER OFF)]")
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with holds " + string.Join(", ", activeRouteHoldsNamesList));
            SetUpRouteByHoldList(route);
        }
        else
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with all holds");
            PreviewAllHolds();
        }
        ApplyModeToRouteHolds();
        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
    }

    public bool SetRouteCatalog(MoonBoardStudyCatalog catalog, out string error)
    {
        if (catalog == null)
        {
            error = "MoonBoard route catalog is unavailable.";
            return false;
        }
        if (!catalog.TryValidate(out error))
        {
            return false;
        }
        if (holdAffordanceCatalog != null &&
            !TryValidateHoldAffordances(catalog, holdAffordanceCatalog, out error))
        {
            return false;
        }

        routeCatalog = catalog;
        holdsDictionary = null;
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (holdsDictionary.Count != catalog.holds.Length)
        {
            error = $"Scene contains {holdsDictionary.Count} holds; catalog requires {catalog.holds.Length}.";
            return false;
        }
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            if (!holdsDictionary.TryGetValue(hold.coordinate, out GameObject sceneHold) ||
                sceneHold.GetComponent<MeshFilter>() == null ||
                sceneHold.GetComponent<MeshRenderer>() == null)
            {
                error = "Scene is missing usable MoonBoard 2016 hold " + hold.coordinate + ".";
                return false;
            }
        }
        foreach (string coordinate in holdsDictionary.Keys)
        {
            if (!catalog.TryGetHold(coordinate, out _))
            {
                error = "Scene contains hold outside the MoonBoard 2016 catalog: " + coordinate + ".";
                return false;
            }
        }

        currentRouteName = catalog.routes[0].id;
        SetUpRouteByName(currentRouteName);
        error = string.Empty;
        return true;
    }

    public bool TryGetRouteDefinition(string routeId, out MoonBoardRouteDefinition route)
    {
        route = null;
        return routeCatalog != null && routeCatalog.TryGetRoute(routeId, out route);
    }

    public string GetRouteDisplayName(string routeId)
    {
        return TryGetRouteDefinition(routeId, out MoonBoardRouteDefinition route) ? route.name : routeId;
    }

    public bool TryValidateRoute(string routeName, out string error)
    {
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (!TryEnsureRouteSourceReady(routeName, out error))
        {
            return false;
        }
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        string[] missing = route.holds.Where(hold => !holdsDictionary.ContainsKey(hold)).ToArray();
        if (missing.Length > 0)
        {
            error = routeName + " is missing hold" + (missing.Length == 1 ? " " : "s ") +
                    string.Join(", ", missing) + ".";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TrySelectBaselineRoute(string routeName, out string error)
    {
        if (!TryValidateRoute(routeName, out error))
        {
            return false;
        }
        if (!TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        error = string.Empty;
        return true;
    }

    /// <summary>Resolves a role-aware route definition: the authoritative catalog first
    /// (start/finish mapped from move roles), then built-ins, then routes.json entries.</summary>
    private bool TryGetRouteDefinition(string routeName, out RouteDefinition route)
    {
        if (routeCatalog != null && routeCatalog.TryGetRoute(routeName, out MoonBoardRouteDefinition catalogRoute))
        {
            MoonBoardRouteMove[] ordered = catalogRoute.moves.OrderBy(move => move.sequence).ToArray();
            route = new RouteDefinition
            {
                name = catalogRoute.name,
                grade = catalogRoute.grade,
                holds = ordered.Select(move => move.coordinate).ToArray(),
                start = ordered.Where(move => move.role == "start").Select(move => move.coordinate).ToArray(),
                finish = ordered.Where(move => move.role == "finish").Select(move => move.coordinate).ToArray(),
            };
            return true;
        }
        if (TryGetBuiltInRouteDefinition(routeName, out route))
        {
            return true;
        }
        if (routeName != null && jsonRoutes.TryGetValue(routeName, out RouteDefinition jsonRoute))
        {
            route = jsonRoute;
            return true;
        }
        route = null;
        return false;
    }

    public bool IsBuiltInRoute(string routeName)
    {
        return TryGetBuiltInRouteDefinition(routeName, out _);
    }

    public string GetRoutesLoadStatusLine()
    {
        return RoutesJsonLoadState switch
        {
            RoutesLoadState.Ready => "READY (" + jsonRouteNames.Count + " imported)",
            RoutesLoadState.Failed => "FAILED: " + RoutesLoadFailureReason,
            _ => "LOADING",
        };
    }

    private bool TryEnsureRouteSourceReady(string routeName, out string error)
    {
        if (IsBuiltInRoute(routeName) ||
            (routeCatalog != null && routeCatalog.TryGetRoute(routeName, out _)) ||
            RoutesJsonLoadState == RoutesLoadState.Ready)
        {
            error = string.Empty;
            return true;
        }

        error = RoutesJsonLoadState == RoutesLoadState.Loading
            ? "routes.json is still loading; imported route '" + routeName + "' cannot start yet."
            : "routes.json failed to load; imported route '" + routeName + "' is unavailable: " +
              RoutesLoadFailureReason;
        return false;
    }

    /// <summary>Catalog routes first, then built-in study routes, then routes.json entries.</summary>
    public List<string> GetAvailableRouteNames()
    {
        List<string> names = new();
        if (routeCatalog != null)
        {
            names.AddRange(routeCatalog.routes.Select(catalogRoute => catalogRoute.id));
        }
        names.AddRange(BuiltInRouteNames);
        names.AddRange(jsonRouteNames);
        return names;
    }

    private IEnumerator LoadRoutesJson()
    {
        RoutesJsonLoadState = RoutesLoadState.Loading;
        RoutesLoadFailureReason = string.Empty;
        RoutesJsonSha256 = null;
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/routes.json";
        string json = null;
        byte[] jsonBytes = null;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
                jsonBytes = request.downloadHandler.data;
            }
            else if (request.responseCode != 404)
            {
                SetRoutesLoadFailed("request failed: " + request.error);
                yield break;
            }
        }
        else if (File.Exists(path))
        {
            json = File.ReadAllText(path);
            jsonBytes = File.ReadAllBytes(path);
        }

        if (json == null)
        {
            SetRoutesLoadFailed("file not found in StreamingAssets.");
            yield break;
        }

        if (!RouteLibrary.TryParseJson(json, BuiltInRouteNames, out List<RouteDefinition> parsed, out string error))
        {
            SetRoutesLoadFailed(error);
            yield break;
        }

        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        foreach (RouteDefinition route in parsed)
        {
            jsonRoutes[route.name] = route;
            jsonRouteNames.Add(route.name);
        }
        RoutesJsonSha256 = ComputeSha256(jsonBytes);
        RoutesJsonLoadState = RoutesLoadState.Ready;
        Debug.Log("[SceneConfiguror] Loaded " + jsonRouteNames.Count + " route(s) from routes.json: " +
                  string.Join(", ", jsonRouteNames));
    }

    private void SetRoutesLoadFailed(string reason)
    {
        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        RoutesJsonSha256 = null;
        RoutesLoadFailureReason = reason;
        RoutesJsonLoadState = RoutesLoadState.Failed;
        Debug.LogError("[SceneConfiguror] routes.json failed: " + reason);
    }

    private IEnumerator LoadHoldAffordances()
    {
        HoldAffordancesState = HoldAffordancesLoadState.Loading;
        HoldAffordancesFailureReason = string.Empty;
        holdAffordanceCatalog = null;
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/hold_affordances.json";
        string json = null;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
            }
            else
            {
                SetHoldAffordancesFailed("request failed: " + request.error);
                yield break;
            }
        }
        else
        {
            Exception readException = null;
            try
            {
                if (File.Exists(path))
                {
                    json = File.ReadAllText(path);
                }
            }
            catch (Exception exception)
            {
                readException = exception;
                Debug.LogException(exception);
            }
            if (readException != null)
            {
                SetHoldAffordancesFailed("read failed: " + readException.Message);
                yield break;
            }
        }

        if (json == null)
        {
            SetHoldAffordancesFailed("file not found in StreamingAssets.");
            yield break;
        }
        if (!HoldAffordanceCatalog.TryParse(json, out holdAffordanceCatalog, out string error))
        {
            SetHoldAffordancesFailed(error);
            yield break;
        }
        if (routeCatalog != null &&
            !TryValidateHoldAffordances(routeCatalog, holdAffordanceCatalog, out error))
        {
            SetHoldAffordancesFailed(error);
            yield break;
        }

        HoldAffordancesState = HoldAffordancesLoadState.Ready;
        Debug.Log("[SceneConfiguror] Loaded " + holdAffordanceCatalog.Count +
                  " pocket affordance override(s).");
    }

    private void SetHoldAffordancesFailed(string reason)
    {
        holdAffordanceCatalog = null;
        HoldAffordancesState = HoldAffordancesLoadState.Failed;
        HoldAffordancesFailureReason = reason;
        Debug.LogError("[SceneConfiguror] hold_affordances.json failed: " + reason);
    }

    private static bool TryValidateHoldAffordances(
        MoonBoardStudyCatalog catalog,
        HoldAffordanceCatalog affordances,
        out string error)
    {
        HashSet<string> knownScans = new(catalog.holds.Select(hold => hold.scanId),
            StringComparer.OrdinalIgnoreCase);
        foreach (string scanId in affordances.ScanIds)
        {
            if (!knownScans.Contains(scanId))
            {
                error = "Hold affordance references unknown scan ID " + scanId + ".";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    private static string ComputeSha256(byte[] value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(value);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool TryGetBuiltInRouteDefinition(string routeName, out RouteDefinition route)
    {
        switch (routeName)
        {
            case "DEATH STAR":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "DEATH STAR",
                    holds = new[] { "D15", "D18", "G13", "H11", "I4", "J6", "K9" },
                    start = new[] { "I4", "J6" },
                    finish = new[] { "D18" },
                };
                return true;
            case "TO JUG, OR NOT TO JUG...":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "TO JUG, OR NOT TO JUG...",
                    holds = new[] { "D9", "D15", "F5", "F12", "G13", "H10", "H18" },
                    start = new[] { "F5" },
                    finish = new[] { "H18" },
                };
                return true;
            case "[PREVIEW ALL (SHADER OFF)]":
                route = new RouteDefinition
                {
                    name = "[PREVIEW ALL (SHADER OFF)]",
                    holds = new[] { // this was the fastest way to get this working, sue me
                    "A1", "B1", "C1", "D1", "E1", "F1", "G1", "H1", "I1", "J1", "K1",
                    "A2", "B2", "C2", "D2", "E2", "F2", "G2", "H2", "I2", "J2", "K2",
                    "A3", "B3", "C3", "D3", "E3", "F3", "G3", "H3", "I3", "J3", "K3",
                    "A4", "B4", "C4", "D4", "E4", "F4", "G4", "H4", "I4", "J4", "K4",
                    "A5", "B5", "C5", "D5", "E5", "F5", "G5", "H5", "I5", "J5", "K5",
                    "A6", "B6", "C6", "D6", "E6", "F6", "G6", "H6", "I6", "J6", "K6",
                    "A7", "B7", "C7", "D7", "E7", "F7", "G7", "H7", "I7", "J7", "K7",
                    "A8", "B8", "C8", "D8", "E8", "F8", "G8", "H8", "I8", "J8", "K8",
                    "A9", "B9", "C9", "D9", "E9", "F9", "G9", "H9", "I9", "J9", "K9",
                    "A10", "B10", "C10", "D10", "E10", "F10", "G10", "H10", "I10", "J10", "K10",
                    "A11", "B11", "C11", "D11", "E11", "F11", "G11", "H11", "I11", "J11", "K11",
                    "A12", "B12", "C12", "D12", "E12", "F12", "G12", "H12", "I12", "J12", "K12",
                    "A13", "B13", "C13", "D13", "E13", "F13", "G13", "H13", "I13", "J13", "K13",
                    "A14", "B14", "C14", "D14", "E14", "F14", "G14", "H14", "I14", "J14", "K14",
                    "A15", "B15", "C15", "D15", "E15", "F15", "G15", "H15", "I15", "J15", "K15",
                    "A16", "B16", "C16", "D16", "E16", "F16", "G16", "H16", "I16", "J16", "K16",
                    "A17", "B17", "C17", "D17", "E17", "F17", "G17", "H17", "I17", "J17", "K17",
                    "A18", "B18", "C18", "D18", "E18", "F18", "G18", "H18", "I18", "J18", "K18"
                    },
                };
                return true;
            default:
                route = null;
                return false;
        }
    }

    private bool TryGetRouteHolds(string routeName, out List<string> routeHolds)
    {
        if (routeName == "[PREVIEW ALL (SHADER OFF)]")
        {
            EnsureHoldsDictionary();
            routeHolds = holdsDictionary.Keys.OrderBy(coordinate => coordinate).ToList();
            return true;
        }
        if (routeCatalog != null && routeCatalog.TryGetRoute(routeName, out MoonBoardRouteDefinition route))
        {
            routeHolds = route.moves.OrderBy(move => move.sequence).Select(move => move.coordinate).ToList();
            return true;
        }
        routeHolds = null;
        return false;
    }

    public void SetGameMode(GameMode newMode)
    {
        EnsureExaminationHeadlamp();
        bool leavingGhostMode = gameMode == GameMode.Ghost && newMode != GameMode.Ghost;
        if (leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(false);
        }
        ResetInteractionState();
        gameMode = newMode;
        SetRouteCuePresentation(newMode == GameMode.Basic
            ? baselineRouteCuePresentation
            : RouteCuePresentation.VirtualHalos);
        if (examinationHeadlamp != null)
        {
            examinationHeadlamp.enabled = newMode == GameMode.Grip || newMode == GameMode.Ghost;
        }
        ApplyModeToRouteHolds();
        if (newMode == GameMode.Grip || newMode == GameMode.Ghost)
        {
            PrewarmDegradedGripGeometry(activeHoldsList);
        }
        if (!leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(newMode == GameMode.Ghost);
        }
        if (newMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
        else
        {
            gripContactPipeline?.ClearFeedback();
            gripContactPipeline?.Prepare((IReadOnlyList<GameObject>)null);
        }
        actionRecorder?.Record("ModeChanged", "", null, newMode.ToString());
    }

    public RouteCuePresentation BaselineRouteCuePresentation => baselineRouteCuePresentation;
    public RouteCuePresentation CurrentRouteCuePresentation { get; private set; } =
        RouteCuePresentation.Hidden;
    public bool AreVirtualRouteCuesVisible =>
        CurrentRouteCuePresentation == RouteCuePresentation.VirtualHalos;

    public RouteCuePresentation GetRouteCuePresentationForCondition(string condition)
    {
        return RouteCuePolicy.ForCondition(condition, baselineRouteCuePresentation);
    }

    public RouteCueRole GetRouteCueRole(string holdCoordinate)
    {
        if (ActiveRouteDefinition?.start != null &&
            Array.Exists(ActiveRouteDefinition.start,
                coordinate => string.Equals(coordinate, holdCoordinate, StringComparison.OrdinalIgnoreCase)))
        {
            return RouteCueRole.Start;
        }
        if (ActiveRouteDefinition?.finish != null &&
            Array.Exists(ActiveRouteDefinition.finish,
                coordinate => string.Equals(coordinate, holdCoordinate, StringComparison.OrdinalIgnoreCase)))
        {
            return RouteCueRole.Finish;
        }
        return RouteCueRole.Intermediate;
    }

    public RouteCueStyle GetRouteCueStyle(string holdCoordinate)
    {
        return RouteCuePolicy.GetStyle(GetRouteCueRole(holdCoordinate));
    }

    public void SetBaselineRouteCuePresentation(RouteCuePresentation presentation)
    {
        baselineRouteCuePresentation = presentation;
        if (gameMode == GameMode.Basic)
        {
            SetRouteCuePresentation(presentation);
        }
    }

    public void SetRouteCuePresentation(RouteCuePresentation presentation)
    {
        CurrentRouteCuePresentation = presentation;
        bool showVirtualHalos = presentation == RouteCuePresentation.VirtualHalos;
        foreach (GameObject circle in activeHighlightCircles)
        {
            if (circle != null)
            {
                circle.SetActive(showVirtualHalos);
            }
        }
    }

    public void PrepareGripHold(GameObject hold)
    {
        gripContactPipeline?.Prepare(hold);
    }

    public void ResetMoonBoardTransform()
    {
        CacheMoonBoardTransform();
        if (!hasInitialMoonBoardTransform)
        {
            return;
        }

        moonBoardEnv.transform.SetLocalPositionAndRotation(
            initialMoonBoardLocalPosition,
            initialMoonBoardLocalRotation);
        moonBoardEnv.transform.localScale = initialMoonBoardLocalScale;
    }

    public void SetStudyEnvironmentVisible(bool visible)
    {
        Transform alignmentRoot = moonBoardEnv != null ? moonBoardEnv.transform.parent : null;
        if (!visible)
        {
            if (!studyEnvironmentHidden)
            {
                CaptureAndHideSupplementalScenery();
                CaptureAndHideStudyCameraBackground();
                studyEnvironmentHidden = true;
            }
            if (environment != null)
            {
                // Keep the environment root and the alignment root active so spatial-anchor
                // registration survives Condition A; everything else hides.
                environment.SetActive(true);
                foreach (Transform child in environment.transform)
                {
                    child.gameObject.SetActive(child == alignmentRoot);
                }
            }
            if (moonBoardEnv != null)
            {
                moonBoardEnv.SetActive(false);
            }
            return;
        }

        if (environment != null)
        {
            environment.SetActive(true);
            foreach (Transform child in environment.transform)
            {
                child.gameObject.SetActive(true);
            }
        }
        if (moonBoardEnv != null)
        {
            moonBoardEnv.SetActive(true);
        }
        if (studyEnvironmentHidden)
        {
            RestoreSupplementalScenery();
            RestoreStudyCameraBackground();
            studyEnvironmentHidden = false;
        }
    }

    private void CaptureAndHideSupplementalScenery()
    {
        supplementalSceneryActiveStates.Clear();
        int waterLayer = LayerMask.NameToLayer("Water");
        foreach (Transform candidate in FindObjectsByType<Transform>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            if (candidate == null || candidate.gameObject.scene != gameObject.scene ||
                (environment != null &&
                 (candidate == environment.transform || candidate.IsChildOf(environment.transform))))
            {
                continue;
            }

            GameObject sceneryRoot = FindSupplementalSceneryRoot(candidate, waterLayer);
            if (sceneryRoot != null && !supplementalSceneryActiveStates.ContainsKey(sceneryRoot))
            {
                supplementalSceneryActiveStates.Add(sceneryRoot, sceneryRoot.activeSelf);
            }
        }

        foreach (GameObject scenery in supplementalSceneryActiveStates.Keys)
        {
            if (scenery != null)
            {
                scenery.SetActive(false);
            }
        }
    }

    private GameObject FindSupplementalSceneryRoot(Transform candidate, int waterLayer)
    {
        Transform match = null;
        for (Transform current = candidate; current != null; current = current.parent)
        {
            if (environment != null && current == environment.transform)
            {
                return null;
            }
            if ((waterLayer >= 0 && current.gameObject.layer == waterLayer) ||
                SupplementalSceneryNameMarkers.Any(marker =>
                    current.name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                match = current;
            }
        }
        return match != null ? match.gameObject : null;
    }

    private void RestoreSupplementalScenery()
    {
        foreach (KeyValuePair<GameObject, bool> entry in supplementalSceneryActiveStates)
        {
            if (entry.Key != null)
            {
                entry.Key.SetActive(entry.Value);
            }
        }
        supplementalSceneryActiveStates.Clear();
    }

    private void CaptureAndHideStudyCameraBackground()
    {
        studyEnvironmentCameraStates.Clear();
        foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (camera == null || !camera.isActiveAndEnabled ||
                camera.targetTexture != null)
            {
                continue;
            }
            studyEnvironmentCameraStates[camera] = (camera.clearFlags, camera.backgroundColor);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
        }
    }

    private void RestoreStudyCameraBackground()
    {
        foreach (KeyValuePair<Camera, (CameraClearFlags flags, Color background)> entry
                 in studyEnvironmentCameraStates)
        {
            if (entry.Key == null)
            {
                continue;
            }
            entry.Key.clearFlags = entry.Value.flags;
            entry.Key.backgroundColor = entry.Value.background;
        }
        studyEnvironmentCameraStates.Clear();
    }

    public bool IsGripFeedbackReady => !IsGripFeedbackDegraded &&
                                        HoldAffordancesState == HoldAffordancesLoadState.Ready &&
                                        gripContactPipeline != null && gripContactPipeline.IsSupported;

    public void SetStudyFeedbackVisible(bool visible)
    {
        bool effectiveVisibility = visible && !IsGripFeedbackDegraded;
        studyFeedbackVisible = effectiveVisibility;
        foreach (HandBoneTracker tracker in FindObjectsByType<HandBoneTracker>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None))
        {
            tracker.SetFeedbackVisible(effectiveVisibility);
        }
        gripContactPipeline?.SetFeedbackVisible(effectiveVisibility);
    }

    public void DebugInjectGripReadbackFailures(int epochCount = 1)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        EnsureGripPipeline();
        gripContactPipeline?.DebugInjectReadbackFailures(epochCount);
    }

    public void DebugSetGripReadbackFailures(bool enabled)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        debugForceGripReadbackFailures = enabled;
        EnsureGripPipeline();
        gripContactPipeline?.DebugSetReadbackFailures(enabled);
    }

    public Transform GetGripNormalReference(GameObject hold)
    {
        return IsGhostHold(hold) && ghostHoldController.WallReferent != null
            ? ghostHoldController.WallReferent.transform
            : hold.transform;
    }

    public bool IsActiveRouteHold(GameObject candidate)
    {
        return GetActiveRouteHold(candidate) != null;
    }

    public GameObject GetActiveRouteHold(GameObject candidate)
    {
        if (candidate == null || activeHoldsList == null)
        {
            return null;
        }

        Transform candidateTransform = candidate.transform;
        foreach (GameObject activeHold in activeHoldsList)
        {
            if (activeHold != null &&
                (candidateTransform == activeHold.transform || candidateTransform.IsChildOf(activeHold.transform)))
            {
                return activeHold;
            }
        }
        return null;
    }

    public bool IsGhostHold(GameObject candidate)
    {
        return ghostHoldController != null && ghostHoldController.IsGhostHold(candidate);
    }

    public void RegisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        ResolveStudyLayers();
        if (studyGhostHoldsLayer >= 0)
        {
            SetLayerRecursively(ghost, studyGhostHoldsLayer);
        }

        XRGrabInteractable grab = ghost.GetComponent<XRGrabInteractable>();
        if (grab != null)
        {
            grab.enabled = true;
        }
    }

    public void UnregisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        leftHoverContacts.Remove(ghost);
        rightHoverContacts.Remove(ghost);
        RefreshHandHoverTarget(0);
        RefreshHandHoverTarget(1);
        int ghostId = ghost.GetInstanceID();
        if (leftGripLatch != null && leftGripLatch.HoldId == ghostId)
        {
            leftGripLatch.Reset();
            leftLatchedHold = null;
            StopGripLocomotion(Hand.Left);
        }
        if (rightGripLatch != null && rightGripLatch.HoldId == ghostId)
        {
            rightGripLatch.Reset();
            rightLatchedHold = null;
            StopGripLocomotion(Hand.Right);
        }
        leftHandIsGripping = leftGripLatch != null && leftGripLatch.IsEngaged;
        rightHandIsGripping = rightGripLatch != null && rightGripLatch.IsEngaged;
    }

    private void ApplyModeToRouteHolds()
    {
        if (activeHoldsList == null)
        {
            return;
        }

        bool enableWallColliders = gameMode != GameMode.Basic;
        bool enableWallGrab = gameMode == GameMode.Grip;
        foreach (GameObject hold in activeHoldsList)
        {
            if (hold == null)
            {
                continue;
            }

            XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
            if (grab != null)
            {
                grab.enabled = enableWallGrab;
            }
            foreach (Collider collider in hold.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = enableWallColliders;
            }
        }
    }

    private void ResetInteractionState()
    {
        StopGripLocomotion();
        SetInteractionVisual(leftHandInteractingClimbingHold, false);
        if (rightHandInteractingClimbingHold != leftHandInteractingClimbingHold)
        {
            SetInteractionVisual(rightHandInteractingClimbingHold, false);
        }
        leftHandInteractingClimbingHold = null;
        rightHandInteractingClimbingHold = null;
        leftHoverContacts.Clear();
        rightHoverContacts.Clear();
        HoverContactEpoch++;
        gripContactPipeline?.ClearFeedback();
        leftGripLatch?.Reset();
        rightGripLatch?.Reset();
        leftGripAcquisitionSample.Invalidate();
        rightGripAcquisitionSample.Invalidate();
        ResetHandDistances(0);
        ResetHandDistances(1);
        leftFingerContactMask = 0;
        rightFingerContactMask = 0;
        perFingerContactMask = 0;
        leftHandGripScore = 0f;
        rightHandGripScore = 0f;
        currentGripScore = 0f;
        leftLatchedHold = null;
        rightLatchedHold = null;
        leftLegacyFiveTipContact = false;
        rightLegacyFiveTipContact = false;
        leftHandIsGripping = false;
        rightHandIsGripping = false;
        isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    private void StopGripLocomotion(Hand? hand = null)
    {
        if (!isGripLocomotionActive || (hand.HasValue && gripLocomotionHand != hand))
        {
            return;
        }

        if (gripLocomotionHand == Hand.Left)
        {
            leftLocomotionFilter?.Cancel();
        }
        else if (gripLocomotionHand == Hand.Right)
        {
            rightLocomotionFilter?.Cancel();
        }
        actionRecorder?.Record("LocomotionStop", "", null, "grip locomotion stopped");
        isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    private void SetInteractionVisual(GameObject hold, bool active, float maxDistance = -1f)
    {
        if (hold != null && hold.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.GetPropertyBlock(HoldProperties);
            HoldProperties.SetInt("_IsBeingInteracted", active ? 1 : 0);
            if (maxDistance >= 0f)
            {
                HoldProperties.SetFloat("_InteractionColorMaxDistance", maxDistance);
            }
            meshRenderer.SetPropertyBlock(HoldProperties);
        }
    }

    private void SetHoldAlpha(Renderer renderer, float alpha)
    {
        renderer.GetPropertyBlock(HoldProperties);
        HoldProperties.SetFloat("_HoldAlpha", alpha);
        renderer.SetPropertyBlock(HoldProperties);
    }

    void SetUpRouteByHoldList(RouteDefinition route)
    {
        ActiveRouteDefinition = route;
        List<string> holdsList = new(route.holds);
        ClearHighlightCircles();
        // Disable all holds
        activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    SetHoldAlpha(renderer, inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in holdsList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                SetHoldAlpha(renderer, activeHoldAlpha);
            }

            CoACD coACD = holdsDictionary[holdName].GetComponent<CoACD>();
            if (coACD != null)
            {
                holdsDictionary[holdName].GetComponent<CoACD>().enabled = true;
                MeshCollider[] meshColliders = holdsDictionary[holdName].GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = true;
                }
            }
            SphereCollider sphere = holdsDictionary[holdName].GetComponent<SphereCollider>();
            if (sphere != null)
            {
                holdsDictionary[holdName].GetComponent<SphereCollider>().enabled = true;
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
        SpawnRouteHalos(route);
    }

    private void SpawnRouteHalos(RouteDefinition route)
    {
        if (highlightCirclePrefab == null || holdsParentGameObject == null || route?.holds == null)
        {
            return;
        }

        Transform board = holdsParentGameObject.transform;
        Transform boardSurface = board.parent?.Find("Main Surface") ?? board.parent?.Find("Plane");
        if (boardSurface == null)
        {
            Debug.LogError("MoonBoard main surface is unavailable; route halos cannot be projected.");
            return;
        }

        Vector3 boardNormal = boardSurface.up.normalized;
        Vector3 boardHorizontal = boardSurface.right.normalized;
        Vector3 boardVertical = RouteCuePolicy.GetBoardVertical(boardNormal);
        Vector3 boardPlanePoint = boardSurface.position;
        Transform viewer = centerEyeAnchor != null ? centerEyeAnchor.transform : mainCamera?.transform;
        if (viewer != null && Vector3.Dot(boardNormal, viewer.position - boardPlanePoint) < 0f)
        {
            boardNormal = -boardNormal;
        }

        bool hasRoles = route.start != null && route.start.Length > 0 &&
                        route.finish != null && route.finish.Length > 0;
        HashSet<string> starts = hasRoles
            ? new HashSet<string>(route.start, StringComparer.OrdinalIgnoreCase)
            : null;
        HashSet<string> finishes = hasRoles
            ? new HashSet<string>(route.finish, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string holdName in route.holds)
        {
            if (!holdsDictionary.TryGetValue(holdName, out GameObject hold) ||
                !TryGetCombinedRendererBounds(hold, out Bounds bounds))
            {
                continue;
            }

            float width = ProjectedBoundsDiameter(bounds, boardHorizontal);
            float height = ProjectedBoundsDiameter(bounds, boardVertical);
            float outerDiameter = Mathf.Clamp(Mathf.Max(width, height) * 1.35f, 0.14f, 0.30f);
            // Renderer bounds are scan-frame data and can be off-center. The hold transform is
            // the calibrated MoonBoard grid anchor, so it is the only valid point to project.
            Vector3 position = RouteCuePolicy.ProjectGridAnchorOntoBoard(
                hold.transform.position,
                boardPlanePoint,
                boardNormal,
                0.015f);
            Quaternion rotation = Quaternion.LookRotation(boardNormal, boardVertical);

            bool isStart = hasRoles && starts.Contains(holdName);
            bool isFinish = hasRoles && finishes.Contains(holdName);
            RouteCueRole role = isStart
                ? RouteCueRole.Start
                : isFinish
                    ? RouteCueRole.Finish
                    : RouteCueRole.Intermediate;
            RouteCueStyle style = RouteCuePolicy.GetStyle(role);
            CreateHaloRing(holdName, position, rotation, outerDiameter, style.Color, 0);
            if (style.RingCount == 2)
            {
                CreateHaloRing(holdName, position, rotation, outerDiameter * 0.65f, style.Color, 1);
            }
        }
        SetRouteCuePresentation(CurrentRouteCuePresentation);
    }

    private void CreateHaloRing(
        string holdName,
        Vector3 position,
        Quaternion rotation,
        float diameter,
        Color color,
        int ringIndex)
    {
        GameObject circle = Instantiate(highlightCirclePrefab);
        circle.name = holdName + (ringIndex == 0 ? " Route Halo" : " Route Halo Inner");
        circle.transform.SetPositionAndRotation(position, rotation);
        if (circle.TryGetComponent(out SpriteRenderer spriteRenderer))
        {
            spriteRenderer.color = color;
            spriteRenderer.sharedMaterial = GetHighlightCircleMaterial();
            spriteRenderer.sortingLayerName = "Default";
            spriteRenderer.sortingOrder = -100 + ringIndex;
            if (spriteRenderer.sprite != null)
            {
                Vector3 spriteSize = spriteRenderer.sprite.bounds.size;
                float sourceDiameter = Mathf.Max(spriteSize.x, spriteSize.y);
                circle.transform.localScale = Vector3.one * (diameter / sourceDiameter);
            }
        }
        if (indicatorLayer >= 0)
        {
            SetLayerRecursively(circle, indicatorLayer);
        }
        circle.transform.SetParent(holdsParentGameObject.transform, true);
        activeHighlightCircles.Add(circle);
    }

    private Material GetHighlightCircleMaterial()
    {
        if (highlightCircleMaterial == null)
        {
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default");
            if (shader != null)
            {
                highlightCircleMaterial = new Material(shader) { name = "Route Halo Material" };
            }
        }
        return highlightCircleMaterial;
    }

    private static float ProjectedBoundsDiameter(Bounds bounds, Vector3 axis)
    {
        Vector3 extents = bounds.extents;
        return 2f * (Mathf.Abs(axis.x) * extents.x +
                     Mathf.Abs(axis.y) * extents.y +
                     Mathf.Abs(axis.z) * extents.z);
    }

    private static bool TryGetCombinedRendererBounds(GameObject target, out Bounds bounds)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++)
        {
            bounds.Encapsulate(renderers[index].bounds);
        }
        return true;
    }
    /// <summary>
    /// Recursively sets go and all its children to the given layer.
    /// </summary>
    void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    void PreviewAllHolds()
    {
        // Disable all holds
        activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    SetHoldAlpha(renderer, inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in activeRouteHoldsNamesList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                SetHoldAlpha(renderer, activeHoldAlpha);
            }

            activeHoldsList.Add(holdsDictionary[holdName]);
        }
    }
    private void ClearHighlightCircles()
    {
        foreach (var circle in activeHighlightCircles)
        {
            if (circle != null)
            {
                circle.SetActive(false);
                Destroy(circle);
            }
        }
        activeHighlightCircles.Clear();
    }

    private static XRGrabInteractable EnsureHoldInteractionComponents(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        if (sphere == null)
        {
            sphere = hold.AddComponent<SphereCollider>();
        }
        MeshRenderer renderer = hold.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            sphere.center = renderer.localBounds.center;
            sphere.radius = renderer.localBounds.extents.magnitude;
        }
        sphere.isTrigger = true;

        XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
        if (grab == null)
        {
            grab = hold.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
        }
        if (!grab.colliders.Contains(sphere))
        {
            grab.colliders.Add(sphere);
        }
        return grab;
    }

    private void EnsureHoldsDictionary()
    {
        if (holdsDictionary != null && holdsDictionary.Count > 0)
        {
            return;
        }

        holdsDictionary = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        holdsDictionaryError = string.Empty;
        if (holdsParentGameObject == null)
        {
            return;
        }
        ResolveStudyLayers();
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0].ToUpperInvariant();
            if (!MoonBoardStudyCatalog.TryParseCoordinate(holdName, out _, out _))
            {
                holdsDictionaryError = "Invalid direct child in hold hierarchy: " + child.name + ".";
                continue;
            }
            if (!holdsDictionary.TryAdd(holdName, child.gameObject))
            {
                holdsDictionaryError = "Duplicate hold coordinate in scene: " + holdName + ".";
            }
            if (studyHoldsLayer >= 0)
            {
                SetLayerRecursively(child.gameObject, studyHoldsLayer);
            }
            FitHoldHoverCollider(child.gameObject);
        }
    }

    private void EnsureExaminationHeadlamp()
    {
        if (examinationHeadlamp != null || centerEyeAnchor == null)
        {
            return;
        }

        ResolveStudyLayers();
        Transform existing = centerEyeAnchor.transform.Find("Examination Headlamp");
        GameObject headlampObject;
        if (existing != null)
        {
            headlampObject = existing.gameObject;
            examinationHeadlamp = headlampObject.GetComponent<Light>();
        }
        else
        {
            headlampObject = new GameObject("Examination Headlamp");
            headlampObject.transform.SetParent(centerEyeAnchor.transform, false);
        }

        examinationHeadlamp ??= headlampObject.AddComponent<Light>();
        headlampObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        examinationHeadlamp.type = LightType.Spot;
        examinationHeadlamp.color = Color.white;
        examinationHeadlamp.intensity = 2f;
        examinationHeadlamp.range = 3.5f;
        examinationHeadlamp.spotAngle = 70f;
        examinationHeadlamp.innerSpotAngle = 55f;
        examinationHeadlamp.shadows = LightShadows.None;
        examinationHeadlamp.renderMode = LightRenderMode.ForcePixel;
        examinationHeadlamp.cullingMask =
            (studyHoldsLayer >= 0 ? 1 << studyHoldsLayer : 0) |
            (studyGhostHoldsLayer >= 0 ? 1 << studyGhostHoldsLayer : 0);
        examinationHeadlamp.enabled = false;
    }

    private void ResolveStudyLayers()
    {
        if (studyHoldsLayer < 0)
        {
            studyHoldsLayer = LayerMask.NameToLayer(StudyHoldsLayerName);
        }
        if (studyGhostHoldsLayer < 0)
        {
            studyGhostHoldsLayer = LayerMask.NameToLayer(StudyGhostHoldsLayerName);
        }
        if (studyHoldsLayer < 0 || studyGhostHoldsLayer < 0)
        {
            Debug.LogError("Study hold layers are missing; the examination headlamp cannot be isolated.");
        }
    }

    // The scene's baked hold SphereColliders are authored with a hardcoded local radius that
    // the FBX import scale inflates to ~2 m world (measured live 2026-07-16): every hover
    // volume overlapped most of the board, hover targeting was last-enter-wins from meters
    // away, and the solid spheres intercepted the experimenter panel's pinch raycast. Fit the
    // sphere to the actual mesh bounds instead (the same math GhostHoldController applies to
    // ghost clones) and make it a trigger so HandHoverCollider still fires while
    // Physics.Raycast(..., QueryTriggerInteraction.Ignore) passes straight through.
    private static void FitHoldHoverCollider(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        MeshFilter meshFilter = hold.GetComponent<MeshFilter>();
        if (sphere == null || meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        Bounds meshBounds = meshFilter.sharedMesh.bounds;
        sphere.center = meshBounds.center;
        sphere.radius = meshBounds.extents.magnitude;
        sphere.isTrigger = true;
    }

    private void CacheMoonBoardTransform()
    {
        if (hasInitialMoonBoardTransform || moonBoardEnv == null)
        {
            return;
        }

        initialMoonBoardLocalPosition = moonBoardEnv.transform.localPosition;
        initialMoonBoardLocalRotation = moonBoardEnv.transform.localRotation;
        initialMoonBoardLocalScale = moonBoardEnv.transform.localScale;
        hasInitialMoonBoardTransform = true;
    }

    private void InitializeGripFacades()
    {
        if (leftGripLatch != null)
        {
            return;
        }
        GripEngagementGate.ValidateMinFingers(defaultMinFingers);
        if (gripFlexionReleaseThreshold >= gripFlexionEngageThreshold)
        {
            throw new InvalidOperationException(
                "Grip release flexion threshold must be lower than the engagement threshold.");
        }

        leftGripLatch = new GripLatchStateMachine(
            gripReleaseGraceSeconds,
            gripTrackingFreezeSeconds,
            gripFrozenTimeoutSeconds);
        rightGripLatch = new GripLatchStateMachine(
            gripReleaseGraceSeconds,
            gripTrackingFreezeSeconds,
            gripFrozenTimeoutSeconds);
        leftLocomotionFilter = new GripLocomotionFilter(
            gripOneEuroMinCutoff,
            gripOneEuroBeta,
            gripMaximumAcceleration);
        rightLocomotionFilter = new GripLocomotionFilter(
            gripOneEuroMinCutoff,
            gripOneEuroBeta,
            gripMaximumAcceleration);
    }

    public int GetMinFingersForHold(GameObject hold)
    {
        return ResolveMinFingers(hold);
    }

    private int ResolveMinFingers(GameObject hold)
    {
        if (holdAffordanceCatalog == null || hold == null)
        {
            return defaultMinFingers;
        }

        GameObject sourceHold = IsGhostHold(hold) && ghostHoldController.WallReferent != null
            ? ghostHoldController.WallReferent
            : hold;
        string coordinate = sourceHold.name.Split('.')[0];
        int ghostMarker = coordinate.IndexOf('#');
        if (ghostMarker >= 0)
        {
            coordinate = coordinate.Substring(0, ghostMarker);
        }
        coordinate = coordinate.ToUpperInvariant();
        return routeCatalog != null &&
               routeCatalog.TryGetHold(coordinate, out MoonBoardHoldDefinition definition)
            ? holdAffordanceCatalog.ResolveMinFingers(definition.scanId, defaultMinFingers)
            : defaultMinFingers;
    }

    private static bool IsHandTrackingValid(OVRSkeleton skeleton, ref OVRHand hand)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count < TrackedBoneCount)
        {
            return false;
        }

        hand ??= skeleton.GetComponent<OVRHand>();
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence;
    }

    private bool TryGetDegradedGripGeometry(
        GameObject hold,
        out DegradedGripContactGeometry geometry,
        out string error)
    {
        geometry = null;
        if (hold == null)
        {
            error = "A hovered hold is required.";
            return false;
        }

        if (!hold.TryGetComponent(out MeshFilter meshFilter) ||
            meshFilter.sharedMesh == null)
        {
            error = "No root MeshFilter geometry is available.";
            return false;
        }

        Mesh mesh = meshFilter.sharedMesh;
        if (degradedGripGeometry.TryGetValue(mesh, out geometry) &&
            geometry != null)
        {
            error = string.Empty;
            return true;
        }

        if (!DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out geometry,
                out error))
        {
            return false;
        }

        degradedGripGeometry[mesh] = geometry;
        return true;
    }

    private void PrewarmDegradedGripGeometry(IReadOnlyList<GameObject> holds)
    {
        if (holds == null)
        {
            return;
        }

        foreach (GameObject hold in holds)
        {
            if (!TryGetDegradedGripGeometry(hold, out _, out string error))
            {
                throw new InvalidOperationException(
                    "Cannot prepare degraded grip acquisition for " +
                    (hold != null ? hold.name : "<missing>") + ": " + error);
            }
        }
    }

    private void RecordDegradedGripGeometryFailure(Hand hand, GameObject hold, string reason)
    {
        string holdName = hold != null ? hold.name : "<missing>";
        string details = "hold=" + holdName + "; " + reason;
        DegradedGripAcquisitionFailureReason = details;
        int holdId = hold != null ? hold.GetInstanceID() : 0;
        if (!reportedDegradedGripGeometryFailures.Add(holdId))
        {
            return;
        }

        string side = hand == Hand.Left ? "Left" : "Right";
        Debug.LogError("[SceneConfiguror] DEGRADED CPU grip acquisition rejected: " + details);
        actionRecorder?.Record("GripAcquisitionFallbackRejected", side, hold, details);
    }

    private void ActivateDegradedGripAcquisition()
    {
        if (IsDegradedGripAcquisitionActive)
        {
            return;
        }

        IsDegradedGripAcquisitionActive = true;
        DegradedGripAcquisitionFailureReason = string.Empty;
        reportedDegradedGripGeometryFailures.Clear();
        const string activationDetails =
            "cpu_mesh_vertex_distance; degraded_only; grip_visuals_off";
        Debug.LogWarning(
            "[SceneConfiguror] DEGRADED CPU mesh-vertex grip acquisition ACTIVE; " +
            "GPU grip cues remain off.");
        actionRecorder?.Record(
            "GripAcquisitionFallbackActivated",
            "",
            null,
            activationDetails);

        // Geometry is cached lazily for the hovered hold so degradation cannot create a
        // frame-spike by copying every active route mesh at once.
    }

    private void EnsureGripPipeline()
    {
        if (gripContactPipeline != null || IsGripFeedbackDegraded ||
            distanceToClosestBoneComputeShader == null)
        {
            return;
        }

        gripScoreConfig ??= Resources.Load<GripScoreConfig>("GripScoreConfig");
        if (gripScoreConfig == null)
        {
            gripScoreConfig = ScriptableObject.CreateInstance<GripScoreConfig>();
            Debug.LogWarning("GripScoreConfig asset was not found; using runtime defaults.");
        }
        gripContactPipeline = new GripContactPipeline(
            this,
            distanceToClosestBoneComputeShader,
            gripScoreConfig,
            gripRecoveryAttempted);
        gripContactPipeline.SetFeedbackVisible(studyFeedbackVisible && !IsGripFeedbackDegraded);
        gripContactPipeline.DebugSetReadbackFailures(debugForceGripReadbackFailures);
    }

    private void HandleGripPipelineHealth()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gripContactPipeline.IsRecoveryReady)
        {
            Debug.LogWarning("[SceneConfiguror] Grip feedback recovery attempt after sustained readback failure.");
            actionRecorder?.Record(
                "GripFeedbackRecoveryAttempt",
                "",
                null,
                "recreating GPU readback pipeline");
            gripContactPipeline.ClearFeedback();
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
            gripRecoveryAttempted = true;
            EnsureGripPipeline();
            PrepareCurrentGripTargets();
        }
        else if (gripContactPipeline.IsDegradationReady)
        {
            IsGripFeedbackDegraded = true;
            GripFeedbackDegradedUtc = DateTime.UtcNow.ToString("o");
            Debug.LogError("[SceneConfiguror] Grip feedback entered DEGRADED; block continues.");
            SetStudyFeedbackVisible(false);
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
            ActivateDegradedGripAcquisition();
        }
    }

    private void PrepareCurrentGripTargets()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline.Prepare(activeHoldsList);
        }
        else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                 ghostHoldController.CurrentGhost != null)
        {
            gripContactPipeline.Prepare(ghostHoldController.CurrentGhost);
        }
    }

    private void EnsureRuntimeControllers()
    {
        ghostHoldController = ghostHoldController != null
            ? ghostHoldController
            : FindAnyObjectByType<GhostHoldController>();
        if (ghostHoldController == null)
        {
            GameObject controllerObject = new("GhostHoldController");
            ghostHoldController = controllerObject.AddComponent<GhostHoldController>();
        }
        if (ghostHoldController.GetComponent<StudyManager>() == null)
        {
            ghostHoldController.gameObject.AddComponent<StudyManager>();
        }
    }

    private void OnDestroy()
    {
        gripContactPipeline?.Dispose();
        if (highlightCircleMaterial != null)
        {
            Destroy(highlightCircleMaterial);
        }
    }

}
