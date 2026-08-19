using System;
using NUnit.Framework;

/// <summary>
/// The shadow acquisition paths log latches they would have granted without granting them, so
/// their evidence rules have to count distinct digits rather than bones, sustain rather than
/// flicker, and read the epoch sample without consuming what the real gate will see.
/// </summary>
public sealed class GripOpenSurfaceShadowTests
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
    public void ThreeBonesOfOneFingerCountAsOneDigit()
    {
        // Index intermediate, distal, and tip all touching: one digit, three pad samples.
        GripOpenSurfaceEvidence evidence = GripOpenSurfacePolicy.Measure(
            BuildDistances((8, 0.01f), (9, 0.01f), (10, 0.01f)),
            new[] { 0f, 0.3f, 0f, 0f, 0f },
            0.015f,
            0.03f);

        Assert.That(evidence.DigitCount, Is.EqualTo(1));
        Assert.That(evidence.DigitContactMask, Is.EqualTo(0b00010));
        Assert.That(evidence.PadSampleCount, Is.EqualTo(3));
        Assert.That(evidence.MaxDigitCurl, Is.EqualTo(0.3f).Within(0.0001f));
        Assert.That(
            GripOpenSurfacePolicy.IsEligible(evidence, 2, 3, 0.1f),
            Is.False,
            "One draped finger must never satisfy a two-digit rule, however many bones touch.");
    }

    [Test]
    public void TwoDrapedDigitsWithPadContactAreEligible()
    {
        // Index and middle pads on the surface, low drag-style curls.
        GripOpenSurfaceEvidence evidence = GripOpenSurfacePolicy.Measure(
            BuildDistances((9, 0.01f), (10, 0.012f), (14, 0.01f), (15, 0.012f)),
            new[] { 0f, 0.28f, 0.25f, 0.05f, 0.05f },
            0.015f,
            0.03f);

        Assert.That(evidence.DigitCount, Is.EqualTo(2));
        Assert.That(evidence.PadSampleCount, Is.EqualTo(4));
        Assert.That(evidence.MaxDigitCurl, Is.EqualTo(0.28f).Within(0.0001f));
        Assert.That(GripOpenSurfacePolicy.IsEligible(evidence, 2, 3, 0.1f), Is.True);
    }

    [Test]
    public void CurlOfANonContactingDigitDoesNotCarryTheFloor()
    {
        // Ring is tightly curled in the air; only index touches. The floor must be judged on
        // digits that are actually on the surface.
        GripOpenSurfaceEvidence evidence = GripOpenSurfacePolicy.Measure(
            BuildDistances((9, 0.01f), (10, 0.01f)),
            new[] { 0f, 0.05f, 0f, 0.9f, 0f },
            0.015f,
            0.03f);

        Assert.That(evidence.MaxDigitCurl, Is.EqualTo(0.05f).Within(0.0001f));
        Assert.That(GripOpenSurfacePolicy.IsEligible(evidence, 1, 2, 0.1f), Is.False);
    }

    [Test]
    public void PalmEvidenceReadsFromThePalmAndWristBones()
    {
        Assert.That(
            GripOpenSurfacePolicy.Measure(
                BuildDistances((GripOpenSurfacePolicy.PalmBoneIndex, 0.02f)),
                new float[5],
                0.015f,
                0.03f).PalmClose,
            Is.True);
        Assert.That(
            GripOpenSurfacePolicy.Measure(
                BuildDistances((GripOpenSurfacePolicy.WristBoneIndex, 0.02f)),
                new float[5],
                0.015f,
                0.03f).PalmClose,
            Is.True);
        Assert.That(
            GripOpenSurfacePolicy.Measure(
                BuildDistances(),
                new float[5],
                0.015f,
                0.03f).PalmClose,
            Is.False);
    }

    [Test]
    public void MeasureAndEligibilityRejectInvalidInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            GripOpenSurfacePolicy.Measure(new float[5], new float[5], 0.015f, 0.03f));
        Assert.Throws<ArgumentException>(() =>
            GripOpenSurfacePolicy.Measure(BuildDistances(), new float[2], 0.015f, 0.03f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripOpenSurfacePolicy.Measure(BuildDistances(), new float[5], 0f, 0.03f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripOpenSurfacePolicy.IsEligible(default, 0, 3, 0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripOpenSurfacePolicy.IsEligible(default, 2, 3, 1.5f));
    }

    [Test]
    public void DwellTrackerFiresOncePerSustainedEpisode()
    {
        GripShadowDwellTracker tracker = new(0.12f, 0.5f);

        Assert.That(tracker.Update(true, 7, 0f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.06f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.12f, out float sustained), Is.True);
        Assert.That(sustained, Is.EqualTo(0.12f).Within(0.0001f));
        Assert.That(tracker.Update(true, 7, 0.3f, out _), Is.False,
            "Continuous eligibility must not refire.");

        // A brief lapse below the refire gap keeps the episode spent.
        Assert.That(tracker.Update(false, 7, 0.35f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.4f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.6f, out _), Is.False);

        // A lapse beyond the refire gap re-arms the same hold.
        Assert.That(tracker.Update(false, 7, 0.7f, out _), Is.False);
        Assert.That(tracker.Update(false, 7, 1.3f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 1.4f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 1.52f, out _), Is.True);
    }

    [Test]
    public void DwellTrackerTreatsANewHoldAsANewEpisode()
    {
        GripShadowDwellTracker tracker = new(0.1f, 10f);

        Assert.That(tracker.Update(true, 7, 0f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.1f, out _), Is.True);
        Assert.That(tracker.Update(true, 8, 0.2f, out _), Is.False,
            "A new hold restarts the dwell rather than inheriting the old one.");
        Assert.That(tracker.Update(true, 8, 0.3f, out _), Is.True);
    }

    [Test]
    public void DwellTrackerIgnoresHoverFlickerThroughNoHold()
    {
        GripShadowDwellTracker tracker = new(0.1f, 5f);

        Assert.That(tracker.Update(true, 7, 0f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.1f, out _), Is.True);
        Assert.That(tracker.Update(false, 0, 0.15f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.2f, out _), Is.False);
        Assert.That(tracker.Update(true, 7, 0.35f, out _), Is.False,
            "Passing through no-hold must not reset the spent episode.");
    }

    [Test]
    public void DwellTrackerDemandsMonotonicTime()
    {
        GripShadowDwellTracker tracker = new(0.1f, 0.5f);
        tracker.Update(true, 7, 5f, out _);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(true, 7, 4f, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GripShadowDwellTracker(float.NaN, 0.5f));
    }

    [Test]
    public void PeekReadsTheSampleWithoutConsumingIt()
    {
        GripAcquisitionSample sample = new();
        GripAcquisitionCriteria criteria = new(3, false, 1, 0.55f, 0.75f, 0.02f, 0.01f);
        float[] curls = { 0.1f, 0.6f, 0.6f, 0.6f, 0.1f };
        float[] distances = BuildDistances((10, 0.015f), (15, 0.015f), (20, 0.015f));
        sample.Publish(42, curls, distances, 1f);

        GripAcquisitionMasks peeked = sample.PeekMasks(42, curls, criteria, 1.05f);
        Assert.That(peeked.FlexedContact, Is.EqualTo(0b01110));
        Assert.That(sample.IsValid, Is.True, "Peeking must leave the sample for the real gate.");

        GripAcquisitionMasks consumed = sample.ConsumeMasks(42, curls, criteria, 1.05f);
        Assert.That(consumed.FlexedContact, Is.EqualTo(peeked.FlexedContact));
        Assert.That(sample.IsValid, Is.False, "Consuming still invalidates.");
    }

    [Test]
    public void PeekReturnsNothingButKeepsTheSampleOnAgeOutOrMismatch()
    {
        GripAcquisitionSample sample = new();
        GripAcquisitionCriteria criteria = new(3, false, 1, 0.55f, 0.75f, 0.02f, 0.01f);
        float[] curls = { 0.1f, 0.6f, 0.6f, 0.6f, 0.1f };
        sample.Publish(42, curls, BuildDistances((10, 0.015f)), 1f);

        Assert.That(sample.PeekMasks(99, curls, criteria, 1.05f).Contact, Is.EqualTo(0));
        Assert.That(sample.IsValid, Is.True, "A mismatched hold must not invalidate on a peek.");
        Assert.That(sample.PeekMasks(42, curls, criteria, 1.2f, 0.1f).Contact, Is.EqualTo(0));
        Assert.That(sample.IsValid, Is.True, "An aged-out peek must not invalidate.");
    }

    [Test]
    public void ShadowDetailsStayInTheNormalizedVocabulary()
    {
        GripOpenSurfaceEvidence evidence = new(0b00110, 4, true, 0.28f);
        Assert.That(
            GripOpenSurfacePolicy.FormatCoverageDetails(
                evidence, 0.15f, 0.12f, new[] { 0.1f, 0.28f, 0.25f, 0.05f, 0f }),
            Is.EqualTo(
                "path=coverage;digits=2;digitMask=6;padSamples=4;palm=true;" +
                "maxCurl=0.28;dwellMs=150;boardSpeed=0.12;curls=(0.10,0.28,0.25,0.05,0.00)"));

        GripAcquisitionMasks masks = new(0b01110, 0b01110, 0b01110, 0b00010);
        Assert.That(
            GripOpenSurfacePolicy.FormatGraceDetails(0.033f, masks, 3, 3, 0b00111),
            Is.EqualTo(
                "path=grace;ageMs=33;confidence=7;flexedContact=14;strongContact=2;" +
                "counted=3;required=3"));
    }

    /// <summary>
    /// The dropout-frame mechanism: the pipeline clears its own acquisition sample on the very
    /// frame a tracking dropout nulls the hand's target, so the grace shadow reads a
    /// coordinator-owned snapshot instead. The snapshot must keep answering after the real
    /// sample is gone.
    /// </summary>
    [Test]
    public void ShadowSnapshotOutlivesTheRealSamplesInvalidation()
    {
        GripAcquisitionSample real = new();
        GripAcquisitionSample shadow = new();
        GripAcquisitionCriteria criteria = new(3, false, 1, 0.55f, 0.75f, 0.02f, 0.01f);
        float[] curls = { 0.1f, 0.6f, 0.6f, 0.6f, 0.1f };
        float[] distances = BuildDistances((10, 0.015f), (15, 0.015f), (20, 0.015f));
        real.Publish(42, curls, distances, 1f);
        shadow.Publish(42, curls, distances, 1f);

        real.Invalidate();

        Assert.That(shadow.IsValid, Is.True);
        Assert.That(
            shadow.PeekMasks(42, curls, criteria, 1.03f, 0.08f).FlexedContact,
            Is.EqualTo(0b01110),
            "The shadow copy is what records a qualified grip that evaporated into a dropout.");
    }
}
