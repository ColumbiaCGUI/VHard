using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Recorder-side visibility into why a hand is or is not acquiring a hold. Both pilots had to
/// contort the wrist before grips registered, and the working theory is occlusion: fingers the
/// headset cannot see keep stale curls, so the gate never sees the flexion. The capture stream
/// carries curls and masks but nothing about per-finger tracking confidence, which makes
/// "not flexed" indistinguishable from "not seen" in every recorded session. This event closes
/// that gap without touching the gate itself: it snapshots the acquisition state whenever the
/// slow-moving signals change, so a session replay can count how often low confidence - rather
/// than an open hand - was what stood between the participant and a latch.
/// </summary>
public static class GripAcquisitionTelemetry
{
    public const string ActionName = "GripAcquisitionState";

    /// <summary>Confidence flicker at an occlusion boundary is the signature being studied, but
    /// it can toggle every frame; one snapshot per hundred milliseconds per hand bounds the row
    /// rate while keeping the flicker visible.</summary>
    public const float MinimumIntervalSeconds = 0.1f;

    /// <summary>Sentinel for "nothing recorded yet": guarantees the first evaluated state of a
    /// recording is always emitted, whatever it is.</summary>
    public const int NoStateKey = -1;

    /// <summary>
    /// Packs the slow-moving signals into a change-detection key. The per-frame flexion and
    /// contact masks are deliberately excluded: contact evidence arrives in GPU epochs and would
    /// toggle the key every frame, turning a state stream into a frame stream.
    /// </summary>
    public static int BuildStateKey(
        GripLatchPhase phase,
        bool trackingValid,
        int confidenceMask,
        bool hasCandidate)
    {
        if (confidenceMask < 0 || confidenceMask > GripEngagementGate.AllFingersMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidenceMask),
                "Confidence mask must be a five-finger mask.");
        }

        return ((int)phase & 0b11) |
               ((trackingValid ? 1 : 0) << 2) |
               ((hasCandidate ? 1 : 0) << 3) |
               (confidenceMask << 4);
    }

    public static bool ShouldRecord(
        int stateKey,
        int lastRecordedKey,
        float now,
        float lastRecordedAt,
        float minimumIntervalSeconds = MinimumIntervalSeconds)
    {
        if (float.IsNaN(now) || float.IsInfinity(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Telemetry time must be finite.");
        }
        if (minimumIntervalSeconds < 0f || float.IsNaN(minimumIntervalSeconds) ||
            float.IsInfinity(minimumIntervalSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumIntervalSeconds),
                "Telemetry interval must be finite and non-negative.");
        }

        return stateKey != lastRecordedKey && now - lastRecordedAt >= minimumIntervalSeconds;
    }

    /// <summary>
    /// The uniformly normalized key=value details for one snapshot. Masks are the standard
    /// five-finger bitmasks (thumb = bit 0), matching the capture stream's finger masks, and the
    /// per-finger curls are carried verbatim so a stale value is recorded as the gate saw it.
    /// </summary>
    public static string FormatDetails(
        GripLatchPhase phase,
        GripEngagementBlock block,
        bool trackingValid,
        int confidenceMask,
        in GripAcquisitionMasks masks,
        int countedFingers,
        int requiredFingers,
        IReadOnlyList<float> curls)
    {
        if (curls == null || curls.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException("Telemetry curls must contain five values.", nameof(curls));
        }

        StringBuilder details = new();
        details.Append("phase=").Append(phase)
            .Append(";block=").Append(block)
            .Append(";trackingValid=").Append(trackingValid ? "true" : "false")
            .Append(";confidence=").Append(confidenceMask.ToString(CultureInfo.InvariantCulture))
            .Append(";flexed=").Append(masks.Flexed.ToString(CultureInfo.InvariantCulture))
            .Append(";contact=").Append(masks.Contact.ToString(CultureInfo.InvariantCulture))
            .Append(";flexedContact=").Append(masks.FlexedContact.ToString(CultureInfo.InvariantCulture))
            .Append(";counted=").Append(countedFingers.ToString(CultureInfo.InvariantCulture))
            .Append(";required=").Append(requiredFingers.ToString(CultureInfo.InvariantCulture))
            .Append(";curls=").Append(FormatCurlList(curls));
        return details.ToString();
    }

    /// <summary>The shared curl rendering: five F2 values, thumb first, in parentheses.</summary>
    public static string FormatCurlList(IReadOnlyList<float> curls)
    {
        if (curls == null || curls.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException("Telemetry curls must contain five values.", nameof(curls));
        }

        StringBuilder list = new("(");
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            if (finger > 0)
            {
                list.Append(',');
            }
            list.Append(curls[finger].ToString("F2", CultureInfo.InvariantCulture));
        }
        return list.Append(')').ToString();
    }

    public static int BuildConfidenceMask(IReadOnlyList<bool> highConfidence)
    {
        if (highConfidence == null || highConfidence.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException(
                "Finger confidence must contain five values.",
                nameof(highConfidence));
        }

        int mask = 0;
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            if (highConfidence[finger])
            {
                mask |= 1 << finger;
            }
        }
        return mask;
    }
}
