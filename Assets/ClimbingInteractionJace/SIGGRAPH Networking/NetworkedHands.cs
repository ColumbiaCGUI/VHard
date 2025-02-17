using System.Collections.Generic;
using Fusion;
using UnityEditor.SearchService;
using UnityEngine;

public class NetworkedHands : NetworkBehaviour
{
    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(26)]
    public NetworkLinkedList<Vector3> leftHandJointPositionsNetworked { get; }

    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(26)]
    public NetworkLinkedList<Vector3> rightHandJointPositionsNetworked { get; }

    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(26)]
    public NetworkLinkedList<Quaternion> leftHandJointQuaternionNetworked { get; }

    [UnitySerializeField] // Show this private property in the inspector.
    [Networked]
    [Capacity(26)]
    public NetworkLinkedList<Quaternion> rightHandJointQuaternionNetworked { get; }

    [Header("Local References")]
    public SceneConfiguror sceneConfiguror;
    public List<Vector3> leftHandJointPositionsSelf;
    public List<Vector3> rightHandJointPositionsSelf;
    public List<Quaternion> leftHandJointQuaternionSelf;
    public List<Quaternion> rightHandJointQuaternionSelf;

    public override void Spawned()
    {
        Debug.Log("NetworkedHands: Spawned called.");
        sceneConfiguror = FindAnyObjectByType<SceneConfiguror>();

        // Only the remote player should render networkedHands.
        if (!HasStateAuthority)
        {
        sceneConfiguror.networkedHands = this;
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (sceneConfiguror != null)
        {
            if (sceneConfiguror.networkedHands == this)
            {
                sceneConfiguror.networkedHands = null;
            }
        }
    }

    public override void FixedUpdateNetwork()
    {
        // Only the local player should update the networked data.
        if (HasStateAuthority)
        {
            leftHandJointPositionsNetworked.Clear();
            rightHandJointPositionsNetworked.Clear();
            leftHandJointQuaternionNetworked.Clear();
            rightHandJointQuaternionNetworked.Clear();

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
            leftHandJointQuaternionSelf = sceneConfiguror.leftHandBoneQuaternions;
            rightHandJointQuaternionSelf = sceneConfiguror.rightHandBoneQuaternions;
            foreach (Vector3 joint in leftHandJointPositionsSelf)
            {
                leftHandJointPositionsNetworked.Add(joint);
            }
            foreach (Vector3 joint in rightHandJointPositionsSelf)
            {
                rightHandJointPositionsNetworked.Add(joint);
            }
            foreach (Quaternion joint in leftHandJointQuaternionSelf)
            {
                leftHandJointQuaternionNetworked.Add(joint);
            }
            foreach (Quaternion joint in rightHandJointQuaternionSelf)
            {
                rightHandJointQuaternionNetworked.Add(joint);
            }
        }
    }
}
