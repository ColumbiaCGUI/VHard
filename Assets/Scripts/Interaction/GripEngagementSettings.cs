using UnityEngine;

/// <summary>Tuning the grip rework adds on top of the SceneConfiguror grip block. Added to the
/// SceneConfiguror object at runtime by <see cref="GripInteractionCoordinator"/>, so it is never
/// baked into the study scene and every value here can be turned while a pilot run is live.
/// </summary>
public sealed class GripEngagementSettings : MonoBehaviour
{
    public const string ObjectSuffix = " Grip Tuning";

    [Header("Partial-finger engagement")]
    [Tooltip("Fewest fingers that can engage a hold on strong evidence alone, when the hold's " +
             "normal minimum is out of reach. Never demands more fingers than that minimum.")]
    [Range(1, 4)] public int strongContactFingerFloor = 1;
    [Tooltip("Flexion a finger must reach on the strong path. Raised above the engagement " +
             "threshold so a relaxed hand brushing past a hold cannot latch on one finger.")]
    [Range(0f, 1f)] public float strongFlexionThreshold = 0.75f;
    [Tooltip("Fingertip-to-hold distance a finger must reach on the strong path. Clamped to the " +
             "SceneConfiguror contact range when that range is tighter.")]
    [Min(0.001f)] public float strongContactRangeMeters = 0.01f;
    [Tooltip("Let the thumb count toward a hold's finger minimum, for pinch grips. Spec 08 " +
             "excludes it; an engagement always needs at least one non-thumb finger either way.")]
    public bool thumbCountsTowardMinimum;

    [Header("Live acquisition paths (gate v2, 2026-08-19)")]
    [Tooltip("Coverage is a REAL acquisition path: the open-surface rule latches drags, slopers " +
             "and palms the curl gate structurally excludes. Uses the frozen shadow-era values " +
             "below. While on, the coverage shadow logger stands down (the latch itself records " +
             "path=coverage).")]
    public bool coverageAcquisitionEnabled = true;
    [Tooltip("Grace is a REAL acquisition path: a fresh, fully qualified GPU sample landing on " +
             "the frame a hand-level dropout nulls the target completes the latch, which then " +
             "rides the existing freeze/resume machinery. While on, the grace shadow logger " +
             "stands down (the latch itself records path=grace).")]
    public bool graceAcquisitionEnabled = true;
    [Tooltip("Pad/tip bone distance that SUSTAINS a coverage-latched grip. Wider than the " +
             "acquisition range so evidence flicker at the surface cannot pump releases.")]
    [Min(0.001f)] public float coverageReleaseContactRangeMeters = 0.025f;
    [Tooltip("Maximum age of the contact epoch used to sustain a coverage-latched grip. More " +
             "lenient than acquisition freshness: a brief pipeline stall must not shed a held " +
             "drag, and flexion evidence still sustains in parallel.")]
    [Range(0.05f, 0.5f)] public float coverageReleaseFreshnessSeconds = 0.3f;

    [Header("Shadow acquisition paths (log-only; never latch)")]
    [Tooltip("Log GripShadowLatch path=coverage events when the open-surface contact-coverage " +
             "rule would have latched. Ignored while the live coverage path is on.")]
    public bool shadowOpenSurfaceEnabled = true;
    [Tooltip("Pad/tip bone distance a digit must reach to count toward coverage.")]
    [Min(0.001f)] public float shadowCoverageContactRangeMeters = 0.015f;
    [Tooltip("Palm or wrist bone distance recorded as palm evidence. Telemetry only.")]
    [Min(0.001f)] public float shadowCoveragePalmRangeMeters = 0.03f;
    [Tooltip("Distinct non-thumb digits the coverage rule requires.")]
    [Range(1, 4)] public int shadowCoverageMinDigits = 2;
    [Tooltip("Contacting pad/tip bones across those digits the rule requires.")]
    [Range(1, 12)] public int shadowCoverageMinPadSamples = 3;
    [Tooltip("Lowest max-digit curl that still reads as gripping rather than hanging slack.")]
    [Range(0f, 1f)] public float shadowCoverageCurlFloor = 0.1f;
    [Tooltip("Seconds the coverage evidence must hold continuously before it would latch.")]
    [Min(0f)] public float shadowCoverageDwellSeconds = 0.12f;
    [Tooltip("Maximum age of the GPU contact epoch the coverage rule may treat as current. A " +
             "stalled pipeline must read as no evidence, never as sustained contact.")]
    [Range(0.02f, 0.2f)] public float shadowCoverageEpochFreshnessSeconds = 0.1f;
    [Tooltip("Board-relative wrist speed above which the hand reads as reaching past, not gripping.")]
    [Min(0.01f)] public float shadowCoverageMaxSpeedMetersPerSecond = 0.25f;
    [Tooltip("Ineligibility gap after a fire before the same hold may fire again.")]
    [Min(0f)] public float shadowRefireSeconds = 0.5f;
    [Tooltip("Log GripShadowLatch path=grace events when a fresh high-confidence GPU sample " +
             "would have latched during a hand-level confidence dropout. Ignored while the live " +
             "grace path is on.")]
    public bool shadowGraceEnabled = true;
    [Tooltip("Maximum sample age the grace would accept during a confidence dropout.")]
    [Range(0.01f, 0.1f)] public float shadowGraceWindowSeconds = 0.08f;

    [Header("Two-hand locomotion")]
    [Tooltip("Let two latched, tracked hands drive the board together. Off restores the single " +
             "driving hand, where a second latch stops the board instead of sharing it.")]
    public bool allowBimanualLocomotion = true;

    [Header("Top-out reset button")]
    [Tooltip("Spawn a pokeable BACK TO START button once both hands have latched the route's " +
             "finish. Grip mode only; pressing it releases the grips and restores the board " +
             "to its start pose without touching the run or the console.")]
    public bool topOutResetButtonEnabled = true;
    [Tooltip("Seconds both hands must stay latched on the finish before the button appears.")]
    [Min(0f)] public float topOutHoldSeconds = 0.5f;
    [Tooltip("Seconds the button lingers after a hand leaves the finish, so it can be poked.")]
    [Min(0.5f)] public float topOutLingerSeconds = 8f;
    [Tooltip("Seconds a fingertip must stay on the button to press it; filters brushes.")]
    [Min(0f)] public float topOutPressDwellSeconds = 0.25f;
    [Tooltip("How far in front of the button face a fingertip already counts as pressing.")]
    [Min(0.01f)] public float topOutPressDepthMeters = 0.05f;
    [Tooltip("Height of the button centre above the finish hold, up the vertical wall the " +
             "button is mounted on.")]
    [Min(0f)] public float topOutButtonAboveFinishMeters = 0.3f;

    [Header("Grip diagnostics panel")]
    [Tooltip("Build and drive the anatomy panel at all. Identical in conditions B and C.")]
    public bool showDiagnosticsPanel = true;
    [Tooltip("Keep the panel up for the whole block. Off shows it only while a hand is on a hold, " +
             "part-way into a grip, or latched.")]
    public bool alwaysShowDiagnosticsPanel = true;
    [Tooltip("How long the panel lingers after the last hand leaves a hold, when it is not pinned.")]
    [Min(0f)] public float diagnosticsLingerSeconds = 1.5f;
    [Tooltip("Seconds between text rebuilds. The panel follows the head every frame regardless.")]
    [Min(0.02f)] public float diagnosticsRefreshSeconds = 0.1f;
    [Tooltip("Panel placement in the head's yaw frame: metres forward, metres down, degrees of " +
             "tilt (0 = vertical, positive leans the top toward the viewer, negative away). " +
             "Parked below the eye line so it never covers the board.")]
    [Min(0.2f)] public float diagnosticsForwardMeters = 0.62f;
    public float diagnosticsDownMeters = 0.34f;
    [Range(-45f, 80f)] public float diagnosticsTiltDegrees = 0f;
    [Tooltip("Seconds for the panel to catch up with the head. Zero locks it rigidly.")]
    [Min(0f)] public float diagnosticsFollowSeconds = 0.12f;

    public bool TryDescribeClampedStrongPath(
        float engageCurl,
        float contactRange,
        int minFingers,
        out string reason)
    {
        if (strongFlexionThreshold < engageCurl)
        {
            reason = "strong flexion " + strongFlexionThreshold + " is below the engagement " +
                     "threshold " + engageCurl + "; the strong path will use the engagement value.";
            return true;
        }
        if (strongContactRangeMeters > contactRange)
        {
            reason = "strong contact range " + strongContactRangeMeters + " m is wider than the " +
                     "grip fingertip range " + contactRange + " m; the strong path will use the " +
                     "fingertip range.";
            return true;
        }
        if (strongContactFingerFloor > minFingers)
        {
            reason = "strong finger floor " + strongContactFingerFloor + " exceeds the default " +
                     "minimum " + minFingers + "; the strong path will use the default minimum.";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    public string DescribeGateVersion()
    {
        return GripGateVersionPolicy.Describe(coverageAcquisitionEnabled, graceAcquisitionEnabled);
    }

    public GripAcquisitionCriteria BuildCriteria(
        int minFingers,
        float engageCurl,
        float contactRange)
    {
        return new GripAcquisitionCriteria(
            minFingers,
            thumbCountsTowardMinimum,
            strongContactFingerFloor,
            engageCurl,
            Mathf.Max(strongFlexionThreshold, engageCurl),
            contactRange,
            Mathf.Min(strongContactRangeMeters, contactRange));
    }
}
