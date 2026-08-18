using System;
using NUnit.Framework;
using UnityEngine;

public sealed class TopOutResetPolicyTests
{
    [Test]
    public void TrackerValidatesItsTimings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TopOutResetTracker(-0.1f, 8f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TopOutResetTracker(float.NaN, 8f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TopOutResetTracker(0.5f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TopOutResetTracker(0.5f, float.PositiveInfinity));
        TopOutResetTracker tracker = new(0.5f, 8f);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(true, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Update(true, -1f));
    }

    [Test]
    public void SustainedTopOutSpawnsTheButtonExactlyOnce()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        Assert.That(tracker.Update(true, 10f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
        Assert.That(tracker.Update(true, 10.4f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
        Assert.That(tracker.Update(true, 10.5f), Is.True);
        Assert.That(tracker.IsButtonVisible, Is.True);
        Assert.That(tracker.Update(true, 11f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.True);
    }

    [Test]
    public void ReleasingBeforeTheHoldWindowNeverArmsAndRestartsTheClock()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        Assert.That(tracker.Update(true, 10f), Is.False);
        Assert.That(tracker.Update(false, 10.3f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
        // The earlier partial hold must not count toward the new one.
        Assert.That(tracker.Update(true, 10.4f), Is.False);
        Assert.That(tracker.Update(true, 10.8f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
        Assert.That(tracker.Update(true, 10.9f), Is.True);
    }

    [Test]
    public void ButtonLingersAfterReleaseAndExpires()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        tracker.Update(true, 10f);
        Assert.That(tracker.Update(true, 10.5f), Is.True);
        Assert.That(tracker.Update(false, 11f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.True, "Button must survive a hand leaving the finish.");
        Assert.That(tracker.Update(false, 18.5f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.True, "Linger runs from the last topped-out frame.");
        Assert.That(tracker.Update(false, 18.6f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
    }

    [Test]
    public void RegrippingTheFinishDuringLingerExtendsTheEpisodeWithoutANewEvent()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        tracker.Update(true, 10f);
        Assert.That(tracker.Update(true, 10.5f), Is.True);
        tracker.Update(false, 12f);
        Assert.That(tracker.Update(true, 14f), Is.False, "Same episode: no second event.");
        Assert.That(tracker.Update(false, 15f), Is.False);
        Assert.That(tracker.Update(false, 21.9f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.True, "Re-grip must extend the linger deadline.");
        Assert.That(tracker.Update(false, 22.1f), Is.False);
        Assert.That(tracker.IsButtonVisible, Is.False);
    }

    [Test]
    public void ANewEpisodeAfterExpiryFiresTheEventAgain()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        tracker.Update(true, 10f);
        Assert.That(tracker.Update(true, 10.5f), Is.True);
        tracker.Update(false, 11f);
        tracker.Update(false, 19.1f);
        Assert.That(tracker.IsButtonVisible, Is.False);
        Assert.That(tracker.Update(true, 20f), Is.False);
        Assert.That(tracker.Update(true, 20.5f), Is.True);
    }

    [Test]
    public void TrackerResetEndsTheEpisodeImmediately()
    {
        TopOutResetTracker tracker = new(0.5f, 8f);
        tracker.Update(true, 10f);
        tracker.Update(true, 10.5f);
        Assert.That(tracker.IsButtonVisible, Is.True);
        tracker.Reset();
        Assert.That(tracker.IsButtonVisible, Is.False);
        Assert.That(tracker.Update(true, 10.6f), Is.False, "Reset must also restart the hold clock.");
        Assert.That(tracker.Update(true, 11.1f), Is.True);
    }

    [Test]
    public void PressTrackerValidatesItsInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TopOutPressTracker(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TopOutPressTracker(float.NaN));
        TopOutPressTracker press = new(0.25f);
        Assert.Throws<ArgumentOutOfRangeException>(() => press.Update(true, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => press.Update(true, -1f));
    }

    [Test]
    public void PressRequiresTheFullDwellAndFiresOncePerContact()
    {
        TopOutPressTracker press = new(0.25f);
        Assert.That(press.Update(true, 10f), Is.False);
        Assert.That(press.Progress01, Is.EqualTo(0f));
        Assert.That(press.Update(true, 10.1f), Is.False);
        Assert.That(press.Progress01, Is.EqualTo(0.4f).Within(0.001f));
        Assert.That(press.Update(true, 10.25f), Is.True);
        // Holding the fingertip in place must not fire again.
        Assert.That(press.Update(true, 10.5f), Is.False);
        Assert.That(press.Update(true, 60f), Is.False);
        // Breaking and re-making contact starts a fresh press.
        Assert.That(press.Update(false, 61f), Is.False);
        Assert.That(press.Update(true, 62f), Is.False);
        Assert.That(press.Update(true, 62.25f), Is.True);
    }

    [Test]
    public void BrushingThroughTheButtonDoesNotPress()
    {
        TopOutPressTracker press = new(0.25f);
        Assert.That(press.Update(true, 10f), Is.False);
        Assert.That(press.Update(false, 10.1f), Is.False);
        Assert.That(press.Progress01, Is.EqualTo(0f));
        Assert.That(press.Update(true, 10.2f), Is.False);
        Assert.That(press.Update(true, 10.44f), Is.False);
        Assert.That(press.Update(true, 10.45f), Is.True);
    }

    [Test]
    public void ZeroDwellPressesOnFirstContact()
    {
        TopOutPressTracker press = new(0f);
        Assert.That(press.Update(true, 10f), Is.True);
        Assert.That(press.Update(true, 10.1f), Is.False);
        Assert.That(press.Update(false, 10.2f), Is.False);
        Assert.That(press.Update(true, 10.3f), Is.True);
    }

    [Test]
    public void WallMountedButtonSitsOnTheClimberFaceAboveTheFinish()
    {
        // Axis-aligned upper wall, the participant's head out on the +Z side.
        Pose pose = TopOutResetPolicy.GetWallMountedButtonPose(
            new Vector3(0f, 3f, -0.4f),
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0.08f),
            new Vector3(0.5f, 2.4f, -0.1f),
            new Vector3(0.5f, 2f, 2.5f),
            0.3f,
            0.012f,
            0.15f,
            0.06f);
        Assert.That(
            Vector3.Distance(pose.position, new Vector3(0.5f, 2.7f, -0.348f)),
            Is.LessThan(0.0001f));
        // Facing out of the wall: local +Z into the wall, so the label side looks at the climber.
        Assert.That(
            Vector3.Distance(pose.rotation * Vector3.forward, new Vector3(0f, 0f, -1f)),
            Is.LessThan(0.0001f));
        Assert.That(
            Vector3.Distance(pose.rotation * Vector3.up, Vector3.up),
            Is.LessThan(0.0001f));
    }

    [Test]
    public void WallMountedButtonClampsInsideTheWallPanel()
    {
        Pose pose = TopOutResetPolicy.GetWallMountedButtonPose(
            new Vector3(0f, 3f, -0.4f),
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0.08f),
            new Vector3(5f, 4.5f, -0.1f),
            new Vector3(0f, 3f, 2f),
            0.3f,
            0.012f,
            0.15f,
            0.06f);
        // Lateral limit 1.22 - 0.15 - 0.02; height limit 0.8 - 0.06 - 0.02.
        Assert.That(
            Vector3.Distance(pose.position, new Vector3(1.05f, 3.72f, -0.348f)),
            Is.LessThan(0.0001f));
    }

    [Test]
    public void WallMountedButtonFollowsTheViewerSideAndTheWallFrame()
    {
        // A viewer on the -Z side flips the mounting face; the finish hold near the seam plane
        // must not decide it (that ambiguity is what once mirrored the label).
        Pose flipped = TopOutResetPolicy.GetWallMountedButtonPose(
            new Vector3(0f, 3f, -0.4f),
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0.08f),
            new Vector3(0f, 2.4f, -0.1f),
            new Vector3(0f, 2.4f, -0.9f),
            0.3f,
            0.012f,
            0.15f,
            0.06f);
        Assert.That(flipped.position.z, Is.EqualTo(-0.452f).Within(0.0001f));
        Assert.That(
            Vector3.Distance(flipped.rotation * Vector3.forward, Vector3.forward),
            Is.LessThan(0.0001f));

        // A yawed wall: all offsets must follow the wall's own axes, not the world's.
        Pose yawed = TopOutResetPolicy.GetWallMountedButtonPose(
            Vector3.zero,
            Quaternion.Euler(0f, 90f, 0f),
            new Vector3(2f, 2f, 0.1f),
            new Vector3(0.5f, -0.2f, 0.3f),
            new Vector3(2f, 0f, 0f),
            0.3f,
            0.012f,
            0.15f,
            0.06f);
        Assert.That(
            Vector3.Distance(yawed.position, new Vector3(0.062f, 0.1f, 0.3f)),
            Is.LessThan(0.0001f));
        Assert.That(
            Vector3.Distance(yawed.rotation * Vector3.forward, new Vector3(-1f, 0f, 0f)),
            Is.LessThan(0.0001f));
    }

    [Test]
    public void WallMountedButtonValidatesItsInputs()
    {
        Assert.Throws<ArgumentException>(() => TopOutResetPolicy.GetWallMountedButtonPose(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0f),
            Vector3.one,
            Vector3.forward,
            0.3f,
            0.012f,
            0.15f,
            0.06f));
        Assert.Throws<ArgumentException>(
            () => TopOutResetPolicy.GetWallMountedButtonPose(
                Vector3.zero,
                Quaternion.identity,
                new Vector3(2.44f, 1.6f, 0.08f),
                Vector3.forward,
                Vector3.right * 0.5f,
                0.3f,
                0.012f,
                0.15f,
                0.06f),
            "A viewer on the wall plane has no readable face.");
        Assert.Throws<ArgumentOutOfRangeException>(() => TopOutResetPolicy.GetWallMountedButtonPose(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0.08f),
            Vector3.forward,
            Vector3.forward,
            float.NaN,
            0.012f,
            0.15f,
            0.06f));
        Assert.Throws<ArgumentOutOfRangeException>(() => TopOutResetPolicy.GetWallMountedButtonPose(
            Vector3.zero,
            Quaternion.identity,
            new Vector3(2.44f, 1.6f, 0.08f),
            Vector3.forward,
            Vector3.forward,
            0.3f,
            0.012f,
            0f,
            0.06f));
    }

    [Test]
    public void FingertipEngagesOnlyWithinTheButtonFaceSlab()
    {
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0f, 0f, -0.02f), 0.15f, 0.06f, 0.05f, 0f),
            Is.True);
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0.14f, 0.05f, -0.049f), 0.15f, 0.06f, 0.05f, 0f),
            Is.True);
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0f, 0f, -0.06f), 0.15f, 0.06f, 0.05f, 0f),
            Is.False,
            "A fingertip hovering further out than the press depth is not touching.");
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0f, 0f, 0.019f), 0.15f, 0.06f, 0.05f, 0f),
            Is.True,
            "A fingertip slightly inside the face still counts (tracking noise).");
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0f, 0f, 0.03f), 0.15f, 0.06f, 0.05f, 0f),
            Is.False,
            "A fingertip well behind the wall does not press.");
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0.2f, 0f, -0.02f), 0.15f, 0.06f, 0.05f, 0f),
            Is.False);
        Assert.That(
            TopOutResetPolicy.IsFingertipOnButton(
                new Vector3(0.2f, 0f, -0.02f), 0.15f, 0.06f, 0.05f, 0.06f),
            Is.True,
            "The hover query pads the face rectangle.");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TopOutResetPolicy.IsFingertipOnButton(Vector3.zero, 0f, 0.06f, 0.05f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TopOutResetPolicy.IsFingertipOnButton(Vector3.zero, 0.15f, 0.06f, 0.05f, -0.01f));
    }

    [Test]
    public void HoldCoordinateNormalizesSceneHoldNames()
    {
        Assert.That(TopOutResetPolicy.GetHoldCoordinate("F16.002"), Is.EqualTo("F16"));
        Assert.That(TopOutResetPolicy.GetHoldCoordinate("f16.001"), Is.EqualTo("F16"));
        Assert.That(TopOutResetPolicy.GetHoldCoordinate("A5"), Is.EqualTo("A5"));
        Assert.Throws<ArgumentException>(() => TopOutResetPolicy.GetHoldCoordinate(" "));
    }

    [Test]
    public void SummonDwellProgressReportsTheArmedAndDwellingStates()
    {
        Assert.That(
            StudyRehearsalTiming.ComputeSummonDwellProgress(true, -1f, 5f, 0.6f),
            Is.EqualTo(1f));
        Assert.That(
            StudyRehearsalTiming.ComputeSummonDwellProgress(false, -1f, 5f, 0.6f),
            Is.EqualTo(0f));
        Assert.That(
            StudyRehearsalTiming.ComputeSummonDwellProgress(false, 5f, 5.3f, 0.6f),
            Is.EqualTo(0.5f).Within(0.001f));
        Assert.That(
            StudyRehearsalTiming.ComputeSummonDwellProgress(false, 5f, 9f, 0.6f),
            Is.EqualTo(1f));
        Assert.That(
            StudyRehearsalTiming.ComputeSummonDwellProgress(false, 5f, 4.9f, 0.6f),
            Is.EqualTo(0f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StudyRehearsalTiming.ComputeSummonDwellProgress(false, 5f, float.NaN, 0.6f));
    }
}
