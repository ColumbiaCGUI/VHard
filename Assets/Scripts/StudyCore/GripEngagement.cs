using System;
using System.Collections.Generic;
using UnityEngine;

public static class FingerCurlEstimator
{
    public const int FingerCount = 5;
    public const int RequiredBoneCount = 25;
    public const float OpenReferenceDegrees = 15f;
    public const float ClosedReferenceDegrees = 210f;

    private static readonly int[][] JointChains =
    {
        new[] { 2, 3, 4 },
        new[] { 6, 7, 8, 9 },
        new[] { 11, 12, 13, 14 },
        new[] { 16, 17, 18, 19 },
        new[] { 21, 22, 23, 24 },
    };

    public static void Update(
        IReadOnlyList<Quaternion> boneRotations,
        IReadOnlyList<bool> highConfidence,
        float[] curls)
    {
        if (boneRotations == null || boneRotations.Count < RequiredBoneCount)
        {
            throw new ArgumentException("OpenXR hand rotations must contain at least 25 bones.", nameof(boneRotations));
        }
        if (highConfidence == null || highConfidence.Count < FingerCount)
        {
            throw new ArgumentException("Finger confidence must contain five values.", nameof(highConfidence));
        }
        if (curls == null || curls.Length < FingerCount)
        {
            throw new ArgumentException("Curl output must contain five values.", nameof(curls));
        }

        for (int finger = 0; finger < FingerCount; finger++)
        {
            if (highConfidence[finger])
            {
                curls[finger] = Calculate(boneRotations, JointChains[finger]);
            }
        }
    }

    private static float Calculate(IReadOnlyList<Quaternion> rotations, IReadOnlyList<int> chain)
    {
        float bendDegrees = 0f;
        for (int joint = 1; joint < chain.Count; joint++)
        {
            bendDegrees += Quaternion.Angle(rotations[chain[joint - 1]], rotations[chain[joint]]);
        }
        return Mathf.Clamp01(
            (bendDegrees - OpenReferenceDegrees) /
            (ClosedReferenceDegrees - OpenReferenceDegrees));
    }
}

public static class GripEngagementGate
{
    public const int ThumbMask = 1;
    public const int NonThumbMask = 0b1_1110;
    public const int RequiredBoneDistanceCount = 26;

    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };

    public static int BuildFlexedContactMask(
        IReadOnlyList<float> curls,
        IReadOnlyList<float> boneDistances,
        float curlThreshold,
        float contactRange)
    {
        ValidateCurls(curls);
        if (boneDistances == null || boneDistances.Count < RequiredBoneDistanceCount)
        {
            throw new ArgumentException("Hand distances must contain all OpenXR fingertips.", nameof(boneDistances));
        }

        int mask = 0;
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            if (curls[finger] > curlThreshold &&
                boneDistances[FingertipBoneIndices[finger]] <= contactRange)
            {
                mask |= 1 << finger;
            }
        }
        return mask;
    }

    public static int BuildFlexedMask(IReadOnlyList<float> curls, float curlThreshold)
    {
        ValidateCurls(curls);
        int mask = 0;
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            if (curls[finger] >= curlThreshold)
            {
                mask |= 1 << finger;
            }
        }
        return mask;
    }

    public static bool CanAcquire(bool insideAcquisitionVolume, int minFingers, int flexedContactMask)
    {
        ValidateMinFingers(minFingers);
        return insideAcquisitionVolume && CountNonThumbFingers(flexedContactMask) >= minFingers;
    }

    public static int CountNonThumbFingers(int fingerMask)
    {
        int value = fingerMask & NonThumbMask;
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }

    public static void ValidateMinFingers(int minFingers)
    {
        if (minFingers < 1 || minFingers > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(minFingers), "minFingers must be between 1 and 4.");
        }
    }

    private static void ValidateCurls(IReadOnlyList<float> curls)
    {
        if (curls == null || curls.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException("Finger curls must contain five values.", nameof(curls));
        }
    }
}

public sealed class GripAcquisitionSample
{
    public const float MaximumAgeSeconds = 0.1f;

    private readonly float[] curls = new float[FingerCurlEstimator.FingerCount];
    private readonly float[] boneDistances = new float[GripEngagementGate.RequiredBoneDistanceCount];

    public bool IsValid { get; private set; }
    public int HoldId { get; private set; }
    public float SampledAt { get; private set; }

    public void Publish(
        int holdId,
        IReadOnlyList<float> sampledCurls,
        IReadOnlyList<float> sampledBoneDistances,
        float sampledAt)
    {
        if (holdId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(holdId), "Acquisition samples require a hold ID.");
        }
        if (float.IsNaN(sampledAt) || float.IsInfinity(sampledAt))
        {
            throw new ArgumentOutOfRangeException(nameof(sampledAt), "Acquisition sample time must be finite.");
        }
        if (sampledCurls == null || sampledCurls.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException("Acquisition curls must contain five values.", nameof(sampledCurls));
        }
        if (sampledBoneDistances == null ||
            sampledBoneDistances.Count < GripEngagementGate.RequiredBoneDistanceCount)
        {
            throw new ArgumentException(
                "Acquisition distances must contain all OpenXR hand bones.",
                nameof(sampledBoneDistances));
        }

        HoldId = holdId;
        SampledAt = sampledAt;
        for (int index = 0; index < curls.Length; index++)
        {
            curls[index] = sampledCurls[index];
        }
        for (int index = 0; index < boneDistances.Length; index++)
        {
            boneDistances[index] = sampledBoneDistances[index];
        }
        IsValid = true;
    }

    public int ConsumeFlexedContactMask(
        int holdId,
        IReadOnlyList<float> currentCurls,
        float curlThreshold,
        float contactRange,
        float now,
        float maximumAgeSeconds = MaximumAgeSeconds)
    {
        if (maximumAgeSeconds < 0f || float.IsNaN(maximumAgeSeconds) ||
            float.IsInfinity(maximumAgeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAgeSeconds),
                "Acquisition sample age must be finite and non-negative.");
        }
        if (!IsValid)
        {
            return 0;
        }
        if (float.IsNaN(now) || float.IsInfinity(now) || now < SampledAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Acquisition time must be finite and monotonic.");
        }
        if (HoldId != holdId || now - SampledAt > maximumAgeSeconds)
        {
            Invalidate();
            return 0;
        }

        int sampledMask = GripEngagementGate.BuildFlexedContactMask(
            curls,
            boneDistances,
            curlThreshold,
            contactRange);
        int currentFlexedMask = GripEngagementGate.BuildFlexedMask(currentCurls, curlThreshold);
        Invalidate();
        return sampledMask & currentFlexedMask;
    }

    public void Invalidate()
    {
        IsValid = false;
        HoldId = 0;
        SampledAt = 0f;
    }
}

public enum GripLatchPhase
{
    Free,
    Latched,
    Frozen,
}

public enum GripLatchTransitionKind
{
    None,
    Latched,
    Frozen,
    Resumed,
    Released,
}

public enum GripReleaseReason
{
    None,
    OpenHand,
    CountDrop,
    FrozenTimeout,
}

public readonly struct GripLatchTransition
{
    public GripLatchTransition(
        GripLatchTransitionKind kind,
        int holdId,
        GripReleaseReason releaseReason = GripReleaseReason.None,
        bool resetAnchor = false)
    {
        Kind = kind;
        HoldId = holdId;
        ReleaseReason = releaseReason;
        ResetAnchor = resetAnchor;
    }

    public GripLatchTransitionKind Kind { get; }
    public int HoldId { get; }
    public GripReleaseReason ReleaseReason { get; }
    public bool ResetAnchor { get; }
}

public sealed class GripLatchStateMachine
{
    private readonly float releaseGraceSeconds;
    private readonly float trackingFreezeSeconds;
    private readonly float frozenTimeoutSeconds;
    private float countDropStartedAt = float.NaN;
    private float trackingLostAt = float.NaN;
    private float frozenAt = float.NaN;
    private float lastUpdateTime = float.NaN;
    private bool lastTrackingValid;
    private int engagedFingerMask;
    private int releaseCount;

    public GripLatchStateMachine(
        float releaseGraceSeconds = 0.15f,
        float trackingFreezeSeconds = 0.25f,
        float frozenTimeoutSeconds = 2f)
    {
        if (releaseGraceSeconds < 0f || trackingFreezeSeconds < 0f || frozenTimeoutSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseGraceSeconds), "Latch timings must be non-negative and timeout must be positive.");
        }
        this.releaseGraceSeconds = releaseGraceSeconds;
        this.trackingFreezeSeconds = trackingFreezeSeconds;
        this.frozenTimeoutSeconds = frozenTimeoutSeconds;
    }

    public GripLatchPhase Phase { get; private set; }
    public int HoldId { get; private set; }
    public bool IsEngaged => Phase != GripLatchPhase.Free;

    public GripLatchTransition Update(
        float now,
        bool trackingValid,
        bool insideAcquisitionVolume,
        int candidateHoldId,
        int minFingers,
        int highFlexedContactMask,
        int lowFlexedMask)
    {
        if (!float.IsNaN(lastUpdateTime) && now < lastUpdateTime)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Latch time must be monotonic.");
        }
        lastUpdateTime = now;

        if (Phase == GripLatchPhase.Free)
        {
            lastTrackingValid = trackingValid;
            if (!trackingValid || candidateHoldId == 0 ||
                !GripEngagementGate.CanAcquire(insideAcquisitionVolume, minFingers, highFlexedContactMask))
            {
                return default;
            }

            Phase = GripLatchPhase.Latched;
            HoldId = candidateHoldId;
            releaseCount = Mathf.Max(1, minFingers);
            engagedFingerMask = highFlexedContactMask & GripEngagementGate.NonThumbMask;
            ClearTimers();
            return new GripLatchTransition(GripLatchTransitionKind.Latched, HoldId, resetAnchor: true);
        }

        if (Phase == GripLatchPhase.Frozen)
        {
            if (now - frozenAt > frozenTimeoutSeconds)
            {
                return Release(GripReleaseReason.FrozenTimeout);
            }
            lastTrackingValid = trackingValid;
            if (!trackingValid)
            {
                return default;
            }
            if ((lowFlexedMask & engagedFingerMask) == 0)
            {
                return Release(GripReleaseReason.OpenHand);
            }
            if (GripEngagementGate.CountNonThumbFingers(lowFlexedMask & engagedFingerMask) < releaseCount)
            {
                return Release(GripReleaseReason.CountDrop);
            }

            Phase = GripLatchPhase.Latched;
            trackingLostAt = float.NaN;
            frozenAt = float.NaN;
            countDropStartedAt = float.NaN;
            return new GripLatchTransition(GripLatchTransitionKind.Resumed, HoldId, resetAnchor: true);
        }

        if (!trackingValid)
        {
            lastTrackingValid = false;
            countDropStartedAt = float.NaN;
            if (float.IsNaN(trackingLostAt))
            {
                trackingLostAt = now;
            }
            if (now - trackingLostAt > trackingFreezeSeconds)
            {
                Phase = GripLatchPhase.Frozen;
                frozenAt = now;
                countDropStartedAt = float.NaN;
                return new GripLatchTransition(GripLatchTransitionKind.Frozen, HoldId);
            }
            return default;
        }

        bool trackingRecovered = !lastTrackingValid || !float.IsNaN(trackingLostAt);
        lastTrackingValid = true;
        trackingLostAt = float.NaN;

        if ((lowFlexedMask & engagedFingerMask) == 0)
        {
            return Release(GripReleaseReason.OpenHand);
        }

        if (GripEngagementGate.CountNonThumbFingers(lowFlexedMask & engagedFingerMask) < releaseCount)
        {
            if (float.IsNaN(countDropStartedAt))
            {
                countDropStartedAt = now;
            }
            if (now - countDropStartedAt > releaseGraceSeconds)
            {
                return Release(GripReleaseReason.CountDrop);
            }
        }
        else
        {
            countDropStartedAt = float.NaN;
        }

        return trackingRecovered
            ? new GripLatchTransition(GripLatchTransitionKind.None, HoldId, resetAnchor: true)
            : default;
    }

    public void Reset()
    {
        Phase = GripLatchPhase.Free;
        HoldId = 0;
        engagedFingerMask = 0;
        releaseCount = 0;
        lastTrackingValid = false;
        lastUpdateTime = float.NaN;
        ClearTimers();
    }

    private GripLatchTransition Release(GripReleaseReason reason)
    {
        int releasedHoldId = HoldId;
        Phase = GripLatchPhase.Free;
        HoldId = 0;
        engagedFingerMask = 0;
        releaseCount = 0;
        ClearTimers();
        return new GripLatchTransition(GripLatchTransitionKind.Released, releasedHoldId, reason);
    }

    private void ClearTimers()
    {
        countDropStartedAt = float.NaN;
        trackingLostAt = float.NaN;
        frozenAt = float.NaN;
    }
}

public static class GripReleaseReasonExtensions
{
    public static string ToRecorderValue(this GripReleaseReason reason)
    {
        return reason switch
        {
            GripReleaseReason.OpenHand => "open_hand",
            GripReleaseReason.CountDrop => "count_drop",
            GripReleaseReason.FrozenTimeout => "frozen_timeout",
            _ => string.Empty,
        };
    }
}
