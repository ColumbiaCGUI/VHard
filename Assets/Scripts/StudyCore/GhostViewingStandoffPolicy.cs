using System;
using UnityEngine;

/// <summary>
/// Extra board standoff held while detached ghost-hold inspection is the active technique.
/// <para>
/// The shared standoff in <see cref="BoardStandoffPolicy"/> is chosen so a standing participant can
/// reach the board: it seats the vertical kicker plane far enough ahead of the tracking origin that
/// the overhanging face clears their crown, and no further. Detached inspection has the opposite
/// requirement. Nothing on the wall is ever touched - the participant points at a hold and pulls a
/// proxy copy back to their hands - so reach is irrelevant, while seeing and aiming at the whole
/// grid is the entire technique.
/// </para>
/// <para>
/// Those two requirements are not merely different, they conflict. A 40-degree face travels
/// tan(40) horizontally per metre it rises, so by row 18 it has closed 2.13 m on the participant.
/// At the reach standoff of 1.50 m that puts the top rows roughly 0.6 m <em>behind</em> the eye:
/// they cannot be looked at, let alone pointed at, without turning around. This policy solves for
/// the distance at which the whole grid instead subtends a comfortable vertical angle, and the
/// ghost technique adds the difference on top of whatever pose board alignment resolved.
/// </para>
/// </summary>
public static class GhostViewingStandoffPolicy
{
    /// <summary>Eye height of a standing adult at the tracking origin.</summary>
    public const float StandingEyeHeightMeters = 1.62f;

    /// <summary>
    /// Vertical angle the grid is allowed to subtend. The Quest 3 renders far more than this, so
    /// the binding constraint is the neck rather than the display: at 70 degrees the top row sits
    /// about 48 degrees above the horizon, which is the edge of sustained comfortable extension,
    /// and the whole grid stays inside one head pose.
    /// </summary>
    public const float ComfortableVerticalFieldOfViewDegrees = 70f;

    /// <summary>
    /// <see cref="GetExtraStandoffMeters"/> evaluated on the approved MoonBoard 2016 geometry
    /// (rows 1-18 of the 40-degree main surface, a standing eye, 70 degrees of comfortable
    /// vertical angle), rounded to the decimetre. The solved viewing distance is 3.28 m against
    /// the 1.50 m reach standoff. Rounding up to 1.80 m of extra standoff costs about a degree of
    /// subtended angle and buys a round, hand-checkable number.
    /// <para>
    /// The pointing cost of the extra distance is bounded: the farthest hold ends up about 3.35 m
    /// away, where the 0.20 m grid pitch still separates neighbouring holds by 3.4 degrees, which
    /// is wider than a stabilised hand ray wanders.
    /// </para>
    /// </summary>
    public const float DefaultExtraStandoffMeters = 1.8f;

    /// <summary>Widest extra standoff the ghost technique may be configured to take.</summary>
    public const float MaximumExtraStandoffMeters = 4f;

    /// <summary>
    /// Vertical angle between the highest and lowest grid rows as seen from a standing eye at the
    /// tracking origin. Row poses are board-local: <paramref name="topLocalZ"/> and
    /// <paramref name="bottomLocalZ"/> are signed offsets from the board base plane, negative
    /// towards the participant, so a row's distance ahead of the eye is the standoff plus its
    /// local z.
    /// </summary>
    public static float GetSubtendedVerticalAngleDegrees(
        float boardBaseDistanceMeters,
        float topHeightMeters,
        float topLocalZ,
        float bottomHeightMeters,
        float bottomLocalZ,
        float eyeHeightMeters)
    {
        ValidateViewingGeometry(
            topHeightMeters,
            topLocalZ,
            bottomHeightMeters,
            bottomLocalZ,
            eyeHeightMeters);
        if (!IsFinite(boardBaseDistanceMeters))
        {
            throw new ArgumentException(
                "Board standoff must be finite.",
                nameof(boardBaseDistanceMeters));
        }

        float topDistance = boardBaseDistanceMeters + topLocalZ;
        float bottomDistance = boardBaseDistanceMeters + bottomLocalZ;
        if (topDistance <= 0f || bottomDistance <= 0f)
        {
            throw new ArgumentException(
                "Board standoff leaves part of the grid level with or behind the eye.",
                nameof(boardBaseDistanceMeters));
        }

        return Mathf.Rad2Deg * (
            Mathf.Atan((topHeightMeters - eyeHeightMeters) / topDistance) +
            Mathf.Atan((eyeHeightMeters - bottomHeightMeters) / bottomDistance));
    }

    /// <summary>
    /// Standoff at which the grid subtends exactly
    /// <paramref name="comfortableVerticalFieldOfViewDegrees"/>. The subtended angle falls
    /// monotonically once the whole grid is ahead of the eye, so this bisects on that branch.
    /// </summary>
    public static float GetViewingDistanceMeters(
        float topHeightMeters,
        float topLocalZ,
        float bottomHeightMeters,
        float bottomLocalZ,
        float eyeHeightMeters,
        float comfortableVerticalFieldOfViewDegrees)
    {
        ValidateViewingGeometry(
            topHeightMeters,
            topLocalZ,
            bottomHeightMeters,
            bottomLocalZ,
            eyeHeightMeters);
        if (!IsFinite(comfortableVerticalFieldOfViewDegrees) ||
            comfortableVerticalFieldOfViewDegrees <= 0f ||
            comfortableVerticalFieldOfViewDegrees >= 180f)
        {
            throw new ArgumentException(
                "Comfortable vertical field of view must be finite and between 0 and 180 degrees.",
                nameof(comfortableVerticalFieldOfViewDegrees));
        }

        // Both rows are ahead of the eye only beyond this distance, and the subtended angle is
        // monotone decreasing from there, so the solution is unique on [near, far].
        float near = Mathf.Max(-topLocalZ, -bottomLocalZ) + MinimumEyeToGridClearanceMeters;
        float far = near;
        for (int doubling = 0; doubling < MaximumBracketDoublings; doubling++)
        {
            far = near + BracketSpanMeters * (1 << doubling);
            if (GetSubtendedVerticalAngleDegrees(
                    far,
                    topHeightMeters,
                    topLocalZ,
                    bottomHeightMeters,
                    bottomLocalZ,
                    eyeHeightMeters) <= comfortableVerticalFieldOfViewDegrees)
            {
                break;
            }
            if (doubling == MaximumBracketDoublings - 1)
            {
                throw new ArgumentException(
                    "The grid never falls inside the requested field of view at any standoff.",
                    nameof(comfortableVerticalFieldOfViewDegrees));
            }
        }

        for (int iteration = 0; iteration < BisectionIterations; iteration++)
        {
            float middle = (near + far) * 0.5f;
            if (GetSubtendedVerticalAngleDegrees(
                    middle,
                    topHeightMeters,
                    topLocalZ,
                    bottomHeightMeters,
                    bottomLocalZ,
                    eyeHeightMeters) > comfortableVerticalFieldOfViewDegrees)
            {
                near = middle;
            }
            else
            {
                far = middle;
            }
        }
        return far;
    }

    /// <summary>
    /// How much farther than <paramref name="baseBoardDistanceMeters"/> the board has to sit for
    /// the whole grid to fall inside the comfortable viewing angle. Never negative: a reach
    /// standoff that already reads the whole grid is left alone.
    /// </summary>
    public static float GetExtraStandoffMeters(
        float baseBoardDistanceMeters,
        float topHeightMeters,
        float topLocalZ,
        float bottomHeightMeters,
        float bottomLocalZ,
        float eyeHeightMeters,
        float comfortableVerticalFieldOfViewDegrees)
    {
        if (!IsFinite(baseBoardDistanceMeters) || baseBoardDistanceMeters <= 0f)
        {
            throw new ArgumentException(
                "Base board standoff must be finite and positive.",
                nameof(baseBoardDistanceMeters));
        }

        return Mathf.Max(
            0f,
            GetViewingDistanceMeters(
                topHeightMeters,
                topLocalZ,
                bottomHeightMeters,
                bottomLocalZ,
                eyeHeightMeters,
                comfortableVerticalFieldOfViewDegrees) - baseBoardDistanceMeters);
    }

    /// <summary>
    /// Horizontal, board-facing direction the extra standoff is taken along. The alignment root's
    /// local +z points away from the participant - the grid hangs towards -z, which is what makes
    /// the face overhang them - so moving the root along its own +z retreats the board without
    /// disturbing the calibrated heading.
    /// </summary>
    public static Vector3 GetRetreatDirection(Quaternion boardAlignmentRotation)
    {
        Vector3 forward = boardAlignmentRotation * Vector3.forward;
        Vector3 horizontal = new(forward.x, 0f, forward.z);
        if (horizontal.sqrMagnitude < MinimumHeadingSqrMagnitude)
        {
            throw new ArgumentException(
                "Board alignment has no horizontal heading to retreat along.",
                nameof(boardAlignmentRotation));
        }
        return horizontal.normalized;
    }

    /// <summary>Clamps a serialized override into the admissible range.</summary>
    public static float ClampExtraStandoffMeters(float extraStandoffMeters)
    {
        if (!IsFinite(extraStandoffMeters))
        {
            throw new ArgumentException(
                "Ghost viewing standoff must be finite.",
                nameof(extraStandoffMeters));
        }
        return Mathf.Clamp(extraStandoffMeters, 0f, MaximumExtraStandoffMeters);
    }

    private const float MinimumEyeToGridClearanceMeters = 0.25f;
    private const float BracketSpanMeters = 2f;
    private const int MaximumBracketDoublings = 12;
    private const int BisectionIterations = 60;
    private const float MinimumHeadingSqrMagnitude = 1e-6f;

    private static void ValidateViewingGeometry(
        float topHeightMeters,
        float topLocalZ,
        float bottomHeightMeters,
        float bottomLocalZ,
        float eyeHeightMeters)
    {
        if (!IsFinite(topHeightMeters) || !IsFinite(topLocalZ) ||
            !IsFinite(bottomHeightMeters) || !IsFinite(bottomLocalZ) || !IsFinite(eyeHeightMeters))
        {
            throw new ArgumentException("Board viewing geometry must be finite.");
        }
        if (topHeightMeters <= bottomHeightMeters)
        {
            throw new ArgumentException(
                "The top of the grid must sit above its bottom.",
                nameof(topHeightMeters));
        }
        if (eyeHeightMeters <= bottomHeightMeters || eyeHeightMeters >= topHeightMeters)
        {
            throw new ArgumentException(
                "The eye must sit between the bottom and top of the grid.",
                nameof(eyeHeightMeters));
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
