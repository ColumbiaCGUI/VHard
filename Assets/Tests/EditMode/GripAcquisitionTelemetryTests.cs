using System;
using NUnit.Framework;

/// <summary>
/// The acquisition-state snapshot exists to separate "the hand was open" from "the tracker could
/// not see the fingers" in recorded sessions, so its change detection has to fire on exactly the
/// slow-moving signals and its details string has to stay in the uniformly normalized key=value
/// vocabulary the events pipeline parses.
/// </summary>
public sealed class GripAcquisitionTelemetryTests
{
    [Test]
    public void StateKeyChangesWithEachSlowMovingSignal()
    {
        int baseline = GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, 0b11011, true);

        Assert.That(
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, 0b11011, true),
            Is.EqualTo(baseline),
            "Identical signals must produce an identical key.");
        Assert.That(
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Latched, true, 0b11011, true),
            Is.Not.EqualTo(baseline));
        Assert.That(
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, false, 0b11011, true),
            Is.Not.EqualTo(baseline));
        Assert.That(
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, 0b01011, true),
            Is.Not.EqualTo(baseline));
        Assert.That(
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, 0b11011, false),
            Is.Not.EqualTo(baseline));
    }

    [Test]
    public void StateKeyRejectsAnOutOfRangeConfidenceMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, -1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripAcquisitionTelemetry.BuildStateKey(GripLatchPhase.Free, true, 0b100000, true));
    }

    [Test]
    public void RecordingRequiresBothAChangedKeyAndTheMinimumInterval()
    {
        // The first evaluated state of a recording always emits: the sentinel key differs and the
        // last-recorded time sits at negative infinity.
        Assert.That(
            GripAcquisitionTelemetry.ShouldRecord(
                7, GripAcquisitionTelemetry.NoStateKey, 0f, float.NegativeInfinity),
            Is.True);
        Assert.That(GripAcquisitionTelemetry.ShouldRecord(7, 7, 10f, 0f), Is.False);
        Assert.That(GripAcquisitionTelemetry.ShouldRecord(8, 7, 0.05f, 0f), Is.False);
        Assert.That(GripAcquisitionTelemetry.ShouldRecord(8, 7, 0.1f, 0f), Is.True);
    }

    [Test]
    public void RecordingGateRejectsInvalidTimes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripAcquisitionTelemetry.ShouldRecord(1, 0, float.NaN, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripAcquisitionTelemetry.ShouldRecord(1, 0, 0f, 0f, -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GripAcquisitionTelemetry.ShouldRecord(1, 0, 0f, 0f, float.PositiveInfinity));
    }

    [Test]
    public void DetailsCarryEveryClauseInTheNormalizedVocabulary()
    {
        GripAcquisitionMasks masks = new(0b00110, 0b01110, 0b00110, 0b00000);
        string details = GripAcquisitionTelemetry.FormatDetails(
            GripLatchPhase.Free,
            GripEngagementBlock.TooFewFingers,
            true,
            0b11011,
            masks,
            1,
            3,
            new[] { 0.1f, 0.6f, 0.55f, 0.3f, 0.2f });

        Assert.That(details, Is.EqualTo(
            "phase=Free;block=TooFewFingers;trackingValid=true;confidence=27;" +
            "flexed=6;contact=14;flexedContact=6;counted=1;required=3;" +
            "curls=(0.10,0.60,0.55,0.30,0.20)"));
    }

    [Test]
    public void DetailsRejectShortCurlArrays()
    {
        Assert.Throws<ArgumentException>(() =>
            GripAcquisitionTelemetry.FormatDetails(
                GripLatchPhase.Free,
                GripEngagementBlock.None,
                true,
                0,
                default,
                0,
                3,
                new[] { 0.1f, 0.2f }));
    }

    [Test]
    public void ConfidenceMaskPacksThumbIntoBitZero()
    {
        Assert.That(
            GripAcquisitionTelemetry.BuildConfidenceMask(
                new[] { true, false, true, false, true }),
            Is.EqualTo(0b10101));
        Assert.That(
            GripAcquisitionTelemetry.BuildConfidenceMask(
                new[] { false, false, false, false, false }),
            Is.EqualTo(0));
        Assert.Throws<ArgumentException>(() =>
            GripAcquisitionTelemetry.BuildConfidenceMask(new[] { true, false }));
    }
}
