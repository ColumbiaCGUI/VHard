using System;
using System.Collections.Generic;
using UnityEngine;

public static class FingerCurlEstimator
{
    public const int FingerCount = 5;
    public const int RequiredBoneCount = 25;
    public const int ThumbFinger = 0;
    public const int MaximumJointsPerFinger = 3;
    public const float OpenReferenceDegrees = 15f;
    public const float ClosedReferenceDegrees = 210f;
    // The thumb bends across two joints (MCP, IP) rather than three, so the four-finger span
    // saturates its curl below 0.45 and no engagement threshold can ever see a loaded thumb.
    public const float ThumbOpenReferenceDegrees = 10f;
    public const float ThumbClosedReferenceDegrees = 110f;

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
                curls[finger] = Calculate(boneRotations, finger);
            }
        }
    }

    /// <summary>Writes the individual joint bends of one finger, proximal to distal, so the
    /// diagnostics panel can report MCP/PIP/DIP (thumb: MCP/IP) instead of the pooled curl.</summary>
    public static int SampleJointDegrees(
        IReadOnlyList<Quaternion> boneRotations,
        int finger,
        float[] jointDegrees)
    {
        ValidateFinger(finger);
        if (boneRotations == null || boneRotations.Count < RequiredBoneCount)
        {
            throw new ArgumentException("OpenXR hand rotations must contain at least 25 bones.", nameof(boneRotations));
        }
        if (jointDegrees == null || jointDegrees.Length < MaximumJointsPerFinger)
        {
            throw new ArgumentException("Joint output must hold three values.", nameof(jointDegrees));
        }

        int[] chain = JointChains[finger];
        for (int joint = 1; joint < chain.Length; joint++)
        {
            jointDegrees[joint - 1] = Quaternion.Angle(
                boneRotations[chain[joint - 1]],
                boneRotations[chain[joint]]);
        }
        return chain.Length - 1;
    }

    public static int GetJointCount(int finger)
    {
        ValidateFinger(finger);
        return JointChains[finger].Length - 1;
    }

    public static float GetOpenReferenceDegrees(int finger)
    {
        ValidateFinger(finger);
        return finger == ThumbFinger ? ThumbOpenReferenceDegrees : OpenReferenceDegrees;
    }

    public static float GetClosedReferenceDegrees(int finger)
    {
        ValidateFinger(finger);
        return finger == ThumbFinger ? ThumbClosedReferenceDegrees : ClosedReferenceDegrees;
    }

    private static float Calculate(IReadOnlyList<Quaternion> rotations, int finger)
    {
        int[] chain = JointChains[finger];
        float bendDegrees = 0f;
        for (int joint = 1; joint < chain.Length; joint++)
        {
            bendDegrees += Quaternion.Angle(rotations[chain[joint - 1]], rotations[chain[joint]]);
        }
        float open = GetOpenReferenceDegrees(finger);
        return Mathf.Clamp01((bendDegrees - open) / (GetClosedReferenceDegrees(finger) - open));
    }

    private static void ValidateFinger(int finger)
    {
        if (finger < 0 || finger >= FingerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(finger), "A hand has five fingers.");
        }
    }
}

/// <summary>Why the gate is refusing to latch: the last three values come from the finger
/// predicate itself, the rest from the conditions the coordinator resolves before a predicate can
/// be evaluated at all. The diagnostics panel prints one sentence per value, so a pilot never has
/// to guess which clause is short.</summary>
public enum GripEngagementBlock
{
    None,
    Latched,
    InputSuppressed,
    AwaitingOpenHand,
    TrackingLost,
    NoCandidateHold,
    AffordancesUnavailable,
    NoContactSample,
    NoFlexedFinger,
    NoContactFinger,
    TooFewFingers,
}

/// <summary>The thresholds one hold imposes on one hand. The normal path is spec 08's
/// count-of-flexed-contacting-fingers rule; the strong path accepts fewer fingers when each of
/// them carries much stronger evidence, which is what makes one- and two-finger grips reachable
/// without lowering the global count and inviting reach-past latches.</summary>
public readonly struct GripAcquisitionCriteria
{
    public GripAcquisitionCriteria(
        int minFingers,
        bool thumbCountsTowardMinimum,
        int strongFingerFloor,
        float engageCurl,
        float strongCurl,
        float contactRange,
        float strongContactRange)
    {
        GripEngagementGate.ValidateMinFingers(minFingers);
        GripEngagementGate.ValidateMinFingers(strongFingerFloor);
        if (engageCurl <= 0f || engageCurl > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(engageCurl),
                "Engagement flexion must lie within (0, 1].");
        }
        if (strongCurl < engageCurl || strongCurl > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strongCurl),
                "Strong flexion must be at least the engagement flexion and at most 1.");
        }
        if (contactRange <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contactRange),
                "Contact range must be positive.");
        }
        if (strongContactRange <= 0f || strongContactRange > contactRange)
        {
            throw new ArgumentOutOfRangeException(
                nameof(strongContactRange),
                "Strong contact range must be positive and no wider than the contact range.");
        }

        MinFingers = minFingers;
        ThumbCountsTowardMinimum = thumbCountsTowardMinimum;
        StrongFingerFloor = Mathf.Min(strongFingerFloor, minFingers);
        EngageCurl = engageCurl;
        StrongCurl = strongCurl;
        ContactRange = contactRange;
        StrongContactRange = strongContactRange;
    }

    public int MinFingers { get; }
    public bool ThumbCountsTowardMinimum { get; }
    public int StrongFingerFloor { get; }
    public float EngageCurl { get; }
    public float StrongCurl { get; }
    public float ContactRange { get; }
    public float StrongContactRange { get; }
}

/// <summary>Per-finger evidence for one acquisition attempt, split by clause so a failure can name
/// the clause that fell short instead of only reporting "no grip".</summary>
public readonly struct GripAcquisitionMasks
{
    public GripAcquisitionMasks(int flexed, int contact, int flexedContact, int strongContact)
    {
        Flexed = flexed;
        Contact = contact;
        FlexedContact = flexedContact;
        StrongContact = strongContact;
    }

    public int Flexed { get; }
    public int Contact { get; }
    public int FlexedContact { get; }
    public int StrongContact { get; }

    public static GripAcquisitionMasks Build(
        IReadOnlyList<float> curls,
        IReadOnlyList<float> boneDistances,
        in GripAcquisitionCriteria criteria)
    {
        return new GripAcquisitionMasks(
            GripEngagementGate.BuildFlexedMask(curls, criteria.EngageCurl),
            GripEngagementGate.BuildContactMask(boneDistances, criteria.ContactRange),
            GripEngagementGate.BuildFlexedContactMask(
                curls,
                boneDistances,
                criteria.EngageCurl,
                criteria.ContactRange),
            GripEngagementGate.BuildFlexedContactMask(
                curls,
                boneDistances,
                criteria.StrongCurl,
                criteria.StrongContactRange));
    }
}

public readonly struct GripAcquisitionVerdict
{
    public GripAcquisitionVerdict(
        bool canAcquire,
        int acquiredMask,
        int countedFingers,
        int requiredFingers,
        GripEngagementBlock block)
    {
        CanAcquire = canAcquire;
        AcquiredMask = acquiredMask;
        CountedFingers = countedFingers;
        RequiredFingers = requiredFingers;
        Block = block;
    }

    public bool CanAcquire { get; }
    public int AcquiredMask { get; }
    public int CountedFingers { get; }
    public int RequiredFingers { get; }
    public GripEngagementBlock Block { get; }
}

public static class GripEngagementGate
{
    public const int ThumbMask = 1;
    public const int NonThumbMask = 0b1_1110;
    public const int AllFingersMask = 0b1_1111;
    public const int RequiredBoneDistanceCount = 26;

    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };

    public static int GetFingertipBoneIndex(int finger)
    {
        if (finger < 0 || finger >= FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(finger), "A hand has five fingers.");
        }
        return FingertipBoneIndices[finger];
    }

    /// <summary>Spec 08's count rule first; when it is short, the same fingers are re-tested
    /// against the stricter strong-contact thresholds, which is how a mono or two-finger pocket
    /// engages a hold whose default minimum it can never physically satisfy. Every acquisition
    /// still needs at least one non-thumb finger, so the latch always has a finger to release on.
    /// </summary>
    public static GripAcquisitionVerdict Evaluate(
        in GripAcquisitionCriteria criteria,
        in GripAcquisitionMasks masks)
    {
        int normalCount = CountFingers(masks.FlexedContact, criteria.ThumbCountsTowardMinimum);
        int strongCount = CountFingers(masks.StrongContact, criteria.ThumbCountsTowardMinimum);
        bool normalSatisfied = normalCount >= criteria.MinFingers;
        bool strongSatisfied = strongCount >= criteria.StrongFingerFloor;
        int acquiredMask = normalSatisfied
            ? masks.FlexedContact
            : strongSatisfied ? masks.StrongContact : 0;
        if ((normalSatisfied || strongSatisfied) && CountNonThumbFingers(acquiredMask) >= 1)
        {
            return new GripAcquisitionVerdict(
                true,
                acquiredMask,
                normalSatisfied ? normalCount : strongCount,
                normalSatisfied ? criteria.MinFingers : criteria.StrongFingerFloor,
                GripEngagementBlock.None);
        }

        GripEngagementBlock block;
        if (masks.Flexed == 0)
        {
            block = GripEngagementBlock.NoFlexedFinger;
        }
        else if (masks.Contact == 0)
        {
            block = GripEngagementBlock.NoContactFinger;
        }
        else
        {
            block = GripEngagementBlock.TooFewFingers;
        }
        return new GripAcquisitionVerdict(
            false,
            0,
            normalCount,
            criteria.MinFingers,
            block);
    }

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

    public static int BuildContactMask(IReadOnlyList<float> boneDistances, float contactRange)
    {
        if (boneDistances == null || boneDistances.Count < RequiredBoneDistanceCount)
        {
            throw new ArgumentException("Hand distances must contain all OpenXR fingertips.", nameof(boneDistances));
        }

        int mask = 0;
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            if (boneDistances[FingertipBoneIndices[finger]] <= contactRange)
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

    public static int CountFingers(int fingerMask, bool includeThumb)
    {
        return CountBits(fingerMask & (includeThumb ? AllFingersMask : NonThumbMask));
    }

    public static int CountNonThumbFingers(int fingerMask)
    {
        return CountBits(fingerMask & NonThumbMask);
    }

    private static int CountBits(int value)
    {
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

    /// <summary>Read-only views of the epoch payload, for shadow evaluation that needs the raw
    /// per-bone evidence (pad coverage) in epoch-coherent form rather than the fingertip masks.</summary>
    public IReadOnlyList<float> SampledCurls => curls;
    public IReadOnlyList<float> SampledBoneDistances => boneDistances;

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
        GripAcquisitionCriteria criteria = new(
            1,
            false,
            1,
            curlThreshold,
            curlThreshold,
            contactRange,
            contactRange);
        return ConsumeMasks(holdId, currentCurls, criteria, now, maximumAgeSeconds).FlexedContact;
    }

    /// <summary>Combines the GPU epoch's distances with the flexion of that same epoch and of the
    /// current frame, so a pose struck after the distances were measured can never acquire.</summary>
    public GripAcquisitionMasks ConsumeMasks(
        int holdId,
        IReadOnlyList<float> currentCurls,
        in GripAcquisitionCriteria criteria,
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
            return default;
        }
        if (float.IsNaN(now) || float.IsInfinity(now) || now < SampledAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Acquisition time must be finite and monotonic.");
        }
        if (HoldId != holdId || now - SampledAt > maximumAgeSeconds)
        {
            Invalidate();
            return default;
        }

        GripAcquisitionMasks sampled = GripAcquisitionMasks.Build(curls, boneDistances, criteria);
        int currentFlexedMask = GripEngagementGate.BuildFlexedMask(currentCurls, criteria.EngageCurl);
        int currentStrongMask = GripEngagementGate.BuildFlexedMask(currentCurls, criteria.StrongCurl);
        Invalidate();
        return new GripAcquisitionMasks(
            sampled.Flexed & currentFlexedMask,
            sampled.Contact,
            sampled.FlexedContact & currentFlexedMask,
            sampled.StrongContact & currentStrongMask);
    }

    /// <summary>
    /// <see cref="ConsumeMasks"/> without the consumption: reads the epoch evidence for shadow
    /// evaluation while leaving the sample exactly as the real gate will find it. A mismatched
    /// hold or an aged-out sample returns nothing and - unlike consuming - does NOT invalidate,
    /// because peeking must never alter what the real acquisition path sees next frame.
    /// </summary>
    public GripAcquisitionMasks PeekMasks(
        int holdId,
        IReadOnlyList<float> currentCurls,
        in GripAcquisitionCriteria criteria,
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
            return default;
        }
        if (float.IsNaN(now) || float.IsInfinity(now) || now < SampledAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Acquisition time must be finite and monotonic.");
        }
        if (HoldId != holdId || now - SampledAt > maximumAgeSeconds)
        {
            return default;
        }

        GripAcquisitionMasks sampled = GripAcquisitionMasks.Build(curls, boneDistances, criteria);
        int currentFlexedMask = GripEngagementGate.BuildFlexedMask(currentCurls, criteria.EngageCurl);
        int currentStrongMask = GripEngagementGate.BuildFlexedMask(currentCurls, criteria.StrongCurl);
        return new GripAcquisitionMasks(
            sampled.Flexed & currentFlexedMask,
            sampled.Contact,
            sampled.FlexedContact & currentFlexedMask,
            sampled.StrongContact & currentStrongMask);
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
    Handoff,
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
            engagedFingerMask = highFlexedContactMask & GripEngagementGate.NonThumbMask;
            // A partial grip that acquired on fewer fingers than the hold's nominal minimum must
            // be released on the fingers it actually caught, or it drops on its own next frame.
            releaseCount = Mathf.Max(
                1,
                Mathf.Min(minFingers, GripEngagementGate.CountNonThumbFingers(engagedFingerMask)));
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

    /// <summary>
    /// Releases the latch immediately for an external cause - the auto-handoff when the other
    /// hand commits to a new hold - producing the same Released transition the evidence-driven
    /// releases produce, so every release flows through one handler. A free latch has nothing
    /// to release and returns the empty transition.
    /// </summary>
    public GripLatchTransition ForceRelease(GripReleaseReason reason)
    {
        if (reason == GripReleaseReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "A forced release needs a cause.");
        }
        return Phase == GripLatchPhase.Free ? default : Release(reason);
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
            GripReleaseReason.Handoff => "handoff",
            _ => string.Empty,
        };
    }
}

/// <summary>Which acquisition mechanism granted a latch. Recorded on every GripLatched event so
/// the analysis can split latches by path without inferring it from the surrounding evidence.</summary>
public enum GripAcquirePath
{
    Curl,
    Coverage,
    Grace,
}

public static class GripAcquirePathExtensions
{
    public static string ToRecorderValue(this GripAcquirePath path)
    {
        return path switch
        {
            GripAcquirePath.Coverage => "coverage",
            GripAcquirePath.Grace => "grace",
            _ => "curl",
        };
    }
}

/// <summary>
/// Names the acquisition-gate configuration a run was recorded under. Stamped into every run
/// manifest so a recording is never analyzed against the wrong gate: v1 is the curl-only gate
/// P1 and P2 ran (2026-08-17/18); v2 adds the coverage and grace paths live (2026-08-19,
/// enabled on Ben's go per the plan of record); v3 adds the auto-handoff release. A partial
/// toggle names the exact combination so a mid-study experiment is visible in the stamp rather
/// than masquerading as a neighboring version.
/// </summary>
public static class GripGateVersionPolicy
{
    public const string CurlOnly = "curl-v1";
    public const string Full = "curl+coverage+grace-v2";
    public const string FullWithHandoff = "curl+coverage+grace+handoff-v3";

    public static string Describe(bool coverageLive, bool graceLive)
    {
        return Describe(coverageLive, graceLive, handoffLive: false);
    }

    public static string Describe(bool coverageLive, bool graceLive, bool handoffLive)
    {
        string paths = coverageLive && graceLive
            ? "curl+coverage+grace"
            : coverageLive
                ? "curl+coverage"
                : graceLive ? "curl+grace" : "curl";
        if (handoffLive)
        {
            return paths + "+handoff-v3";
        }
        return paths + (coverageLive || graceLive ? "-v2" : "-v1");
    }
}

/// <summary>
/// The auto-handoff rule: committing a latch to a NEW route hold releases the other hand's
/// latch, because weight transfer - the thing that releases a trailing hand on a real wall -
/// does not exist in VR, and the flexion release demands an open-hand gesture a flowing
/// climber never makes (P1/P2: median trailing overlap 0.5-1.2 s, 42 handoff moments each).
/// Two carve-outs keep the real bimanual interactions: a match on the SAME hold keeps both
/// latches (the top-out button needs both hands on the finish), and ghost proxies are exempt
/// on either side (two-handed inspection of two ghosts must not evict itself).
/// </summary>
public static class GripHandoffPolicy
{
    public static bool ShouldEvictOtherHand(
        bool enabled,
        int newHoldId,
        bool newIsRouteHold,
        bool otherEngaged,
        int otherHoldId,
        bool otherIsRouteHold)
    {
        if (newHoldId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newHoldId), "A latch always names its hold.");
        }
        if (otherEngaged && otherHoldId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(otherHoldId), "An engaged latch always names its hold.");
        }

        return enabled &&
               otherEngaged &&
               newIsRouteHold &&
               otherIsRouteHold &&
               newHoldId != otherHoldId;
    }
}
