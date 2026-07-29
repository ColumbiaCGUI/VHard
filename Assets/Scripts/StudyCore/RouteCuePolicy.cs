using System;
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

    public static RouteCuePresentation ForCondition(
        string condition,
        RouteCuePresentation baselinePresentation)
    {
        return condition switch
        {
            "A" => baselinePresentation,
            "B" => RouteCuePresentation.VirtualHalos,
            "C" => RouteCuePresentation.VirtualHalos,
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
