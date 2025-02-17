using System.Collections.Generic;
using Fusion;
using UnityEditor.SearchService;
using UnityEngine;

public class NetworkedHands : NetworkBehaviour
{
    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(30)]
    // [OnChangedRender(nameof(OnLeftHandJointsChanged))]
    public NetworkLinkedList<Vector3> leftHandJointPositionsNetworked { get; }
    public NetworkLinkedList<Vector3> rightHandJointPositionsNetworked { get; }

    [Header("Local References")]
    public SceneConfiguror sceneConfiguror;
    public List<Vector3> leftHandJointPositionsSelf;
    public List<Vector3> rightHandJointPositionsSelf;
    public GameObject leftHandOther;
    public GameObject rightHandOther;

    public override void Spawned()
    {
        Debug.Log("NetworkedHands: Spawned called.");
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();
    }

    public override void FixedUpdateNetwork()
    {
        // Only the local player should update the networked data.
        if (HasStateAuthority)
        {
            leftHandJointPositionsSelf = sceneConfiguror.leftHandBonePositions;
            rightHandJointPositionsSelf = sceneConfiguror.rightHandBonePositions;

            leftHandJointPositionsNetworked.Clear();
            foreach (Vector3 joint in leftHandJointPositionsSelf)
            {
                leftHandJointPositionsNetworked.Add(joint);
            }
            rightHandJointPositionsNetworked.Clear();
            foreach (Vector3 joint in rightHandJointPositionsSelf)
            {
                rightHandJointPositionsNetworked.Add(joint);
            }
        }
    }
}
