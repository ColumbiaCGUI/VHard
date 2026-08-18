using System;
using UnityEngine;

/// <summary>Decides when the in-scene BACK TO START button exists: after both hands have stayed
/// latched on the route's finish for a sustained moment, and for a linger window afterwards so a
/// hand can leave the finish to poke it. Pure state, driven once per frame by the presenter.</summary>
public sealed class TopOutResetTracker
{
    private readonly float holdSeconds;
    private readonly float lingerSeconds;
    private float topOutSince = -1f;
    private float lingerDeadline = -1f;
    private bool episodeArmed;

    public TopOutResetTracker(float holdSeconds, float lingerSeconds)
    {
        if (float.IsNaN(holdSeconds) || float.IsInfinity(holdSeconds) || holdSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        }
        if (float.IsNaN(lingerSeconds) || float.IsInfinity(lingerSeconds) || lingerSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(lingerSeconds));
        }
        this.holdSeconds = holdSeconds;
        this.lingerSeconds = lingerSeconds;
    }

    public bool IsButtonVisible => episodeArmed;

    /// <summary>Advances the episode. Returns true exactly once per episode, on the frame the
    /// sustained top-out is first recognized, so the caller can record the event.</summary>
    public bool Update(bool topOutActive, float now)
    {
        if (float.IsNaN(now) || float.IsInfinity(now) || now < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        bool episodeStarted = false;
        if (topOutActive)
        {
            if (topOutSince < 0f)
            {
                topOutSince = now;
            }
            if (!episodeArmed && now - topOutSince >= holdSeconds)
            {
                episodeArmed = true;
                episodeStarted = true;
            }
            if (episodeArmed)
            {
                lingerDeadline = now + lingerSeconds;
            }
        }
        else
        {
            topOutSince = -1f;
            if (episodeArmed && now > lingerDeadline)
            {
                episodeArmed = false;
            }
        }
        return episodeStarted;
    }

    public void Reset()
    {
        topOutSince = -1f;
        lingerDeadline = -1f;
        episodeArmed = false;
    }
}

/// <summary>Turns a sustained fingertip contact into one press: the fingertip must stay engaged
/// for the whole dwell, and a press cannot repeat until contact is broken and re-made.</summary>
public sealed class TopOutPressTracker
{
    private readonly float dwellSeconds;
    private float pressStart = -1f;
    private bool consumed;

    public TopOutPressTracker(float dwellSeconds)
    {
        if (float.IsNaN(dwellSeconds) || float.IsInfinity(dwellSeconds) || dwellSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(dwellSeconds));
        }
        this.dwellSeconds = dwellSeconds;
    }

    public float Progress01 { get; private set; }

    public bool Update(bool engaged, float now)
    {
        if (float.IsNaN(now) || float.IsInfinity(now) || now < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        if (!engaged)
        {
            Reset();
            return false;
        }
        // Holding the fingertip in place must not machine-gun resets: a press consumes the
        // contact, and nothing more happens until contact breaks and is re-made.
        if (consumed)
        {
            return false;
        }
        if (pressStart < 0f)
        {
            pressStart = now;
        }

        Progress01 = dwellSeconds <= 0f
            ? 1f
            : Mathf.Clamp01((now - pressStart) / dwellSeconds);
        if (Progress01 < 1f)
        {
            return false;
        }

        consumed = true;
        Progress01 = 0f;
        return true;
    }

    public void Reset()
    {
        pressStart = -1f;
        consumed = false;
        Progress01 = 0f;
    }
}

public static class TopOutResetPolicy
{
    /// <summary>Keeps the mounted button clear of the wall panel's edges.</summary>
    public const float WallEdgeMarginMeters = 0.02f;

    /// <summary>How far past the button face a fingertip may sink and still count as touching,
    /// covering tracking noise and the finger visually entering the wall.</summary>
    public const float SurfaceSlackMeters = 0.02f;

    /// <summary>Pose of the button mounted flat on the upper vertical board wall: on the wall's
    /// climber-facing face — the side the viewer's head is on, which is unambiguous where the
    /// finish hold is not, since row-18 holds sit essentially in the wall's seam plane — laterally
    /// tracking the finish hold, its centre <paramref name="aboveFinishMeters"/> above the hold,
    /// clamped inside the panel. The pose faces out of the wall, so the button's label side looks
    /// at the climber and its text reads correctly.</summary>
    public static Pose GetWallMountedButtonPose(
        Vector3 wallCentre,
        Quaternion wallRotation,
        Vector3 wallLossyScale,
        Vector3 finishCentre,
        Vector3 viewerPosition,
        float aboveFinishMeters,
        float surfaceGapMeters,
        float buttonHalfWidthMeters,
        float buttonHalfHeightMeters)
    {
        if (float.IsNaN(aboveFinishMeters) || float.IsInfinity(aboveFinishMeters) ||
            aboveFinishMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(aboveFinishMeters));
        }
        if (float.IsNaN(surfaceGapMeters) || float.IsInfinity(surfaceGapMeters) ||
            surfaceGapMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(surfaceGapMeters));
        }
        if (!IsPositiveFinite(buttonHalfWidthMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(buttonHalfWidthMeters));
        }
        if (!IsPositiveFinite(buttonHalfHeightMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(buttonHalfHeightMeters));
        }
        if (!IsPositiveFinite(Mathf.Abs(wallLossyScale.x)) ||
            !IsPositiveFinite(Mathf.Abs(wallLossyScale.y)) ||
            !IsPositiveFinite(Mathf.Abs(wallLossyScale.z)))
        {
            throw new ArgumentException("Wall scale must be finite and non-zero.", nameof(wallLossyScale));
        }

        Vector3 right = wallRotation * Vector3.right;
        Vector3 up = wallRotation * Vector3.up;
        Vector3 forward = wallRotation * Vector3.forward;
        float halfWidth = Mathf.Abs(wallLossyScale.x) * 0.5f;
        float halfHeight = Mathf.Abs(wallLossyScale.y) * 0.5f;
        float halfThickness = Mathf.Abs(wallLossyScale.z) * 0.5f;

        Vector3 toFinish = finishCentre - wallCentre;
        float side = Vector3.Dot(viewerPosition - wallCentre, forward);
        if (Mathf.Abs(side) <= Mathf.Epsilon)
        {
            throw new ArgumentException(
                "Viewer lies on the wall plane; the mounting face is undefined.",
                nameof(viewerPosition));
        }
        Vector3 outward = side > 0f ? forward : -forward;

        float lateralLimit = halfWidth - buttonHalfWidthMeters - WallEdgeMarginMeters;
        float lateral = lateralLimit > 0f
            ? Mathf.Clamp(Vector3.Dot(toFinish, right), -lateralLimit, lateralLimit)
            : 0f;
        float heightLimit = halfHeight - buttonHalfHeightMeters - WallEdgeMarginMeters;
        float height = heightLimit > 0f
            ? Mathf.Clamp(Vector3.Dot(toFinish, up) + aboveFinishMeters, -heightLimit, heightLimit)
            : 0f;

        Vector3 position = wallCentre +
                           right * lateral +
                           up * height +
                           outward * (halfThickness + surfaceGapMeters);
        return new Pose(position, Quaternion.LookRotation(-outward, up));
    }

    /// <summary>Whether a fingertip, expressed in the button root's unscaled local frame, is
    /// touching the button face: within the (optionally padded) face rectangle and inside the
    /// shallow slab reaching <paramref name="pressDepthMeters"/> out of the face. Local -Z is the
    /// label side, so touching fingertips carry a negative local Z.</summary>
    public static bool IsFingertipOnButton(
        Vector3 buttonLocalPoint,
        float halfWidthMeters,
        float halfHeightMeters,
        float pressDepthMeters,
        float facePadMeters)
    {
        if (!IsPositiveFinite(halfWidthMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(halfWidthMeters));
        }
        if (!IsPositiveFinite(halfHeightMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(halfHeightMeters));
        }
        if (!IsPositiveFinite(pressDepthMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(pressDepthMeters));
        }
        if (float.IsNaN(facePadMeters) || float.IsInfinity(facePadMeters) || facePadMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(facePadMeters));
        }

        return Mathf.Abs(buttonLocalPoint.x) <= halfWidthMeters + facePadMeters &&
               Mathf.Abs(buttonLocalPoint.y) <= halfHeightMeters + facePadMeters &&
               buttonLocalPoint.z >= -pressDepthMeters &&
               buttonLocalPoint.z <= SurfaceSlackMeters;
    }

    private static bool IsPositiveFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }

    /// <summary>The wall-hold coordinate a latched hold GameObject names, in catalog form.</summary>
    public static string GetHoldCoordinate(string holdName)
    {
        if (string.IsNullOrWhiteSpace(holdName))
        {
            throw new ArgumentException("Hold name is required.", nameof(holdName));
        }
        return holdName.Split('.')[0].ToUpperInvariant();
    }
}
