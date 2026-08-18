using System;
using UnityEngine;

/// <summary>
/// Amplified rotation for detached-proxy manipulation. A proxy is turned by pinch-grabbing it and
/// rotating the hand, and one grab only spans the wrist's comfortable range - well under a half
/// turn - so examining a hold's far side takes repeated grab-turn-release cycles ("flexing the
/// wrist back and forth is hard", P2). Scaling the rotation delta measured from the grab anchor
/// lets one comfortable wrist turn cover the inspection range while translation keeps its 1:1
/// physical mapping; the surplus is applied about the hold's own centre so the proxy turns in
/// place instead of orbiting the hand faster than the wrist does.
/// </summary>
public static class GhostRotationAmplification
{
    /// <summary>Angles below this are treated as no rotation: ToAngleAxis is numerically
    /// meaningless at identity, and no wrist reading is this steady anyway.</summary>
    public const float MinimumAngleDegrees = 1e-3f;

    /// <summary>
    /// Scales a rotation about its own axis by <paramref name="factor"/>, measuring the angle on
    /// the shortest path. The winding of the quaternion representation must not leak into the
    /// result: a delta that reads as 350 degrees is a 10-degree turn the other way, and a
    /// non-integer factor would otherwise produce a completely different orientation. At exactly
    /// 180 degrees the shortest path is ambiguous by definition (axis and -axis are both valid);
    /// callers feed anchor-relative wrist deltas, which stay well under a half turn per grab, so
    /// the ambiguity is unreachable in practice rather than resolved here.
    /// </summary>
    public static Quaternion ScaleRotation(Quaternion rotation, float factor)
    {
        if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                "Rotation scale factor must be finite and non-negative.");
        }

        rotation.ToAngleAxis(out float angleDegrees, out Vector3 axis);
        if (float.IsNaN(angleDegrees) || float.IsInfinity(angleDegrees))
        {
            throw new ArgumentException("Rotation must be a finite quaternion.", nameof(rotation));
        }
        if (angleDegrees > 180f)
        {
            angleDegrees -= 360f;
        }
        if (Mathf.Abs(angleDegrees) < MinimumAngleDegrees ||
            float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-8f)
        {
            return Quaternion.identity;
        }
        return Quaternion.AngleAxis(angleDegrees * factor, axis);
    }
}

/// <summary>
/// Returning a detached proxy to the orientation its hold actually holds on the wall. P1's pilot
/// showed proxies spent almost all of their held time outside wall-orientation tolerance with
/// nearly no manual realignment: once turned, the wall frame was effectively lost. The align
/// control hands that frame back on demand - deliberately on demand and never on release, because
/// turning the proxy away from true IS the inspection technique, and snapping back uninvited
/// would undo the pose the participant chose. The return animates at a bounded angular rate so
/// the path itself reads as the hold rotating back to true rather than a teleport.
/// </summary>
public static class GhostAlignAnimation
{
    /// <summary>A half turn completes in half a second - fast enough to feel like a response,
    /// slow enough that the rotation path back to true stays readable.</summary>
    public const float DefaultSpeedDegreesPerSecond = 360f;

    /// <summary>Remaining error at which the animation snaps exactly onto the target.</summary>
    public const float CompletionEpsilonDegrees = 0.1f;

    /// <summary>
    /// One animation step toward <paramref name="target"/>. The target is re-read by the caller
    /// every frame because board alignment can move the wall hold while the proxy is animating.
    /// </summary>
    public static Quaternion Step(
        Quaternion current,
        Quaternion target,
        float speedDegreesPerSecond,
        float deltaSeconds,
        out bool completed)
    {
        if (float.IsNaN(speedDegreesPerSecond) || float.IsInfinity(speedDegreesPerSecond) ||
            speedDegreesPerSecond <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedDegreesPerSecond),
                "Align speed must be finite and positive.");
        }
        if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Align time step must be finite and non-negative.");
        }

        Quaternion next = Quaternion.RotateTowards(
            current,
            target,
            speedDegreesPerSecond * deltaSeconds);
        completed = Quaternion.Angle(next, target) <= CompletionEpsilonDegrees;
        return completed ? target : next;
    }
}
