using System;
using NUnit.Framework;

/// <summary>
/// Gate v2: the coverage and grace rules as LIVE acquisition paths. These tests pin the pieces
/// the flip added - the gate-version stamp, the sustain-side contact mask, the pocket-bound
/// digit requirement - and document, executably, how crimp-family grips interact with the
/// union of the curl and coverage paths.
/// </summary>
public sealed class GripLiveAcquisitionTests
{
    private static float[] BuildDistances(params (int bone, float meters)[] contacts)
    {
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        for (int index = 0; index < distances.Length; index++)
        {
            distances[index] = 1f;
        }
        foreach ((int bone, float meters) contact in contacts)
        {
            distances[contact.bone] = contact.meters;
        }
        return distances;
    }

    [Test]
    public void GateVersionNamesEveryConfiguration()
    {
        Assert.That(GripGateVersionPolicy.Describe(true, true), Is.EqualTo("curl+coverage+grace-v2"));
        Assert.That(GripGateVersionPolicy.Describe(false, false), Is.EqualTo("curl-v1"));
        Assert.That(GripGateVersionPolicy.Describe(true, false), Is.EqualTo("curl+coverage-v2"));
        Assert.That(GripGateVersionPolicy.Describe(false, true), Is.EqualTo("curl+grace-v2"));
        Assert.That(GripGateVersionPolicy.Full, Is.EqualTo(GripGateVersionPolicy.Describe(true, true)));
        Assert.That(GripGateVersionPolicy.CurlOnly, Is.EqualTo(GripGateVersionPolicy.Describe(false, false)));
    }

    [Test]
    public void AcquirePathRecorderValuesStayInTheNormalizedVocabulary()
    {
        Assert.That(GripAcquirePath.Curl.ToRecorderValue(), Is.EqualTo("curl"));
        Assert.That(GripAcquirePath.Coverage.ToRecorderValue(), Is.EqualTo("coverage"));
        Assert.That(GripAcquirePath.Grace.ToRecorderValue(), Is.EqualTo("grace"));
    }

    [Test]
    public void SustainMaskCountsDigitsWithinTheReleaseRange()
    {
        // Index tip and middle intermediate inside 2.5 cm; ring just outside.
        int mask = GripOpenSurfacePolicy.MeasureDigitContactMask(
            BuildDistances((10, 0.02f), (13, 0.024f), (18, 0.026f)),
            0.025f);
        Assert.That(mask, Is.EqualTo(0b00110));

        Assert.That(
            GripOpenSurfacePolicy.MeasureDigitContactMask(BuildDistances(), 0.025f),
            Is.EqualTo(0));
        Assert.Throws<ArgumentException>(
            () => GripOpenSurfacePolicy.MeasureDigitContactMask(new float[4], 0.025f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripOpenSurfacePolicy.MeasureDigitContactMask(BuildDistances(), 0f));
    }

    [Test]
    public void ReleaseRangeIsWiderThanAcquisitionSoContactFlickerCannotPumpReleases()
    {
        // A digit hovering at 2 cm no longer satisfies acquisition (1.5 cm) but still sustains
        // (2.5 cm): the hysteresis band that keeps a held drag from strobing at the boundary.
        float[] distances = BuildDistances((10, 0.02f));
        GripOpenSurfaceEvidence acquire = GripOpenSurfacePolicy.Measure(
            distances, new[] { 0f, 0.3f, 0f, 0f, 0f }, 0.015f, 0.03f);
        Assert.That(acquire.DigitCount, Is.EqualTo(0));
        Assert.That(
            GripOpenSurfacePolicy.MeasureDigitContactMask(distances, 0.025f),
            Is.EqualTo(0b00010));
    }

    [Test]
    public void PocketMinimumRaisesTheCoverageDigitRequirement()
    {
        Assert.That(GripOpenSurfacePolicy.RequiredCoverageDigits(2, 2), Is.EqualTo(2));
        Assert.That(GripOpenSurfacePolicy.RequiredCoverageDigits(2, 3), Is.EqualTo(3),
            "A pocket's spec-08 finger minimum must bind the coverage path exactly as it binds curl.");
        Assert.That(GripOpenSurfacePolicy.RequiredCoverageDigits(2, 1), Is.EqualTo(2),
            "Geometry never lowers the coverage requirement below its own minimum.");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripOpenSurfacePolicy.RequiredCoverageDigits(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripOpenSurfacePolicy.RequiredCoverageDigits(2, 0));
    }

    [Test]
    public void FullCrimpOnTipsAloneSatisfiesCoverage()
    {
        // Four fingertips at a flexed angle, pads off the surface - a crimp rack. Tip bones
        // count as contact, and a crimped digit clears the 0.1 curl floor by a wide margin, so
        // the rule reads four digits and four samples: crimps satisfy coverage even though the
        // path exists for the open styles curl cannot see.
        GripOpenSurfaceEvidence evidence = GripOpenSurfacePolicy.Measure(
            BuildDistances((10, 0.008f), (15, 0.008f), (20, 0.009f), (25, 0.01f)),
            new[] { 0.2f, 0.85f, 0.85f, 0.8f, 0.75f },
            0.015f,
            0.03f);

        Assert.That(evidence.DigitCount, Is.EqualTo(4));
        Assert.That(evidence.PadSampleCount, Is.EqualTo(4));
        Assert.That(evidence.MaxDigitCurl, Is.EqualTo(0.85f).Within(0.0001f));
        Assert.That(GripOpenSurfacePolicy.IsEligible(evidence, 2, 3, 0.1f), Is.True);
    }

    [Test]
    public void TwoFingerHalfCrimpFailsCoverageButLatchesThroughTheCurlPath()
    {
        // Only index and middle tips touch, at half-crimp flexion: two digits and two samples
        // miss the coverage floor of three, and that is fine - the union latches it through the
        // curl path, whose flexed-contact rule was built for exactly this shape.
        float[] distances = BuildDistances((10, 0.008f), (15, 0.008f));
        float[] curls = { 0.2f, 0.79f, 0.79f, 0.1f, 0.1f };

        GripOpenSurfaceEvidence evidence = GripOpenSurfacePolicy.Measure(
            distances, curls, 0.015f, 0.03f);
        Assert.That(GripOpenSurfacePolicy.IsEligible(evidence, 2, 3, 0.1f), Is.False);

        int flexedContact = GripEngagementGate.BuildFlexedContactMask(curls, distances, 0.55f, 0.015f);
        Assert.That(GripEngagementGate.CountNonThumbFingers(flexedContact), Is.EqualTo(2));
        Assert.That(GripEngagementGate.CanAcquire(true, 2, flexedContact), Is.True);
    }

    [Test]
    public void CoverageLatchedDragSustainsOnContactAndReleasesWhenContactCollapses()
    {
        // Acquire the way the live coverage path does: two draped digits, hold minimum two.
        // The hand's curls sit below every flexion threshold for the entire grip, so without
        // the contact-sustain mask this latch would release open_hand on its first update.
        GripLatchStateMachine latch = new(releaseGraceSeconds: 0.15f);
        const int digitMask = 0b00110;
        GripLatchTransition acquired = latch.Update(
            now: 0f,
            trackingValid: true,
            insideAcquisitionVolume: true,
            candidateHoldId: 7,
            minFingers: 2,
            highFlexedContactMask: digitMask,
            lowFlexedMask: 0);
        Assert.That(acquired.Kind, Is.EqualTo(GripLatchTransitionKind.Latched));

        for (float now = 0.1f; now < 1f; now += 0.1f)
        {
            GripLatchTransition held = latch.Update(
                now, true, false, 0, 2,
                highFlexedContactMask: 0,
                lowFlexedMask: digitMask);
            Assert.That(held.Kind, Is.Not.EqualTo(GripLatchTransitionKind.Released),
                "Contact-sustained digits must carry a low-curl grip.");
        }

        // One digit peels off: below the release count, released after the grace window.
        Assert.That(
            latch.Update(1.0f, true, false, 0, 2, 0, 0b00010).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        GripLatchTransition released = latch.Update(1.2f, true, false, 0, 2, 0, 0b00010);
        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void GateVersionNamesTheHandoffConfigurations()
    {
        Assert.That(GripGateVersionPolicy.Describe(true, true, true),
            Is.EqualTo("curl+coverage+grace+handoff-v3"));
        Assert.That(GripGateVersionPolicy.FullWithHandoff,
            Is.EqualTo(GripGateVersionPolicy.Describe(true, true, true)));
        Assert.That(GripGateVersionPolicy.Describe(false, false, true), Is.EqualTo("curl+handoff-v3"));
        Assert.That(GripGateVersionPolicy.Describe(true, false, true), Is.EqualTo("curl+coverage+handoff-v3"));
        Assert.That(GripGateVersionPolicy.Describe(true, true, false),
            Is.EqualTo(GripGateVersionPolicy.Full),
            "The two-argument overload's meaning must not drift when handoff is off.");
    }

    [Test]
    public void HandoffEvictsOnlyADifferentRouteHold()
    {
        // The flow case: new latch on route hold 2 while the other hand holds route hold 1.
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(true, 2, true, true, 1, true), Is.True);
        // Matching the same hold keeps both latches - the top-out button depends on it.
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(true, 1, true, true, 1, true), Is.False);
        // Ghost proxies are exempt on either side: two-handed ghost inspection must survive.
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(true, 2, false, true, 1, true), Is.False);
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(true, 2, true, true, 1, false), Is.False);
        // Nothing to evict, or the feature is off.
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(true, 2, true, false, 0, false), Is.False);
        Assert.That(GripHandoffPolicy.ShouldEvictOtherHand(false, 2, true, true, 1, true), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripHandoffPolicy.ShouldEvictOtherHand(true, 0, true, true, 1, true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripHandoffPolicy.ShouldEvictOtherHand(true, 2, true, true, 0, true));
    }

    [Test]
    public void ForceReleaseFreesTheLatchWithItsCauseAndAllowsReacquisition()
    {
        GripLatchStateMachine latch = new();
        Assert.That(latch.ForceRelease(GripReleaseReason.Handoff).Kind,
            Is.EqualTo(GripLatchTransitionKind.None),
            "A free latch has nothing to release.");

        Assert.That(latch.Update(0f, true, true, 7, 2, 0b00110, 0).Kind,
            Is.EqualTo(GripLatchTransitionKind.Latched));
        GripLatchTransition released = latch.ForceRelease(GripReleaseReason.Handoff);
        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.Handoff));
        Assert.That(released.HoldId, Is.EqualTo(7));
        Assert.That(latch.IsEngaged, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => latch.ForceRelease(GripReleaseReason.None));

        // The freed hand can immediately latch the next hold - the handoff rhythm.
        Assert.That(latch.Update(0.1f, true, true, 9, 2, 0b01100, 0).Kind,
            Is.EqualTo(GripLatchTransitionKind.Latched));
        Assert.That(GripReleaseReason.Handoff.ToRecorderValue(), Is.EqualTo("handoff"));
    }

    [Test]
    public void GraceAcquiredLatchRidesTheFreezeMachineryThroughTheDropout()
    {
        // The grace path acquires with tracking reported valid for that single update (the
        // qualifying epoch was measured while confidence was high), then the ongoing dropout
        // freezes the latch and tracking's return resumes it - no new machinery, by design.
        GripLatchStateMachine latch = new(trackingFreezeSeconds: 0.25f);
        GripLatchTransition acquired = latch.Update(0f, true, true, 7, 2, 0b00110, 0);
        Assert.That(acquired.Kind, Is.EqualTo(GripLatchTransitionKind.Latched));

        Assert.That(latch.Update(0.1f, false, false, 0, 2, 0, 0).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(latch.Update(0.4f, false, false, 0, 2, 0, 0).Kind,
            Is.EqualTo(GripLatchTransitionKind.Frozen));
        GripLatchTransition resumed = latch.Update(0.5f, true, false, 0, 2, 0, 0b00110);
        Assert.That(resumed.Kind, Is.EqualTo(GripLatchTransitionKind.Resumed));
    }
}
