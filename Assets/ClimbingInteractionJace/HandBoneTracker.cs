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
        if (sceneConfiguror == null || meshRenderer == null || !feedbackVisible || trackedHand == null ||
            !trackedHand.IsTracked || !trackedHand.IsDataHighConfidence)
        {
            if (meshRenderer != null)
            {
                meshRenderer.enabled = false;
            }
            return;
        }
        meshRenderer.enabled = true;

        if (hand == Hand.Left)
        {
            if (sceneConfiguror.leftHandBonePositions == null || sceneConfiguror.leftHandBonePositions.Count <= trackedBoneIndex)
            {
                return;
            }
        }
        else if (hand == Hand.Right)
        {
            if (sceneConfiguror.rightHandBonePositions == null || sceneConfiguror.rightHandBonePositions.Count <= trackedBoneIndex)
            {
                return;
            }
        }


        if (hand == Hand.Left)
        {
            transform.position = sceneConfiguror.leftHandBonePositions[trackedBoneIndex] + transformOffsetFromTrackedBone;
        }
        else if (hand == Hand.Right)
        {
            transform.position = sceneConfiguror.rightHandBonePositions[trackedBoneIndex] + transformOffsetFromTrackedBone;
        }


        if (handBoneTrackerType == HandBoneTrackerType.ContactRangeVisual)
        {
            meshRenderer.enabled = true;
            transform.localScale = new Vector3(
                sceneConfiguror.gripFingertipRange,
                sceneConfiguror.gripFingertipRange,
                sceneConfiguror.gripFingertipRange);
            float handBoneDistanceToHold = hand == Hand.Left ?
                sceneConfiguror.leftHandBoneToHoldMinDistances[trackedBoneIndex] : sceneConfiguror.rightHandBoneToHoldMinDistances[trackedBoneIndex];
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
            material.color = GetScoreColor();
        }
        else if (handBoneTrackerType == HandBoneTrackerType.HandGripStatus)
        {
            material.color = GetScoreColor();
        }
    }

    private Color GetScoreColor()
    {
        float score = hand == Hand.Left
            ? sceneConfiguror.leftHandGripScore
            : sceneConfiguror.rightHandGripScore;
        return sceneConfiguror.gripScoreConfig != null
            ? sceneConfiguror.gripScoreConfig.EvaluateScoreColor(score)
            : Color.Lerp(Color.red, Color.green, score);
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
