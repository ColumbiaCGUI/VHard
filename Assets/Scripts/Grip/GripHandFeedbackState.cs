using System;
using UnityEngine;
using static GripContactConstants;

internal sealed class GripHandFeedbackState
{
    private readonly SceneConfiguror sceneConfiguror;
    private readonly GripScoreConfig config;
    private float lastLeftScoreTime;
    private float lastRightScoreTime;
    private GameObject lastLeftTarget;
    private GameObject lastRightTarget;
    private int leftTargetGeneration;
    private int rightTargetGeneration;

    public GripHandFeedbackState(SceneConfiguror sceneConfiguror, GripScoreConfig config)
    {
        this.sceneConfiguror = sceneConfiguror;
        this.config = config;
    }

    public int LeftTargetGeneration => leftTargetGeneration;
    public int RightTargetGeneration => rightTargetGeneration;

    public GameObject GetTarget(Hand hand)
    {
        return hand == Hand.Left ? lastLeftTarget : lastRightTarget;
    }

    public void CommitTarget(Hand hand, GameObject hold)
    {
        if (hand == Hand.Left)
        {
            lastLeftTarget = hold;
            leftTargetGeneration++;
        }
        else
        {
            lastRightTarget = hold;
            rightTargetGeneration++;
        }
    }

    public void AdvanceTargetGeneration(Hand hand)
    {
        if (hand == Hand.Left)
        {
            leftTargetGeneration++;
        }
        else
        {
            rightTargetGeneration++;
        }
    }

    public void InvalidateAcquisitionSamples()
    {
        sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Left);
        sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Right);
    }

    public void EnsureDistanceArrays()
    {
        if (sceneConfiguror.leftHandBoneToHoldMinDistances == null ||
            sceneConfiguror.leftHandBoneToHoldMinDistances.Length != BoneCount)
        {
            sceneConfiguror.leftHandBoneToHoldMinDistances = CreateInfinityDistanceArray();
        }
        if (sceneConfiguror.rightHandBoneToHoldMinDistances == null ||
            sceneConfiguror.rightHandBoneToHoldMinDistances.Length != BoneCount)
        {
            sceneConfiguror.rightHandBoneToHoldMinDistances = CreateInfinityDistanceArray();
        }
    }

    public void ClearHandFeedback(int handMask)
    {
        if ((handMask & LeftHandMask) != 0)
        {
            sceneConfiguror.leftFingerContactMask = 0;
            sceneConfiguror.leftHandGripScore = 0f;
            Array.Fill(sceneConfiguror.leftHandBoneToHoldMinDistances, float.PositiveInfinity);
            sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Left);
            lastLeftScoreTime = 0f;
        }
        if ((handMask & RightHandMask) != 0)
        {
            sceneConfiguror.rightFingerContactMask = 0;
            sceneConfiguror.rightHandGripScore = 0f;
            Array.Fill(sceneConfiguror.rightHandBoneToHoldMinDistances, float.PositiveInfinity);
            sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Right);
            lastRightScoreTime = 0f;
        }
        PublishCombinedHandState();
    }

    public void PublishCombinedHandState()
    {
        sceneConfiguror.perFingerContactMask = sceneConfiguror.leftFingerContactMask |
                                               (sceneConfiguror.rightFingerContactMask << 5);
        sceneConfiguror.currentGripScore = Mathf.Max(
            sceneConfiguror.leftHandGripScore,
            sceneConfiguror.rightHandGripScore);
    }

    public void Clear()
    {
        sceneConfiguror.leftFingerContactMask = 0;
        sceneConfiguror.rightFingerContactMask = 0;
        sceneConfiguror.perFingerContactMask = 0;
        sceneConfiguror.leftHandGripScore = 0f;
        sceneConfiguror.rightHandGripScore = 0f;
        sceneConfiguror.currentGripScore = 0f;
        EnsureDistanceArrays();
        Array.Fill(sceneConfiguror.leftHandBoneToHoldMinDistances, float.PositiveInfinity);
        Array.Fill(sceneConfiguror.rightHandBoneToHoldMinDistances, float.PositiveInfinity);
        sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Left);
        sceneConfiguror.InvalidateGripAcquisitionSample(Hand.Right);
        lastLeftScoreTime = 0f;
        lastRightScoreTime = 0f;
        lastLeftTarget = null;
        lastRightTarget = null;
        leftTargetGeneration++;
        rightTargetGeneration++;
    }

    public void ApplyCompletedEpoch(GripContactOutputSet output)
    {
        GripHoldContactState state = output.state;
        if (state.hold == null)
        {
            return;
        }

        bool applied = false;
        if ((output.handMask & LeftHandMask) != 0 &&
            output.leftTargetGeneration == leftTargetGeneration &&
            sceneConfiguror.leftHandInteractingClimbingHold == state.hold)
        {
            applied = true;
            GripScoreResult result = GripScoreCalculator.Calculate(output.accumulators, 0, config);
            sceneConfiguror.leftFingerContactMask = result.contactMask;
            sceneConfiguror.leftHandGripScore = SmoothScore(
                sceneConfiguror.leftHandGripScore,
                result.score,
                ref lastLeftScoreTime);
            for (int index = 0; index < BoneCount; index++)
            {
                sceneConfiguror.leftHandBoneToHoldMinDistances[index] =
                    GripUIntFloat.ToFloat(output.boneDistanceValues[index]);
            }
            sceneConfiguror.PublishGripAcquisitionSample(
                Hand.Left,
                state.hold.GetInstanceID(),
                output.leftFingerCurls,
                sceneConfiguror.leftHandBoneToHoldMinDistances,
                output.sampledAt);
        }
        if ((output.handMask & RightHandMask) != 0 &&
            output.rightTargetGeneration == rightTargetGeneration &&
            sceneConfiguror.rightHandInteractingClimbingHold == state.hold)
        {
            applied = true;
            GripScoreResult result = GripScoreCalculator.Calculate(output.accumulators, 5, config);
            sceneConfiguror.rightFingerContactMask = result.contactMask;
            sceneConfiguror.rightHandGripScore = SmoothScore(
                sceneConfiguror.rightHandGripScore,
                result.score,
                ref lastRightScoreTime);
            for (int index = 0; index < BoneCount; index++)
            {
                sceneConfiguror.rightHandBoneToHoldMinDistances[index] =
                    GripUIntFloat.ToFloat(output.boneDistanceValues[BoneCount + index]);
            }
            sceneConfiguror.PublishGripAcquisitionSample(
                Hand.Right,
                state.hold.GetInstanceID(),
                output.rightFingerCurls,
                sceneConfiguror.rightHandBoneToHoldMinDistances,
                output.sampledAt);
        }

        if (!applied)
        {
            return;
        }

        PublishCombinedHandState();
    }

    public void InvalidateFailedEpoch(GripContactOutputSet output)
    {
        GripHoldContactState state = output.state;
        if ((output.handMask & LeftHandMask) != 0 &&
            output.leftTargetGeneration == leftTargetGeneration &&
            sceneConfiguror.leftHandInteractingClimbingHold == state.hold)
        {
            ClearHandFeedback(LeftHandMask);
        }
        if ((output.handMask & RightHandMask) != 0 &&
            output.rightTargetGeneration == rightTargetGeneration &&
            sceneConfiguror.rightHandInteractingClimbingHold == state.hold)
        {
            ClearHandFeedback(RightHandMask);
        }
    }

    private float SmoothScore(float current, float target, ref float lastUpdateTime)
    {
        float now = Time.unscaledTime;
        float delta = lastUpdateTime > 0f ? now - lastUpdateTime : Time.unscaledDeltaTime;
        lastUpdateTime = now;
        float alpha = config.smoothingSeconds <= 0f
            ? 1f
            : 1f - Mathf.Exp(-Mathf.Max(delta, 0f) / config.smoothingSeconds);
        return Mathf.Lerp(current, target, alpha);
    }

    private static float[] CreateInfinityDistanceArray()
    {
        float[] values = new float[BoneCount];
        Array.Fill(values, float.PositiveInfinity);
        return values;
    }
}
