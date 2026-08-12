using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class GripEngagementTests
{
    private const int ThreeFingerMask = 0b0_1110;

    [Test]
    public void LatchedOverlayIsBinaryAndSurvivesHoverInvalidationForEitherHand()
    {
        const int leftHandMask = 1;
        const int rightHandMask = 2;
        if (!SystemInfo.supportsComputeShaders)
        {
            Assert.Ignore("The contact-state fixture requires compute-buffer support.");
        }

        GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GripScoreConfig config = ScriptableObject.CreateInstance<GripScoreConfig>();
        object state = null;
        ComputeBuffer contactBuffer = null;
        try
        {
            Type stateType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("GripHoldContactState"))
                .Single(type => type != null);
            ConstructorInfo constructor = stateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            state = constructor.Invoke(new object[]
            {
                null,
                config,
                hold,
                hold.GetComponent<MeshFilter>(),
                Resources.Load<Material>("ContactPatchOverlay"),
            });
            MethodInfo setLatchedHand = stateType.GetMethod("SetLatchedHand");
            MethodInfo setOverlayVisible = stateType.GetMethod("SetOverlayVisible");
            MethodInfo setContactBuffer = stateType.GetMethod("SetContactBuffer");
            MethodInfo invalidateContactData = stateType.GetMethod("InvalidateContactData");
            PropertyInfo latchedHandMask = stateType.GetProperty("LatchedHandMask");
            Assert.That(setLatchedHand, Is.Not.Null);
            Assert.That(setOverlayVisible, Is.Not.Null);
            Assert.That(setContactBuffer, Is.Not.Null);
            Assert.That(invalidateContactData, Is.Not.Null);
            Assert.That(latchedHandMask, Is.Not.Null);

            Renderer overlay = hold.transform.Find("Contact Patch Overlay")?.GetComponent<Renderer>();
            Assert.That(overlay, Is.Not.Null);
            Assert.That(overlay.enabled, Is.False);

            contactBuffer = new ComputeBuffer(hold.GetComponent<MeshFilter>().sharedMesh.vertexCount, sizeof(float) * 4);
            setOverlayVisible.Invoke(state, new object[] { true });
            setContactBuffer.Invoke(state, new object[] { contactBuffer, 1L });
            Assert.That(overlay.enabled, Is.False, "Proximity alone must not render a graded hold cue.");

            setLatchedHand.Invoke(state, new object[] { leftHandMask, true });
            Assert.That((int)latchedHandMask.GetValue(state), Is.EqualTo(leftHandMask));
            Assert.That(overlay.enabled, Is.True);
            MaterialPropertyBlock properties = new();
            overlay.GetPropertyBlock(properties);
            Assert.That(properties.GetFloat("_GripLatched"), Is.EqualTo(1f));

            setOverlayVisible.Invoke(state, new object[] { false });
            invalidateContactData.Invoke(state, new object[] { -1L });
            Assert.That(overlay.enabled, Is.True, "Hover exit must not hide a latched hold.");

            setLatchedHand.Invoke(state, new object[] { rightHandMask, true });
            setLatchedHand.Invoke(state, new object[] { leftHandMask, false });
            Assert.That((int)latchedHandMask.GetValue(state), Is.EqualTo(rightHandMask));
            Assert.That(overlay.enabled, Is.True, "One hand releasing must preserve the other latch.");

            setLatchedHand.Invoke(state, new object[] { rightHandMask, false });
            Assert.That((int)latchedHandMask.GetValue(state), Is.Zero);
            Assert.That(overlay.enabled, Is.False);
            overlay.GetPropertyBlock(properties);
            Assert.That(properties.GetFloat("_GripLatched"), Is.Zero);
        }
        finally
        {
            (state as IDisposable)?.Dispose();
            contactBuffer?.Release();
            UnityEngine.Object.DestroyImmediate(config);
            UnityEngine.Object.DestroyImmediate(hold);
        }
    }

    [Test]
    public void CurlEstimatorNormalizesSyntheticJointBendsAndRetainsLowConfidenceFinger()
    {
        Quaternion[] rotations = new Quaternion[26];
        Array.Fill(rotations, Quaternion.identity);
        rotations[6] = Quaternion.Euler(0f, 0f, 0f);
        rotations[7] = Quaternion.Euler(60f, 0f, 0f);
        rotations[8] = Quaternion.Euler(120f, 0f, 0f);
        rotations[9] = Quaternion.Euler(180f, 0f, 0f);
        bool[] confidence = { true, true, false, true, true };
        float[] curls = { 0f, 0f, 0.8f, 0f, 0f };

        FingerCurlEstimator.Update(rotations, confidence, curls);

        Assert.That(curls[1], Is.EqualTo((180f - 15f) / (210f - 15f)).Within(0.001f));
        Assert.That(curls[2], Is.EqualTo(0.8f), "Low confidence must retain the prior curl.");
    }

    [TestCase(1, 0b0_0010, true)]
    [TestCase(2, 0b0_0110, true)]
    [TestCase(3, 0b0_1110, true)]
    [TestCase(3, 0b0_0110, false)]
    [TestCase(1, 0b0_0001, false)]
    public void AcquisitionGateHonorsPerHoldMinimumAndExcludesThumb(
        int minFingers,
        int contactMask,
        bool expected)
    {
        Assert.That(GripEngagementGate.CanAcquire(true, minFingers, contactMask), Is.EqualTo(expected));
        Assert.That(GripEngagementGate.CanAcquire(false, minFingers, contactMask), Is.False);
    }

    [Test]
    public void FlexedContactMaskRequiresCurlAndTipDistance()
    {
        float[] curls = { 1f, 0.8f, 0.7f, 0.6f, 0.9f };
        float[] distances = new float[26];
        Array.Fill(distances, float.PositiveInfinity);
        distances[5] = 0.005f;
        distances[10] = 0.005f;
        distances[15] = 0.005f;
        distances[20] = 0.05f;
        distances[25] = 0.005f;

        int mask = GripEngagementGate.BuildFlexedContactMask(curls, distances, 0.55f, 0.01f);

        Assert.That(mask, Is.EqualTo(0b1_0111));
        Assert.That(GripEngagementGate.CountNonThumbFingers(mask), Is.EqualTo(3));
    }

    [Test]
    public void AcquisitionSampleKeepsCurlsAndDistancesFromTheSameGpuEpoch()
    {
        GripAcquisitionSample sample = new();
        float[] curls = { 0f, 0f, 0f, 0f, 0f };
        float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
        Array.Fill(distances, float.PositiveInfinity);
        distances[10] = 0.005f;
        distances[15] = 0.005f;
        distances[20] = 0.005f;
        sample.Publish(42, curls, distances, 0f);

        curls[1] = 1f;
        curls[2] = 1f;
        curls[3] = 1f;
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 0.01f), Is.Zero,
            "A later flexed pose must not combine with an earlier GPU distance sample.");

        sample.Publish(42, curls, distances, 0.02f);
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 0.03f),
            Is.EqualTo(ThreeFingerMask));
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 0.04f), Is.Zero,
            "A completed GPU snapshot must be consumed by one acquisition evaluation.");

        sample.Publish(42, curls, distances, 0.05f);
        Assert.That(sample.ConsumeFlexedContactMask(99, curls, 0.55f, 0.01f, 0.06f), Is.Zero);
        sample.Publish(42, curls, distances, 0.07f);
        Array.Fill(curls, 0f);
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 0.08f), Is.Zero,
            "An open hand must not reacquire from an older flexed-contact sample.");
        curls[1] = 1f;
        curls[2] = 1f;
        curls[3] = 1f;
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 0.09f), Is.Zero,
            "Re-flexing requires a new contact epoch.");

        sample.Publish(42, curls, distances, 1f);
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 1.101f), Is.Zero,
            "Late GPU readbacks must not become acquisition input.");
        sample.Publish(42, curls, distances, 2f);
        sample.Invalidate();
        Assert.That(sample.ConsumeFlexedContactMask(42, curls, 0.55f, 0.01f, 2.01f), Is.Zero);
    }

    [Test]
    public void LatchUsesLowThresholdAndContinuousCountDropGrace()
    {
        GripLatchStateMachine latch = new();
        GripLatchTransition acquired = latch.Update(0f, true, true, 42, 3, ThreeFingerMask, ThreeFingerMask);

        Assert.That(acquired.Kind, Is.EqualTo(GripLatchTransitionKind.Latched));
        Assert.That(latch.Phase, Is.EqualTo(GripLatchPhase.Latched));
        Assert.That(latch.Update(0.01f, true, false, 0, 3, 0, 0b0_0110).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(latch.Update(0.159f, true, false, 0, 3, 0, 0b0_0110).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));

        GripLatchTransition released = latch.Update(0.161f, true, false, 0, 3, 0, 0b0_0110);
        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void ExplicitOpenHandReleasesWithoutWaitingForGrace()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 7, 3, ThreeFingerMask, ThreeFingerMask);

        GripLatchTransition released = latch.Update(0.01f, true, false, 0, 3, 0, 0);

        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.OpenHand));
    }

    [Test]
    public void CountDropGraceRestartsAfterFlexionRecovers()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 8, 3, ThreeFingerMask, ThreeFingerMask);
        latch.Update(0.01f, true, false, 0, 3, 0, 0b0_0110);
        latch.Update(0.1f, true, false, 0, 3, 0, ThreeFingerMask);
        latch.Update(0.2f, true, false, 0, 3, 0, 0b0_0110);

        Assert.That(latch.Update(0.349f, true, false, 0, 3, 0, 0b0_0110).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(latch.Update(0.351f, true, false, 0, 3, 0, 0b0_0110).ReleaseReason,
            Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void ReplacementFingersCannotPreventCountDropRelease()
    {
        const int acquiredMask = 0b0_0110;
        const int oneAcquiredPlusReplacementMask = 0b0_1010;
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 13, 2, acquiredMask, acquiredMask);

        latch.Update(0.01f, true, false, 0, 2, 0, oneAcquiredPlusReplacementMask);
        Assert.That(latch.Update(0.159f, true, false, 0, 2, 0,
            oneAcquiredPlusReplacementMask).Kind, Is.EqualTo(GripLatchTransitionKind.None));

        GripLatchTransition released = latch.Update(
            0.161f, true, false, 0, 2, 0, oneAcquiredPlusReplacementMask);
        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void TrackingLossFreezesThenResumesWithoutContactRecheck()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 9, 3, ThreeFingerMask, ThreeFingerMask);

        Assert.That(latch.Update(0.1f, false, false, 0, 3, 0, 0).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        GripLatchTransition frozen = latch.Update(0.351f, false, false, 0, 3, 0, 0);
        Assert.That(frozen.Kind, Is.EqualTo(GripLatchTransitionKind.Frozen));
        Assert.That(latch.Phase, Is.EqualTo(GripLatchPhase.Frozen));

        GripLatchTransition resumed = latch.Update(0.5f, true, false, 0, 3, 0, ThreeFingerMask);
        Assert.That(resumed.Kind, Is.EqualTo(GripLatchTransitionKind.Resumed));
        Assert.That(resumed.ResetAnchor, Is.True);
        Assert.That(latch.HoldId, Is.EqualTo(9));
    }

    [Test]
    public void FrozenLatchRejectsReplacementFingersOnTrackingReturn()
    {
        const int acquiredMask = 0b0_0110;
        const int oneAcquiredPlusReplacementMask = 0b0_1010;
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 14, 2, acquiredMask, acquiredMask);
        latch.Update(0.1f, false, false, 0, 2, 0, 0);
        latch.Update(0.351f, false, false, 0, 2, 0, 0);

        GripLatchTransition released = latch.Update(
            0.5f, true, false, 0, 2, 0, oneAcquiredPlusReplacementMask);

        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void TrackingRecoveryBeforeFreezeRequestsFreshWristAnchor()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 10, 3, ThreeFingerMask, ThreeFingerMask);
        latch.Update(0.1f, false, false, 0, 3, 0, 0);

        GripLatchTransition recovered = latch.Update(
            0.2f, true, false, 0, 3, 0, ThreeFingerMask);

        Assert.That(recovered.Kind, Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(recovered.ResetAnchor, Is.True);
        Assert.That(latch.Phase, Is.EqualTo(GripLatchPhase.Latched));
    }

    [Test]
    public void TrackingLossInterruptsContinuousCountDropGrace()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 12, 3, ThreeFingerMask, ThreeFingerMask);
        latch.Update(0.01f, true, false, 0, 3, 0, 0b0_0110);
        latch.Update(0.1f, false, false, 0, 3, 0, 0);

        Assert.That(latch.Update(0.2f, true, false, 0, 3, 0, 0b0_0110).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(latch.Update(0.349f, true, false, 0, 3, 0, 0b0_0110).Kind,
            Is.EqualTo(GripLatchTransitionKind.None));
        Assert.That(latch.Update(0.351f, true, false, 0, 3, 0, 0b0_0110).ReleaseReason,
            Is.EqualTo(GripReleaseReason.CountDrop));
    }

    [Test]
    public void FrozenLatchTimesOutAndUsesRecorderReason()
    {
        GripLatchStateMachine latch = new();
        latch.Update(0f, true, true, 11, 3, ThreeFingerMask, ThreeFingerMask);
        latch.Update(0.1f, false, false, 0, 3, 0, 0);
        latch.Update(0.351f, false, false, 0, 3, 0, 0);

        GripLatchTransition released = latch.Update(2.352f, false, false, 0, 3, 0, 0);

        Assert.That(released.Kind, Is.EqualTo(GripLatchTransitionKind.Released));
        Assert.That(released.ReleaseReason, Is.EqualTo(GripReleaseReason.FrozenTimeout));
        Assert.That(released.ReleaseReason.ToRecorderValue(), Is.EqualTo("frozen_timeout"));
    }

    [Test]
    public void HoldAffordanceSidecarResolvesMonoAndTwoFingerPockets()
    {
        Assert.That(HoldAffordanceCatalog.TryParse(
            "{\"W12\":1,\"B34\":2}", out HoldAffordanceCatalog catalog, out string error),
            Is.True, error);

        Assert.That(catalog.ResolveMinFingers("W12", 3), Is.EqualTo(1));
        Assert.That(catalog.ResolveMinFingers("b34", 3), Is.EqualTo(2));
        Assert.That(catalog.ResolveMinFingers("Y9", 3), Is.EqualTo(3));
        Assert.That(HoldAffordanceCatalog.TryParse("{\"W12\":3}", out _, out error), Is.False);
        Assert.That(error, Does.Contain("must be 1 or 2"));
    }

    [Test]
    public void ShippedHoldAffordanceSidecarParses()
    {
        string json = File.ReadAllText(
            Path.Combine(Application.streamingAssetsPath, "hold_affordances.json"));

        Assert.That(HoldAffordanceCatalog.TryParse(json, out _, out string error), Is.True, error);
    }

    [Test]
    public void LocomotionFilterHasUnitSteadyStateGainAndNoPostReleaseTail()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(Vector3.zero, 0f);

        Vector3 displacement = Vector3.zero;
        Vector3 previousVelocity = Vector3.zero;
        const float frameRate = 90f;
        bool observedFiltering = false;
        for (int frame = 1; frame <= 360; frame++)
        {
            float time = frame / frameRate;
            float position = Mathf.Min(1f, time * 0.4f);
            Vector3 movement = filter.Update(Vector3.right * position, time);
            Vector3 velocity = movement * frameRate;
            Assert.That((velocity - previousVelocity).magnitude * frameRate,
                Is.LessThanOrEqualTo(12.001f));
            if (frame == 1)
            {
                observedFiltering = movement.x < position;
            }
            previousVelocity = velocity;
            displacement += movement;
        }
        filter.Complete();

        Assert.That(observedFiltering, Is.True, "The One Euro stage must not emit raw deltas.");
        Assert.That(displacement.x, Is.EqualTo(1f).Within(0.001f));
        Assert.That(displacement.y, Is.EqualTo(0f).Within(0.0001f));
        Assert.That(displacement.z, Is.EqualTo(0f).Within(0.0001f));
        Assert.That(filter.Update(Vector3.one * 100f, 2f + 1f / frameRate),
            Is.EqualTo(Vector3.zero), "A completed grip must have no post-release tail.");
    }

    [Test]
    public void LocomotionLimiterRepaysWithheldPositionForStepAndReversal()
    {
        const float frameRate = 90f;
        const float deltaTime = 1f / frameRate;

        GripLocomotionFilter stepFilter = new();
        stepFilter.Reset(Vector3.zero, 0f);
        Vector3 stepDisplacement = Vector3.zero;
        Vector3 previousVelocity = Vector3.zero;
        for (int frame = 1; frame <= 360; frame++)
        {
            Vector3 movement = stepFilter.Update(Vector3.right * 0.04f, frame * deltaTime);
            Vector3 velocity = movement / deltaTime;
            Assert.That((velocity - previousVelocity).magnitude / deltaTime,
                Is.LessThanOrEqualTo(12.001f));
            previousVelocity = velocity;
            stepDisplacement += movement;
        }
        Assert.That(stepDisplacement.x, Is.EqualTo(0.04f).Within(0.00001f));

        GripLocomotionFilter reversalFilter = new();
        reversalFilter.Reset(Vector3.zero, 0f);
        Vector3 reversalDisplacement = Vector3.zero;
        previousVelocity = Vector3.zero;
        for (int frame = 1; frame <= 360; frame++)
        {
            Vector3 wrist = frame == 1 ? Vector3.right * 0.04f : Vector3.zero;
            Vector3 movement = reversalFilter.Update(wrist, frame * deltaTime);
            Vector3 velocity = movement / deltaTime;
            Assert.That((velocity - previousVelocity).magnitude / deltaTime,
                Is.LessThanOrEqualTo(12.001f));
            previousVelocity = velocity;
            reversalDisplacement += movement;
        }
        Assert.That(reversalDisplacement.magnitude, Is.LessThan(0.00001f));
    }

    [Test]
    public void LocomotionUsesOpenXrWristAndTrackingLossCannotFling()
    {
        Vector3[] bones = { Vector3.zero, Vector3.right };
        GripLocomotionFilter filter = new();
        filter.Reset(GripLocomotionAnchor.GetWristPosition(bones), 0f);

        bones[0] = Vector3.one * 100f;
        Assert.That(filter.Update(GripLocomotionAnchor.GetWristPosition(bones), 0.1f),
            Is.EqualTo(Vector3.zero), "Palm motion must not move a stationary wrist anchor.");

        bones[1] += Vector3.right * 0.005f;
        float movement = filter.Update(
            GripLocomotionAnchor.GetWristPosition(bones), 0.2f).x;
        Assert.That(movement, Is.GreaterThan(0f));
        Assert.That(movement, Is.LessThan(0.005f));
        filter.Cancel();
        bones[1] = Vector3.one * 10f;
        Assert.That(filter.Update(GripLocomotionAnchor.GetWristPosition(bones), 0.3f),
            Is.EqualTo(Vector3.zero), "Tracking loss must not drain or apply retained motion.");

        filter.Reset(GripLocomotionAnchor.GetWristPosition(bones), 0.4f);
        Assert.That(filter.Update(GripLocomotionAnchor.GetWristPosition(bones), 0.5f),
            Is.EqualTo(Vector3.zero), "A tracking-recovery reset must establish a fresh anchor.");
    }

    [Test]
    public void LocomotionAccelerationIsClampedWithoutPermanentlyStopping()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(Vector3.zero, 0f);
        const float deltaTime = 1f / 90f;

        Vector3 first = filter.Update(Vector3.right * 4f * deltaTime, deltaTime);
        Vector3 second = filter.Update(Vector3.right * 8f * deltaTime, 2f * deltaTime);
        Vector3 firstVelocity = first / deltaTime;
        Vector3 secondVelocity = second / deltaTime;

        Assert.That(firstVelocity.magnitude / deltaTime, Is.LessThanOrEqualTo(12.001f));
        Assert.That((secondVelocity - firstVelocity).magnitude / deltaTime,
            Is.LessThanOrEqualTo(12.001f));
        Assert.That(first.x, Is.GreaterThan(0f));
        Assert.That(second.x, Is.GreaterThan(first.x));
        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.None));
    }

    [Test]
    public void LocomotionNewLatchStartsFromRestAndIgnoresDuplicateTrackerPose()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(Vector3.zero, 0f);

        Assert.That(filter.Update(Vector3.zero, 0.01f), Is.EqualTo(Vector3.zero));
        Assert.That(filter.Update(Vector3.right * 0.01f, 0.02f).x,
            Is.GreaterThan(0f));
        Assert.That(filter.Update(Vector3.right * 0.01f, 0.02f), Is.EqualTo(Vector3.zero));
        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.None));
    }

    [Test]
    public void LocomotionLongSampleGapReanchorsWithoutTeleportAndRecovers()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(Vector3.zero, 0f);

        Assert.That(filter.Update(Vector3.right, 0.5f), Is.EqualTo(Vector3.zero));
        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.SampleGap));
        Assert.That(filter.Update(Vector3.right * 1.01f, 0.51f).x, Is.GreaterThan(0f));
        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.None));
    }

    [Test]
    public void LocomotionImplausibleSampleReanchorsWithoutAFilteredTail()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(Vector3.zero, 0f);
        const float deltaTime = 1f / 90f;

        Assert.That(filter.Update(Vector3.right, deltaTime), Is.EqualTo(Vector3.zero));
        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.ImplausibleSpeed));
        Assert.That(filter.Update(Vector3.right, 2f * deltaTime), Is.EqualTo(Vector3.zero));
        Assert.That(filter.Update(Vector3.right * 1.01f, 3f * deltaTime).x,
            Is.GreaterThan(0f));
    }

    [Test]
    public void LocomotionInvalidSampleFailsClosedAndRecoversFromFreshAnchor()
    {
        GripLocomotionFilter filter = new();
        filter.Reset(new Vector3(float.NaN, 0f, 0f), 0f);

        Assert.That(filter.LastDiscontinuityReason,
            Is.EqualTo(GripLocomotionDiscontinuityReason.InvalidSample));
        Assert.That(filter.Update(Vector3.zero, 0.01f), Is.EqualTo(Vector3.zero));
        Assert.That(filter.Update(Vector3.right * 0.01f, 0.02f).x, Is.GreaterThan(0f));
    }

    [Test]
    public void LocomotionRequiresExactlyOneEngagedTrackedLatch()
    {
        Assert.That(GripLocomotionPolicy.SelectDriver(
            GripLatchPhase.Latched,
            true,
            GripLatchPhase.Free,
            false), Is.EqualTo(GripLocomotionDriver.Left));

        Assert.That(GripLocomotionPolicy.SelectDriver(
            GripLatchPhase.Frozen,
            false,
            GripLatchPhase.Free,
            false), Is.EqualTo(GripLocomotionDriver.None));
        Assert.That(GripLocomotionPolicy.SelectDriver(
            GripLatchPhase.Latched,
            false,
            GripLatchPhase.Latched,
            true), Is.EqualTo(GripLocomotionDriver.None),
            "Tracking loss must not reinterpret the other engaged hand as a one-hand grip.");
        Assert.That(GripLocomotionPolicy.SelectDriver(
            GripLatchPhase.Latched,
            true,
            GripLatchPhase.Frozen,
            false), Is.EqualTo(GripLocomotionDriver.None),
            "A frozen hand must hold the board even while the other hand remains tracked.");
    }
}
