using System;
using UnityEngine;

/// <summary>
/// Horizontal placement of the board against the floor-level tracking origin the participant is
/// standing on. An overhanging climbing face has no single distance from that origin: it closes on
/// the participant by tan(overhang) of horizontal travel per metre of height above the kicker, so
/// the low holds a ground rehearsal starts on are the furthest part of the board away while the
/// crown of a standing participant is the part nearest to being inside it. One number - the
/// horizontal gap between the origin and the vertical kicker plane the board is authored around -
/// fixes both, and the two pull in opposite directions.
/// </summary>
public static class BoardStandoffPolicy
{
    /// <summary>Crown height of a tall standing adult; the highest point the face has to clear.</summary>
    public const float StandingHeightMeters = 1.9f;

    /// <summary>Horizontal gap kept between that crown and the overhanging face.</summary>
    public const float StandingHeadClearanceMeters = 0.22f;

    /// <summary>
    /// <see cref="GetBoardBaseDistanceMeters"/> evaluated on the approved MoonBoard 2016 geometry
    /// (40 degrees above a 0.37 m kicker), rounded to the centimetre. This is the closest the board
    /// can stand before the face starts cutting through a tall participant, and 40 degrees is steep
    /// enough that the bottom grid rows stay beyond a standing reach at every admissible standoff.
    /// </summary>
    public const float DefaultBoardBaseDistanceMeters = 1.5f;

    /// <summary>
    /// Standoff that leaves <paramref name="headClearanceMeters"/> of horizontal room between the
    /// overhanging face and a participant standing <paramref name="standingHeightMeters"/> tall at
    /// the tracking origin.
    /// </summary>
    public static float GetBoardBaseDistanceMeters(
        float overhangAngleDegrees,
        float kickerHeightMeters,
        float standingHeightMeters,
        float headClearanceMeters)
    {
        ValidateBoardGeometry(overhangAngleDegrees, kickerHeightMeters);
        if (!IsFinite(standingHeightMeters) || standingHeightMeters <= kickerHeightMeters)
        {
            throw new ArgumentException(
                "Standing height must be finite and above the kicker.",
                nameof(standingHeightMeters));
        }
        if (!IsFinite(headClearanceMeters) || headClearanceMeters < 0f)
        {
            throw new ArgumentException(
                "Head clearance must be finite and non-negative.",
                nameof(headClearanceMeters));
        }
        return headClearanceMeters +
               GetOverhangRunMeters(overhangAngleDegrees, kickerHeightMeters, standingHeightMeters);
    }

    /// <summary>
    /// Horizontal distance from the tracking origin to the climbing face at a given height above
    /// the floor. Turns negative once the face has overhung past the origin.
    /// </summary>
    public static float GetFaceDistanceMeters(
        float boardBaseDistanceMeters,
        float overhangAngleDegrees,
        float kickerHeightMeters,
        float heightAboveFloorMeters)
    {
        ValidateBoardGeometry(overhangAngleDegrees, kickerHeightMeters);
        if (!IsFinite(boardBaseDistanceMeters) || !IsFinite(heightAboveFloorMeters))
        {
            throw new ArgumentException(
                "Board standoff and sample height must be finite.",
                nameof(boardBaseDistanceMeters));
        }
        return boardBaseDistanceMeters -
               GetOverhangRunMeters(overhangAngleDegrees, kickerHeightMeters, heightAboveFloorMeters);
    }

    private static void ValidateBoardGeometry(float overhangAngleDegrees, float kickerHeightMeters)
    {
        if (!IsFinite(overhangAngleDegrees) || overhangAngleDegrees <= 0f || overhangAngleDegrees >= 90f)
        {
            throw new ArgumentException(
                "Overhang angle must be finite and between 0 and 90 degrees.",
                nameof(overhangAngleDegrees));
        }
        if (!IsFinite(kickerHeightMeters) || kickerHeightMeters < 0f)
        {
            throw new ArgumentException(
                "Kicker height must be finite and non-negative.",
                nameof(kickerHeightMeters));
        }
    }

    private static float GetOverhangRunMeters(
        float overhangAngleDegrees,
        float kickerHeightMeters,
        float heightAboveFloorMeters)
    {
        return Mathf.Tan(overhangAngleDegrees * Mathf.Deg2Rad) *
               (heightAboveFloorMeters - kickerHeightMeters);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
