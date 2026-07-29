using System;
using UnityEngine;

public sealed class CaptureFrame
{
    public const int BoneCount = 26;

    public long utcTicks;
    public float sessionTime;
    public int frame;
    public float blockTime;
    public string mode = string.Empty;
    public string route = string.Empty;
    public string hold = string.Empty;
    public Vector3 headPosition;
    public Quaternion headRotation = Quaternion.identity;
    public readonly Vector3[] leftPositions = new Vector3[BoneCount];
    public readonly Quaternion[] leftRotations = CreateIdentityRotations();
    public int leftConfidence;
    public readonly Vector3[] rightPositions = new Vector3[BoneCount];
    public readonly Quaternion[] rightRotations = CreateIdentityRotations();
    public int rightConfidence;
    public string touchedHold = string.Empty;
    public int gripFlag;
    public int perFingerContactMask = -1;
    public float gripScore = -1f;

    public void CopyFrom(CaptureFrame source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        utcTicks = source.utcTicks;
        sessionTime = source.sessionTime;
        frame = source.frame;
        blockTime = source.blockTime;
        mode = source.mode;
        route = source.route;
        hold = source.hold;
        headPosition = source.headPosition;
        headRotation = source.headRotation;
        Array.Copy(source.leftPositions, leftPositions, BoneCount);
        Array.Copy(source.leftRotations, leftRotations, BoneCount);
        leftConfidence = source.leftConfidence;
        Array.Copy(source.rightPositions, rightPositions, BoneCount);
        Array.Copy(source.rightRotations, rightRotations, BoneCount);
        rightConfidence = source.rightConfidence;
        touchedHold = source.touchedHold;
        gripFlag = source.gripFlag;
        perFingerContactMask = source.perFingerContactMask;
        gripScore = source.gripScore;
    }

    private static Quaternion[] CreateIdentityRotations()
    {
        Quaternion[] rotations = new Quaternion[BoneCount];
        for (int i = 0; i < rotations.Length; i++)
        {
            rotations[i] = Quaternion.identity;
        }
        return rotations;
    }
}
