using System;
using UnityEngine;

/// <summary>
/// Re-expresses the scene-load board seating in the participant's current standing frame.
/// <para>
/// The alignment root is seated once, at load, against the tracking origin: the participant is
/// assumed to be standing on that origin facing +Z, and the board, kicker, and reconstructed
/// room all hang off that assumption. A participant who was standing somewhere else at load -
/// or whose tracking origin moved mid-session - cannot repair it themselves: the study runs
/// bare-handed, so the controller-held system recenter gesture is out of reach. Recentring
/// repairs it in the app's own convention (the rig never moves; the world does): keep only the
/// yaw of the current head pose and rebuild the load-time seating in that frame, so the board
/// fronts the participant exactly as a fresh load would have, had they been standing on the
/// origin. Height is deliberately not taken from the head: world y = 0 is the physical floor
/// the participant is standing on, and a head pose cannot improve on the floor.
/// </para>
/// </summary>
public static class BoardRecenterPolicy
{
    private const float DegenerateAxisSquareMagnitude = 1e-6f;

    /// <summary>
    /// Yaw-only standing frame of a head pose. A head looking toward the horizon defines its yaw
    /// by its forward axis; one looking straight up or down carries no yaw in forward, so the
    /// head's up axis stands in (it tips toward the facing direction when looking down and away
    /// from it when looking up). Fails only when both axes are vertical, which no rigid head
    /// pose produces.
    /// </summary>
    public static bool TryGetStandingYaw(
        Vector3 headForward,
        Vector3 headUp,
        out Quaternion standingYaw)
    {
        RequireFinite(headForward, nameof(headForward));
        RequireFinite(headUp, nameof(headUp));

        Vector3 flatForward = new(headForward.x, 0f, headForward.z);
        if (flatForward.sqrMagnitude < DegenerateAxisSquareMagnitude)
        {
            Vector3 fallback = headForward.y <= 0f ? headUp : -headUp;
            flatForward = new Vector3(fallback.x, 0f, fallback.z);
        }
        if (flatForward.sqrMagnitude < DegenerateAxisSquareMagnitude)
        {
            standingYaw = Quaternion.identity;
            return false;
        }

        standingYaw = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
        return true;
    }

    /// <summary>
    /// The load-time world pose of the alignment root, re-expressed in a standing frame:
    /// horizontal placement follows the head's floor point, height stays the seated pose's own.
    /// </summary>
    public static void GetRecenteredPose(
        Quaternion standingYaw,
        Vector3 headPosition,
        Vector3 seatedPosition,
        Quaternion seatedRotation,
        out Vector3 recenteredPosition,
        out Quaternion recenteredRotation)
    {
        RequireFinite(headPosition, nameof(headPosition));
        RequireFinite(seatedPosition, nameof(seatedPosition));

        Vector3 headFloorPoint = new(headPosition.x, 0f, headPosition.z);
        recenteredPosition = headFloorPoint + standingYaw * seatedPosition;
        recenteredRotation = standingYaw * seatedRotation;
    }

    private static void RequireFinite(Vector3 value, string name)
    {
        if (float.IsNaN(value.x) || float.IsInfinity(value.x) ||
            float.IsNaN(value.y) || float.IsInfinity(value.y) ||
            float.IsNaN(value.z) || float.IsInfinity(value.z))
        {
            throw new ArgumentException("Vector components must be finite.", name);
        }
    }
}
