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

    void Start()
    {
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();
    }

    void Update()
    {
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
            bool handIsGripping = hand == Hand.Left ? sceneConfiguror.leftHandIsGripping : sceneConfiguror.rightHandIsGripping;
            if (handIsGripping)
            {
                // Hide
                GetComponent<MeshRenderer>().enabled = false;
                return;
            }
            else
            {
                // Show
                GetComponent<MeshRenderer>().enabled = true;
            }
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
                GetComponent<MeshRenderer>().material.SetFloat("_Metallic", 0.25f);
            }
            else
            {
                // Change material metallic value to 0f
                GetComponent<MeshRenderer>().material.SetFloat("_Metallic", 0f);
            }
        }
        else if (handBoneTrackerType == HandBoneTrackerType.HandGripStatus)
        {
            bool handIsGripping = hand == Hand.Left ? sceneConfiguror.leftHandIsGripping : sceneConfiguror.rightHandIsGripping;
            if (handIsGripping)
            {
                GetComponent<MeshRenderer>().material.color = Color.green;
            }
            else
            {
                GetComponent<MeshRenderer>().material.color = Color.red;
            }
        }
    }
}
