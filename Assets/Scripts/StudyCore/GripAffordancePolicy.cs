using System;
using UnityEngine;

/// <summary>How firmly one hand is holding a hold. The grip pipeline decides when contact counts
/// as engagement; the affordance only reports which of the three states that decision produced, so
/// changing the engagement rule (minimum finger count, flexion thresholds) needs no change here.</summary>
public enum GripAffordanceState
{
    None = 0,
    Partial = 1,
    Latched = 2,
}

/// <summary>The rim cue for one hold: which colour to draw it in, how opaque, and how broad a band
/// of the silhouette it covers.</summary>
public readonly struct GripAffordance
{
    public GripAffordance(GripAffordanceState state, float quality, Color color, float alpha, float rimPower)
    {
        State = state;
        Quality = quality;
        Color = color;
        Alpha = alpha;
        RimPower = rimPower;
    }

    public GripAffordanceState State { get; }
    public float Quality { get; }
    public Color Color { get; }
    public float Alpha { get; }
    public float RimPower { get; }

    public bool IsVisible => State != GripAffordanceState.None;
}

/// <summary>Maps the grip pipeline's graded contact score onto the hold's rim cue, restoring the
/// spectrum spec 04 defines: hue carries grip quality continuously (red - amber - green), never a
/// binary latched/unlatched flag. Engagement is carried by opacity and rim breadth instead, so the
/// two questions a climber asks - "am I holding it" and "how well" - read on separate channels, and
/// the magnitude survives for a participant who cannot separate red from green.</summary>
public static class GripAffordancePolicy
{
    /// <summary>Spec 04's ramp. Quality is a geometric salience heuristic (contact coverage plus
    /// surface opposition against gravity), never a force or friction measurement.</summary>
    public static readonly Color LowQualityColor = new(0.9f, 0.08f, 0.06f, 1f);
    public static readonly Color MediumQualityColor = new(1f, 0.55f, 0.05f, 1f);
    public static readonly Color HighQualityColor = new(0.1f, 0.85f, 0.2f, 1f);

    public const float PartialAlpha = 0.38f;
    public const float LatchedAlpha = 0.85f;

    /// <summary>Fresnel exponents. The rim is pow(1 - dot(normal, view), power), so a high exponent
    /// leaves a thin sliver at the silhouette and a low one broadens the band inward.</summary>
    public const float LowQualityRimPower = 5f;
    public const float HighQualityRimPower = 1.8f;

    public const int FingerCount = 5;
    public const int FullContactMask = (1 << FingerCount) - 1;

    public static GripAffordanceState ResolveState(bool engaged, int contactMask)
    {
        ValidateContactMask(contactMask);
        if (engaged)
        {
            return GripAffordanceState.Latched;
        }
        return contactMask != 0 ? GripAffordanceState.Partial : GripAffordanceState.None;
    }

    public static Color EvaluateQualityColor(float quality)
    {
        return EvaluateQualityColor(quality, LowQualityColor, MediumQualityColor, HighQualityColor);
    }

    public static Color EvaluateQualityColor(float quality, Color low, Color medium, Color high)
    {
        float clamped = ClampQuality(quality);
        return clamped < 0.5f
            ? Color.Lerp(low, medium, clamped * 2f)
            : Color.Lerp(medium, high, (clamped - 0.5f) * 2f);
    }

    public static float EvaluateRimPower(float quality)
    {
        return Mathf.Lerp(LowQualityRimPower, HighQualityRimPower, ClampQuality(quality));
    }

    public static float EvaluateAlpha(GripAffordanceState state)
    {
        return state switch
        {
            GripAffordanceState.Latched => LatchedAlpha,
            GripAffordanceState.Partial => PartialAlpha,
            _ => 0f,
        };
    }

    public static GripAffordance Resolve(bool engaged, int contactMask, float quality)
    {
        return Resolve(engaged, contactMask, quality, LowQualityColor, MediumQualityColor, HighQualityColor);
    }

    public static GripAffordance Resolve(
        bool engaged,
        int contactMask,
        float quality,
        Color low,
        Color medium,
        Color high)
    {
        GripAffordanceState state = ResolveState(engaged, contactMask);
        float clamped = ClampQuality(quality);
        return new GripAffordance(
            state,
            clamped,
            EvaluateQualityColor(clamped, low, medium, high),
            EvaluateAlpha(state),
            EvaluateRimPower(clamped));
    }

    /// <summary>Keeps the stronger of two hands' cues when both hold the same hold: a latched hand
    /// outranks a merely touching one, and between equals the higher quality wins, so the rim never
    /// reports the weaker of a matched pair.</summary>
    public static GripAffordance Combine(GripAffordance first, GripAffordance second)
    {
        if (first.State != second.State)
        {
            return first.State > second.State ? first : second;
        }
        return first.Quality >= second.Quality ? first : second;
    }

    private static float ClampQuality(float quality)
    {
        if (float.IsNaN(quality) || float.IsInfinity(quality))
        {
            throw new ArgumentException("Grip quality must be finite.", nameof(quality));
        }
        return Mathf.Clamp01(quality);
    }

    /// <summary>Rejects a ten-bit both-hands mask: the affordance is per hand, and the combined
    /// SceneConfiguror mask packs the right hand into bits 5-9.</summary>
    private static void ValidateContactMask(int contactMask)
    {
        if (contactMask < 0 || contactMask > FullContactMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contactMask),
                contactMask,
                "Contact mask must be a five-finger mask for a single hand.");
        }
    }
}
