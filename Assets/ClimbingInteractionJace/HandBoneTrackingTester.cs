using UnityEngine;

public class HandBoneTrackingTester : MonoBehaviour
{
    public SceneConfiguror sceneConfiguror;
    public int handIndex;
    public int boneIndex;
    public float transformHeightOffset;
    public bool isGripGrangeVisual;

    void Start()
    {
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();
    }

    void Update()
    {
        if (handIndex == 0)
        {
            if (sceneConfiguror.leftHandBonePositions != null && sceneConfiguror.leftHandBonePositions.Count > 0)
            {
                transform.position = sceneConfiguror.leftHandBonePositions[boneIndex] + new Vector3(0, transformHeightOffset, 0);
            }
        }
        else
        {
            if (sceneConfiguror.rightHandBonePositions != null && sceneConfiguror.rightHandBonePositions.Count > 0)
            {
                transform.position = sceneConfiguror.rightHandBonePositions[boneIndex] + new Vector3(0, transformHeightOffset, 0);
            }
        }

        if (isGripGrangeVisual)
        {
            transform.localScale = new Vector3(sceneConfiguror.gripFingertipRange, sceneConfiguror.gripFingertipRange, sceneConfiguror.gripFingertipRange);
        }
    }
}
