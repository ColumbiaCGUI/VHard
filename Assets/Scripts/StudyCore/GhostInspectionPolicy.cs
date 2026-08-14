using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// How many detached proxies may be live at once, and which one gives way when another is summoned.
/// <para>
/// A ghost is an <c>Instantiate</c> of a scene hold, so it shares the source hold's mesh and
/// material rather than allocating its own: the marginal cost of a live ghost is one draw call and
/// one near-field copy of a mesh the board is already drawing 140 of, plus a sphere collider and a
/// kinematic body. Rendering is therefore not what bounds the count. Occlusion is. Proxies are
/// pulled to roughly half a metre in front of the face, and past a handful of them at that range
/// they hide each other and the board behind them, which is the opposite of what detached
/// inspection is for. The ceiling is the route: a locked route carries at most eight holds, so no
/// more than eight distinct proxies can ever exist.
/// </para>
/// </summary>
public static class GhostRegistryPolicy
{
    /// <summary>A locked study route carries at most eight holds, so eight distinct proxies is the
    /// hard ceiling regardless of configuration.</summary>
    public const int MaximumLiveGhostCeiling = 8;

    /// <summary>Proxies live at arm's length; four is as many as stay individually legible there.</summary>
    public const int DefaultMaximumLiveGhosts = 4;

    public static int ClampMaximumLiveGhosts(int maximumLiveGhosts)
    {
        return Mathf.Clamp(maximumLiveGhosts, 1, MaximumLiveGhostCeiling);
    }

    /// <summary>
    /// Index of the ghost to retire so a newly summoned hold fits, or -1 when there is room.
    /// Oldest first, except that a proxy a hand is currently holding is never pulled out of that
    /// hand: it yields only when every live proxy is held.
    /// </summary>
    public static int SelectEvictionIndex(
        IReadOnlyList<float> spawnTimes,
        IReadOnlyList<bool> manipulated,
        int maximumLiveGhosts)
    {
        if (spawnTimes == null)
        {
            throw new ArgumentNullException(nameof(spawnTimes));
        }
        if (manipulated == null)
        {
            throw new ArgumentNullException(nameof(manipulated));
        }
        if (spawnTimes.Count != manipulated.Count)
        {
            throw new ArgumentException(
                "Ghost spawn times and manipulation flags must describe the same registry.",
                nameof(manipulated));
        }
        if (maximumLiveGhosts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLiveGhosts));
        }
        if (spawnTimes.Count < maximumLiveGhosts)
        {
            return -1;
        }

        int oldestFree = -1;
        int oldestOverall = -1;
        for (int index = 0; index < spawnTimes.Count; index++)
        {
            float spawnTime = spawnTimes[index];
            if (float.IsNaN(spawnTime) || float.IsInfinity(spawnTime))
            {
                throw new ArgumentException("Ghost spawn times must be finite.", nameof(spawnTimes));
            }
            if (oldestOverall < 0 || spawnTime < spawnTimes[oldestOverall])
            {
                oldestOverall = index;
            }
            if (!manipulated[index] && (oldestFree < 0 || spawnTime < spawnTimes[oldestFree]))
            {
                oldestFree = index;
            }
        }
        return oldestFree >= 0 ? oldestFree : oldestOverall;
    }
}

/// <summary>
/// Angular distance between a detached proxy's current pose and the orientation its hold actually
/// holds on the wall, and how that distance is shown.
/// <para>
/// This is the point of detached inspection rather than a decoration on it. Pulling a hold off the
/// wall makes its shape readable but destroys the one thing a climber has to internalise about it -
/// how it is turned - so the technique has to hand that back. The indicator encodes the deviation
/// as the <em>angular extent of an arc</em>, one degree of arc per degree of error, closing to
/// nothing when the proxy is turned true, plus the number in degrees. Extent, not hue, carries the
/// signal: the red-amber-green ramp and the silhouette fresnel both already mean grip quality on
/// these same objects, and a second hue ramp on the same hold would be unreadable. The arc's colour
/// only brightens, violet to white, which no other cue in the scene uses.
/// </para>
/// </summary>
public static class GhostOrientationIndicatorPolicy
{
    /// <summary>Deviation at or below which the proxy reads as turned true.</summary>
    public const float AlignmentToleranceDegrees = 5f;

    /// <summary>Colour of a proxy turned far from its wall orientation.</summary>
    public static readonly Color MisalignedColor = new(0.62f, 0.45f, 0.85f, 0.85f);

    /// <summary>Colour of a proxy turned true.</summary>
    public static readonly Color AlignedColor = new(1f, 1f, 1f, 0.95f);

    public static float GetDeviationDegrees(Quaternion ghostRotation, Quaternion wallRotation)
    {
        float deviation = Quaternion.Angle(ghostRotation, wallRotation);
        if (float.IsNaN(deviation) || float.IsInfinity(deviation))
        {
            throw new ArgumentException("Ghost orientation deviation is not a finite angle.");
        }
        return Mathf.Clamp(deviation, 0f, 180f);
    }

    /// <summary>One degree of arc per degree of error, so the ring closes as the proxy comes true.</summary>
    public static float GetArcSweepDegrees(float deviationDegrees)
    {
        return ValidateDeviation(deviationDegrees);
    }

    /// <summary>Zero when the proxy is turned as far from true as it can be, one when it is true.</summary>
    public static float GetAlignmentFraction(float deviationDegrees)
    {
        return 1f - ValidateDeviation(deviationDegrees) / 180f;
    }

    public static bool IsAligned(float deviationDegrees)
    {
        return ValidateDeviation(deviationDegrees) <= AlignmentToleranceDegrees;
    }

    /// <summary>
    /// Brightens towards white as the proxy comes true. The ramp is squared so the last few degrees
    /// - the ones a participant is actually hunting for - carry most of the visible change.
    /// </summary>
    public static Color GetIndicatorColor(float deviationDegrees)
    {
        float alignment = GetAlignmentFraction(deviationDegrees);
        return Color.Lerp(MisalignedColor, AlignedColor, alignment * alignment);
    }

    public static string FormatDeviationDegrees(float deviationDegrees)
    {
        return Mathf.RoundToInt(ValidateDeviation(deviationDegrees))
                   .ToString(CultureInfo.InvariantCulture) + "°";
    }

    private static float ValidateDeviation(float deviationDegrees)
    {
        if (float.IsNaN(deviationDegrees) || float.IsInfinity(deviationDegrees) ||
            deviationDegrees < 0f || deviationDegrees > 180f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviationDegrees),
                "Ghost orientation deviation must lie between 0 and 180 degrees.");
        }
        return deviationDegrees;
    }
}
