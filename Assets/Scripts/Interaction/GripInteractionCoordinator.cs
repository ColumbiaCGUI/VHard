using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the per-hand grip interaction state: hand-tracking validity, finger curls,
/// flexion-latch state machines, locomotion anchors/filters, acquisition samples, and the
/// degraded CPU acquisition path. The SceneConfiguror facade keeps the serialized tunables
/// and public state fields; this coordinator reads and writes them through the owner.
/// </summary>
public sealed class GripInteractionCoordinator
{
    private const int TrackedBoneCount = 26;
    private static readonly OVRHand.HandFinger[] TrackedFingers =
    {
        OVRHand.HandFinger.Thumb,
        OVRHand.HandFinger.Index,
        OVRHand.HandFinger.Middle,
        OVRHand.HandFinger.Ring,
        OVRHand.HandFinger.Pinky,
    };

    private readonly SceneConfiguror owner;
    private OVRHand leftTrackedHand;
    private OVRHand rightTrackedHand;
    private Hand? gripLocomotionHand;
    private GripLatchStateMachine leftGripLatch;
    private GripLatchStateMachine rightGripLatch;
    private GripLocomotionFilter leftLocomotionFilter;
    private GripLocomotionFilter rightLocomotionFilter;
    private GameObject leftLatchedHold;
    private GameObject rightLatchedHold;
    private readonly float[] leftFingerCurls = new float[FingerCurlEstimator.FingerCount];
    private readonly float[] rightFingerCurls = new float[FingerCurlEstimator.FingerCount];
    private readonly GripAcquisitionSample leftGripAcquisitionSample = new();
    private readonly GripAcquisitionSample rightGripAcquisitionSample = new();
    private readonly bool[] leftFingerConfidence = new bool[FingerCurlEstimator.FingerCount];
    private readonly bool[] rightFingerConfidence = new bool[FingerCurlEstimator.FingerCount];
    private readonly Dictionary<Mesh, DegradedGripContactGeometry> degradedGripGeometry = new();
    private readonly HashSet<int> reportedDegradedGripGeometryFailures = new();
    private readonly float[] leftDegradedGripDistances =
        new float[GripEngagementGate.RequiredBoneDistanceCount];
    private readonly float[] rightDegradedGripDistances =
        new float[GripEngagementGate.RequiredBoneDistanceCount];
    private bool leftLegacyFiveTipContact;
    private bool rightLegacyFiveTipContact;
    private bool inputSuppressed;
    private bool leftAcquisitionArmed = true;
    private bool rightAcquisitionArmed = true;

    public GripInteractionCoordinator(SceneConfiguror owner)
    {
        this.owner = owner;
    }

    public bool LeftTrackingValid { get; private set; }
    public bool RightTrackingValid { get; private set; }
    public IReadOnlyList<float> LeftFingerCurls => leftFingerCurls;
    public IReadOnlyList<float> RightFingerCurls => rightFingerCurls;
    public bool IsDegradedAcquisitionActive { get; private set; }
    public string DegradedAcquisitionFailureReason { get; private set; } = string.Empty;

    public void Initialize()
    {
        if (leftGripLatch != null)
        {
            return;
        }
        GripEngagementGate.ValidateMinFingers(owner.defaultMinFingers);
        if (owner.gripFlexionReleaseThreshold >= owner.gripFlexionEngageThreshold)
        {
            throw new InvalidOperationException(
                "Grip release flexion threshold must be lower than the engagement threshold.");
        }

        leftGripLatch = new GripLatchStateMachine(
            owner.gripReleaseGraceSeconds,
            owner.gripTrackingFreezeSeconds,
            owner.gripFrozenTimeoutSeconds);
        rightGripLatch = new GripLatchStateMachine(
            owner.gripReleaseGraceSeconds,
            owner.gripTrackingFreezeSeconds,
            owner.gripFrozenTimeoutSeconds);
        leftLocomotionFilter = new GripLocomotionFilter(
            owner.gripOneEuroMinCutoff,
            owner.gripOneEuroBeta,
            owner.gripMaximumAcceleration);
        rightLocomotionFilter = new GripLocomotionFilter(
            owner.gripOneEuroMinCutoff,
            owner.gripOneEuroBeta,
            owner.gripMaximumAcceleration);
    }

    /// <summary>Per-frame hand tracking refresh: validity, bone copies, and finger curls.
    /// Preserves the facade's original call order (left before right).</summary>
    public void UpdateTracking()
    {
        LeftTrackingValid = IsHandTrackingValid(owner.leftHandOVRSkeleton, ref leftTrackedHand);
        RightTrackingValid = IsHandTrackingValid(owner.rightHandOVRSkeleton, ref rightTrackedHand);
        owner.numBonesPerHand = LeftTrackingValid || RightTrackingValid ? TrackedBoneCount : 0;
        if (LeftTrackingValid)
        {
            CopyHandBones(
                owner.leftHandOVRSkeleton,
                owner.leftHandBonePositions,
                owner.leftHandBoneQuaternions,
                TrackedBoneCount);
            UpdateFingerCurls(Hand.Left, leftFingerCurls);
        }
        if (RightTrackingValid)
        {
            CopyHandBones(
                owner.rightHandOVRSkeleton,
                owner.rightHandBonePositions,
                owner.rightHandBoneQuaternions,
                TrackedBoneCount);
            UpdateFingerCurls(Hand.Right, rightFingerCurls);
        }
    }

    public void UpdateGripMode(bool allowLocomotion)
    {
        Initialize();
        float now = Time.unscaledTime;
        UpdateHandGripLatch(Hand.Left, now);
        UpdateHandGripLatch(Hand.Right, now);
        UpdateLegacyGripTelemetry(Hand.Left);
        UpdateLegacyGripTelemetry(Hand.Right);
        owner.leftHandIsGripping = leftGripLatch.IsEngaged;
        owner.rightHandIsGripping = rightGripLatch.IsEngaged;

        if (!allowLocomotion)
        {
            StopGripLocomotion();
            return;
        }

        GripLocomotionDriver driver = GripLocomotionPolicy.SelectDriver(
            leftGripLatch.Phase,
            LeftTrackingValid,
            rightGripLatch.Phase,
            RightTrackingValid);
        if (driver == GripLocomotionDriver.None)
        {
            StopGripLocomotion();
            return;
        }

        Hand drivingHand = driver == GripLocomotionDriver.Left ? Hand.Left : Hand.Right;
        if (!owner.isGripLocomotionActive || gripLocomotionHand != drivingHand)
        {
            StopGripLocomotion();
            StartGripLocomotion(drivingHand, now);
        }

        Vector3 movement;
        if (drivingHand == Hand.Left)
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(owner.leftHandBonePositions);
            movement = AdvanceGripLocomotion(Hand.Left, wristPosition, now);
            owner.leftHandGripLastPosition = wristPosition;
        }
        else
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(owner.rightHandBonePositions);
            movement = AdvanceGripLocomotion(Hand.Right, wristPosition, now);
            owner.rightHandGripLastPosition = wristPosition;
        }
        if (owner.moonBoardEnv != null)
        {
            owner.moonBoardEnv.transform.position += movement;
        }
    }

    public void SetInputSuppressed(bool suppressed)
    {
        if (inputSuppressed == suppressed)
        {
            return;
        }

        inputSuppressed = suppressed;
        if (suppressed)
        {
            leftAcquisitionArmed = false;
            rightAcquisitionArmed = false;
        }
    }

    public void StopGripLocomotion(Hand? hand = null)
    {
        if (!owner.isGripLocomotionActive || (hand.HasValue && gripLocomotionHand != hand))
        {
            return;
        }

        if (gripLocomotionHand == Hand.Left)
        {
            leftLocomotionFilter?.Cancel();
        }
        else if (gripLocomotionHand == Hand.Right)
        {
            rightLocomotionFilter?.Cancel();
        }
        owner.actionRecorder?.Record("LocomotionStop", "", null, "grip locomotion stopped");
        owner.isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    /// <summary>Resets every per-hand grip artifact; the facade owns the visual/hover parts of a
    /// full interaction reset and calls this for the grip share.</summary>
    public void ResetState()
    {
        StopGripLocomotion();
        leftGripLatch?.Reset();
        rightGripLatch?.Reset();
        leftGripAcquisitionSample.Invalidate();
        rightGripAcquisitionSample.Invalidate();
        leftLatchedHold = null;
        rightLatchedHold = null;
        leftLegacyFiveTipContact = false;
        rightLegacyFiveTipContact = false;
        owner.leftHandIsGripping = false;
        owner.rightHandIsGripping = false;
        owner.isGripLocomotionActive = false;
        gripLocomotionHand = null;
    }

    /// <summary>Releases a hold (e.g. an unregistering ghost) from whichever latch holds it.</summary>
    public void ReleaseHold(int holdId)
    {
        if (leftGripLatch != null && leftGripLatch.HoldId == holdId)
        {
            leftGripLatch.Reset();
            leftLatchedHold = null;
            StopGripLocomotion(Hand.Left);
        }
        if (rightGripLatch != null && rightGripLatch.HoldId == holdId)
        {
            rightGripLatch.Reset();
            rightLatchedHold = null;
            StopGripLocomotion(Hand.Right);
        }
        owner.leftHandIsGripping = leftGripLatch != null && leftGripLatch.IsEngaged;
        owner.rightHandIsGripping = rightGripLatch != null && rightGripLatch.IsEngaged;
    }

    public void PublishAcquisitionSample(
        Hand hand,
        int holdId,
        IReadOnlyList<float> curls,
        IReadOnlyList<float> distances,
        float sampledAt)
    {
        GripAcquisitionSample sample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        sample.Publish(holdId, curls, distances, sampledAt);
    }

    public void InvalidateAcquisitionSample(Hand hand)
    {
        GripAcquisitionSample sample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        sample.Invalidate();
    }

    public void ClearDegradedGeometryCache()
    {
        degradedGripGeometry.Clear();
    }

    public void PrewarmDegradedGeometry(IReadOnlyList<GameObject> holds)
    {
        if (holds == null)
        {
            return;
        }

        foreach (GameObject hold in holds)
        {
            if (!TryGetDegradedGripGeometry(hold, out _, out string error))
            {
                throw new InvalidOperationException(
                    "Cannot prepare degraded grip acquisition for " +
                    (hold != null ? hold.name : "<missing>") + ": " + error);
            }
        }
    }

    public void ActivateDegradedAcquisition()
    {
        if (IsDegradedAcquisitionActive)
        {
            return;
        }

        IsDegradedAcquisitionActive = true;
        DegradedAcquisitionFailureReason = string.Empty;
        reportedDegradedGripGeometryFailures.Clear();
        const string activationDetails =
            "cpu_mesh_vertex_distance; degraded_only; grip_visuals_off";
        Debug.LogWarning(
            "[SceneConfiguror] DEGRADED CPU mesh-vertex grip acquisition ACTIVE; " +
            "GPU grip cues remain off.");
        owner.actionRecorder?.Record(
            "GripAcquisitionFallbackActivated",
            "",
            null,
            activationDetails);

        // Geometry is cached lazily for the hovered hold so degradation cannot create a
        // frame-spike by copying every active route mesh at once.
    }

    private void UpdateHandGripLatch(Hand hand, float now)
    {
        bool trackingValid = hand == Hand.Left ? LeftTrackingValid : RightTrackingValid;
        GameObject candidate = hand == Hand.Left
            ? owner.leftHandInteractingClimbingHold
            : owner.rightHandInteractingClimbingHold;
        float[] curls = hand == Hand.Left ? leftFingerCurls : rightFingerCurls;
        GripAcquisitionSample acquisitionSample = hand == Hand.Left
            ? leftGripAcquisitionSample
            : rightGripAcquisitionSample;
        GripLatchStateMachine latch = hand == Hand.Left ? leftGripLatch : rightGripLatch;
        int lowFlexedMask = GripEngagementGate.BuildFlexedMask(curls, owner.gripFlexionReleaseThreshold);

        if (inputSuppressed)
        {
            return;
        }

        bool acquisitionArmed = hand == Hand.Left ? leftAcquisitionArmed : rightAcquisitionArmed;
        if (!acquisitionArmed)
        {
            if (trackingValid && GripEngagementGate.CountNonThumbFingers(lowFlexedMask) == 0)
            {
                if (hand == Hand.Left)
                {
                    leftAcquisitionArmed = true;
                }
                else
                {
                    rightAcquisitionArmed = true;
                }
            }
            return;
        }

        int minFingers = owner.GetMinFingersForHold(candidate);
        int candidateHoldId = candidate != null ? candidate.GetInstanceID() : 0;
        bool canEvaluateAcquisition = latch.Phase == GripLatchPhase.Free &&
                                      candidate != null &&
                                      trackingValid &&
                                      owner.HoldAffordancesState == HoldAffordancesLoadState.Ready;
        bool useDegradedCpu = DegradedGripContactAcquisition.ShouldUseCpu(
            IsDegradedAcquisitionActive,
            GetGripAcquisitionContext());
        bool acquisitionReady = false;
        int highFlexedContactMask = 0;
        if (canEvaluateAcquisition && useDegradedCpu)
        {
            acquisitionReady = TryBuildDegradedGripContactMask(
                hand,
                candidate,
                curls,
                out highFlexedContactMask);
        }
        else if (canEvaluateAcquisition && !owner.IsGripFeedbackDegraded && acquisitionSample.IsValid)
        {
            acquisitionReady = true;
            highFlexedContactMask = acquisitionSample.ConsumeFlexedContactMask(
                candidateHoldId,
                curls,
                owner.gripFlexionEngageThreshold,
                owner.gripFingertipRange,
                now);
        }
        GripLatchTransition transition = latch.Update(
            now,
            trackingValid,
            acquisitionReady,
            acquisitionReady ? candidateHoldId : 0,
            minFingers,
            highFlexedContactMask,
            lowFlexedMask);

        HandleGripLatchTransition(hand, candidate, minFingers, transition, now, trackingValid);
    }

    private GripAcquisitionContext GetGripAcquisitionContext()
    {
        return owner.gameMode switch
        {
            GameMode.Grip => GripAcquisitionContext.WallGrip,
            GameMode.Ghost => GripAcquisitionContext.DetachedInspection,
            _ => GripAcquisitionContext.None,
        };
    }

    private bool TryBuildDegradedGripContactMask(
        Hand hand,
        GameObject hold,
        IReadOnlyList<float> curls,
        out int contactMask)
    {
        contactMask = 0;
        if (!TryGetDegradedGripGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error))
        {
            RecordDegradedGripGeometryFailure(hand, hold, error);
            return false;
        }

        List<Vector3> positions = hand == Hand.Left
            ? owner.leftHandBonePositions
            : owner.rightHandBonePositions;
        float[] distances = hand == Hand.Left
            ? leftDegradedGripDistances
            : rightDegradedGripDistances;
        if (!DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                hold,
                geometry,
                positions,
                distances,
                out error))
        {
            RecordDegradedGripGeometryFailure(hand, hold, error);
            return false;
        }

        contactMask = GripEngagementGate.BuildFlexedContactMask(
            curls,
            distances,
            owner.gripFlexionEngageThreshold,
            owner.gripFingertipRange);
        return true;
    }

    private void UpdateFingerCurls(Hand hand, float[] curls)
    {
        OVRHand trackedHand = hand == Hand.Left ? leftTrackedHand : rightTrackedHand;
        bool[] confidence = hand == Hand.Left ? leftFingerConfidence : rightFingerConfidence;
        List<Quaternion> rotations = hand == Hand.Left
            ? owner.leftHandBoneQuaternions
            : owner.rightHandBoneQuaternions;
        for (int finger = 0; finger < TrackedFingers.Length; finger++)
        {
            confidence[finger] = trackedHand.GetFingerConfidence(TrackedFingers[finger]) ==
                                 OVRHand.TrackingConfidence.High;
        }
        FingerCurlEstimator.Update(rotations, confidence, curls);
    }

    private void HandleGripLatchTransition(
        Hand hand,
        GameObject candidate,
        int minFingers,
        GripLatchTransition transition,
        float now,
        bool trackingValid)
    {
        GameObject latchedHold = hand == Hand.Left ? leftLatchedHold : rightLatchedHold;
        if (transition.Kind == GripLatchTransitionKind.Latched)
        {
            latchedHold = candidate;
            SetLatchedHold(hand, latchedHold);
            owner.RaiseGripEngagement(
                "GripLatched",
                hand,
                latchedHold,
                "min_fingers=" + minFingers);
        }
        else if (transition.Kind == GripLatchTransitionKind.Frozen)
        {
            owner.RaiseGripEngagement("GripFrozen", hand, latchedHold, "tracking_lost");
        }
        else if (transition.Kind == GripLatchTransitionKind.Released)
        {
            CompleteGripLocomotion(hand);
            owner.RaiseGripEngagement(
                "GripReleased",
                hand,
                latchedHold,
                transition.ReleaseReason.ToRecorderValue());
            SetLatchedHold(hand, null);
            InvalidateAcquisitionSample(hand);
        }

        if (transition.ResetAnchor && trackingValid)
        {
            ResetGripAnchor(hand, now);
        }
    }

    private void StartGripLocomotion(Hand hand, float now)
    {
        ResetGripAnchor(hand, now);
        gripLocomotionHand = hand;
        owner.isGripLocomotionActive = true;
        owner.actionRecorder?.Record(
            "LocomotionStart",
            hand == Hand.Left ? "Left" : "Right",
            hand == Hand.Left ? leftLatchedHold : rightLatchedHold,
            "one-hand grip locomotion started");
    }

    private Vector3 AdvanceGripLocomotion(
        Hand hand,
        Vector3 wristPosition,
        float now)
    {
        GripLocomotionFilter filter = hand == Hand.Left
            ? leftLocomotionFilter
            : rightLocomotionFilter;
        Vector3 movement = filter.Update(wristPosition, now);
        if (filter.LastDiscontinuityReason != GripLocomotionDiscontinuityReason.None)
        {
            Debug.LogWarning("[SceneConfiguror] " +
                             (hand == Hand.Left ? "Left" : "Right") +
                             " grip locomotion re-anchored after " +
                             filter.LastDiscontinuityReason + ".");
        }
        return movement;
    }

    private void CompleteGripLocomotion(Hand hand)
    {
        if (!owner.isGripLocomotionActive || gripLocomotionHand != hand)
        {
            return;
        }

        GripLocomotionFilter filter = hand == Hand.Left
            ? leftLocomotionFilter
            : rightLocomotionFilter;
        filter.Complete();
    }

    private void ResetGripAnchor(Hand hand, float now)
    {
        List<Vector3> positions = hand == Hand.Left
            ? owner.leftHandBonePositions
            : owner.rightHandBonePositions;
        if (positions == null || positions.Count <= GripLocomotionAnchor.OpenXrWristBoneIndex)
        {
            return;
        }

        Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(positions);
        if (hand == Hand.Left)
        {
            owner.leftHandGripStartPosition = wristPosition;
            owner.leftHandGripLastPosition = wristPosition;
            leftLocomotionFilter.Reset(wristPosition, now);
            if (leftLocomotionFilter.LastDiscontinuityReason !=
                GripLocomotionDiscontinuityReason.None)
            {
                Debug.LogError("[SceneConfiguror] Left grip anchor rejected: " +
                               leftLocomotionFilter.LastDiscontinuityReason + ".");
            }
        }
        else
        {
            owner.rightHandGripStartPosition = wristPosition;
            owner.rightHandGripLastPosition = wristPosition;
            rightLocomotionFilter.Reset(wristPosition, now);
            if (rightLocomotionFilter.LastDiscontinuityReason !=
                GripLocomotionDiscontinuityReason.None)
            {
                Debug.LogError("[SceneConfiguror] Right grip anchor rejected: " +
                               rightLocomotionFilter.LastDiscontinuityReason + ".");
            }
        }
    }

    private void UpdateLegacyGripTelemetry(Hand hand)
    {
        GameObject hold = hand == Hand.Left
            ? owner.leftHandInteractingClimbingHold
            : owner.rightHandInteractingClimbingHold;
        bool trackingValid = hand == Hand.Left ? LeftTrackingValid : RightTrackingValid;
        bool isFiveTipContact = trackingValid && hold != null &&
                                owner.CheckIfHandIsGrippingHold((int)hand, hold);
        bool wasFiveTipContact = hand == Hand.Left
            ? leftLegacyFiveTipContact
            : rightLegacyFiveTipContact;
        if (isFiveTipContact && !wasFiveTipContact)
        {
            owner.actionRecorder?.Record(
                "GripStart",
                hand == Hand.Left ? "Left" : "Right",
                hold,
                "legacy_all_five_tips");
        }
        if (hand == Hand.Left)
        {
            leftLegacyFiveTipContact = isFiveTipContact;
        }
        else
        {
            rightLegacyFiveTipContact = isFiveTipContact;
        }
    }

    private void SetLatchedHold(Hand hand, GameObject hold)
    {
        if (hand == Hand.Left)
        {
            leftLatchedHold = hold;
        }
        else
        {
            rightLatchedHold = hold;
        }
    }

    private static void CopyHandBones(
        OVRSkeleton skeleton,
        List<Vector3> positions,
        List<Quaternion> rotations,
        int count)
    {
        positions.Clear();
        rotations.Clear();
        if (positions.Capacity < count)
        {
            positions.Capacity = count;
        }
        if (rotations.Capacity < count)
        {
            rotations.Capacity = count;
        }

        for (int i = 0; i < count; i++)
        {
            Transform bone = skeleton.Bones[i].Transform;
            positions.Add(bone.position);
            rotations.Add(bone.rotation);
        }
    }

    private static bool IsHandTrackingValid(OVRSkeleton skeleton, ref OVRHand hand)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count < TrackedBoneCount)
        {
            return false;
        }

        hand ??= skeleton.GetComponent<OVRHand>();
        return hand != null && hand.IsTracked && hand.IsDataHighConfidence;
    }

    private bool TryGetDegradedGripGeometry(
        GameObject hold,
        out DegradedGripContactGeometry geometry,
        out string error)
    {
        geometry = null;
        if (hold == null)
        {
            error = "A hovered hold is required.";
            return false;
        }

        if (!hold.TryGetComponent(out MeshFilter meshFilter) ||
            meshFilter.sharedMesh == null)
        {
            error = "No root MeshFilter geometry is available.";
            return false;
        }

        Mesh mesh = meshFilter.sharedMesh;
        if (degradedGripGeometry.TryGetValue(mesh, out geometry) &&
            geometry != null)
        {
            error = string.Empty;
            return true;
        }

        if (!DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out geometry,
                out error))
        {
            return false;
        }

        degradedGripGeometry[mesh] = geometry;
        return true;
    }

    private void RecordDegradedGripGeometryFailure(Hand hand, GameObject hold, string reason)
    {
        string holdName = hold != null ? hold.name : "<missing>";
        string details = "hold=" + holdName + "; " + reason;
        DegradedAcquisitionFailureReason = details;
        int holdId = hold != null ? hold.GetInstanceID() : 0;
        if (!reportedDegradedGripGeometryFailures.Add(holdId))
        {
            return;
        }

        string side = hand == Hand.Left ? "Left" : "Right";
        Debug.LogError("[SceneConfiguror] DEGRADED CPU grip acquisition rejected: " + details);
        owner.actionRecorder?.Record("GripAcquisitionFallbackRejected", side, hold, details);
    }
}
