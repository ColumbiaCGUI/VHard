using UnityEngine;

public enum GripLocomotionMode
{
    None,
    Left,
    Right,
    Bimanual,
}

public readonly struct GripLocomotionPlan
{
    public GripLocomotionPlan(GripLocomotionMode mode)
    {
        Mode = mode;
    }

    public GripLocomotionMode Mode { get; }
    public bool UsesLeft => Mode == GripLocomotionMode.Left || Mode == GripLocomotionMode.Bimanual;
    public bool UsesRight => Mode == GripLocomotionMode.Right || Mode == GripLocomotionMode.Bimanual;
    public int AnchorCount => (UsesLeft ? 1 : 0) + (UsesRight ? 1 : 0);
}

/// <summary>Chooses which latched hands drive the board. A hand drives only while it is latched
/// and tracked; any hand that is engaged without being able to drive — frozen, or latched with
/// tracking lost — holds the board still instead, because its hold has not been let go of. With
/// two driving hands the board follows both, which is what a match or a two-hand pull looks like;
/// setting <c>allowBimanual</c> false restores the single-anchor rule exactly.</summary>
public static class GripLocomotionPlanner
{
    public static GripLocomotionPlan Select(
        GripLatchPhase leftPhase,
        bool leftTrackingValid,
        GripLatchPhase rightPhase,
        bool rightTrackingValid,
        bool allowBimanual)
    {
        bool leftDrives = leftPhase == GripLatchPhase.Latched && leftTrackingValid;
        bool rightDrives = rightPhase == GripLatchPhase.Latched && rightTrackingValid;
        bool leftHolds = leftPhase != GripLatchPhase.Free && !leftDrives;
        bool rightHolds = rightPhase != GripLatchPhase.Free && !rightDrives;
        if (leftHolds || rightHolds)
        {
            return new GripLocomotionPlan(GripLocomotionMode.None);
        }

        if (leftDrives && rightDrives)
        {
            return new GripLocomotionPlan(
                allowBimanual ? GripLocomotionMode.Bimanual : GripLocomotionMode.None);
        }
        if (leftDrives)
        {
            return new GripLocomotionPlan(GripLocomotionMode.Left);
        }
        return new GripLocomotionPlan(
            rightDrives ? GripLocomotionMode.Right : GripLocomotionMode.None);
    }

    /// <summary>The board is translated only, so the displacement that best satisfies every anchor
    /// at once is the mean of their displacements: two hands moving together move the board with
    /// them, and a hand that stays put halves what the moving hand can drag the board by.</summary>
    public static Vector3 CombineAnchorMovement(in GripLocomotionPlan plan, Vector3 left, Vector3 right)
    {
        int anchors = plan.AnchorCount;
        if (anchors == 0)
        {
            return Vector3.zero;
        }

        Vector3 total = Vector3.zero;
        if (plan.UsesLeft)
        {
            total += left;
        }
        if (plan.UsesRight)
        {
            total += right;
        }
        return total / anchors;
    }
}
