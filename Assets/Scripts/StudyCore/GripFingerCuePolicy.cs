using System;
using UnityEngine;

/// <summary>Colours the per-hand grip markers from the same five-bit contact mask the recorder packs
/// into <c>perFingerContactMask</c>, so a finger the capture calls "off the hold" can never be drawn
/// as if it were loaded. Engagement stays on the wrist marker: a latched hand whose ring finger is
/// clear of the hold reports one latch and four honest fingers, not five green ones.</summary>
public static class GripFingerCuePolicy
{
    public static readonly Color ContactColor = Color.green;
    public static readonly Color NeutralColor = Color.red;

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

    public static Color ResolveFingertipColor(int handContactMask, int finger)
    {
        return IsFingerContacting(handContactMask, finger) ? ContactColor : NeutralColor;
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
