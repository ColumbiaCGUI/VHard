using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using static GripContactConstants;

internal sealed class GripContactReadbackProcessor
{
    private readonly GripHandFeedbackState feedback;
    private readonly GripReadbackHealth readbackHealth;
    private readonly Queue<GripContactOutputSet> pendingEpochs = new();
    private bool disposed;
    private bool dispatchPaused;
    private GripReadbackAction pendingAction;
    private int failedEpochCount;

    public GripContactReadbackProcessor(GripHandFeedbackState feedback, GripReadbackHealth readbackHealth)
    {
        this.feedback = feedback;
        this.readbackHealth = readbackHealth;
    }

    public bool IsDispatchPaused => dispatchPaused;
    public bool IsRecoveryReady => pendingAction == GripReadbackAction.Recover && pendingEpochs.Count == 0;
    public bool IsDegradationReady => pendingAction == GripReadbackAction.Degrade && pendingEpochs.Count == 0;

    public void Enqueue(GripContactOutputSet output)
    {
        pendingEpochs.Enqueue(output);
    }

    public void MarkDisposed()
    {
        disposed = true;
    }

    public void OnStatsReadback(GripContactOutputSet output, AsyncGPUReadbackRequest request)
    {
        bool succeeded = !disposed && !output.forceFailure && !request.hasError;
        try
        {
            if (succeeded)
            {
                NativeArray<GripContactAccumulator> data = request.GetData<GripContactAccumulator>();
                for (int i = 0; i < TipCount; i++)
                {
                    output.accumulators[i] = data[i];
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            succeeded = false;
        }
        output.RecordStatsResult(succeeded);
    }

    public void OnBoneReadback(GripContactOutputSet output, AsyncGPUReadbackRequest request)
    {
        bool succeeded = !disposed && !output.forceFailure && !request.hasError;
        try
        {
            if (succeeded)
            {
                NativeArray<uint> data = request.GetData<uint>();
                for (int i = 0; i < output.boneDistanceValues.Length; i++)
                {
                    output.boneDistanceValues[i] = data[i];
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            succeeded = false;
        }
        output.RecordBonesResult(succeeded);
    }

    public void ProcessCompletedEpochs(float now)
    {
        while (pendingEpochs.Count > 0 && pendingEpochs.Peek().IsReadbackComplete)
        {
            GripContactOutputSet output = pendingEpochs.Dequeue();
            if (output.IsCanceled || pendingAction != GripReadbackAction.None)
            {
                output.MarkProcessed();
                continue;
            }

            bool succeeded = output.Succeeded;
            if (succeeded)
            {
                feedback.ApplyCompletedEpoch(output);
            }
            else
            {
                output.state.InvalidateContactData(output.epoch);
                feedback.InvalidateFailedEpoch(output);
                failedEpochCount++;
                if (failedEpochCount == 1 || failedEpochCount % 30 == 0)
                {
                    Debug.LogError("Grip feedback GPU readback epoch " + output.epoch +
                                   " failed (failure " + failedEpochCount + ").");
                }
            }

            GripReadbackAction action = readbackHealth.RecordEpoch(succeeded, now);
            output.MarkProcessed();
            BeginHealthAction(action);
        }
    }

    public void EvaluateIdleHealth(float now)
    {
        if (pendingAction == GripReadbackAction.None)
        {
            BeginHealthAction(readbackHealth.Evaluate(now));
        }
    }

    private void BeginHealthAction(GripReadbackAction action)
    {
        if (pendingAction != GripReadbackAction.None || action == GripReadbackAction.None)
        {
            return;
        }

        pendingAction = action;
        dispatchPaused = true;
    }
}
