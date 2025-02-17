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

    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(30)]
    public NetworkLinkedList<Vector3> rightHandJointPositionsNetworked { get; }

    [Header("Local References")]
    public SceneConfiguror sceneConfiguror;
    public List<Vector3> leftHandJointPositionsSelf;
    public List<Vector3> rightHandJointPositionsSelf;

    public override void Spawned()
    {
        Debug.Log("NetworkedHands: Spawned called.");
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();
        sceneConfiguror.networkedHands = this;
    }

    public override void FixedUpdateNetwork()
    {
        // Only the local player should update the networked data.
        if (HasStateAuthority)
        {
            leftHandJointPositionsNetworked.Clear();
            rightHandJointPositionsNetworked.Clear();

            // Don't send any data if we're not using hands
            if (OVRInput.GetActiveController() != OVRInput.Controller.Hands)
            {
                return;
            }
            if (!OVRInput.IsControllerConnected(OVRInput.Controller.LHand) || !OVRInput.IsControllerConnected(OVRInput.Controller.RHand))
            {
                return;
            }

            leftHandJointPositionsSelf = sceneConfiguror.leftHandBonePositions;
            rightHandJointPositionsSelf = sceneConfiguror.rightHandBonePositions;
            foreach (Vector3 joint in leftHandJointPositionsSelf)
            {
                leftHandJointPositionsNetworked.Add(joint);
            }
            foreach (Vector3 joint in rightHandJointPositionsSelf)
            {
                rightHandJointPositionsNetworked.Add(joint);
            }
        }
    }
}
