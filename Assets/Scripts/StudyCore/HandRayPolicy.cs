using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One Euro filter over a vector-valued pointer signal. A hand ray carries two kinds of error at
/// once: a fast tremor that has to be smoothed away, and a deliberate sweep that has to survive
/// untouched. A fixed low-pass cannot do both - enough smoothing to settle a resting hand adds
/// visible lag to a moving one - so the cutoff rises with the signal's own speed.
/// </summary>
public sealed class PointerOneEuroFilter
{
    /// <summary>Longer gaps than this are treated as a fresh pointer rather than a slow frame.</summary>
    public const float MaximumSampleIntervalSeconds = 0.1f;

    /// <summary>Cutoff a resting hand is filtered at, which sets how much tremor is removed.</summary>
    public const float DefaultMinimumCutoffHertz = 1.6f;

    /// <summary>
    /// How fast the cutoff opens with hand speed. It can be this aggressive because the speed it
    /// reads is the <em>filtered</em> derivative: tremor alternates in sign and averages to nearly
    /// nothing, so only a deliberate sweep opens the cutoff. At a sweep of one metre per second
    /// this keeps the ray inside about two centimetres of the hand.
    /// </summary>
    public const float DefaultSpeedCoefficient = 4f;

    /// <summary>Cutoff the speed estimate itself is smoothed at.</summary>
    public const float DefaultDerivativeCutoffHertz = 1f;

    private readonly float minimumCutoffHertz;
    private readonly float speedCoefficient;
    private readonly float derivativeCutoffHertz;
    private Vector3 filteredValue;
    private Vector3 filteredDerivative;
    private float lastSampleTime;
    private bool hasSample;

    public PointerOneEuroFilter(
        float minimumCutoffHertz,
        float speedCoefficient,
        float derivativeCutoffHertz)
    {
        if (!IsFinite(minimumCutoffHertz) || minimumCutoffHertz <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCutoffHertz));
        }
        if (!IsFinite(speedCoefficient) || speedCoefficient < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(speedCoefficient));
        }
        if (!IsFinite(derivativeCutoffHertz) || derivativeCutoffHertz <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(derivativeCutoffHertz));
        }

        this.minimumCutoffHertz = minimumCutoffHertz;
        this.speedCoefficient = speedCoefficient;
        this.derivativeCutoffHertz = derivativeCutoffHertz;
    }

    public bool HasSample => hasSample;

    public void Reset()
    {
        hasSample = false;
        filteredDerivative = Vector3.zero;
    }

    public Vector3 Update(Vector3 value, float now)
    {
        if (!IsFinite(value) || !IsFinite(now))
        {
            throw new ArgumentException("Pointer samples must be finite.", nameof(value));
        }

        float interval = now - lastSampleTime;
        if (!hasSample || interval <= 0f || interval > MaximumSampleIntervalSeconds)
        {
            hasSample = true;
            lastSampleTime = now;
            filteredValue = value;
            filteredDerivative = Vector3.zero;
            return filteredValue;
        }

        Vector3 derivative = (value - filteredValue) / interval;
        filteredDerivative = Vector3.Lerp(
            filteredDerivative,
            derivative,
            GetSmoothingFactor(derivativeCutoffHertz, interval));
        float cutoff = minimumCutoffHertz + speedCoefficient * filteredDerivative.magnitude;
        filteredValue = Vector3.Lerp(filteredValue, value, GetSmoothingFactor(cutoff, interval));
        lastSampleTime = now;
        return filteredValue;
    }

    private static float GetSmoothingFactor(float cutoffHertz, float intervalSeconds)
    {
        float timeConstant = 1f / (2f * Mathf.PI * cutoffHertz);
        return intervalSeconds / (timeConstant + intervalSeconds);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

/// <summary>
/// Turning a stabilised hand ray into a hold choice.
/// <para>
/// Hit-testing a ray against hold geometry is the obvious approach and it is the wrong one at this
/// range. A hold's world-axis-aligned bounding box is far larger than the hold and overlaps its
/// neighbours', so "nearest box the ray enters" resolves to whichever box is clipped first, which
/// at a shallow angle is routinely the hold next door. Angle does not have that failure: the
/// question asked here is which hold the ray is pointing most directly <em>at</em>, measured as the
/// angle between the ray and the direction to the hold, which is scale-free, unaffected by
/// overlapping volumes, and degrades gracefully - a ray that misses every hold by a little still
/// names the one it came closest to.
/// </para>
/// <para>
/// The choice is then held still. A bare nearest-angle rule flips between two adjacent holds on
/// sub-degree tremor, so acquisition and release use different tolerances and a rival has to beat
/// the incumbent by a margin before it takes over.
/// </para>
/// </summary>
public static class HandRayTargeting
{
    /// <summary>Half-angle of the acquisition cone. Neighbouring holds on the 0.20 m grid stay
    /// further apart than this at every admissible viewing standoff.</summary>
    public const float DefaultAcquireHalfAngleDegrees = 4f;

    /// <summary>A held target survives out to here before it is dropped.</summary>
    public const float DefaultReleaseHalfAngleDegrees = 7f;

    /// <summary>How much closer a rival has to be before it takes an acquired target over.</summary>
    public const float DefaultSwitchMarginDegrees = 1.25f;

    public const float NoTarget = -1f;

    /// <summary>
    /// Angle between the ray and the direction to a candidate, in degrees. Returns
    /// <see cref="NoTarget"/> for a candidate behind the ray origin or coincident with it.
    /// </summary>
    public static float GetAcquisitionAngleDegrees(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 candidateCenter)
    {
        if (rayDirection.sqrMagnitude < MinimumDirectionSqrMagnitude)
        {
            throw new ArgumentException("Pointer direction must be non-zero.", nameof(rayDirection));
        }

        Vector3 toCandidate = candidateCenter - rayOrigin;
        if (toCandidate.sqrMagnitude < MinimumSeparationSqrMagnitude)
        {
            return NoTarget;
        }

        float angle = Vector3.Angle(rayDirection, toCandidate);
        return angle >= 90f ? NoTarget : angle;
    }

    /// <summary>
    /// Widens the acquisition cone by the angular radius the candidate itself covers, so a large
    /// hold is as easy to acquire as its size suggests rather than being treated as a point.
    /// </summary>
    public static float GetAngularRadiusDegrees(
        Vector3 rayOrigin,
        Vector3 candidateCenter,
        float candidateRadiusMeters)
    {
        if (!IsFinite(candidateRadiusMeters) || candidateRadiusMeters < 0f)
        {
            throw new ArgumentException(
                "Candidate radius must be finite and non-negative.",
                nameof(candidateRadiusMeters));
        }

        float distance = Vector3.Distance(rayOrigin, candidateCenter);
        if (distance <= candidateRadiusMeters)
        {
            return 90f;
        }
        return Mathf.Rad2Deg * Mathf.Asin(candidateRadiusMeters / distance);
    }

    /// <summary>
    /// Index of the hold the ray is pointing at, given the angle to each candidate
    /// (<see cref="NoTarget"/> for candidates that are behind the pointer) and the previously held
    /// index. Returns -1 when nothing is close enough.
    /// </summary>
    public static int SelectStickyTarget(
        int previousIndex,
        IReadOnlyList<float> angles,
        float acquireHalfAngleDegrees,
        float releaseHalfAngleDegrees,
        float switchMarginDegrees)
    {
        if (angles == null)
        {
            throw new ArgumentNullException(nameof(angles));
        }
        if (!IsFinite(acquireHalfAngleDegrees) || acquireHalfAngleDegrees <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(acquireHalfAngleDegrees));
        }
        if (!IsFinite(releaseHalfAngleDegrees) || releaseHalfAngleDegrees < acquireHalfAngleDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(releaseHalfAngleDegrees),
                "The release cone must be at least as wide as the acquisition cone.");
        }
        if (!IsFinite(switchMarginDegrees) || switchMarginDegrees < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(switchMarginDegrees));
        }

        int bestIndex = -1;
        float bestAngle = float.PositiveInfinity;
        for (int index = 0; index < angles.Count; index++)
        {
            float angle = angles[index];
            if (angle == NoTarget)
            {
                continue;
            }
            if (!IsFinite(angle) || angle < 0f)
            {
                throw new ArgumentException("Candidate angles must be finite.", nameof(angles));
            }
            if (angle <= acquireHalfAngleDegrees && angle < bestAngle)
            {
                bestIndex = index;
                bestAngle = angle;
            }
        }

        bool previousStillValid = previousIndex >= 0 && previousIndex < angles.Count &&
                                  angles[previousIndex] != NoTarget &&
                                  angles[previousIndex] <= releaseHalfAngleDegrees;
        if (!previousStillValid)
        {
            return bestIndex;
        }
        if (bestIndex < 0 || bestIndex == previousIndex)
        {
            return previousIndex;
        }
        return bestAngle <= angles[previousIndex] - switchMarginDegrees ? bestIndex : previousIndex;
    }

    private const float MinimumDirectionSqrMagnitude = 1e-8f;
    private const float MinimumSeparationSqrMagnitude = 1e-8f;

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

/// <summary>
/// Edge-detected index pinch with separate press and release thresholds.
/// <para>
/// Two properties matter for a select trigger driven by a tracked hand. It must not chatter while
/// the fingers hover near the threshold, which is what the hysteresis band is for, and a pinch that
/// was already closed when the trigger became live must not count as a press - otherwise entering a
/// technique or closing a panel mid-pinch fires an unintended selection. The latch therefore has to
/// see an open hand before it will arm.
/// </para>
/// </summary>
public sealed class PinchLatch
{
    public const float DefaultPressStrength = 0.7f;
    public const float DefaultReleaseStrength = 0.35f;

    private readonly float pressStrength;
    private readonly float releaseStrength;
    private bool closed;
    private bool armed;

    public PinchLatch(float pressStrength, float releaseStrength)
    {
        if (float.IsNaN(pressStrength) || pressStrength <= 0f || pressStrength > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(pressStrength));
        }
        if (float.IsNaN(releaseStrength) || releaseStrength < 0f || releaseStrength >= pressStrength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(releaseStrength),
                "The release threshold must sit below the press threshold.");
        }

        this.pressStrength = pressStrength;
        this.releaseStrength = releaseStrength;
    }

    public bool IsClosed => closed;
    public bool IsArmed => armed;

    /// <summary>Forgets the pinch entirely; the next press needs a fresh open hand to arm.</summary>
    public void Reset()
    {
        closed = false;
        armed = false;
    }

    /// <summary>
    /// Advances the latch and reports whether this frame carries a press edge.
    /// <paramref name="pinchStrength"/> is the tracked 0-1 index pinch; <paramref name="reported"/>
    /// is the runtime's own pinch verdict, which closes the latch even when the strength curve is
    /// slow to arrive.
    /// </summary>
    public bool Update(bool trackingConfident, float pinchStrength, bool reported)
    {
        if (float.IsNaN(pinchStrength) || float.IsInfinity(pinchStrength))
        {
            throw new ArgumentOutOfRangeException(nameof(pinchStrength));
        }
        if (!trackingConfident)
        {
            Reset();
            return false;
        }

        bool nowClosed = closed
            ? reported || pinchStrength > releaseStrength
            : reported || pinchStrength >= pressStrength;
        bool pressed = nowClosed && !closed && armed;
        if (!nowClosed)
        {
            armed = true;
        }
        else if (pressed)
        {
            armed = false;
        }
        closed = nowClosed;
        return pressed;
    }
}
