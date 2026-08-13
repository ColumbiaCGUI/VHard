using System;
using System.Text;
using NUnit.Framework;
using UnityEngine;

public sealed class GripPartialEngagementTests
{
    private const float EngageCurl = 0.55f;
    private const float StrongCurl = 0.75f;
    private const float ContactRange = 0.02f;
    private const float StrongContactRange = 0.01f;

    private static GripAcquisitionCriteria Criteria(int minFingers = 3, bool thumbCounts = false)
    {
        return new GripAcquisitionCriteria(
            minFingers,
            thumbCounts,
            1,
            EngageCurl,
            StrongCurl,
            ContactRange,
            StrongContactRange);
    }

    private static GripAcquisitionMasks Grip(int fingerCount, float curl, float tipDistance)
    {
        float[] curls = new float[FingerCurlEstimator.FingerCount];
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        Array.Fill(distances, float.PositiveInfinity);
        for (int finger = 1; finger <= fingerCount; finger++)
        {
            curls[finger] = curl;
            distances[GripEngagementGate.GetFingertipBoneIndex(finger)] = tipDistance;
        }
        return GripAcquisitionMasks.Build(curls, distances, Criteria());
    }

    [TestCase(4, true)]
    [TestCase(3, true)]
    [TestCase(2, true)]
    [TestCase(1, true)]
    public void FirmGripsEngageDownToASingleFingerAgainstAThreeFingerHold(
        int fingerCount,
        bool expected)
    {
        GripAcquisitionCriteria criteria = Criteria();

        GripAcquisitionVerdict verdict = GripEngagementGate.Evaluate(
            criteria,
            Grip(fingerCount, 0.9f, 0.004f));

        Assert.That(verdict.CanAcquire, Is.EqualTo(expected));
        Assert.That(
            GripEngagementGate.CountNonThumbFingers(verdict.AcquiredMask),
            Is.EqualTo(fingerCount));
    }

    [Test]
    public void ARelaxedHandBrushingAHoldNeverEngagesOnTheStrongPath()
    {
        GripAcquisitionCriteria criteria = Criteria();

        GripAcquisitionVerdict verdict = GripEngagementGate.Evaluate(
            criteria,
            Grip(2, 0.6f, 0.004f));

        Assert.That(verdict.CanAcquire, Is.False,
            "Two fingers below the strong flexion must not satisfy a three-finger hold.");
        Assert.That(verdict.Block, Is.EqualTo(GripEngagementBlock.TooFewFingers));
        Assert.That(verdict.CountedFingers, Is.EqualTo(2));
        Assert.That(verdict.RequiredFingers, Is.EqualTo(3));
    }

    [Test]
    public void StrongFlexionStillNeedsTheTighterContactRange()
    {
        GripAcquisitionCriteria criteria = Criteria();

        Assert.That(
            GripEngagementGate.Evaluate(criteria, Grip(1, 0.9f, 0.015f)).CanAcquire,
            Is.False,
            "A deeply curled finger hovering outside the strong contact range must not latch.");
        Assert.That(
            GripEngagementGate.Evaluate(criteria, Grip(1, 0.9f, 0.009f)).CanAcquire,
            Is.True);
    }

    [Test]
    public void EveryEngagementKeepsANonThumbFingerToReleaseOn()
    {
        GripAcquisitionCriteria criteria = Criteria(minFingers: 1, thumbCounts: true);
        float[] curls = new float[FingerCurlEstimator.FingerCount];
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        Array.Fill(distances, float.PositiveInfinity);
        curls[FingerCurlEstimator.ThumbFinger] = 0.95f;
        distances[GripEngagementGate.GetFingertipBoneIndex(FingerCurlEstimator.ThumbFinger)] = 0.004f;

        GripAcquisitionVerdict verdict = GripEngagementGate.Evaluate(
            criteria,
            GripAcquisitionMasks.Build(curls, distances, criteria));

        Assert.That(verdict.CanAcquire, Is.False);
    }

    [Test]
    public void ThumbAndIndexPinchEngagesOnlyWhenTheThumbIsAllowedToCount()
    {
        float[] curls = new float[FingerCurlEstimator.FingerCount];
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        Array.Fill(distances, float.PositiveInfinity);
        curls[FingerCurlEstimator.ThumbFinger] = 0.8f;
        curls[1] = 0.6f;
        distances[GripEngagementGate.GetFingertipBoneIndex(FingerCurlEstimator.ThumbFinger)] = 0.004f;
        distances[GripEngagementGate.GetFingertipBoneIndex(1)] = 0.004f;
        GripAcquisitionCriteria counted = Criteria(minFingers: 2, thumbCounts: true);
        GripAcquisitionCriteria excluded = Criteria(minFingers: 2, thumbCounts: false);

        Assert.That(
            GripEngagementGate.Evaluate(counted, GripAcquisitionMasks.Build(curls, distances, counted))
                .CanAcquire,
            Is.True);
        Assert.That(
            GripEngagementGate.Evaluate(excluded, GripAcquisitionMasks.Build(curls, distances, excluded))
                .CanAcquire,
            Is.False);
    }

    [Test]
    public void AShortGripNamesTheClauseThatIsMissing()
    {
        GripAcquisitionCriteria criteria = Criteria();
        float[] curls = new float[FingerCurlEstimator.FingerCount];
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        Array.Fill(distances, float.PositiveInfinity);

        Assert.That(
            GripEngagementGate.Evaluate(criteria, GripAcquisitionMasks.Build(curls, distances, criteria))
                .Block,
            Is.EqualTo(GripEngagementBlock.NoFlexedFinger));

        for (int finger = 1; finger <= 3; finger++)
        {
            curls[finger] = 0.9f;
        }
        Assert.That(
            GripEngagementGate.Evaluate(criteria, GripAcquisitionMasks.Build(curls, distances, criteria))
                .Block,
            Is.EqualTo(GripEngagementBlock.NoContactFinger),
            "A closed hand away from the hold must report contact, not flexion.");
    }

    [Test]
    public void StrongFloorNeverDemandsMoreFingersThanTheHoldItself()
    {
        GripAcquisitionCriteria criteria = new(
            1,
            false,
            4,
            EngageCurl,
            StrongCurl,
            ContactRange,
            StrongContactRange);

        Assert.That(criteria.StrongFingerFloor, Is.EqualTo(1));
        Assert.That(
            GripEngagementGate.Evaluate(criteria, Grip(1, 0.9f, 0.004f)).CanAcquire,
            Is.True);
    }

    [Test]
    public void CriteriaRejectAStrongPathWeakerThanTheNormalPath()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GripAcquisitionCriteria(
            3, false, 1, EngageCurl, EngageCurl - 0.01f, ContactRange, StrongContactRange));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GripAcquisitionCriteria(
            3, false, 1, EngageCurl, StrongCurl, ContactRange, ContactRange + 0.001f));
    }

    [Test]
    public void APartialLatchReleasesOnTheFingersItCaughtRatherThanTheHoldMinimum()
    {
        const int indexOnly = 0b0_0010;
        GripLatchStateMachine latch = new();

        GripLatchTransition acquired = latch.Update(0f, true, true, 21, 1, indexOnly, indexOnly);
        Assert.That(acquired.Kind, Is.EqualTo(GripLatchTransitionKind.Latched));

        Assert.That(latch.Update(0.5f, true, false, 0, 1, 0, indexOnly).Kind,
            Is.EqualTo(GripLatchTransitionKind.None),
            "A one-finger latch must survive on that one finger.");
        Assert.That(latch.Update(1f, true, false, 0, 1, 0, 0).ReleaseReason,
            Is.EqualTo(GripReleaseReason.OpenHand));
    }

    [Test]
    public void ThumbCurlUsesItsOwnTwoJointSpan()
    {
        Quaternion[] rotations = new Quaternion[26];
        Array.Fill(rotations, Quaternion.identity);
        rotations[3] = Quaternion.Euler(40f, 0f, 0f);
        rotations[4] = Quaternion.Euler(80f, 0f, 0f);
        bool[] confidence = { true, true, true, true, true };
        float[] curls = new float[FingerCurlEstimator.FingerCount];

        FingerCurlEstimator.Update(rotations, confidence, curls);

        Assert.That(FingerCurlEstimator.GetJointCount(FingerCurlEstimator.ThumbFinger), Is.EqualTo(2));
        Assert.That(FingerCurlEstimator.GetJointCount(1), Is.EqualTo(3));
        Assert.That(curls[FingerCurlEstimator.ThumbFinger],
            Is.EqualTo((80f - 10f) / (110f - 10f)).Within(0.001f));
        Assert.That(curls[FingerCurlEstimator.ThumbFinger], Is.GreaterThan(EngageCurl),
            "A loaded thumb must be able to reach the engagement threshold.");
    }

    [Test]
    public void JointSamplerReportsEachFingerJointSeparately()
    {
        Quaternion[] rotations = new Quaternion[26];
        Array.Fill(rotations, Quaternion.identity);
        rotations[7] = Quaternion.Euler(30f, 0f, 0f);
        rotations[8] = Quaternion.Euler(110f, 0f, 0f);
        rotations[9] = Quaternion.Euler(140f, 0f, 0f);
        float[] joints = new float[FingerCurlEstimator.MaximumJointsPerFinger];

        int jointCount = FingerCurlEstimator.SampleJointDegrees(rotations, 1, joints);

        Assert.That(jointCount, Is.EqualTo(3));
        Assert.That(joints[0], Is.EqualTo(30f).Within(0.01f));
        Assert.That(joints[1], Is.EqualTo(80f).Within(0.01f));
        Assert.That(joints[2], Is.EqualTo(30f).Within(0.01f));
    }

    [Test]
    public void DiagnosticsReportAnatomyLabelsAndTheFailingClause()
    {
        GripAcquisitionCriteria criteria = Criteria();
        GripHandDiagnostics diagnostics = new("LEFT")
        {
            TrackingValid = true,
            HoldLabel = "E5",
            Criteria = criteria,
            Masks = Grip(2, 0.9f, 0.004f),
            Block = GripEngagementBlock.TooFewFingers,
            CountedFingers = 2,
            RequiredFingers = 3,
        };
        float[] joints = { 45f, 95f, 20f };
        diagnostics.SetFinger(FingerCurlEstimator.ThumbFinger, 0.4f, 0.03f, joints, 2);
        for (int finger = 1; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            diagnostics.SetFinger(finger, 0.9f, 0.004f, joints, 3);
        }
        StringBuilder builder = new();

        GripDiagnosticsFormatter.AppendHand(builder, diagnostics);
        string report = builder.ToString();

        Assert.That(GripDiagnosticsFormatter.GetJointLabels(FingerCurlEstimator.ThumbFinger),
            Is.EqualTo(new[] { "MCP", "FPL" }));
        Assert.That(GripDiagnosticsFormatter.GetJointLabels(1),
            Is.EqualTo(new[] { "MCP", "PIP", "DIP" }));
        Assert.That(report, Does.Contain("LEFT"));
        Assert.That(report, Does.Contain("HOLD E5"));
        Assert.That(report, Does.Contain("MCP  PIP  DIP"));
        Assert.That(report, Does.Contain("ONLY 2 OF 3 FINGERS ARE FLEXED AND TOUCHING"));
        Assert.That(report, Does.Contain("4MM"));
        foreach (string finger in GripDiagnosticsFormatter.FingerNames)
        {
            Assert.That(report, Does.Contain(finger));
        }
    }

    [Test]
    public void DiagnosticsWordEveryBlockedReason()
    {
        GripHandDiagnostics diagnostics = new("RIGHT") { Criteria = Criteria() };

        foreach (GripEngagementBlock block in Enum.GetValues(typeof(GripEngagementBlock)))
        {
            diagnostics.Block = block;
            Assert.That(GripDiagnosticsFormatter.DescribeBlock(diagnostics),
                Is.Not.Null.And.Not.Empty,
                block.ToString());
        }
    }

    [Test]
    public void TwoTrackedLatchesDriveTheBoardTogetherAndEitherAloneStillDrivesIt()
    {
        GripLocomotionPlan bimanual = GripLocomotionPlanner.Select(
            GripLatchPhase.Latched, true, GripLatchPhase.Latched, true, true);

        Assert.That(bimanual.Mode, Is.EqualTo(GripLocomotionMode.Bimanual));
        Assert.That(bimanual.AnchorCount, Is.EqualTo(2));
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Latched, true, GripLatchPhase.Free, false, true).Mode,
            Is.EqualTo(GripLocomotionMode.Left));
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Free, true, GripLatchPhase.Latched, true, true).Mode,
            Is.EqualTo(GripLocomotionMode.Right));
    }

    [Test]
    public void AnEngagedHandThatCannotDriveHoldsTheBoardStill()
    {
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Latched, true, GripLatchPhase.Frozen, false, true).Mode,
            Is.EqualTo(GripLocomotionMode.None),
            "A frozen hand still has the hold, so the board must not follow the other hand.");
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Latched, false, GripLatchPhase.Latched, true, true).Mode,
            Is.EqualTo(GripLocomotionMode.None));
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Frozen, false, GripLatchPhase.Free, false, true).Mode,
            Is.EqualTo(GripLocomotionMode.None));
        Assert.That(GripLocomotionPlanner.Select(
                GripLatchPhase.Free, true, GripLatchPhase.Free, true, true).Mode,
            Is.EqualTo(GripLocomotionMode.None));
    }

    [Test]
    public void DisablingTwoHandLocomotionReproducesTheSingleDriverPolicyExactly()
    {
        foreach (GripLatchPhase leftPhase in Enum.GetValues(typeof(GripLatchPhase)))
        {
            foreach (GripLatchPhase rightPhase in Enum.GetValues(typeof(GripLatchPhase)))
            {
                for (int tracking = 0; tracking < 4; tracking++)
                {
                    bool leftTracked = (tracking & 1) != 0;
                    bool rightTracked = (tracking & 2) != 0;
                    GripLocomotionDriver expected = GripLocomotionPolicy.SelectDriver(
                        leftPhase, leftTracked, rightPhase, rightTracked);
                    GripLocomotionMode actual = GripLocomotionPlanner.Select(
                        leftPhase, leftTracked, rightPhase, rightTracked, false).Mode;

                    Assert.That(actual.ToString(), Is.EqualTo(expected.ToString()),
                        leftPhase + "/" + leftTracked + " " + rightPhase + "/" + rightTracked);
                }
            }
        }
    }

    [Test]
    public void TheBoardFollowsTheMeanOfItsAnchors()
    {
        GripLocomotionPlan bimanual = new(GripLocomotionMode.Bimanual);
        GripLocomotionPlan single = new(GripLocomotionMode.Right);

        Assert.That(
            GripLocomotionPlanner.CombineAnchorMovement(
                bimanual, new Vector3(0.2f, 0f, 0f), new Vector3(0.2f, 0f, 0f)),
            Is.EqualTo(new Vector3(0.2f, 0f, 0f)),
            "Two hands pulling together move the board with them, not at double speed.");
        Assert.That(
            GripLocomotionPlanner.CombineAnchorMovement(
                bimanual, new Vector3(0.2f, 0f, 0f), Vector3.zero).x,
            Is.EqualTo(0.1f).Within(0.00001f));
        Assert.That(
            GripLocomotionPlanner.CombineAnchorMovement(
                single, new Vector3(9f, 9f, 9f), new Vector3(0.3f, 0f, 0f)),
            Is.EqualTo(new Vector3(0.3f, 0f, 0f)),
            "An idle hand must contribute nothing while it is not an anchor.");
        Assert.That(
            GripLocomotionPlanner.CombineAnchorMovement(
                new GripLocomotionPlan(GripLocomotionMode.None), Vector3.one, Vector3.one),
            Is.EqualTo(Vector3.zero));
    }
}
