using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>One frame of grip evidence for one hand: the anatomy the hand is actually showing
/// (per-joint flexion and fingertip distance) beside the predicate that evidence is being judged
/// against. Held as a mutable snapshot the coordinator refills each frame so the panel costs no
/// per-frame allocation.</summary>
public sealed class GripHandDiagnostics
{
    private readonly float[] jointDegrees =
        new float[FingerCurlEstimator.FingerCount * FingerCurlEstimator.MaximumJointsPerFinger];
    private readonly float[] curls = new float[FingerCurlEstimator.FingerCount];
    private readonly float[] tipDistances = new float[FingerCurlEstimator.FingerCount];

    public GripHandDiagnostics(string handLabel)
    {
        if (string.IsNullOrEmpty(handLabel))
        {
            throw new ArgumentException("Grip diagnostics need a hand label.", nameof(handLabel));
        }
        HandLabel = handLabel;
        Array.Fill(tipDistances, float.PositiveInfinity);
    }

    public string HandLabel { get; }
    public bool TrackingValid { get; set; }
    public string HoldLabel { get; set; } = string.Empty;
    public GripLatchPhase Phase { get; set; }
    public GripEngagementBlock Block { get; set; }
    public GripAcquisitionMasks Masks { get; set; }
    public GripAcquisitionCriteria Criteria { get; set; }
    public int CountedFingers { get; set; }
    public int RequiredFingers { get; set; }

    public void SetFinger(
        int finger,
        float curl,
        float tipDistance,
        IReadOnlyList<float> joints,
        int jointCount)
    {
        ValidateFinger(finger);
        if (joints == null || joints.Count < jointCount)
        {
            throw new ArgumentException("Joint samples are shorter than the joint count.", nameof(joints));
        }

        curls[finger] = curl;
        tipDistances[finger] = tipDistance;
        int stride = finger * FingerCurlEstimator.MaximumJointsPerFinger;
        for (int joint = 0; joint < FingerCurlEstimator.MaximumJointsPerFinger; joint++)
        {
            jointDegrees[stride + joint] = joint < jointCount ? joints[joint] : float.NaN;
        }
    }

    public float GetCurl(int finger)
    {
        ValidateFinger(finger);
        return curls[finger];
    }

    public float GetTipDistance(int finger)
    {
        ValidateFinger(finger);
        return tipDistances[finger];
    }

    public float GetJointDegrees(int finger, int joint)
    {
        ValidateFinger(finger);
        if (joint < 0 || joint >= FingerCurlEstimator.MaximumJointsPerFinger)
        {
            throw new ArgumentOutOfRangeException(nameof(joint), "A finger chain holds three joints.");
        }
        return jointDegrees[finger * FingerCurlEstimator.MaximumJointsPerFinger + joint];
    }

    private static void ValidateFinger(int finger)
    {
        if (finger < 0 || finger >= FingerCurlEstimator.FingerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(finger), "A hand has five fingers.");
        }
    }
}

/// <summary>Renders a hand snapshot as the panel's text. Kept apart from the panel itself so the
/// wording of every verdict — above all the sentence naming the clause that is short — is asserted
/// in EditMode rather than read off a headset.</summary>
public static class GripDiagnosticsFormatter
{
    public static readonly string[] FingerNames = { "THUMB", "INDEX", "MIDDLE", "RING", "PINKY" };

    private static readonly string[] FingerJointLabels = { "MCP", "PIP", "DIP" };
    // Climbers name the thumb's distal flexor after its tendon rather than the joint it crosses.
    private static readonly string[] ThumbJointLabels = { "MCP", "FPL" };

    public static IReadOnlyList<string> GetJointLabels(int finger)
    {
        return finger == FingerCurlEstimator.ThumbFinger ? ThumbJointLabels : FingerJointLabels;
    }

    public static void AppendHand(StringBuilder builder, GripHandDiagnostics hand)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }
        if (hand == null)
        {
            throw new ArgumentNullException(nameof(hand));
        }

        builder.Append(hand.HandLabel).Append("  ");
        builder.Append(hand.TrackingValid ? "TRACKED" : "NO TRACKING");
        if (!string.IsNullOrEmpty(hand.HoldLabel))
        {
            builder.Append("  HOLD ").Append(hand.HoldLabel);
        }
        builder.Append('\n');
        builder.Append("FINGER   MCP  PIP  DIP   CURL    TIP   STATE\n");
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            AppendFinger(builder, hand, finger);
        }
        builder.Append(DescribeBlock(hand)).Append('\n');
    }

    public static string DescribeBlock(GripHandDiagnostics hand)
    {
        if (hand == null)
        {
            throw new ArgumentNullException(nameof(hand));
        }

        GripAcquisitionCriteria criteria = hand.Criteria;
        return hand.Block switch
        {
            GripEngagementBlock.None => "ENGAGED ON " + Fingers(hand.CountedFingers),
            GripEngagementBlock.Latched => hand.Phase == GripLatchPhase.Frozen
                ? "LATCHED, HOLDING POSITION WHILE TRACKING IS LOST"
                : hand.CountedFingers >= 1
                    ? "LATCHED ON " + Fingers(hand.CountedFingers)
                    : "LATCHED",
            GripEngagementBlock.InputSuppressed => "PAUSED WHILE THE CONSOLE IS OPEN",
            GripEngagementBlock.AwaitingOpenHand => "OPEN THIS HAND ONCE TO RE-ARM IT",
            GripEngagementBlock.TrackingLost => "HAND TRACKING LOST",
            GripEngagementBlock.NoCandidateHold => "NOT ON A HOLD",
            GripEngagementBlock.AffordancesUnavailable => "HOLD AFFORDANCES UNAVAILABLE",
            GripEngagementBlock.NoContactSample => "WAITING FOR A CONTACT MEASUREMENT",
            GripEngagementBlock.NoFlexedFinger => "NOT FLEXED: NEED CURL " +
                                                  Number(criteria.EngageCurl, "0.00") + " ON " +
                                                  Fingers(criteria.MinFingers) + ", OR " +
                                                  Number(criteria.StrongCurl, "0.00") + " ON " +
                                                  Fingers(criteria.StrongFingerFloor),
            GripEngagementBlock.NoContactFinger => "NOT TOUCHING: NEED A FINGERTIP WITHIN " +
                                                   Millimetres(criteria.ContactRange),
            GripEngagementBlock.TooFewFingers => "ONLY " + hand.CountedFingers + " OF " +
                                                 Fingers(hand.RequiredFingers) +
                                                 " ARE FLEXED AND TOUCHING",
            _ => throw new ArgumentOutOfRangeException(nameof(hand), "Unknown grip engagement block."),
        };
    }

    private static void AppendFinger(StringBuilder builder, GripHandDiagnostics hand, int finger)
    {
        int bit = 1 << finger;
        builder.Append(FingerNames[finger].PadRight(8));
        IReadOnlyList<string> labels = GetJointLabels(finger);
        for (int joint = 0; joint < FingerCurlEstimator.MaximumJointsPerFinger; joint++)
        {
            float degrees = hand.GetJointDegrees(finger, joint);
            builder.Append(
                joint < labels.Count && !float.IsNaN(degrees)
                    ? Number(degrees, "0").PadLeft(4)
                    : "   -");
            builder.Append(' ');
        }
        builder.Append(Number(hand.GetCurl(finger), "0.00").PadLeft(5)).Append("  ");
        builder.Append(Millimetres(hand.GetTipDistance(finger)).PadLeft(6)).Append("  ");
        builder.Append(DescribeFinger(hand.Masks, bit)).Append('\n');
    }

    private static string DescribeFinger(GripAcquisitionMasks masks, int bit)
    {
        if ((masks.StrongContact & bit) != 0)
        {
            return "GRIP+";
        }
        if ((masks.FlexedContact & bit) != 0)
        {
            return "GRIP";
        }
        if ((masks.Flexed & bit) != 0)
        {
            return "flexed";
        }
        return (masks.Contact & bit) != 0 ? "touching" : "-";
    }

    private static string Fingers(int count)
    {
        return count + (count == 1 ? " FINGER" : " FINGERS");
    }

    private static string Millimetres(float metres)
    {
        return float.IsNaN(metres) || float.IsInfinity(metres)
            ? "-"
            : Number(metres * 1000f, "0") + "MM";
    }

    private static string Number(float value, string format)
    {
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
