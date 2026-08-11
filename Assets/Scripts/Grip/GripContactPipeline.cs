using System;
using System.Collections.Generic;
using UnityEngine;
using static GripContactConstants;

public sealed class GripContactPipeline : IDisposable
{
    private readonly GripHandFeedbackState feedback;
    private readonly GripContactReadbackProcessor readback;
    private readonly GripHoldContactStore holdStore;
    private readonly GripContactDispatcher dispatcher;
    private readonly bool supported;
    private bool disposed;
    private bool feedbackVisible = true;

    public GripContactPipeline(
        SceneConfiguror sceneConfiguror,
        ComputeShader computeShader,
        GripScoreConfig config,
        bool recoveryAttempted = false)
    {
        supported = SystemInfo.supportsAsyncGPUReadback;
        feedback = new GripHandFeedbackState(sceneConfiguror, config);
        readback = new GripContactReadbackProcessor(
            feedback,
            new GripReadbackHealth(recoveryAttempted, Time.unscaledTime));
        holdStore = new GripHoldContactStore(readback, config);
        if (!supported)
        {
            Debug.LogError("Grip feedback requires asynchronous GPU readback support.");
        }
        dispatcher = new GripContactDispatcher(sceneConfiguror, computeShader, config, readback);
    }

    public bool IsSupported => supported && !disposed;
    public bool IsRecoveryReady => readback.IsRecoveryReady;
    public bool IsDegradationReady => readback.IsDegradationReady;

    public void Update(float now)
    {
        if (disposed)
        {
            return;
        }

        feedback.InvalidateAcquisitionSamples();
        readback.ProcessCompletedEpochs(now);
        readback.EvaluateIdleHealth(now);
    }

    public void DebugInjectReadbackFailures(int epochCount)
    {
        if (!Debug.isDebugBuild || epochCount <= 0)
        {
            return;
        }
        dispatcher.InjectReadbackFailures(epochCount);
    }

    public void DebugSetReadbackFailures(bool enabled)
    {
        if (Debug.isDebugBuild)
        {
            dispatcher.SetForcedReadbackFailures(enabled);
        }
    }

    public void SetFeedbackVisible(bool visible)
    {
        feedbackVisible = visible;
        if (!visible)
        {
            ClearFeedback();
        }
    }

    public void NotifyTargetDiscontinuity(Hand hand)
    {
        if (disposed)
        {
            return;
        }

        feedback.EnsureDistanceArrays();
        if (hand == Hand.Left)
        {
            holdStore.InvalidateHoldContact(feedback.GetTarget(Hand.Left));
            feedback.AdvanceTargetGeneration(Hand.Left);
            feedback.ClearHandFeedback(LeftHandMask);
        }
        else
        {
            holdStore.InvalidateHoldContact(feedback.GetTarget(Hand.Right));
            feedback.AdvanceTargetGeneration(Hand.Right);
            feedback.ClearHandFeedback(RightHandMask);
        }
    }

    public void Process(
        GameObject leftHold,
        GameObject rightHold,
        List<Vector3> leftHandBonePositions,
        List<Vector3> rightHandBonePositions,
        IReadOnlyList<float> leftFingerCurls,
        IReadOnlyList<float> rightFingerCurls,
        bool leftHandValid = true,
        bool rightHandValid = true)
    {
        if (disposed)
        {
            return;
        }
        feedback.EnsureDistanceArrays();
        bool leftReady = supported && leftHandValid && leftHandBonePositions.Count >= BoneCount &&
                         leftFingerCurls != null && leftFingerCurls.Count >= FingerCurlEstimator.FingerCount;
        bool rightReady = supported && rightHandValid && rightHandBonePositions.Count >= BoneCount &&
                          rightFingerCurls != null && rightFingerCurls.Count >= FingerCurlEstimator.FingerCount;
        if (!leftReady)
        {
            leftHold = null;
        }
        if (!rightReady)
        {
            rightHold = null;
        }

        if (UpdateTarget(Hand.Left, leftHold))
        {
            feedback.ClearHandFeedback(LeftHandMask);
        }
        if (UpdateTarget(Hand.Right, rightHold))
        {
            feedback.ClearHandFeedback(RightHandMask);
        }

        holdStore.HideAllOverlays();

        if (!supported)
        {
            ClearFeedback();
            return;
        }
        if (!feedbackVisible)
        {
            feedback.ClearHandFeedback(LeftHandMask | RightHandMask);
            return;
        }

        if (leftHold == null)
        {
            feedback.ClearHandFeedback(LeftHandMask);
        }
        if (rightHold == null)
        {
            feedback.ClearHandFeedback(RightHandMask);
        }
        if (readback.IsDispatchPaused)
        {
            return;
        }

        if (leftReady)
        {
            dispatcher.StageHand(Hand.Left, leftHandBonePositions, leftFingerCurls);
        }
        if (rightReady)
        {
            dispatcher.StageHand(Hand.Right, rightHandBonePositions, rightFingerCurls);
        }

        if (leftHold != null && rightHold == leftHold)
        {
            ProcessHold(leftHold, LeftHandMask | RightHandMask);
        }
        else
        {
            if (leftHold != null)
            {
                ProcessHold(leftHold, LeftHandMask);
            }
            if (rightHold != null)
            {
                ProcessHold(rightHold, RightHandMask);
            }
        }

        feedback.PublishCombinedHandState();
        holdStore.RemoveDestroyedStates();
    }

    public void ClearFeedback()
    {
        holdStore.ClearAllLatchFeedback();
        holdStore.InvalidateAllContactData();
        feedback.Clear();
    }

    public void SetLatchFeedback(Hand hand, GameObject hold, bool latched)
    {
        if (disposed || !feedbackVisible)
        {
            return;
        }

        holdStore.SetLatchFeedback(
            hold,
            hand == Hand.Left ? LeftHandMask : RightHandMask,
            latched);
    }

    public void Prepare(IReadOnlyList<GameObject> holds)
    {
        holdStore.Retain(holds);

        if (holds == null)
        {
            return;
        }
        foreach (GameObject hold in holds)
        {
            Prepare(hold);
        }
    }

    public void Prepare(GameObject hold)
    {
        if (disposed)
        {
            return;
        }

        holdStore.Prepare(hold);
    }

    private void ProcessHold(GameObject hold, int handMask)
    {
        GripHoldContactState state = holdStore.ResolveState(hold);
        if (state == null)
        {
            return;
        }

        dispatcher.Dispatch(
            hold,
            state,
            handMask,
            feedback.LeftTargetGeneration,
            feedback.RightTargetGeneration);
    }

    private bool UpdateTarget(Hand hand, GameObject current)
    {
        GameObject previous = feedback.GetTarget(hand);
        if (previous == current)
        {
            return false;
        }

        holdStore.InvalidateHoldContact(previous);
        holdStore.InvalidateHoldContact(current);
        feedback.CommitTarget(hand, current);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        readback.MarkDisposed();
        holdStore.DisposeAll();
    }
}
