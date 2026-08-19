using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Contact-coverage evidence for open-hand grips (drags, slopers, palms). The curl gate
/// structurally excludes these styles - a drag reads about 0.28 curl against the 0.55 engage
/// threshold - and hold geometry cannot predict who will use them, so this path is evaluated on
/// EVERY hold from what the hand actually does: several distinct digits with pad or tip bones on
/// the surface, held there briefly with the hand quiet relative to the board. The path shipped
/// as SHADOW (log-only) during P1/P2; since gate v2 (2026-08-19, Ben's go per the plan of
/// record) the same rule and the same frozen values are the LIVE acquisition path, with the
/// shadow evaluators retained only for whichever path is toggled back off. Crimps and half
/// crimps also satisfy this rule - tip bones count as contact and a flexed digit clears the
/// 0.1 curl floor by a wide margin - but in practice they latch first through the curl path;
/// coverage exists for the styles curl cannot see.
/// </summary>
public readonly struct GripOpenSurfaceEvidence
{
    public GripOpenSurfaceEvidence(int digitContactMask, int padSampleCount, bool palmClose, float maxDigitCurl)
    {
        DigitContactMask = digitContactMask;
        PadSampleCount = padSampleCount;
        PalmClose = palmClose;
        MaxDigitCurl = maxDigitCurl;
    }

    /// <summary>Non-thumb digits with any pad or tip bone in contact, bit = finger index.</summary>
    public int DigitContactMask { get; }

    /// <summary>Contacting pad and tip bones summed across those digits.</summary>
    public int PadSampleCount { get; }

    /// <summary>Palm or wrist bone within the palm range. Telemetry only: drags need no palm.</summary>
    public bool PalmClose { get; }

    /// <summary>Largest curl among the contacting digits; stale values pass through verbatim.</summary>
    public float MaxDigitCurl { get; }

    public int DigitCount => GripEngagementGate.CountNonThumbFingers(DigitContactMask);
}

public static class GripOpenSurfacePolicy
{
    /// <summary>Recorder action for a shadow path's would-have-latched row.</summary>
    public const string ShadowLatchAction = "GripShadowLatch";

    /// <summary>Intermediate, distal, and tip bones per non-thumb finger (OpenXR skeleton):
    /// the surfaces a draped or dragging finger actually loads. The thumb never counts, matching
    /// the engagement gate's rule that every acquisition needs non-thumb evidence.</summary>
    private static readonly int[][] DigitPadBones =
    {
        new[] { 8, 9, 10 },
        new[] { 13, 14, 15 },
        new[] { 18, 19, 20 },
        new[] { 23, 24, 25 },
    };

    public const int PalmBoneIndex = 0;
    public const int WristBoneIndex = 1;
    private const int FirstNonThumbFinger = 1;

    public static GripOpenSurfaceEvidence Measure(
        IReadOnlyList<float> boneDistances,
        IReadOnlyList<float> curls,
        float contactRangeMeters,
        float palmRangeMeters)
    {
        if (boneDistances == null || boneDistances.Count < GripEngagementGate.RequiredBoneDistanceCount)
        {
            throw new ArgumentException(
                "Coverage distances must contain all OpenXR hand bones.",
                nameof(boneDistances));
        }
        if (curls == null || curls.Count < FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentException("Coverage curls must contain five values.", nameof(curls));
        }
        if (contactRangeMeters <= 0f || float.IsNaN(contactRangeMeters) ||
            palmRangeMeters <= 0f || float.IsNaN(palmRangeMeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contactRangeMeters),
                "Coverage contact ranges must be positive.");
        }

        int digitMask = 0;
        int padSamples = 0;
        float maxDigitCurl = 0f;
        for (int digit = 0; digit < DigitPadBones.Length; digit++)
        {
            int contactingBones = 0;
            foreach (int bone in DigitPadBones[digit])
            {
                if (boneDistances[bone] <= contactRangeMeters)
                {
                    contactingBones++;
                }
            }
            if (contactingBones == 0)
            {
                continue;
            }
            int finger = FirstNonThumbFinger + digit;
            digitMask |= 1 << finger;
            padSamples += contactingBones;
            if (curls[finger] > maxDigitCurl)
            {
                maxDigitCurl = curls[finger];
            }
        }

        bool palmClose = boneDistances[PalmBoneIndex] <= palmRangeMeters ||
                         boneDistances[WristBoneIndex] <= palmRangeMeters;
        return new GripOpenSurfaceEvidence(digitMask, padSamples, palmClose, maxDigitCurl);
    }

    /// <summary>
    /// Digit contact mask alone (bit = finger index), without the curl or palm reads: the
    /// evidence that SUSTAINS a live coverage-latched grip each frame. Kept lean because it
    /// runs while latched; the acquisition-side Measure carries the full evidence.
    /// </summary>
    public static int MeasureDigitContactMask(
        IReadOnlyList<float> boneDistances,
        float contactRangeMeters)
    {
        if (boneDistances == null || boneDistances.Count < GripEngagementGate.RequiredBoneDistanceCount)
        {
            throw new ArgumentException(
                "Coverage distances must contain all OpenXR hand bones.",
                nameof(boneDistances));
        }
        if (contactRangeMeters <= 0f || float.IsNaN(contactRangeMeters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contactRangeMeters),
                "Coverage contact ranges must be positive.");
        }

        int digitMask = 0;
        for (int digit = 0; digit < DigitPadBones.Length; digit++)
        {
            int[] bones = DigitPadBones[digit];
            for (int index = 0; index < bones.Length; index++)
            {
                if (boneDistances[bones[index]] <= contactRangeMeters)
                {
                    digitMask |= 1 << (FirstNonThumbFinger + digit);
                    break;
                }
            }
        }
        return digitMask;
    }

    /// <summary>
    /// Digits the live coverage path demands on one hold: the global coverage minimum, raised
    /// to the hold's own finger minimum so a pocket's spec-08 constraint binds this path exactly
    /// as it binds the curl path. Geometry never lowers the requirement.
    /// </summary>
    public static int RequiredCoverageDigits(int coverageMinimumDigits, int holdMinFingers)
    {
        if (coverageMinimumDigits < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coverageMinimumDigits),
                "Coverage minimums must be at least one.");
        }
        GripEngagementGate.ValidateMinFingers(holdMinFingers);
        return Math.Max(coverageMinimumDigits, holdMinFingers);
    }

    /// <summary>
    /// The coverage rule: distinct digits carry it, never raw bones - one finger's three bones
    /// must not read as three digits - plus a curl floor low enough for a drag but above a fully
    /// slack hand hanging in space.
    /// </summary>
    public static bool IsEligible(
        in GripOpenSurfaceEvidence evidence,
        int minimumDigits,
        int minimumPadSamples,
        float curlFloor)
    {
        if (minimumDigits < 1 || minimumPadSamples < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDigits),
                "Coverage minimums must be at least one.");
        }
        if (curlFloor < 0f || curlFloor > 1f || float.IsNaN(curlFloor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(curlFloor),
                "Coverage curl floor must lie within [0, 1].");
        }

        return evidence.DigitCount >= minimumDigits &&
               evidence.PadSampleCount >= minimumPadSamples &&
               evidence.MaxDigitCurl >= curlFloor;
    }

    public static string FormatCoverageDetails(
        in GripOpenSurfaceEvidence evidence,
        float dwellSeconds,
        float boardSpeedMetersPerSecond,
        IReadOnlyList<float> epochCurls)
    {
        return "path=coverage" +
               ";digits=" + evidence.DigitCount.ToString(CultureInfo.InvariantCulture) +
               ";digitMask=" + evidence.DigitContactMask.ToString(CultureInfo.InvariantCulture) +
               ";padSamples=" + evidence.PadSampleCount.ToString(CultureInfo.InvariantCulture) +
               ";palm=" + (evidence.PalmClose ? "true" : "false") +
               ";maxCurl=" + evidence.MaxDigitCurl.ToString("F2", CultureInfo.InvariantCulture) +
               ";dwellMs=" + ((int)Math.Round(dwellSeconds * 1000f)).ToString(CultureInfo.InvariantCulture) +
               ";boardSpeed=" + boardSpeedMetersPerSecond.ToString("F2", CultureInfo.InvariantCulture) +
               ";curls=" + GripAcquisitionTelemetry.FormatCurlList(epochCurls);
    }

    public static string FormatGraceDetails(
        float sampleAgeSeconds,
        in GripAcquisitionMasks masks,
        int countedFingers,
        int requiredFingers,
        int publishConfidenceMask)
    {
        return "path=grace" +
               ";ageMs=" + ((int)Math.Round(sampleAgeSeconds * 1000f)).ToString(CultureInfo.InvariantCulture) +
               ";confidence=" + publishConfidenceMask.ToString(CultureInfo.InvariantCulture) +
               ";flexedContact=" + masks.FlexedContact.ToString(CultureInfo.InvariantCulture) +
               ";strongContact=" + masks.StrongContact.ToString(CultureInfo.InvariantCulture) +
               ";counted=" + countedFingers.ToString(CultureInfo.InvariantCulture) +
               ";required=" + requiredFingers.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Sustains-and-fires gate for one hand's shadow path: eligible continuously for the dwell on
/// one hold fires exactly once, so a would-latch episode produces one row rather than a stream.
/// A hold change re-arms immediately; a lapse in eligibility re-arms only after the refire gap,
/// so evidence flickering at a threshold cannot multiply an episode into many.
/// </summary>
public sealed class GripShadowDwellTracker
{
    private readonly float dwellSeconds;
    private readonly float refireSeconds;
    private int holdId;
    private float eligibleSince = float.NaN;
    private float lastEligibleAt = float.NaN;
    private float lastUpdateAt = float.NaN;
    private bool firedThisEpisode;

    public GripShadowDwellTracker(float dwellSeconds, float refireSeconds)
    {
        if (dwellSeconds < 0f || float.IsNaN(dwellSeconds) || float.IsInfinity(dwellSeconds) ||
            refireSeconds < 0f || float.IsNaN(refireSeconds) || float.IsInfinity(refireSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dwellSeconds),
                "Shadow dwell and refire gaps must be finite and non-negative.");
        }
        this.dwellSeconds = dwellSeconds;
        this.refireSeconds = refireSeconds;
    }

    /// <summary>True exactly when the sustained-evidence threshold is crossed for this episode.
    /// <paramref name="sustainedSeconds"/> reports how long eligibility has been continuous.</summary>
    public bool Update(bool eligible, int currentHoldId, float now, out float sustainedSeconds)
    {
        if (float.IsNaN(now) || float.IsInfinity(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Shadow time must be finite.");
        }
        if (!float.IsNaN(lastUpdateAt) && now < lastUpdateAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Shadow time must be monotonic.");
        }
        lastUpdateAt = now;

        // A change between two real holds is a new episode. A transition through "no hold" is
        // not: hover flicker at a hold's edge must not bypass the refire gap.
        if (currentHoldId != 0 && holdId != 0 && currentHoldId != holdId)
        {
            eligibleSince = float.NaN;
            firedThisEpisode = false;
        }
        if (currentHoldId != 0)
        {
            holdId = currentHoldId;
        }

        if (!eligible)
        {
            eligibleSince = float.NaN;
            if (firedThisEpisode && !float.IsNaN(lastEligibleAt) &&
                now - lastEligibleAt > refireSeconds)
            {
                firedThisEpisode = false;
            }
            sustainedSeconds = 0f;
            return false;
        }

        lastEligibleAt = now;
        if (float.IsNaN(eligibleSince))
        {
            eligibleSince = now;
        }
        sustainedSeconds = now - eligibleSince;
        if (firedThisEpisode || sustainedSeconds < dwellSeconds)
        {
            return false;
        }

        firedThisEpisode = true;
        return true;
    }

    public void Reset()
    {
        holdId = 0;
        eligibleSince = float.NaN;
        lastEligibleAt = float.NaN;
        lastUpdateAt = float.NaN;
        firedThisEpisode = false;
    }
}
