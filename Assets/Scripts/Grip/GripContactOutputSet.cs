using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using static GripContactConstants;

internal sealed class GripContactOutputSet : IDisposable
{
    private static readonly GripContactAccumulator[] EmptyStats = new GripContactAccumulator[TipCount];
    private static readonly uint[] InfinityDistances = CreateInfinityDistances();
    private readonly GripContactReadbackProcessor owner;
    public readonly GripHoldContactState state;
    private readonly Action<AsyncGPUReadbackRequest> statsCallback;
    private readonly Action<AsyncGPUReadbackRequest> bonesCallback;
    private int pendingRequests;
    private bool hasRequests;
    private bool epochPending;
    private bool canceled;
    private bool resourcesDisposed;
    private readonly GripReadbackEpochState epochState = new();
    public readonly ComputeBuffer vertexContactData;
    public readonly ComputeBuffer tipContactStats;
    public readonly ComputeBuffer boneDistances;
    public readonly GripContactAccumulator[] accumulators = new GripContactAccumulator[TipCount];
    public readonly uint[] boneDistanceValues = new uint[BoneCount * 2];
    public readonly float[] leftFingerCurls = new float[FingerCurlEstimator.FingerCount];
    public readonly float[] rightFingerCurls = new float[FingerCurlEstimator.FingerCount];
    public int handMask;
    public long epoch;
    public float sampledAt;
    public bool forceFailure;
    public int leftTargetGeneration;
    public int rightTargetGeneration;
    public AsyncGPUReadbackRequest statsRequest;
    public AsyncGPUReadbackRequest bonesRequest;

    public bool IsPending => epochPending;
    public bool IsReadbackComplete => epochPending && (canceled || pendingRequests == 0);
    public bool Succeeded => epochState.Succeeded;
    public bool IsCanceled => canceled;

    public GripContactOutputSet(GripContactReadbackProcessor owner, GripHoldContactState state)
    {
        this.owner = owner;
        this.state = state;
        statsCallback = request => owner.OnStatsReadback(this, request);
        bonesCallback = request => owner.OnBoneReadback(this, request);
        vertexContactData = new ComputeBuffer(state.vertexCount, sizeof(float) * 4);
        tipContactStats = new ComputeBuffer(TipCount, sizeof(int) * 4);
        boneDistances = new ComputeBuffer(BoneCount * 2, sizeof(uint));
    }

    public void Reset(
        int requestedHandMask,
        long epochId,
        float requestedSampledAt,
        bool injectFailure,
        int requestedLeftTargetGeneration,
        int requestedRightTargetGeneration,
        IReadOnlyList<float> requestedLeftFingerCurls,
        IReadOnlyList<float> requestedRightFingerCurls)
    {
        handMask = requestedHandMask;
        epoch = epochId;
        sampledAt = requestedSampledAt;
        forceFailure = injectFailure;
        leftTargetGeneration = requestedLeftTargetGeneration;
        rightTargetGeneration = requestedRightTargetGeneration;
        if ((requestedHandMask & LeftHandMask) != 0)
        {
            for (int index = 0; index < leftFingerCurls.Length; index++)
            {
                leftFingerCurls[index] = requestedLeftFingerCurls[index];
            }
        }
        if ((requestedHandMask & RightHandMask) != 0)
        {
            for (int index = 0; index < rightFingerCurls.Length; index++)
            {
                rightFingerCurls[index] = requestedRightFingerCurls[index];
            }
        }
        tipContactStats.SetData(EmptyStats);
        boneDistances.SetData(InfinityDistances);
        Array.Copy(InfinityDistances, boneDistanceValues, InfinityDistances.Length);
        pendingRequests = 2;
        hasRequests = false;
        epochPending = true;
        epochState.Reset();
        canceled = false;
    }

    public void RequestReadback()
    {
        hasRequests = true;
        statsRequest = AsyncGPUReadback.Request(tipContactStats, statsCallback);
        bonesRequest = AsyncGPUReadback.Request(boneDistances, bonesCallback);
    }

    public void RecordStatsResult(bool succeeded)
    {
        epochState.RecordStatistics(succeeded);
        pendingRequests = Mathf.Max(0, pendingRequests - 1);
    }

    public void RecordBonesResult(bool succeeded)
    {
        epochState.RecordBones(succeeded);
        pendingRequests = Mathf.Max(0, pendingRequests - 1);
    }

    public void MarkProcessed()
    {
        epochPending = false;
    }

    public void Dispose()
    {
        if (resourcesDisposed)
        {
            return;
        }
        if (hasRequests && !statsRequest.done)
        {
            statsRequest.WaitForCompletion();
        }
        if (hasRequests && !bonesRequest.done)
        {
            bonesRequest.WaitForCompletion();
        }
        canceled = true;
        pendingRequests = 0;
        vertexContactData.Release();
        tipContactStats.Release();
        boneDistances.Release();
        resourcesDisposed = true;
    }

    private static uint[] CreateInfinityDistances()
    {
        uint infinity = GripUIntFloat.ToUInt(float.PositiveInfinity);
        uint[] values = new uint[BoneCount * 2];
        Array.Fill(values, infinity);
        return values;
    }
}
