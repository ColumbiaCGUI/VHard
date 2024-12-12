using System.Collections.Generic;
using UnityEngine;

public class OVRHandTrackingTest : MonoBehaviour
{
    [Header("Left = 0, Right = 1")]
    public OVRSkeleton[] handOVRSkeletons = new OVRSkeleton[2];
    public List<Vector3>[] handBonePositions = new List<Vector3>[2];
    public List<Vector3> leftHandBonePositions = new List<Vector3>();
    public List<Vector3> rightHandBonePositions = new List<Vector3>();

    void Update()
    {
        foreach (OVRSkeleton handOVRSkeleton in handOVRSkeletons)
        {
            int handIndex = (int)handOVRSkeleton.GetSkeletonType();
            handBonePositions[handIndex] = new List<Vector3>(); // Left = 0, Right = 1
            foreach (OVRBone bone in handOVRSkeleton.Bones)
            {
                handBonePositions[handIndex].Add(bone.Transform.position);
            }
        }

        leftHandBonePositions = handBonePositions[0];
        rightHandBonePositions = handBonePositions[1];
    }
}
