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

    [Header("Two-hand locomotion")]
    [Tooltip("Let two latched, tracked hands drive the board together. Off restores the single " +
             "driving hand, where a second latch stops the board instead of sharing it.")]
    public bool allowBimanualLocomotion = true;

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
             "upward tilt. Parked below the eye line so it never covers the board.")]
    [Min(0.2f)] public float diagnosticsForwardMeters = 0.62f;
    public float diagnosticsDownMeters = 0.34f;
    [Range(0f, 80f)] public float diagnosticsTiltDegrees = 32f;
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
