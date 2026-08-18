using System;
using UnityEngine;

/// <summary>
/// Left-hand palm-up dwell-then-pinch gesture that toggles the experimenter panel.
/// </summary>
public sealed class SummonGestureDetector
{
    private readonly StudySessionState state;
    private readonly StudyControlPanel panel;
    private readonly Func<float> summonDwellSeconds;
    private readonly Func<float> summonCooldownSeconds;

    private float summonDwellStart = -1f;
    private bool summonReadyForPinch;
    private float summonCooldownUntil;

    /// <summary>True once the palm-up dwell has completed and a pinch would toggle the panel.</summary>
    public bool ArmedForPinch => summonReadyForPinch;

    /// <summary>True while the guarded summon is visibly in progress: dwelling or armed.</summary>
    public bool IsDwellIndicatorVisible => summonDwellStart >= 0f || summonReadyForPinch;

    public float GetDwellProgress01(float now)
    {
        return StudyRehearsalTiming.ComputeSummonDwellProgress(
            summonReadyForPinch,
            summonDwellStart,
            now,
            summonDwellSeconds());
    }

    public SummonGestureDetector(
        StudySessionState state,
        StudyControlPanel panel,
        Func<float> summonDwellSeconds,
        Func<float> summonCooldownSeconds)
    {
        this.state = state;
        this.panel = panel;
        this.summonDwellSeconds = summonDwellSeconds;
        this.summonCooldownSeconds = summonCooldownSeconds;
    }

    public bool UpdateSummonGesture(
        OVRHand hand,
        OVRSkeleton skeleton,
        bool pinching,
        bool pinchStarted,
        bool isLeft,
        bool panelTargeted)
    {
        if (!isLeft || !panel.IsPanelBuilt)
        {
            return false;
        }
        if (!panel.IsPanelHidden && panelTargeted)
        {
            ResetSummonDwell();
            return false;
        }

        bool trackingConfident = hand != null && hand.IsTracked && hand.IsDataHighConfidence;
        bool palmUp = trackingConfident && IsPalmUp(skeleton);
        if (!StudyRehearsalTiming.RequiresPanelSummonDwell(state.blockRunning, state.IsAuxiliaryActive))
        {
            ResetSummonDwell();
            if (palmUp && pinchStarted)
            {
                panel.TogglePanel();
                return true;
            }
            return false;
        }

        float now = Time.unscaledTime;
        if (now < summonCooldownUntil || !palmUp)
        {
            ResetSummonDwell();
            return false;
        }

        if (summonDwellStart < 0f)
        {
            if (pinching)
            {
                return false;
            }
            summonDwellStart = now;
        }

        // Suppress gameplay input only once the dwell completes: suppression means
        // "the panel is open or about to open", never "a palm happens to be tilted".
        if (!summonReadyForPinch &&
            now - summonDwellStart >= Mathf.Max(0f, summonDwellSeconds()))
        {
            summonReadyForPinch = true;
            panel.SetGameplayInputSuppressed(true);
            panel.SetSummonArmed(true);
        }

        if (summonReadyForPinch && pinchStarted)
        {
            summonCooldownUntil = now + Mathf.Max(0f, summonCooldownSeconds());
            panel.TogglePanel();
            ResetSummonDwell();
            return true;
        }

        if (pinching)
        {
            ResetSummonDwell();
        }
        return false;
    }

    public void ResetSummonDwell()
    {
        summonDwellStart = -1f;
        summonReadyForPinch = false;
        panel.SetSummonArmed(false);
        if (panel.IsPanelHidden)
        {
            panel.SetGameplayInputSuppressed(false);
        }
    }

    private static bool IsPalmUp(OVRSkeleton skeleton)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count == 0 ||
            skeleton.Bones[0].Transform == null)
        {
            return false;
        }

        // OpenXR hand joints (XRHand skeletons) put +Y out the back of the hand, so the
        // palmar direction is -up after OVRSkeleton's FromFlippedZQuatf conversion.
        Transform palm = skeleton.Bones[0].Transform;
        return Vector3.Dot(-palm.up, Vector3.up) > 0.55f;
    }
}
