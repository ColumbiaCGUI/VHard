using System;
using UnityEngine;

/// <summary>Colours the per-hand grip markers from the same five-bit contact mask the recorder packs
/// into <c>perFingerContactMask</c>, so a finger the capture calls "off the hold" can never be drawn
/// as if it were loaded. A fingertip marker carries its finger's contact-patch hue - vivid while its
/// contact bit is set, desaturated and darkened while it is not - so the sphere on the finger and the
/// patch that finger paints on the hold name the same finger in the same colour. Red/green stays
/// reserved for evaluative state: the wrist marker's hand-level latch here, and the grip-quality ramp
/// on the hold. A latched hand whose ring finger is clear of the hold therefore reports one green
/// wrist and four dull fingertips, never five vivid ones.</summary>
public static class GripFingerCuePolicy
{
    public static readonly Color ContactColor = Color.green;
    public static readonly Color NeutralColor = Color.red;

    /// <summary>How far an off-the-hold fingertip collapses toward its own grey, and how much of that
    /// brightness survives. The dull colour must stay recognisably the finger's hue - the finger-to-
    /// patch legend is readable while the hand is still shaping above the hold - while never
    /// competing with a vivid contact.</summary>
    public const float DullDesaturation = 0.65f;
    public const float DullBrightness = 0.4f;

    /// <summary>Inverts the fingertip bone table the compute shader and the engagement gate share, so
    /// a marker only has to know which bone it follows.</summary>
    public static bool TryGetFinger(int fingertipBoneIndex, out int finger)
    {
        for (int candidate = 0; candidate < GripAffordancePolicy.FingerCount; candidate++)
        {
            if (GripEngagementGate.GetFingertipBoneIndex(candidate) == fingertipBoneIndex)
            {
                finger = candidate;
                return true;
            }
        }

        finger = -1;
        return false;
    }

    public static bool IsFingerContacting(int handContactMask, int finger)
    {
        ValidateHandContactMask(handContactMask);
        ValidateFinger(finger);
        return (handContactMask & (1 << finger)) != 0;
    }

    /// <summary>The marker colour for one fingertip. <paramref name="fingerHue"/> is that finger's
    /// contact-patch hue (GripScoreConfig.GetPatchColor), passed in so the spheres can never drift
    /// from whatever the patches are actually drawing.</summary>
    public static Color ResolveFingertipColor(int handContactMask, int finger, Color fingerHue)
    {
        return IsFingerContacting(handContactMask, finger)
            ? fingerHue
            : DullFingertipColor(fingerHue);
    }

    /// <summary>The off-the-hold rendering of a finger's hue: pulled toward its own luminance grey,
    /// then darkened. Both steps scale the channel differences uniformly, so the hue angle is
    /// preserved exactly - identity survives, only the contact state dims.</summary>
    public static Color DullFingertipColor(Color fingerHue)
    {
        float grey = fingerHue.grayscale;
        Color desaturated = Color.Lerp(
            fingerHue, new Color(grey, grey, grey, fingerHue.a), DullDesaturation);
        return new Color(
            desaturated.r * DullBrightness,
            desaturated.g * DullBrightness,
            desaturated.b * DullBrightness,
            fingerHue.a);
    }

    /// <summary>The wrist marker's colour. It is the only marker that reports the hand-level latch,
    /// which is what keeps "am I holding it" readable once the fingertips stop answering it.</summary>
    public static Color ResolveHandStatusColor(bool latched)
    {
        return latched ? ContactColor : NeutralColor;
    }

    /// <summary>Rejects the ten-bit combined mask: the markers are per hand, and SceneConfiguror
    /// packs the right hand into bits 5-9 of the recorder's column.</summary>
    private static void ValidateHandContactMask(int handContactMask)
    {
        if (handContactMask < 0 || handContactMask > GripAffordancePolicy.FullContactMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handContactMask),
                handContactMask,
                "Contact mask must be a five-finger mask for a single hand.");
        }
    }

    private static void ValidateFinger(int finger)
    {
        if (finger < 0 || finger >= GripAffordancePolicy.FingerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(finger), finger, "A hand has five fingers.");
        }
    }
}
