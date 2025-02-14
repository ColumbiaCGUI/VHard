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

    [Header("Scene References")]
    public GameObject environment;
    public SceneConfiguror sceneConfiguror;

    [Header("Our Hands State (Relative to Environment)")]
    public List<Vector3> leftHandBonePositionsSelf;
    public List<Vector3> rightHandBonePositionsSelf;

    [Header("Other Player's Hands State (Relative to Environment)")]
    public bool drawOtherPlayerHands = false;
    public List<Vector3> leftHandBonePositionsOther;
    public List<Vector3> rightHandBonePositionsOther;

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
        // Don't send any data if we're not using hands
        if (OVRInput.GetActiveController() != OVRInput.Controller.Hands)
        {
            Debug.Log("UDPNetworkManager: Not sending data because we're not using hands!");
            return;
        }
        if (!OVRInput.IsControllerConnected(OVRInput.Controller.LHand) || !OVRInput.IsControllerConnected(OVRInput.Controller.RHand))
        {
            Debug.Log("UDPNetworkManager: Not sending data because one or both hands are not connected!");
            return;
        }
        leftHandBonePositionsSelf = sceneConfiguror.leftHandBonePositions;
        rightHandBonePositionsSelf = sceneConfiguror.rightHandBonePositions;
        if (leftHandBonePositionsSelf.Count == 0 || rightHandBonePositionsSelf.Count == 0)
        {
            Debug.Log("UDPNetworkManager: Not sending data because we don't have hand bone positions!");
            return;
        }
        SendData(leftHandBonePositionsSelf, rightHandBonePositionsSelf);
    }

    void OnApplicationQuit()
    {
        udpClient?.Close();
    }

    void SendData(List<Vector3> leftHandBonePositions, List<Vector3> rightHandBonePositions)
    {
        // Send the data relative to the environment, so that the other player can place our hands in the correct position
        Vector3 environmentPosition = environment.transform.position;
        for (int i = 0; i < leftHandBonePositions.Count; i++)
        {
            leftHandBonePositions[i] -= environmentPosition;
        }
        for (int i = 0; i < rightHandBonePositions.Count; i++)
        {
            rightHandBonePositions[i] -= environmentPosition;
        }

        float[] floatsToSend = new float[leftHandBonePositions.Count * 3 * 2];
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

        int numBytesToSend = 1 + floatsToSend.Length * 4; // 1 byte for the first byte, 4 bytes per float
        byte[] bytesToSend = new byte[numBytesToSend];
        bytesToSend[0] = 1; // Set the first byte to 1 to indicate that we have valid data to send
        Buffer.BlockCopy(floatsToSend, 0, bytesToSend, 1, floatsToSend.Length * 4);

        IPEndPoint broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, udpPort);
        udpClient.Send(bytesToSend, numBytesToSend, broadcastEndPoint);
        Debug.Log($"UDPNetworkManager: Sent {numBytesToSend} bytes of data to {broadcastEndPoint}: {BitConverter.ToString(bytesToSend)}");
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
        int totalFloatCountExpected = numBonesPerHand * 3 * 2; // 3 floats per bone, 2 hands
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

        // Add the environment position back to the hands
        Vector3 environmentPosition = environment.transform.position;
        for (int i = 0; i < leftHandBonePositionsOther.Count; i++)
        {
            leftHandBonePositionsOther[i] += environmentPosition;
        }
        for (int i = 0; i < rightHandBonePositionsOther.Count; i++)
        {
            rightHandBonePositionsOther[i] += environmentPosition;
        }

        Debug.Log("UDPNetworkManager: Received data: " +
            $"Left Hand: {string.Join(", ", leftHandBonePositionsOther)}" +
            $"Right Hand: {string.Join(", ", rightHandBonePositionsOther)}");
    }
}
