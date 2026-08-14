using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class GhostViewingStandoffTests
{
    [Test]
    public void ReachStandoffLeavesTheTopOfTheGridBehindTheParticipantsEye()
    {
        (float topHeight, float topLocalZ) = GetRowPose(18);
        (float bottomHeight, float bottomLocalZ) = GetRowPose(1);

        // The 40-degree face has overhung 2.13 m by row 18, so at the 1.35 m reach standoff the
        // top of the board is behind the eye and cannot be looked at, let alone pointed at. This
        // is the whole reason detached inspection needs a standoff of its own.
        Assert.That(
            BoardStandoffPolicy.DefaultBoardBaseDistanceMeters + topLocalZ,
            Is.LessThan(0f));
        Assert.Throws<ArgumentException>(() =>
            GhostViewingStandoffPolicy.GetSubtendedVerticalAngleDegrees(
                BoardStandoffPolicy.DefaultBoardBaseDistanceMeters,
                topHeight,
                topLocalZ,
                bottomHeight,
                bottomLocalZ,
                GhostViewingStandoffPolicy.StandingEyeHeightMeters));
    }

    [Test]
    public void ShippedExtraStandoffPutsTheWholeGridInsideTheComfortableViewingAngle()
    {
        (float topHeight, float topLocalZ) = GetRowPose(18);
        (float bottomHeight, float bottomLocalZ) = GetRowPose(1);

        float derived = GhostViewingStandoffPolicy.GetExtraStandoffMeters(
            BoardStandoffPolicy.DefaultBoardBaseDistanceMeters,
            topHeight,
            topLocalZ,
            bottomHeight,
            bottomLocalZ,
            GhostViewingStandoffPolicy.StandingEyeHeightMeters,
            GhostViewingStandoffPolicy.ComfortableVerticalFieldOfViewDegrees);

        // The shipped constant is that derivation carried outward, and rounding outward may only
        // ever help, so the grid it produces must still fit the comfortable angle.
        Assert.That(derived, Is.EqualTo(1.93f).Within(0.02f));
        Assert.That(GhostViewingStandoffPolicy.DefaultExtraStandoffMeters, Is.GreaterThanOrEqualTo(derived));

        // Detached inspection is derived on an absolute distance, so the extra has to absorb every
        // change to the shared reach standoff rather than ride along with it.
        Assert.That(
            BoardStandoffPolicy.DefaultBoardBaseDistanceMeters +
            GhostViewingStandoffPolicy.DefaultExtraStandoffMeters,
            Is.EqualTo(3.3f).Within(0.005f));

        float shippedAngle = GhostViewingStandoffPolicy.GetSubtendedVerticalAngleDegrees(
            BoardStandoffPolicy.DefaultBoardBaseDistanceMeters +
            GhostViewingStandoffPolicy.DefaultExtraStandoffMeters,
            topHeight,
            topLocalZ,
            bottomHeight,
            bottomLocalZ,
            GhostViewingStandoffPolicy.StandingEyeHeightMeters);
        Assert.That(
            shippedAngle,
            Is.LessThanOrEqualTo(GhostViewingStandoffPolicy.ComfortableVerticalFieldOfViewDegrees));
        Assert.That(shippedAngle, Is.GreaterThan(55f));
    }

    [Test]
    public void EveryGridRowStaysWellAheadOfTheEyeAtTheGhostStandoff()
    {
        float standoff = BoardStandoffPolicy.DefaultBoardBaseDistanceMeters +
                         GhostViewingStandoffPolicy.DefaultExtraStandoffMeters;
        for (int row = 1; row <= 18; row++)
        {
            (float _, float localZ) = GetRowPose(row);
            Assert.That(standoff + localZ, Is.GreaterThan(1f), "row " + row);
        }
    }

    [Test]
    public void SubtendedAngleFallsAsTheBoardRetreats()
    {
        (float topHeight, float topLocalZ) = GetRowPose(18);
        (float bottomHeight, float bottomLocalZ) = GetRowPose(1);

        float previous = float.PositiveInfinity;
        for (float standoff = 2.6f; standoff <= 6f; standoff += 0.2f)
        {
            float angle = GhostViewingStandoffPolicy.GetSubtendedVerticalAngleDegrees(
                standoff,
                topHeight,
                topLocalZ,
                bottomHeight,
                bottomLocalZ,
                GhostViewingStandoffPolicy.StandingEyeHeightMeters);
            Assert.That(angle, Is.LessThan(previous));
            previous = angle;
        }
    }

    [Test]
    public void RetreatDirectionIsHorizontalAndFollowsTheCalibratedHeading()
    {
        Assert.That(
            GhostViewingStandoffPolicy.GetRetreatDirection(Quaternion.identity),
            Is.EqualTo(Vector3.forward).Using(VectorComparer));

        Vector3 turned = GhostViewingStandoffPolicy.GetRetreatDirection(Quaternion.Euler(0f, 90f, 0f));
        Assert.That(turned, Is.EqualTo(Vector3.right).Using(VectorComparer));

        // A board pitched by calibration still retreats along the floor, never into it.
        Vector3 pitched = GhostViewingStandoffPolicy.GetRetreatDirection(Quaternion.Euler(25f, 40f, 0f));
        Assert.That(pitched.y, Is.EqualTo(0f).Within(1e-5f));
        Assert.That(pitched.magnitude, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void RetreatDirectionRejectsABoardFacingStraightUp()
    {
        Assert.Throws<ArgumentException>(() =>
            GhostViewingStandoffPolicy.GetRetreatDirection(Quaternion.Euler(-90f, 0f, 0f)));
    }

    [Test]
    public void ExtraStandoffClampsIntoTheAdmissibleRange()
    {
        Assert.That(GhostViewingStandoffPolicy.ClampExtraStandoffMeters(-3f), Is.EqualTo(0f));
        Assert.That(
            GhostViewingStandoffPolicy.ClampExtraStandoffMeters(99f),
            Is.EqualTo(GhostViewingStandoffPolicy.MaximumExtraStandoffMeters));
        Assert.That(GhostViewingStandoffPolicy.ClampExtraStandoffMeters(1.2f), Is.EqualTo(1.2f));
        Assert.That(
            GhostViewingStandoffPolicy.ClampExtraStandoffMeters(
                GhostViewingStandoffPolicy.DefaultExtraStandoffMeters),
            Is.EqualTo(GhostViewingStandoffPolicy.DefaultExtraStandoffMeters));
        Assert.Throws<ArgumentException>(
            () => GhostViewingStandoffPolicy.ClampExtraStandoffMeters(float.NaN));
    }

    [Test]
    public void ViewingGeometryRejectsAnEyeOutsideTheGrid()
    {
        Assert.Throws<ArgumentException>(() =>
            GhostViewingStandoffPolicy.GetViewingDistanceMeters(2.9f, -2.1f, 0.3f, 0.05f, 3.5f, 70f));
        Assert.Throws<ArgumentException>(() =>
            GhostViewingStandoffPolicy.GetViewingDistanceMeters(0.3f, 0.05f, 2.9f, -2.1f, 1.62f, 70f));
        Assert.Throws<ArgumentException>(() =>
            GhostViewingStandoffPolicy.GetViewingDistanceMeters(2.9f, -2.1f, 0.3f, 0.05f, 1.62f, 0f));
    }

    private static (float heightMeters, float localZ) GetRowPose(int row)
    {
        Vector3 local = LoadCatalog().GetBoardLocalPosition("A" + row);
        return (local.y, local.z);
    }

    private static MoonBoardStudyCatalog LoadCatalog()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        Assert.That(
            MoonBoardStudyCatalog.TryParse(
                File.ReadAllText(path),
                out MoonBoardStudyCatalog catalog,
                out string error),
            Is.True,
            error);
        return catalog;
    }

    private static readonly UnityEngine.TestTools.Utils.Vector3EqualityComparer VectorComparer =
        new UnityEngine.TestTools.Utils.Vector3EqualityComparer(1e-4f);
}

public sealed class GhostRegistryPolicyTests
{
    [Test]
    public void NothingIsEvictedWhileTheRegistryHasRoom()
    {
        Assert.That(
            GhostRegistryPolicy.SelectEvictionIndex(
                new[] { 1f, 2f },
                new[] { false, false },
                4),
            Is.EqualTo(-1));
    }

    [Test]
    public void TheOldestProxyGivesWayOnceTheRegistryIsFull()
    {
        Assert.That(
            GhostRegistryPolicy.SelectEvictionIndex(
                new[] { 9f, 3f, 7f, 5f },
                new[] { false, false, false, false },
                4),
            Is.EqualTo(1));
    }

    [Test]
    public void AProxyInAParticipantsHandIsNotPulledOutOfIt()
    {
        // The oldest is held, so the oldest free one goes instead.
        Assert.That(
            GhostRegistryPolicy.SelectEvictionIndex(
                new[] { 1f, 2f, 3f, 4f },
                new[] { true, false, false, false },
                4),
            Is.EqualTo(1));
    }

    [Test]
    public void EveryProxyHeldFallsBackToTheOldestOverall()
    {
        Assert.That(
            GhostRegistryPolicy.SelectEvictionIndex(
                new[] { 6f, 2f },
                new[] { true, true },
                2),
            Is.EqualTo(1));
    }

    [Test]
    public void TheCapNeverExceedsTheHoldsALockedRouteCanCarry()
    {
        Assert.That(GhostRegistryPolicy.ClampMaximumLiveGhosts(0), Is.EqualTo(1));
        Assert.That(GhostRegistryPolicy.ClampMaximumLiveGhosts(99),
            Is.EqualTo(GhostRegistryPolicy.MaximumLiveGhostCeiling));
        Assert.That(GhostRegistryPolicy.MaximumLiveGhostCeiling, Is.EqualTo(8));
        Assert.That(
            GhostRegistryPolicy.ClampMaximumLiveGhosts(GhostRegistryPolicy.DefaultMaximumLiveGhosts),
            Is.EqualTo(GhostRegistryPolicy.DefaultMaximumLiveGhosts));
    }

    [Test]
    public void EvictionRejectsMismatchedOrUnusableRegistries()
    {
        Assert.Throws<ArgumentNullException>(
            () => GhostRegistryPolicy.SelectEvictionIndex(null, new[] { false }, 2));
        Assert.Throws<ArgumentException>(
            () => GhostRegistryPolicy.SelectEvictionIndex(new[] { 1f }, new[] { false, true }, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GhostRegistryPolicy.SelectEvictionIndex(new[] { 1f }, new[] { false }, 0));
        Assert.Throws<ArgumentException>(
            () => GhostRegistryPolicy.SelectEvictionIndex(
                new[] { float.NaN },
                new[] { false },
                1));
    }
}

public sealed class GhostOrientationIndicatorTests
{
    [Test]
    public void DeviationMeasuresTheTurnAwayFromTheWallOrientation()
    {
        Quaternion wall = Quaternion.Euler(12f, 40f, 7f);
        Assert.That(
            GhostOrientationIndicatorPolicy.GetDeviationDegrees(wall, wall),
            Is.EqualTo(0f).Within(1e-3f));
        Assert.That(
            GhostOrientationIndicatorPolicy.GetDeviationDegrees(
                Quaternion.AngleAxis(90f, Vector3.up) * wall,
                wall),
            Is.EqualTo(90f).Within(1e-3f));
        Assert.That(
            GhostOrientationIndicatorPolicy.GetDeviationDegrees(
                Quaternion.AngleAxis(200f, Vector3.right) * wall,
                wall),
            Is.EqualTo(160f).Within(1e-3f));
    }

    [Test]
    public void TheArcCarriesOneDegreeOfSweepPerDegreeOfError()
    {
        Assert.That(GhostOrientationIndicatorPolicy.GetArcSweepDegrees(0f), Is.EqualTo(0f));
        Assert.That(GhostOrientationIndicatorPolicy.GetArcSweepDegrees(43f), Is.EqualTo(43f));
        Assert.That(GhostOrientationIndicatorPolicy.GetArcSweepDegrees(180f), Is.EqualTo(180f));
    }

    [Test]
    public void AlignmentFractionAndToleranceBracketTheTurn()
    {
        Assert.That(GhostOrientationIndicatorPolicy.GetAlignmentFraction(0f), Is.EqualTo(1f));
        Assert.That(GhostOrientationIndicatorPolicy.GetAlignmentFraction(180f), Is.EqualTo(0f));
        Assert.That(GhostOrientationIndicatorPolicy.GetAlignmentFraction(90f), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(GhostOrientationIndicatorPolicy.IsAligned(0f), Is.True);
        Assert.That(
            GhostOrientationIndicatorPolicy.IsAligned(
                GhostOrientationIndicatorPolicy.AlignmentToleranceDegrees),
            Is.True);
        Assert.That(
            GhostOrientationIndicatorPolicy.IsAligned(
                GhostOrientationIndicatorPolicy.AlignmentToleranceDegrees + 0.1f),
            Is.False);
    }

    [Test]
    public void TheIndicatorBrightensTowardsTrueAndNeverBorrowsTheGripOrRouteRamps()
    {
        Color far = GhostOrientationIndicatorPolicy.GetIndicatorColor(180f);
        Color near = GhostOrientationIndicatorPolicy.GetIndicatorColor(0f);
        Assert.That(far, Is.EqualTo(GhostOrientationIndicatorPolicy.MisalignedColor)
            .Using(UnityEngine.TestTools.Utils.ColorEqualityComparer.Instance));
        Assert.That(near, Is.EqualTo(GhostOrientationIndicatorPolicy.AlignedColor)
            .Using(UnityEngine.TestTools.Utils.ColorEqualityComparer.Instance));
        Assert.That(Brightness(near), Is.GreaterThan(Brightness(far)));

        // Grip quality already owns red-amber-green on these same objects and the route roles own
        // green/blue/red on the wall behind them. A second cue sharing either ramp would be
        // unreadable, so keep the indicator measurably clear of every one of them.
        Color[] taken =
        {
            GripAffordancePolicy.LowQualityColor,
            GripAffordancePolicy.MediumQualityColor,
            GripAffordancePolicy.HighQualityColor,
            RouteCuePolicy.StartColor,
            RouteCuePolicy.IntermediateColor,
            RouteCuePolicy.FinishColor,
        };
        foreach (Color reserved in taken)
        {
            Assert.That(HueDistance(far, reserved), Is.GreaterThan(0.08f));
        }
    }

    [Test]
    public void TheReadoutIsWholeDegreesInInvariantForm()
    {
        Assert.That(GhostOrientationIndicatorPolicy.FormatDeviationDegrees(0f), Is.EqualTo("0°"));
        Assert.That(GhostOrientationIndicatorPolicy.FormatDeviationDegrees(12.4f), Is.EqualTo("12°"));
        Assert.That(GhostOrientationIndicatorPolicy.FormatDeviationDegrees(179.6f), Is.EqualTo("180°"));
    }

    [Test]
    public void DeviationOutsideZeroToOneEightyIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GhostOrientationIndicatorPolicy.GetArcSweepDegrees(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GhostOrientationIndicatorPolicy.GetArcSweepDegrees(181f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GhostOrientationIndicatorPolicy.GetAlignmentFraction(float.NaN));
    }

    private static float Brightness(Color color)
    {
        return color.r + color.g + color.b;
    }

    private static float HueDistance(Color left, Color right)
    {
        Color.RGBToHSV(left, out float leftHue, out float leftSaturation, out _);
        Color.RGBToHSV(right, out float rightHue, out float rightSaturation, out _);
        float hueGap = Mathf.Abs(leftHue - rightHue);
        return Mathf.Min(hueGap, 1f - hueGap) + Mathf.Abs(leftSaturation - rightSaturation) * 0.25f;
    }
}

public sealed class PointerStabilisationTests
{
    [Test]
    public void TheFirstSampleIsPassedStraightThrough()
    {
        PointerOneEuroFilter filter = new(
            PointerOneEuroFilter.DefaultMinimumCutoffHertz,
            PointerOneEuroFilter.DefaultSpeedCoefficient,
            PointerOneEuroFilter.DefaultDerivativeCutoffHertz);
        Assert.That(filter.HasSample, Is.False);
        Vector3 seeded = filter.Update(new Vector3(1f, 2f, 3f), 10f);
        Assert.That(seeded, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        Assert.That(filter.HasSample, Is.True);
    }

    [Test]
    public void RestingTremorIsAttenuated()
    {
        PointerOneEuroFilter filter = new(
            PointerOneEuroFilter.DefaultMinimumCutoffHertz,
            PointerOneEuroFilter.DefaultSpeedCoefficient,
            PointerOneEuroFilter.DefaultDerivativeCutoffHertz);
        Vector3 center = new(0f, 0f, 1f);
        float time = 0f;
        float rawDeviation = 0f;
        float filteredDeviation = 0f;
        for (int frame = 0; frame < 120; frame++)
        {
            time += 1f / 72f;
            Vector3 jitter = new(frame % 2 == 0 ? 0.01f : -0.01f, 0f, 0f);
            Vector3 filtered = filter.Update(center + jitter, time);
            if (frame > 20)
            {
                rawDeviation += jitter.magnitude;
                filteredDeviation += (filtered - center).magnitude;
            }
        }

        Assert.That(filteredDeviation, Is.LessThan(rawDeviation * 0.5f));
    }

    [Test]
    public void ADeliberateSweepIsTrackedWithBoundedLag()
    {
        PointerOneEuroFilter filter = new(
            PointerOneEuroFilter.DefaultMinimumCutoffHertz,
            PointerOneEuroFilter.DefaultSpeedCoefficient,
            PointerOneEuroFilter.DefaultDerivativeCutoffHertz);
        float time = 0f;
        Vector3 filtered = Vector3.zero;
        Vector3 raw = Vector3.zero;
        for (int frame = 0; frame < 72; frame++)
        {
            time += 1f / 72f;
            raw = new Vector3(frame * 0.01f, 0f, 1f);
            filtered = filter.Update(raw, time);
        }

        // A metre per second sweep must not trail by more than a couple of centimetres, which is
        // what the speed term buys over a fixed low pass.
        Assert.That(Vector3.Distance(filtered, raw), Is.LessThan(0.03f));
    }

    [Test]
    public void ALongGapReseedsRatherThanExtrapolating()
    {
        PointerOneEuroFilter filter = new(
            PointerOneEuroFilter.DefaultMinimumCutoffHertz,
            PointerOneEuroFilter.DefaultSpeedCoefficient,
            PointerOneEuroFilter.DefaultDerivativeCutoffHertz);
        filter.Update(Vector3.zero, 0f);
        Vector3 afterGap = filter.Update(
            new Vector3(5f, 0f, 0f),
            PointerOneEuroFilter.MaximumSampleIntervalSeconds * 2f);
        Assert.That(afterGap, Is.EqualTo(new Vector3(5f, 0f, 0f)));
    }

    [Test]
    public void FilterConstructionRejectsUnusableTuning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerOneEuroFilter(0f, 0.25f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerOneEuroFilter(1.6f, -1f, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerOneEuroFilter(1.6f, 0.25f, 0f));
        Assert.Throws<ArgumentException>(
            () => new PointerOneEuroFilter(1.6f, 4f, 1f).Update(
                new Vector3(float.NaN, 0f, 0f),
                0f));
    }
}

public sealed class HandRayTargetingTests
{
    [Test]
    public void AngleMeasuresHowDirectlyTheRayPointsAtACandidate()
    {
        Assert.That(
            HandRayTargeting.GetAcquisitionAngleDegrees(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, 3f)),
            Is.EqualTo(0f).Within(1e-3f));
        Assert.That(
            HandRayTargeting.GetAcquisitionAngleDegrees(Vector3.zero, Vector3.forward, new Vector3(1f, 0f, 1f)),
            Is.EqualTo(45f).Within(1e-3f));
    }

    [Test]
    public void CandidatesBehindThePointerAreNotTargets()
    {
        Assert.That(
            HandRayTargeting.GetAcquisitionAngleDegrees(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, -2f)),
            Is.EqualTo(HandRayTargeting.NoTarget));
        Assert.That(
            HandRayTargeting.GetAcquisitionAngleDegrees(Vector3.zero, Vector3.forward, Vector3.zero),
            Is.EqualTo(HandRayTargeting.NoTarget));
    }

    [Test]
    public void ACandidatesOwnSizeWidensTheConeItCanBeAcquiredThrough()
    {
        float near = HandRayTargeting.GetAngularRadiusDegrees(Vector3.zero, new Vector3(0f, 0f, 1f), 0.05f);
        float far = HandRayTargeting.GetAngularRadiusDegrees(Vector3.zero, new Vector3(0f, 0f, 4f), 0.05f);
        Assert.That(near, Is.GreaterThan(far));
        Assert.That(near, Is.EqualTo(Mathf.Rad2Deg * Mathf.Asin(0.05f)).Within(1e-3f));
        Assert.That(
            HandRayTargeting.GetAngularRadiusDegrees(Vector3.zero, new Vector3(0f, 0f, 0.01f), 0.05f),
            Is.EqualTo(90f));
    }

    [Test]
    public void TheNearestCandidateInsideTheConeIsAcquired()
    {
        Assert.That(
            HandRayTargeting.SelectStickyTarget(-1, new[] { 3.5f, 1.2f, 2.0f }, 4f, 7f, 1.25f),
            Is.EqualTo(1));
        Assert.That(
            HandRayTargeting.SelectStickyTarget(-1, new[] { 9f, 8f }, 4f, 7f, 1.25f),
            Is.EqualTo(-1));
    }

    [Test]
    public void AnAcquiredHoldSurvivesTremorThatWouldFlipANearestAngleRule()
    {
        // The incumbent has drifted to 5 degrees, outside the acquisition cone but inside the
        // release cone, and a rival now reads marginally closer. A bare nearest rule would swap;
        // the margin keeps the choice still.
        Assert.That(
            HandRayTargeting.SelectStickyTarget(0, new[] { 5f, 3.9f }, 4f, 7f, 1.25f),
            Is.EqualTo(0));
        Assert.That(
            HandRayTargeting.SelectStickyTarget(0, new[] { 5f, 3.5f }, 4f, 7f, 1.25f),
            Is.EqualTo(1));
    }

    [Test]
    public void AnIncumbentIsDroppedOnceItLeavesTheReleaseCone()
    {
        Assert.That(
            HandRayTargeting.SelectStickyTarget(0, new[] { 7.5f, 12f }, 4f, 7f, 1.25f),
            Is.EqualTo(-1));
        Assert.That(
            HandRayTargeting.SelectStickyTarget(0, new[] { 7.5f, 3f }, 4f, 7f, 1.25f),
            Is.EqualTo(1));
        Assert.That(
            HandRayTargeting.SelectStickyTarget(
                0,
                new[] { HandRayTargeting.NoTarget, 3f },
                4f,
                7f,
                1.25f),
            Is.EqualTo(1));
    }

    [Test]
    public void AStaleIncumbentIndexIsToleratedWhenTheCandidateListShrinks()
    {
        Assert.That(
            HandRayTargeting.SelectStickyTarget(9, new[] { 2f, 6f }, 4f, 7f, 1.25f),
            Is.EqualTo(0));
    }

    [Test]
    public void TargetingRejectsUnusableConesAndCandidates()
    {
        Assert.Throws<ArgumentException>(() =>
            HandRayTargeting.GetAcquisitionAngleDegrees(Vector3.zero, Vector3.zero, Vector3.forward));
        Assert.Throws<ArgumentNullException>(
            () => HandRayTargeting.SelectStickyTarget(-1, null, 4f, 7f, 1.25f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HandRayTargeting.SelectStickyTarget(-1, new[] { 1f }, 0f, 7f, 1.25f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HandRayTargeting.SelectStickyTarget(-1, new[] { 1f }, 7f, 4f, 1.25f));
        Assert.Throws<ArgumentException>(
            () => HandRayTargeting.SelectStickyTarget(-1, new[] { float.NaN }, 4f, 7f, 1.25f));
    }
}

public sealed class PinchLatchTests
{
    [Test]
    public void APinchAlreadyClosedWhenTheTriggerGoesLiveDoesNotFire()
    {
        PinchLatch latch = new(PinchLatch.DefaultPressStrength, PinchLatch.DefaultReleaseStrength);

        Assert.That(latch.Update(true, 1f, true), Is.False);
        Assert.That(latch.Update(true, 1f, true), Is.False);
        Assert.That(latch.Update(true, 0f, false), Is.False);
        Assert.That(latch.Update(true, 1f, true), Is.True);
    }

    [Test]
    public void HysteresisStopsTheTriggerChatteringNearTheThreshold()
    {
        PinchLatch latch = new(0.7f, 0.35f);
        latch.Update(true, 0f, false);

        Assert.That(latch.Update(true, 0.72f, false), Is.True);
        // Half-open fingers hold the latch closed rather than re-firing it every other frame.
        Assert.That(latch.Update(true, 0.5f, false), Is.False);
        Assert.That(latch.IsClosed, Is.True);
        Assert.That(latch.Update(true, 0.72f, false), Is.False);
        Assert.That(latch.Update(true, 0.2f, false), Is.False);
        Assert.That(latch.IsClosed, Is.False);
        Assert.That(latch.Update(true, 0.72f, false), Is.True);
    }

    [Test]
    public void LosingTrackingDisarmsUntilAFreshOpenHandIsSeen()
    {
        PinchLatch latch = new(0.7f, 0.35f);
        latch.Update(true, 0f, false);
        Assert.That(latch.IsArmed, Is.True);

        Assert.That(latch.Update(false, 1f, true), Is.False);
        Assert.That(latch.IsArmed, Is.False);
        Assert.That(latch.Update(true, 1f, true), Is.False);
        Assert.That(latch.Update(true, 0f, false), Is.False);
        Assert.That(latch.Update(true, 1f, true), Is.True);
    }

    [Test]
    public void AnOpenHandKeepsItsArmingAcrossASuppressionEpisode()
    {
        // Item 1 regression. The console's summon dwell asserts and releases input suppression
        // while the participant is only turning their palm, and the ghost technique advances its
        // latch through those frames while discarding the presses. A hand that stayed open must
        // therefore come out of the episode still armed, so the very next pinch selects instead of
        // needing a release-and-re-pinch that a participant has no way to guess at.
        PinchLatch latch = new(0.7f, 0.35f);
        latch.Update(true, 0f, false);

        for (int suppressedFrame = 0; suppressedFrame < 30; suppressedFrame++)
        {
            bool pressed = latch.Update(true, 0.05f, false);
            Assert.That(pressed, Is.False);
        }

        Assert.That(latch.IsArmed, Is.True);
        Assert.That(latch.Update(true, 1f, true), Is.True);
    }

    [Test]
    public void APinchHeldThroughSuppressionCannotLeakOutOfIt()
    {
        PinchLatch latch = new(0.7f, 0.35f);
        latch.Update(true, 0f, false);

        // Pressed while the console owned input: the press is discarded by the caller, and the
        // latch has spent its arming, so releasing is required before the technique acts.
        Assert.That(latch.Update(true, 1f, true), Is.True);
        Assert.That(latch.Update(true, 1f, true), Is.False);
        Assert.That(latch.IsArmed, Is.False);
        Assert.That(latch.Update(true, 0f, false), Is.False);
        Assert.That(latch.Update(true, 1f, true), Is.True);
    }

    [Test]
    public void LatchConstructionRejectsInvertedThresholds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchLatch(0f, 0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchLatch(1.5f, 0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchLatch(0.5f, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchLatch(0.5f, 0.9f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PinchLatch(0.7f, 0.35f).Update(true, float.NaN, false));
    }
}

public sealed class GhostInspectionSeamTests
{
    [Test]
    public void TheControllerExposesTheMultiGhostRegistrySeams()
    {
        Type controller = FindLoadedType("GhostHoldController");

        Assert.That(controller.GetProperty("HasGhosts"), Is.Not.Null);
        Assert.That(controller.GetProperty("LiveGhostCount"), Is.Not.Null);
        Assert.That(controller.GetMethod("GetGhostRoot"), Is.Not.Null);
        Assert.That(controller.GetMethod("GetWallReferent"), Is.Not.Null);
        Assert.That(controller.GetMethod("CollectGhostRoots"), Is.Not.Null);
        Assert.That(controller.GetMethod("DismissAllGhosts"), Is.Not.Null);

        // The session controllers predate multi-ghost inspection and still ask whether anything is
        // detached through these two, so they have to keep answering.
        Assert.That(controller.GetProperty("CurrentGhost"), Is.Not.Null);
        Assert.That(controller.GetProperty("WallReferent"), Is.Not.Null);
        Assert.That(
            controller.GetMethod("DismissGhost", Type.EmptyTypes),
            Is.Not.Null,
            "SceneConfiguror.ResetManualStudyState still calls the no-argument dismiss.");
    }

    [Test]
    public void TheGhostViewingStandoffIsOwnedByTheModeSeamAndIsSerialized()
    {
        Type configuror = FindLoadedType("SceneConfiguror");
        const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        FieldInfo standoff = configuror.GetField(
            "ghostViewingExtraStandoffMeters",
            NonPublicInstance);
        Assert.That(standoff, Is.Not.Null);
        Assert.That(standoff.FieldType, Is.EqualTo(typeof(float)));
        Assert.That(
            standoff.GetCustomAttribute<SerializeField>(),
            Is.Not.Null,
            "The viewing standoff must stay tunable from the inspector.");

        Assert.That(configuror.GetMethod("ApplyGhostViewingStandoff", NonPublicInstance), Is.Not.Null);
        Assert.That(configuror.GetMethod("RemoveGhostViewingStandoff", NonPublicInstance), Is.Not.Null);
        Assert.That(configuror.GetProperty("BoardAlignmentRoot", NonPublicInstance), Is.Not.Null);
    }

    [Test]
    public void TheGhostStandoffMovesTheBoardAndRestoresTheAlignedPoseExactly()
    {
        GameObject alignmentRoot = new("GhostStandoffAlignmentRoot");
        try
        {
            GameObject board = new("Moonboard");
            board.transform.SetParent(alignmentRoot.transform, false);

            // A calibrated board: turned off the world axes and seated at the reach standoff.
            Quaternion aligned = Quaternion.Euler(0f, 27f, 0f);
            Vector3 alignedPosition = new(0.3f, 0f, BoardStandoffPolicy.DefaultBoardBaseDistanceMeters);
            alignmentRoot.transform.SetPositionAndRotation(alignedPosition, aligned);

            Vector3 retreat = GhostViewingStandoffPolicy.GetRetreatDirection(aligned);
            Vector3 retreated = alignedPosition +
                                retreat * GhostViewingStandoffPolicy.DefaultExtraStandoffMeters;
            alignmentRoot.transform.position = retreated;

            // The retreat is purely horizontal, is taken along the calibrated heading, and gives
            // back exactly the aligned pose when the technique ends.
            Assert.That(alignmentRoot.transform.position.y, Is.EqualTo(alignedPosition.y).Within(1e-4f));
            Assert.That(
                Vector3.Distance(retreated, alignedPosition),
                Is.EqualTo(GhostViewingStandoffPolicy.DefaultExtraStandoffMeters).Within(1e-4f));
            Assert.That(
                Vector3.Dot(retreat, aligned * Vector3.forward),
                Is.GreaterThan(0.99f));

            alignmentRoot.transform.position = retreated -
                retreat * GhostViewingStandoffPolicy.DefaultExtraStandoffMeters;
            Assert.That(
                Vector3.Distance(alignmentRoot.transform.position, alignedPosition),
                Is.LessThan(1e-4f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(alignmentRoot);
        }
    }

    private static Type FindLoadedType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name))
            .Single(type => type != null);
    }
}
