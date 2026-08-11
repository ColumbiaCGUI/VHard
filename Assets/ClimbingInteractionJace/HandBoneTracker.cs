using UnityEngine;


public enum Hand
{
    Left = 0,
    Right = 1
}

public enum HandBoneTrackerType
{
    ContactRangeVisual,
    HandGripStatus,
}

public class HandBoneTracker : MonoBehaviour
{
    public SceneConfiguror sceneConfiguror;
    public HandBoneTrackerType handBoneTrackerType;
    public Hand hand;
    public int trackedBoneIndex;
    public Vector3 transformOffsetFromTrackedBone;
    private MeshRenderer meshRenderer;
    private Material material;
    private OVRHand trackedHand;
    private bool feedbackVisible = true;

    void Start()
    {
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();
        meshRenderer = GetComponent<MeshRenderer>();
        material = meshRenderer != null ? meshRenderer.material : null;
        trackedHand = hand == Hand.Left
            ? sceneConfiguror?.leftHandOVRSkeleton?.GetComponent<OVRHand>()
            : sceneConfiguror?.rightHandOVRSkeleton?.GetComponent<OVRHand>();
    }

    void Update()
    {
        if (sceneConfiguror == null || meshRenderer == null || material == null || !feedbackVisible || trackedHand == null ||
            !trackedHand.IsTracked || !trackedHand.IsDataHighConfidence)
        {
            if (meshRenderer != null)
            {
                meshRenderer.enabled = false;
            }
            return;
        }
        meshRenderer.enabled = true;

        var bonePositions = hand == Hand.Left
            ? sceneConfiguror.leftHandBonePositions
            : sceneConfiguror.rightHandBonePositions;
        if (trackedBoneIndex < 0 || bonePositions == null || bonePositions.Count <= trackedBoneIndex)
        {
            meshRenderer.enabled = false;
            return;
        }

        transform.position = bonePositions[trackedBoneIndex] + transformOffsetFromTrackedBone;

        if (handBoneTrackerType == HandBoneTrackerType.ContactRangeVisual)
        {
            float[] distances = hand == Hand.Left
                ? sceneConfiguror.leftHandBoneToHoldMinDistances
                : sceneConfiguror.rightHandBoneToHoldMinDistances;
            if (distances == null || distances.Length <= trackedBoneIndex)
            {
                meshRenderer.enabled = false;
                return;
            }
            transform.localScale = new Vector3(
                sceneConfiguror.gripFingertipRange,
                sceneConfiguror.gripFingertipRange,
                sceneConfiguror.gripFingertipRange);
            float handBoneDistanceToHold = distances[trackedBoneIndex];
            bool handBoneIsContactingHold = handBoneDistanceToHold <= sceneConfiguror.gripFingertipRange;
            if (handBoneIsContactingHold)
            {
                // Change material metallic value to 0.1f
                material.SetFloat("_Metallic", 0.25f);
            }
            else
            {
                // Change material metallic value to 0f
                material.SetFloat("_Metallic", 0f);
            }
            material.color = GetLatchColor();
        }
        else if (handBoneTrackerType == HandBoneTrackerType.HandGripStatus)
        {
            material.color = GetLatchColor();
        }
    }

    private Color GetLatchColor()
    {
        bool latched = hand == Hand.Left
            ? sceneConfiguror.leftHandIsGripping
            : sceneConfiguror.rightHandIsGripping;
        return latched ? Color.green : Color.red;
    }

    public void SetFeedbackVisible(bool visible)
    {
        feedbackVisible = visible;
        if (meshRenderer != null)
        {
            meshRenderer.enabled = visible && trackedHand != null &&
                                   trackedHand.IsTracked && trackedHand.IsDataHighConfidence;
        }
    }
}
