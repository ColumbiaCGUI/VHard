using System;
using UnityEngine;

/// <summary>
/// Left-hand palm-up dwell-then-pinch gesture that toggles the experimenter panel. The whole
/// gesture stands down while the participant is grip-engaged (see
/// <see cref="SummonGatePolicy"/>), so an undercling grab can never dwell into the console.
/// </summary>
public sealed class SummonGestureDetector
{
    private readonly StudySessionState state;
    private readonly StudyControlPanel panel;
    private readonly Func<float> summonDwellSeconds;
    private readonly Func<float> summonCooldownSeconds;
    private readonly Func<SummonBlockReason> summonBlockReason;

    private float summonDwellStart = -1f;
    private bool summonReadyForPinch;
    private float summonCooldownUntil;
    private bool blockedEpisodeActive;

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
        Func<float> summonCooldownSeconds,
        Func<SummonBlockReason> summonBlockReason)
    {
        this.state = state;
        this.panel = panel;
        this.summonDwellSeconds = summonDwellSeconds;
        this.summonCooldownSeconds = summonCooldownSeconds;
        this.summonBlockReason = summonBlockReason;
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

        // A grip-engaged participant is climbing, not summoning: underclings put the palm into
        // the summon pose, so engagement stands the whole gesture down - the dwell never starts,
        // and an already-armed summon cancels, which lifts the arm-time input suppression so the
        // grab being reached for can still latch. Only the open direction is gated: engagement
        // cannot exist while the panel is open (opening released it), and the palm-up close must
        // never be refusable.
        SummonBlockReason blockReason = panel.IsPanelHidden
            ? summonBlockReason()
            : SummonBlockReason.None;
        if (blockReason != SummonBlockReason.None)
        {
            ResetSummonDwell();
            if (!palmUp)
            {
                blockedEpisodeActive = false;
            }
            else if (!blockedEpisodeActive)
            {
                // One row per continuous blocked palm-up episode: the false-positive counter for
                // the residual rate, not a per-frame log.
                blockedEpisodeActive = true;
                panel.RecordSummonEvent("phase=blocked;reason=" + blockReason.ToRecorderValue());
            }
            return false;
        }
        blockedEpisodeActive = false;

        if (!StudyRehearsalTiming.RequiresPanelSummonDwell(state.blockRunning, state.IsAuxiliaryActive))
        {
            ResetSummonDwell();
            if (palmUp && pinchStarted)
            {
                bool wasHidden = panel.IsPanelHidden;
                panel.TogglePanel();
                if (wasHidden && !panel.IsPanelHidden)
                {
                    panel.RecordSummonEvent("phase=opened");
                }
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
            panel.RecordSummonEvent("phase=armed");
        }

        if (summonReadyForPinch && pinchStarted)
        {
            summonCooldownUntil = now + Mathf.Max(0f, summonCooldownSeconds());
            bool wasHidden = panel.IsPanelHidden;
            panel.TogglePanel();
            if (wasHidden && !panel.IsPanelHidden)
            {
                panel.RecordSummonEvent("phase=opened");
            }
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
