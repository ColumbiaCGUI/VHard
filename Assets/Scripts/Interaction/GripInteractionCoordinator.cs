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
    private GripEngagementSettings settings;
    private GripDiagnosticsHud diagnosticsHud;
    private TopOutResetPresenter topOutPresenter;
    private bool leftLocomotionActive;
    private bool rightLocomotionActive;
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
    private int leftLatchedFingerCount;
    private int rightLatchedFingerCount;
    private bool leftLegacyFiveTipContact;
    private bool rightLegacyFiveTipContact;
    private GameObject leftLegacyGripStartHold;
    private GameObject rightLegacyGripStartHold;
    private bool inputSuppressed;
    private bool leftAcquisitionArmed = true;
    private bool rightAcquisitionArmed = true;
    private int leftTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
    private int rightTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
    private float leftTelemetryRecordedAt = float.NegativeInfinity;
    private float rightTelemetryRecordedAt = float.NegativeInfinity;
    private GripShadowDwellTracker leftCoverageShadow;
    private GripShadowDwellTracker rightCoverageShadow;
    private float coverageTrackerDwellSeconds = float.NaN;
    private float coverageTrackerRefireSeconds = float.NaN;
    private Vector3 leftShadowWristBoardLocal;
    private Vector3 rightShadowWristBoardLocal;
    private float leftShadowWristSampledAt = float.NaN;
    private float rightShadowWristSampledAt = float.NaN;
    private bool leftGraceShadowFired;
    private bool rightGraceShadowFired;
    private bool shadowFaulted;
    private readonly GripAcquisitionSample leftShadowAcquisitionSample = new();
    private readonly GripAcquisitionSample rightShadowAcquisitionSample = new();
    private int leftShadowPublishConfidenceMask;
    private int rightShadowPublishConfidenceMask;

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

    /// <summary>The grip rework's tunables live on their own component rather than in the scene,
    /// so the approved SceneConfiguror serialization is untouched and the values can still be
    /// turned in the inspector during a pilot run.</summary>
    private void EnsureSettings()
    {
        if (settings != null)
        {
            return;
        }

        settings = owner.GetComponent<GripEngagementSettings>();
        if (settings == null)
        {
            settings = owner.gameObject.AddComponent<GripEngagementSettings>();
        }
        if (settings.TryDescribeClampedStrongPath(
                owner.gripFlexionEngageThreshold,
                owner.gripFingertipRange,
                owner.defaultMinFingers,
                out string reason))
        {
            Debug.LogError("[SceneConfiguror] Grip engagement settings were clamped: " + reason);
        }
    }

    private void EnsureDiagnosticsHud()
    {
        if (diagnosticsHud != null || !settings.showDiagnosticsPanel)
        {
            return;
        }

        GameObject hudObject = new(GripDiagnosticsHud.RootName + " Root");
        diagnosticsHud = hudObject.AddComponent<GripDiagnosticsHud>();
        diagnosticsHud.Bind(owner, this, settings);
    }

    private void EnsureTopOutPresenter()
    {
        if (topOutPresenter != null || !settings.topOutResetButtonEnabled)
        {
            return;
        }

        GameObject presenterObject = new(TopOutResetPresenter.RootName + " Root");
        topOutPresenter = presenterObject.AddComponent<TopOutResetPresenter>();
        topOutPresenter.Bind(owner, this, settings);
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
        EnsureSettings();
        EnsureDiagnosticsHud();
        EnsureTopOutPresenter();
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

        GripLocomotionPlan plan = GripLocomotionPlanner.Select(
            leftGripLatch.Phase,
            LeftTrackingValid,
            rightGripLatch.Phase,
            RightTrackingValid,
            settings.allowBimanualLocomotion);
        ApplyLocomotionPlan(plan, now);
        if (plan.Mode == GripLocomotionMode.None)
        {
            return;
        }

        Vector3 leftMovement = Vector3.zero;
        Vector3 rightMovement = Vector3.zero;
        if (plan.UsesLeft)
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(owner.leftHandBonePositions);
            leftMovement = AdvanceGripLocomotion(Hand.Left, wristPosition, now);
            owner.leftHandGripLastPosition = wristPosition;
        }
        if (plan.UsesRight)
        {
            Vector3 wristPosition = GripLocomotionAnchor.GetWristPosition(owner.rightHandBonePositions);
            rightMovement = AdvanceGripLocomotion(Hand.Right, wristPosition, now);
            owner.rightHandGripLastPosition = wristPosition;
        }
        owner.MoveStudyEnvironment(
            GripLocomotionPlanner.CombineAnchorMovement(plan, leftMovement, rightMovement));
    }

    /// <summary>Starts and stops each anchor independently, so a hand joining or leaving a
    /// two-hand grip re-anchors only itself and the hand already holding the board keeps its
    /// filter state instead of jumping.</summary>
    private void ApplyLocomotionPlan(in GripLocomotionPlan plan, float now)
    {
        if (leftLocomotionActive && !plan.UsesLeft)
        {
            StopHandLocomotion(Hand.Left);
        }
        if (rightLocomotionActive && !plan.UsesRight)
        {
            StopHandLocomotion(Hand.Right);
        }
        if (plan.UsesLeft && !leftLocomotionActive)
        {
            StartGripLocomotion(Hand.Left, now, plan.AnchorCount);
        }
        if (plan.UsesRight && !rightLocomotionActive)
        {
            StartGripLocomotion(Hand.Right, now, plan.AnchorCount);
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
        if (hand.HasValue)
        {
            StopHandLocomotion(hand.Value);
            return;
        }

        StopHandLocomotion(Hand.Left);
        StopHandLocomotion(Hand.Right);
    }

    private void StopHandLocomotion(Hand hand)
    {
        if (!(hand == Hand.Left ? leftLocomotionActive : rightLocomotionActive))
        {
            return;
        }

        if (hand == Hand.Left)
        {
            leftLocomotionFilter?.Cancel();
            leftLocomotionActive = false;
        }
        else
        {
            rightLocomotionFilter?.Cancel();
            rightLocomotionActive = false;
        }
        owner.isGripLocomotionActive = leftLocomotionActive || rightLocomotionActive;
        owner.actionRecorder?.Record(
            "LocomotionStop",
            hand == Hand.Left ? "Left" : "Right",
            null,
            string.Empty);
    }

    /// <summary>The hold a hand is currently latched onto, or null while the latch is free.</summary>
    public GameObject GetLatchedHold(Hand hand)
    {
        GripLatchStateMachine latch = hand == Hand.Left ? leftGripLatch : rightGripLatch;
        if (latch == null || !latch.IsEngaged)
        {
            return null;
        }
        return hand == Hand.Left ? leftLatchedHold : rightLatchedHold;
    }

    /// <summary>Resets every per-hand grip artifact; the facade owns the visual/hover parts of a
    /// full interaction reset and calls this for the grip share.</summary>
    public void ResetState()
    {
        topOutPresenter?.NotifyInteractionReset();
        StopGripLocomotion();
        owner.SetGripLatchFeedback(Hand.Left, leftLatchedHold, false);
        owner.SetGripLatchFeedback(Hand.Right, rightLatchedHold, false);
        leftGripLatch?.Reset();
        rightGripLatch?.Reset();
        leftGripAcquisitionSample.Invalidate();
        rightGripAcquisitionSample.Invalidate();
        leftLatchedHold = null;
        rightLatchedHold = null;
        leftLatchedFingerCount = 0;
        rightLatchedFingerCount = 0;
        leftLegacyFiveTipContact = false;
        rightLegacyFiveTipContact = false;
        leftLegacyGripStartHold = null;
        rightLegacyGripStartHold = null;
        owner.leftHandIsGripping = false;
        owner.rightHandIsGripping = false;
        owner.isGripLocomotionActive = false;
        leftLocomotionActive = false;
        rightLocomotionActive = false;
        leftTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
        rightTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
        leftCoverageShadow?.Reset();
        rightCoverageShadow?.Reset();
        leftShadowWristSampledAt = float.NaN;
        rightShadowWristSampledAt = float.NaN;
        leftGraceShadowFired = false;
        rightGraceShadowFired = false;
        leftShadowAcquisitionSample.Invalidate();
        rightShadowAcquisitionSample.Invalidate();
    }

    /// <summary>Releases a hold (e.g. an unregistering ghost) from whichever latch holds it.</summary>
    public void ReleaseHold(int holdId)
    {
        if (leftGripLatch != null && leftGripLatch.HoldId == holdId)
        {
            owner.SetGripLatchFeedback(Hand.Left, leftLatchedHold, false);
            leftGripLatch.Reset();
            leftLatchedHold = null;
            leftLatchedFingerCount = 0;
            StopGripLocomotion(Hand.Left);
        }
        if (rightGripLatch != null && rightGripLatch.HoldId == holdId)
        {
            owner.SetGripLatchFeedback(Hand.Right, rightLatchedHold, false);
            rightGripLatch.Reset();
            rightLatchedHold = null;
            rightLatchedFingerCount = 0;
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

        // The pipeline invalidates the real sample on the very frame a tracking dropout nulls
        // its target - before the latch update can look at it - and each epoch refresh replaces
        // it. This coordinator-owned copy is what the shadow paths read, epoch-coherent and
        // untouched by the pipeline's lifecycle. Confidence is captured at publish time as the
        // closest observable proxy for the epoch's tracking state.
        if (hand == Hand.Left)
        {
            leftShadowAcquisitionSample.Publish(holdId, curls, distances, sampledAt);
            leftShadowPublishConfidenceMask =
                GripAcquisitionTelemetry.BuildConfidenceMask(leftFingerConfidence);
        }
        else
        {
            rightShadowAcquisitionSample.Publish(holdId, curls, distances, sampledAt);
            rightShadowPublishConfidenceMask =
                GripAcquisitionTelemetry.BuildConfidenceMask(rightFingerConfidence);
        }
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
        int minFingers = owner.GetMinFingersForHold(candidate);
        GripAcquisitionCriteria criteria = settings.BuildCriteria(
            minFingers,
            owner.gripFlexionEngageThreshold,
            owner.gripFingertipRange);

        if (inputSuppressed)
        {
            ReportDiagnostics(hand, latch, GripEngagementBlock.InputSuppressed, default, criteria, 0, minFingers);
            RecordAcquisitionTelemetry(
                hand, latch, GripEngagementBlock.InputSuppressed, default, 0, minFingers,
                candidate, trackingValid, now);
            return;
        }

        bool acquisitionArmed = hand == Hand.Left ? leftAcquisitionArmed : rightAcquisitionArmed;
        if (!acquisitionArmed)
        {
            // Re-arming asks only that no finger is still flexed enough to engage. Measuring it at
            // the release threshold instead left a hand whose resting curl sits above that
            // threshold disarmed for the rest of the block.
            int armingMask = GripEngagementGate.BuildFlexedMask(curls, criteria.EngageCurl);
            if (trackingValid && GripEngagementGate.CountNonThumbFingers(armingMask) == 0)
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
            ReportDiagnostics(hand, latch, GripEngagementBlock.AwaitingOpenHand, default, criteria, 0, minFingers);
            RecordAcquisitionTelemetry(
                hand, latch, GripEngagementBlock.AwaitingOpenHand, default, 0, minFingers,
                candidate, trackingValid, now);
            return;
        }

        int candidateHoldId = candidate != null ? candidate.GetInstanceID() : 0;
        bool affordancesReady = owner.HoldAffordancesState == HoldAffordancesLoadState.Ready;
        bool canEvaluateAcquisition = latch.Phase == GripLatchPhase.Free &&
                                      candidate != null &&
                                      trackingValid &&
                                      affordancesReady;
        bool useDegradedCpu = DegradedGripContactAcquisition.ShouldUseCpu(
            IsDegradedAcquisitionActive,
            GetGripAcquisitionContext());
        bool acquisitionReady = false;
        GripAcquisitionMasks masks = default;
        if (canEvaluateAcquisition && useDegradedCpu)
        {
            acquisitionReady = TryBuildDegradedGripContactMask(
                hand,
                candidate,
                curls,
                criteria,
                out masks);
        }
        else if (canEvaluateAcquisition && !owner.IsGripFeedbackDegraded && acquisitionSample.IsValid)
        {
            acquisitionReady = true;
            masks = acquisitionSample.ConsumeMasks(candidateHoldId, curls, criteria, now);
        }

        GripAcquisitionVerdict verdict = acquisitionReady
            ? GripEngagementGate.Evaluate(criteria, masks)
            : default;
        int acquiredFingers = GripEngagementGate.CountNonThumbFingers(verdict.AcquiredMask);
        GripLatchTransition transition = latch.Update(
            now,
            trackingValid,
            verdict.CanAcquire,
            verdict.CanAcquire ? candidateHoldId : 0,
            Mathf.Max(1, acquiredFingers),
            verdict.AcquiredMask,
            lowFlexedMask);

        HandleGripLatchTransition(
            hand,
            candidate,
            minFingers,
            acquiredFingers,
            transition,
            now,
            trackingValid);
        GripEngagementBlock block = ResolveEngagementBlock(
            latch.Phase,
            trackingValid,
            candidate,
            affordancesReady,
            acquisitionReady,
            verdict);
        int requiredFingers = verdict.CanAcquire ? verdict.RequiredFingers : minFingers;
        ReportDiagnostics(
            hand,
            latch,
            block,
            masks,
            criteria,
            verdict.CountedFingers,
            requiredFingers);
        RecordAcquisitionTelemetry(
            hand, latch, block, masks, verdict.CountedFingers, requiredFingers,
            candidate, trackingValid, now);
        UpdateShadowAcquisition(
            hand, latch, candidate, candidateHoldId, curls, criteria,
            trackingValid, affordancesReady, useDegradedCpu, now);
    }

    /// <summary>
    /// Shadow acquisition paths: evaluated on every hold and logged, never latched. Coverage
    /// watches for the open-hand grips the curl gate structurally misses; grace watches for a
    /// high-confidence GPU sample landing during a hand-level confidence dropout - the sample
    /// the real path discards. Their would-latch rates from real sessions are the evidence for
    /// deciding whether either becomes a live path, per the mid-enrollment shadow discipline.
    /// </summary>
    private void UpdateShadowAcquisition(
        Hand hand,
        GripLatchStateMachine latch,
        GameObject candidate,
        int candidateHoldId,
        float[] curls,
        in GripAcquisitionCriteria criteria,
        bool trackingValid,
        bool affordancesReady,
        bool useDegradedCpu,
        float now)
    {
        if (shadowFaulted)
        {
            return;
        }

        // Shadow evaluation runs inline between the two hands' real updates, so a fault here
        // must never take the real gate down with it: log it once and retire the shadows for
        // the session.
        try
        {
            ActionRecorder recorder = owner.actionRecorder;
            bool recording = recorder != null && recorder.IsRecording;
            bool freeOnCandidate = latch.Phase == GripLatchPhase.Free && candidate != null &&
                                   affordancesReady && !useDegradedCpu &&
                                   !owner.IsGripFeedbackDegraded;

            if (settings.shadowGraceEnabled)
            {
                UpdateGraceShadow(
                    hand, curls, criteria, candidate, candidateHoldId,
                    freeOnCandidate, trackingValid, recording, recorder, now);
            }
            else
            {
                leftGraceShadowFired = false;
                rightGraceShadowFired = false;
            }
            if (settings.shadowOpenSurfaceEnabled)
            {
                UpdateCoverageShadow(
                    hand, candidate, candidateHoldId,
                    freeOnCandidate && trackingValid, recording, recorder, now);
            }
            else
            {
                leftCoverageShadow?.Reset();
                rightCoverageShadow?.Reset();
                leftShadowWristSampledAt = float.NaN;
                rightShadowWristSampledAt = float.NaN;
            }
        }
        catch (Exception exception)
        {
            shadowFaulted = true;
            Debug.LogException(exception);
            Debug.LogError(
                "[SceneConfiguror] Shadow grip acquisition disabled for this session after the " +
                "exception above; the real gate is unaffected.");
        }
    }

    /// <summary>
    /// The pipeline clears its acquisition sample on the very frame a tracking dropout nulls the
    /// hand's target, before the latch update runs, so the real gate never sees the epoch that
    /// landed on a dropout frame. The coordinator-owned shadow snapshot survives that clear;
    /// peeking it here is the only record that a fully qualified grip evaporated into a
    /// dropout. One row per dropout: the flag re-arms when tracking returns or the candidate
    /// goes away.
    /// </summary>
    private void UpdateGraceShadow(
        Hand hand,
        float[] curls,
        in GripAcquisitionCriteria criteria,
        GameObject candidate,
        int candidateHoldId,
        bool freeOnCandidate,
        bool trackingValid,
        bool recording,
        ActionRecorder recorder,
        float now)
    {
        if (trackingValid || !freeOnCandidate)
        {
            if (hand == Hand.Left)
            {
                leftGraceShadowFired = false;
            }
            else
            {
                rightGraceShadowFired = false;
            }
            return;
        }
        bool fired = hand == Hand.Left ? leftGraceShadowFired : rightGraceShadowFired;
        GripAcquisitionSample shadowSample = hand == Hand.Left
            ? leftShadowAcquisitionSample
            : rightShadowAcquisitionSample;
        if (fired || !recording || !shadowSample.IsValid)
        {
            return;
        }

        GripAcquisitionMasks masks = shadowSample.PeekMasks(
            candidateHoldId,
            curls,
            criteria,
            now,
            settings.shadowGraceWindowSeconds);
        GripAcquisitionVerdict verdict = GripEngagementGate.Evaluate(criteria, masks);
        if (!verdict.CanAcquire)
        {
            return;
        }

        if (hand == Hand.Left)
        {
            leftGraceShadowFired = true;
        }
        else
        {
            rightGraceShadowFired = true;
        }
        recorder.Record(
            GripOpenSurfacePolicy.ShadowLatchAction,
            hand == Hand.Left ? "Left" : "Right",
            candidate,
            GripOpenSurfacePolicy.FormatGraceDetails(
                now - shadowSample.SampledAt,
                masks,
                verdict.CountedFingers,
                verdict.RequiredFingers,
                hand == Hand.Left
                    ? leftShadowPublishConfidenceMask
                    : rightShadowPublishConfidenceMask));
    }

    private void UpdateCoverageShadow(
        Hand hand,
        GameObject candidate,
        int candidateHoldId,
        bool active,
        bool recording,
        ActionRecorder recorder,
        float now)
    {
        // Live-tunable dwell/refire: a changed value rebuilds both trackers rather than driving
        // them with constants captured at first use.
        if (leftCoverageShadow == null ||
            coverageTrackerDwellSeconds != settings.shadowCoverageDwellSeconds ||
            coverageTrackerRefireSeconds != settings.shadowRefireSeconds)
        {
            coverageTrackerDwellSeconds = settings.shadowCoverageDwellSeconds;
            coverageTrackerRefireSeconds = settings.shadowRefireSeconds;
            leftCoverageShadow = new GripShadowDwellTracker(
                coverageTrackerDwellSeconds,
                coverageTrackerRefireSeconds);
            rightCoverageShadow = new GripShadowDwellTracker(
                coverageTrackerDwellSeconds,
                coverageTrackerRefireSeconds);
        }
        GripShadowDwellTracker tracker = hand == Hand.Left ? leftCoverageShadow : rightCoverageShadow;

        float boardSpeed = float.NaN;
        if (active && recording)
        {
            boardSpeed = MeasureBoardLocalWristSpeed(hand, now);
        }
        else if (hand == Hand.Left)
        {
            leftShadowWristSampledAt = float.NaN;
        }
        else
        {
            rightShadowWristSampledAt = float.NaN;
        }

        // Evidence comes from the coordinator's shadow snapshot of the last GPU epoch, so the
        // distances and curls are epoch-coherent and a stalled pipeline reads as no evidence
        // (the epoch ages out) rather than as sustained contact from one frozen measurement.
        GripAcquisitionSample shadowSample = hand == Hand.Left
            ? leftShadowAcquisitionSample
            : rightShadowAcquisitionSample;
        bool eligible = false;
        GripOpenSurfaceEvidence evidence = default;
        bool epochFresh = shadowSample.IsValid &&
                          shadowSample.HoldId == candidateHoldId &&
                          now - shadowSample.SampledAt <= settings.shadowCoverageEpochFreshnessSeconds;
        if (active && recording && epochFresh &&
            !float.IsNaN(boardSpeed) &&
            boardSpeed <= settings.shadowCoverageMaxSpeedMetersPerSecond)
        {
            evidence = GripOpenSurfacePolicy.Measure(
                shadowSample.SampledBoneDistances,
                shadowSample.SampledCurls,
                settings.shadowCoverageContactRangeMeters,
                settings.shadowCoveragePalmRangeMeters);
            eligible = GripOpenSurfacePolicy.IsEligible(
                evidence,
                settings.shadowCoverageMinDigits,
                settings.shadowCoverageMinPadSamples,
                settings.shadowCoverageCurlFloor);
        }

        if (tracker.Update(eligible, candidateHoldId, now, out float sustainedSeconds))
        {
            recorder.Record(
                GripOpenSurfacePolicy.ShadowLatchAction,
                hand == Hand.Left ? "Left" : "Right",
                candidate,
                GripOpenSurfacePolicy.FormatCoverageDetails(
                    evidence,
                    sustainedSeconds,
                    boardSpeed,
                    shadowSample.SampledCurls));
        }
    }

    /// <summary>Wrist speed measured in the board's frame: grip locomotion moves the environment
    /// under a stationary hand, so world-space speed would read a clean mid-pull hang as fast
    /// motion. Only called while the hand is tracked and a recording is live; returns NaN until
    /// two consecutive samples exist.</summary>
    private float MeasureBoardLocalWristSpeed(Hand hand, float now)
    {
        List<Vector3> positions = hand == Hand.Left
            ? owner.leftHandBonePositions
            : owner.rightHandBonePositions;
        Transform board = owner.moonBoardEnv != null ? owner.moonBoardEnv.transform : null;
        if (board == null || positions == null ||
            positions.Count <= GripLocomotionAnchor.OpenXrWristBoneIndex)
        {
            if (hand == Hand.Left)
            {
                leftShadowWristSampledAt = float.NaN;
            }
            else
            {
                rightShadowWristSampledAt = float.NaN;
            }
            return float.NaN;
        }

        Vector3 local = board.InverseTransformPoint(GripLocomotionAnchor.GetWristPosition(positions));
        float lastAt = hand == Hand.Left ? leftShadowWristSampledAt : rightShadowWristSampledAt;
        Vector3 lastLocal = hand == Hand.Left ? leftShadowWristBoardLocal : rightShadowWristBoardLocal;
        float speed = float.NaN;
        if (!float.IsNaN(lastAt) && now > lastAt && now - lastAt < 0.2f)
        {
            speed = (local - lastLocal).magnitude / (now - lastAt);
        }
        if (hand == Hand.Left)
        {
            leftShadowWristBoardLocal = local;
            leftShadowWristSampledAt = now;
        }
        else
        {
            rightShadowWristBoardLocal = local;
            rightShadowWristSampledAt = now;
        }
        return speed;
    }

    /// <summary>Snapshots the acquisition state to the recorder whenever the slow-moving signals
    /// (latch phase, hand validity, per-finger confidence, candidate presence) change. This is
    /// what lets a recorded session separate "the hand was open" from "the tracker could not see
    /// the fingers" - the distinction the pilots' wrist-twist reports hinge on.</summary>
    private void RecordAcquisitionTelemetry(
        Hand hand,
        GripLatchStateMachine latch,
        GripEngagementBlock block,
        in GripAcquisitionMasks masks,
        int countedFingers,
        int requiredFingers,
        GameObject candidate,
        bool trackingValid,
        float now)
    {
        ActionRecorder recorder = owner.actionRecorder;
        if (recorder == null || !recorder.IsRecording)
        {
            // Reset the change detector so the first evaluated state of the next recording is
            // always captured, whatever it is.
            if (hand == Hand.Left)
            {
                leftTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
            }
            else
            {
                rightTelemetryStateKey = GripAcquisitionTelemetry.NoStateKey;
            }
            return;
        }

        int confidenceMask = GripAcquisitionTelemetry.BuildConfidenceMask(
            hand == Hand.Left ? leftFingerConfidence : rightFingerConfidence);
        int stateKey = GripAcquisitionTelemetry.BuildStateKey(
            latch.Phase,
            trackingValid,
            confidenceMask,
            candidate != null);
        if (!GripAcquisitionTelemetry.ShouldRecord(
                stateKey,
                hand == Hand.Left ? leftTelemetryStateKey : rightTelemetryStateKey,
                now,
                hand == Hand.Left ? leftTelemetryRecordedAt : rightTelemetryRecordedAt))
        {
            return;
        }

        recorder.Record(
            GripAcquisitionTelemetry.ActionName,
            hand == Hand.Left ? "Left" : "Right",
            candidate,
            GripAcquisitionTelemetry.FormatDetails(
                latch.Phase,
                block,
                trackingValid,
                confidenceMask,
                masks,
                countedFingers,
                requiredFingers,
                hand == Hand.Left ? leftFingerCurls : rightFingerCurls));
        if (hand == Hand.Left)
        {
            leftTelemetryStateKey = stateKey;
            leftTelemetryRecordedAt = now;
        }
        else
        {
            rightTelemetryStateKey = stateKey;
            rightTelemetryRecordedAt = now;
        }
    }

    private static GripEngagementBlock ResolveEngagementBlock(
        GripLatchPhase phase,
        bool trackingValid,
        GameObject candidate,
        bool affordancesReady,
        bool acquisitionReady,
        in GripAcquisitionVerdict verdict)
    {
        if (phase != GripLatchPhase.Free)
        {
            return GripEngagementBlock.Latched;
        }
        if (!trackingValid)
        {
            return GripEngagementBlock.TrackingLost;
        }
        if (candidate == null)
        {
            return GripEngagementBlock.NoCandidateHold;
        }
        if (!affordancesReady)
        {
            return GripEngagementBlock.AffordancesUnavailable;
        }
        return acquisitionReady ? verdict.Block : GripEngagementBlock.NoContactSample;
    }

    private void ReportDiagnostics(
        Hand hand,
        GripLatchStateMachine latch,
        GripEngagementBlock block,
        in GripAcquisitionMasks masks,
        in GripAcquisitionCriteria criteria,
        int countedFingers,
        int requiredFingers)
    {
        if (diagnosticsHud == null)
        {
            return;
        }

        int latchedFingers = hand == Hand.Left ? leftLatchedFingerCount : rightLatchedFingerCount;
        diagnosticsHud.ReportHand(
            hand,
            latch.Phase,
            block,
            masks,
            criteria,
            block == GripEngagementBlock.Latched ? latchedFingers : countedFingers,
            requiredFingers);
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
        in GripAcquisitionCriteria criteria,
        out GripAcquisitionMasks masks)
    {
        masks = default;
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

        masks = GripAcquisitionMasks.Build(curls, distances, criteria);
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
        int acquiredFingers,
        GripLatchTransition transition,
        float now,
        bool trackingValid)
    {
        GameObject latchedHold = hand == Hand.Left ? leftLatchedHold : rightLatchedHold;
        if (transition.Kind == GripLatchTransitionKind.Latched)
        {
            latchedHold = candidate;
            SetLatchedHold(hand, latchedHold);
            SetLatchedFingerCount(hand, acquiredFingers);
            owner.SetGripLatchFeedback(hand, latchedHold, true);
            owner.RaiseGripEngagement(
                "GripLatched",
                hand,
                latchedHold,
                "minFingers=" + minFingers + ";fingers=" + acquiredFingers);
        }
        else if (transition.Kind == GripLatchTransitionKind.Frozen)
        {
            owner.RaiseGripEngagement("GripFrozen", hand, latchedHold, "reason=tracking_lost");
        }
        else if (transition.Kind == GripLatchTransitionKind.Released)
        {
            CompleteGripLocomotion(hand);
            owner.SetGripLatchFeedback(hand, latchedHold, false);
            owner.RaiseGripEngagement(
                "GripReleased",
                hand,
                latchedHold,
                "reason=" + transition.ReleaseReason.ToRecorderValue());
            SetLatchedHold(hand, null);
            SetLatchedFingerCount(hand, 0);
            InvalidateAcquisitionSample(hand);
            if (hand == Hand.Left)
            {
                leftLegacyGripStartHold = null;
            }
            else
            {
                rightLegacyGripStartHold = null;
            }
        }

        if (transition.ResetAnchor && trackingValid)
        {
            ResetGripAnchor(hand, now);
        }
    }

    public void RestoreLatchFeedback()
    {
        if (leftGripLatch != null && leftGripLatch.IsEngaged)
        {
            owner.SetGripLatchFeedback(Hand.Left, leftLatchedHold, true);
        }
        if (rightGripLatch != null && rightGripLatch.IsEngaged)
        {
            owner.SetGripLatchFeedback(Hand.Right, rightLatchedHold, true);
        }
    }

    private void StartGripLocomotion(Hand hand, float now, int anchorCount)
    {
        ResetGripAnchor(hand, now);
        if (hand == Hand.Left)
        {
            leftLocomotionActive = true;
        }
        else
        {
            rightLocomotionActive = true;
        }
        owner.isGripLocomotionActive = true;
        owner.actionRecorder?.Record(
            "LocomotionStart",
            hand == Hand.Left ? "Left" : "Right",
            hand == Hand.Left ? leftLatchedHold : rightLatchedHold,
            "anchors=" + anchorCount);
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
        if (!(hand == Hand.Left ? leftLocomotionActive : rightLocomotionActive))
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
        // Five-tip contact flickers while a hold stays gripped, so a new GripStart is recorded
        // only when the contact lands on a hold this hand has not already reported since its
        // last release.
        GameObject reportedHold = hand == Hand.Left
            ? leftLegacyGripStartHold
            : rightLegacyGripStartHold;
        if (isFiveTipContact && !wasFiveTipContact && hold != reportedHold)
        {
            owner.actionRecorder?.Record(
                "GripStart",
                hand == Hand.Left ? "Left" : "Right",
                hold,
                "method=legacy_all_five_tips");
            if (hand == Hand.Left)
            {
                leftLegacyGripStartHold = hold;
            }
            else
            {
                rightLegacyGripStartHold = hold;
            }
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

    private void SetLatchedFingerCount(Hand hand, int fingers)
    {
        if (hand == Hand.Left)
        {
            leftLatchedFingerCount = fingers;
        }
        else
        {
            rightLatchedFingerCount = fingers;
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
