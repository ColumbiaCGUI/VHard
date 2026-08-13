using System;
using System.Collections.Generic;
using UnityEngine;

public enum RouteCueRole
{
    Start,
    Intermediate,
    Finish,
}

public enum RouteCuePresentation
{
    Hidden,
    PhysicalBoardLeds,
    VirtualHalos,
}

public readonly struct RouteCueStyle
{
    public RouteCueStyle(Color color, int ringCount)
    {
        Color = color;
        RingCount = ringCount;
    }

    public Color Color { get; }
    public int RingCount { get; }
}

public static class RouteCuePolicy
{
    public static readonly Color StartColor = new(0f, 0.85f, 0.25f, 1f);
    public static readonly Color IntermediateColor = new(0.2f, 0.55f, 1f, 1f);
    public static readonly Color FinishColor = new(0.95f, 0.15f, 0.1f, 1f);

    public const float RingInnerScale = 0.65f;
    public const float RingOutwardOffsetMeters = 0.015f;
    public const float RingOuterDiameterFactor = 1.35f;
    public const float MinimumRingOuterDiameterMeters = 0.14f;
    public const float MaximumRingOuterDiameterMeters = 0.3f;

    /// <summary>Superseded and unused: the live channel is set by SceneConfiguror.SetGameMode and
    /// read from CurrentRouteCuePresentation, which reports VirtualHalos for B and C since the
    /// 2026-08-13 route-cue freeze. Do not reintroduce this as the source of truth.</summary>
    public static RouteCuePresentation ForCondition(
        string condition,
        RouteCuePresentation baselinePresentation)
    {
        return condition switch
        {
            "A" => baselinePresentation,
            "B" => RouteCuePresentation.Hidden,
            "C" => RouteCuePresentation.Hidden,
            _ => throw new ArgumentException("Study condition must be A, B, or C.", nameof(condition)),
        };
    }

    public static RouteCueStyle GetStyle(RouteCueRole role)
    {
        return role switch
        {
            RouteCueRole.Start => new RouteCueStyle(StartColor, 2),
            RouteCueRole.Finish => new RouteCueStyle(FinishColor, 2),
            _ => new RouteCueStyle(IntermediateColor, 1),
        };
    }

    public static Vector3 ProjectGridAnchorOntoBoard(
        Vector3 gridAnchor,
        Vector3 boardPlanePoint,
        Vector3 boardNormal,
        float outwardOffset)
    {
        if (boardNormal.sqrMagnitude <= Mathf.Epsilon)
        {
            throw new ArgumentException("Board normal cannot be zero.", nameof(boardNormal));
        }
        Vector3 normal = boardNormal.normalized;
        return gridAnchor - normal * Vector3.Dot(gridAnchor - boardPlanePoint, normal) +
               normal * outwardOffset;
    }

    public static float GetRingOuterDiameterMeters(float holdExtentMeters)
    {
        if (!(holdExtentMeters > 0f) || float.IsInfinity(holdExtentMeters))
        {
            throw new ArgumentException(
                "Hold extent must be finite and positive.", nameof(holdExtentMeters));
        }
        return Mathf.Clamp(
            holdExtentMeters * RingOuterDiameterFactor,
            MinimumRingOuterDiameterMeters,
            MaximumRingOuterDiameterMeters);
    }

    /// <summary>Orients the board normal toward the climber using the route's own hold meshes:
    /// holds protrude from the climbing face, so the side their bounds centres fall on is the
    /// outward side. Derived from geometry rather than assumed from the surface's authored
    /// winding, which the twin's board plane does not guarantee.</summary>
    public static Vector3 ResolveOutwardNormal(
        Vector3 boardNormal,
        Vector3 boardPlanePoint,
        IReadOnlyList<Vector3> holdCentres)
    {
        if (boardNormal.sqrMagnitude <= Mathf.Epsilon)
        {
            throw new ArgumentException("Board normal cannot be zero.", nameof(boardNormal));
        }
        if (holdCentres == null || holdCentres.Count == 0)
        {
            throw new ArgumentException(
                "Outward normal requires at least one hold centre.", nameof(holdCentres));
        }

        Vector3 normal = boardNormal.normalized;
        float signedTotal = 0f;
        for (int index = 0; index < holdCentres.Count; index++)
        {
            signedTotal += Vector3.Dot(holdCentres[index] - boardPlanePoint, normal);
        }
        if (Mathf.Abs(signedTotal) <= Mathf.Epsilon)
        {
            throw new ArgumentException(
                "Route holds do not fall on one side of the board plane; outward normal is undefined.",
                nameof(holdCentres));
        }
        return signedTotal > 0f ? normal : -normal;
    }

    public static Vector3 GetBoardVertical(Vector3 boardNormal)
    {
        if (boardNormal.sqrMagnitude <= Mathf.Epsilon)
        {
            throw new ArgumentException("Board normal cannot be zero.", nameof(boardNormal));
        }
        Vector3 vertical = Vector3.ProjectOnPlane(Vector3.up, boardNormal.normalized);
        if (vertical.sqrMagnitude <= Mathf.Epsilon)
        {
            throw new ArgumentException("Board plane cannot be horizontal.", nameof(boardNormal));
        }
        return vertical.normalized;
    }
}
