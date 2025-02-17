using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public class UDPNetworkManager : MonoBehaviour
{
    [Header("UDP Networking Settings")]
    public UdpClient udpClient;
    public int udpPort = 12345;

    [Header("UDP Networking State")]
    public bool isBroadcasting = false;

    [Header("Scene References")]
    public GameObject environment;
    public SceneConfiguror sceneConfiguror;

    [Header("Our Hands State (Relative to Environment)")]
    public Vector3 centerEyePosition;
    public List<Vector3> leftHandBonePositionsSelf;
    public List<Vector3> rightHandBonePositionsSelf;

    [Header("Other Player's Hands State (Relative to Environment)")]
    public Vector3 centerEyePositionOther;
    public List<Vector3> leftHandBonePositionsOther;
    public List<Vector3> rightHandBonePositionsOther;

    [Header("Other Player's Visuals")]
    public bool drawOtherPlayerHead = false;
    public GameObject headOther;
    public bool drawOtherPlayerHands = false;
    public GameObject leftHandRootOther;
    public GameObject rightHandRootOther;
    public List<GameObject> leftHandBonesOther;
    public List<GameObject> rightHandBonesOther;

    void Start()
    {
        udpClient = new UdpClient();
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort)); // Now bind manually.

        // ReceiveData() is an async function that repeats itself when data is received, we don't need to call ReceiveData ourselves!
        udpClient.BeginReceive(ReceiveData, null);
    }

    void Update()
    {
        centerEyePosition = sceneConfiguror.centerEyePosition;

        // Don't send any data if we're not using hands
        if (OVRInput.GetActiveController() != OVRInput.Controller.Hands)
        {
            // Debug.Log("UDPNetworkManager: Not sending data because we're not using hands!");
            isBroadcasting = false;
            return;
        }
        if (!OVRInput.IsControllerConnected(OVRInput.Controller.LHand) || !OVRInput.IsControllerConnected(OVRInput.Controller.RHand))
        {
            // Debug.Log("UDPNetworkManager: Not sending data because one or both hands are not connected!");
            isBroadcasting = false;
            return;
        }
        leftHandBonePositionsSelf = new List<Vector3>(sceneConfiguror.leftHandBonePositions);
        rightHandBonePositionsSelf = new List<Vector3>(sceneConfiguror.rightHandBonePositions);
        if (leftHandBonePositionsSelf.Count == 0 || rightHandBonePositionsSelf.Count == 0)
        {
            // Debug.Log("UDPNetworkManager: Not sending data because we don't have hand bone positions!");
            isBroadcasting = false;
            return;
        }
        isBroadcasting = true;
        SendData(centerEyePosition, new List<Vector3>(leftHandBonePositionsSelf), new List<Vector3>(rightHandBonePositionsSelf));

        // DEV: Offset own hands slightly for now to work on shader
        // leftHandBonePositionsOther = new List<Vector3>(leftHandBonePositionsSelf);
        // for (int i = 0; i < leftHandBonePositionsOther.Count; i++)
        // {
        //     leftHandBonePositionsOther[i] += new Vector3(0, 0.1f, 0);
        // }
        // rightHandBonePositionsOther = new List<Vector3>(rightHandBonePositionsSelf);
        // for (int i = 0; i < rightHandBonePositionsOther.Count; i++)
        // {
        //     rightHandBonePositionsOther[i] += new Vector3(0, 0.1f, 0);
        // }
    }

    void OnApplicationQuit()
    {
        udpClient?.Close();
    }

    void SendData(Vector3 centerEyePosition, List<Vector3> leftHandBonePositions, List<Vector3> rightHandBonePositions)
    {
        // Send the data relative to the environment, so that the other player can place our hands in the correct position
        Vector3 environmentPosition = environment.transform.position;
        centerEyePosition -= environmentPosition;
        for (int i = 0; i < leftHandBonePositions.Count; i++)
        {
            leftHandBonePositions[i] -= environmentPosition;
        }
        for (int i = 0; i < rightHandBonePositions.Count; i++)
        {
            rightHandBonePositions[i] -= environmentPosition;
        }

        float[] floatsToSend = new float[(leftHandBonePositions.Count * 3 * 2) + 3];
        for (int i = 0; i < leftHandBonePositions.Count; i++)
        {
            floatsToSend[i * 3] = leftHandBonePositions[i].x;
            floatsToSend[i * 3 + 1] = leftHandBonePositions[i].y;
            floatsToSend[i * 3 + 2] = leftHandBonePositions[i].z;
        }
        for (int i = 0; i < rightHandBonePositions.Count; i++)
        {
            floatsToSend[leftHandBonePositions.Count * 3 + i * 3] = rightHandBonePositions[i].x;
            floatsToSend[leftHandBonePositions.Count * 3 + i * 3 + 1] = rightHandBonePositions[i].y;
            floatsToSend[leftHandBonePositions.Count * 3 + i * 3 + 2] = rightHandBonePositions[i].z;
        }
        floatsToSend[floatsToSend.Length - 3] = centerEyePosition.x;
        floatsToSend[floatsToSend.Length - 2] = centerEyePosition.y;
        floatsToSend[floatsToSend.Length - 1] = centerEyePosition.z;

        int numBytesToSend = 1 + floatsToSend.Length * 4; // 1 byte for the first byte, 4 bytes per float
        byte[] bytesToSend = new byte[numBytesToSend];
        bytesToSend[0] = 1; // Set the first byte to 1 to indicate that we have valid data to send
        Buffer.BlockCopy(floatsToSend, 0, bytesToSend, 1, floatsToSend.Length * 4);

        IPEndPoint broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, udpPort);
        udpClient.Send(bytesToSend, numBytesToSend, broadcastEndPoint);
        // Debug.Log($"UDPNetworkManager: Sent {numBytesToSend} bytes of data to {broadcastEndPoint}: {BitConverter.ToString(bytesToSend)}");
    }

    void ReceiveData(IAsyncResult result)
    {
        IPEndPoint ip = new IPEndPoint(IPAddress.Any, udpPort);
        byte[] data = udpClient.EndReceive(result, ref ip);

        udpClient.BeginReceive(ReceiveData, null);

        DecodeData(data);
    }
    void DecodeData(byte[] data)
    {
        // Decode the first byte, and if it's 0, we ignore the data (this could happen, for example, when the other player is not in the scene yet)
        // When sending data, we make sure to only set the first byte to 1 if we have valid data to send
        byte firstByte = data[0];
        if (firstByte == 0)
        {
            drawOtherPlayerHands = false;
            return;
        }
        drawOtherPlayerHands = true;
        // Remove the first byte, since we've already decoded it
        data = new byte[data.Length - 1];

        int numBonesPerHand = sceneConfiguror.numBonesPerHand;
        int totalFloatCountExpected = (numBonesPerHand * 3 * 2) + 3; // 3 floats per bone, 2 hands
        // Check the length of the data to make sure it's what we expect
        if (data.Length != totalFloatCountExpected * 4) // 4 bytes per float
        {
            Debug.LogError($"UDPNetworkmManager: Data length {data.Length} bytes not equal to expected data length {totalFloatCountExpected} bytes!");
            return;
        }

        leftHandBonePositionsOther = new List<Vector3>();
        rightHandBonePositionsOther = new List<Vector3>();

        for (int i = 0; i < numBonesPerHand * 3; i += 3) // 3 floats per bone
        {
            Vector3 leftHandBonePosition = new Vector3(
                BitConverter.ToSingle(data, i * 4),
                BitConverter.ToSingle(data, (i + 1) * 4),
                BitConverter.ToSingle(data, (i + 2) * 4));
            leftHandBonePositionsOther.Add(leftHandBonePosition);
        }
        for (int i = numBonesPerHand * 3; i < numBonesPerHand * 3 * 2; i += 3) // 3 floats per bone
        {
            Vector3 rightHandBonePosition = new Vector3(
                BitConverter.ToSingle(data, i * 4),
                BitConverter.ToSingle(data, (i + 1) * 4),
                BitConverter.ToSingle(data, (i + 2) * 4));
            rightHandBonePositionsOther.Add(rightHandBonePosition);
        }

        centerEyePositionOther = new Vector3(
            BitConverter.ToSingle(data, numBonesPerHand * 3 * 4),
            BitConverter.ToSingle(data, (numBonesPerHand * 3 + 1) * 4),
            BitConverter.ToSingle(data, (numBonesPerHand * 3 + 2) * 4));

        // Add the environment position back
        Vector3 environmentPosition = environment.transform.position;
        centerEyePositionOther += environmentPosition;
        for (int i = 0; i < leftHandBonePositionsOther.Count; i++)
        {
            leftHandBonePositionsOther[i] += environmentPosition;
        }
        for (int i = 0; i < rightHandBonePositionsOther.Count; i++)
        {
            rightHandBonePositionsOther[i] += environmentPosition;
        }

        Debug.Log("UDPNetworkManager: Received data: " +
            $"Left Hand: {leftHandBonePositionsOther[0]}" +
            $"Right Hand: {rightHandBonePositionsOther[0]}" +
            $"Center Eye: {centerEyePositionOther}");
            
        // Puppet the other player's hands
        Debug.Log("UDPNetworkManager: Data received and decoded.");
        Debug.Log($"{leftHandBonePositionsOther.Count} left bone positions, {leftHandBonesOther.Count} left bones");
        Debug.Log($"{rightHandBonePositionsOther.Count} right bone positions, {rightHandBonesOther.Count} right bones");
        if (drawOtherPlayerHands && leftHandBonePositionsOther.Count >= leftHandBonesOther.Count && rightHandBonePositionsOther.Count >= rightHandBonesOther.Count)
        {
            for (int i = 0; i < leftHandBonePositionsOther.Count - 1; i++)
            {
                leftHandBonesOther[i].transform.position = leftHandBonePositionsOther[i];
            }
            for (int i = 0; i < rightHandBonePositionsOther.Count - 1; i++)
            {
                // Debug.Log($"Accessing index {i} with {rightHandBonePositionsOther.Count} positions and {rightHandBonesOther.Count} bones.");
                // Debug.Log(rightHandBonesOther[i].name);
                // Debug.Log(rightHandBonePositionsOther[i]);
                rightHandBonesOther[i].transform.position = rightHandBonePositionsOther[i];
            }
        }
    }
}
