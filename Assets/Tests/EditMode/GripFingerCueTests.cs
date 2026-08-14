using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

public sealed class GripFingerCueTests
{
    private const int ThumbMask = 0b0_0001;
    private const int IndexMask = 0b0_0010;
    private const int AllFingersMask = 0b1_1111;

    [Test]
    public void OnlyTheContactingFingerReadsAsContact()
    {
        Assert.That(
            GripFingerCuePolicy.ResolveFingertipColor(IndexMask, 1),
            Is.EqualTo(GripFingerCuePolicy.ContactColor).Using(ColorEqualityComparer.Instance));
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            if (finger == 1)
            {
                continue;
            }
            Assert.That(
                GripFingerCuePolicy.ResolveFingertipColor(IndexMask, finger),
                Is.EqualTo(GripFingerCuePolicy.NeutralColor).Using(ColorEqualityComparer.Instance),
                "Finger " + finger + " is off the hold and must not read as contact.");
        }
    }

    /// <summary>The regression this policy exists for: a hand can be latched on two fingers, and the
    /// three that never touched the hold have to stay neutral. The fingertip colour therefore takes
    /// no latch input at all - only the mask decides.</summary>
    [Test]
    public void AnEmptyMaskLeavesEveryFingerNeutralEvenWhileLatched()
    {
        Assert.That(
            GripFingerCuePolicy.ResolveHandStatusColor(true),
            Is.EqualTo(GripFingerCuePolicy.ContactColor).Using(ColorEqualityComparer.Instance));
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Assert.That(
                GripFingerCuePolicy.ResolveFingertipColor(0, finger),
                Is.EqualTo(GripFingerCuePolicy.NeutralColor).Using(ColorEqualityComparer.Instance));
        }
    }

    [Test]
    public void EveryFingerReadsAsContactUnderAFullMask()
    {
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Assert.That(GripFingerCuePolicy.IsFingerContacting(AllFingersMask, finger), Is.True);
        }
    }

    [Test]
    public void FingertipBonesResolveToTheirOwnFinger()
    {
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            int boneIndex = GripEngagementGate.GetFingertipBoneIndex(finger);
            Assert.That(GripFingerCuePolicy.TryGetFinger(boneIndex, out int resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(finger));
        }
    }

    /// <summary>The wrist marker follows bone 1, and knuckles and phalanges carry no marker of their
    /// own. None of them may claim a finger's contact bit.</summary>
    [Test]
    public void NonFingertipBonesResolveToNoFinger()
    {
        foreach (int boneIndex in new[] { 0, 1, 4, 6, 24, 26, -1 })
        {
            Assert.That(
                GripFingerCuePolicy.TryGetFinger(boneIndex, out int resolved),
                Is.False,
                "Bone " + boneIndex + " is not a fingertip.");
            Assert.That(resolved, Is.EqualTo(-1));
        }
    }

    [Test]
    public void WristMarkerCarriesTheHandLatch()
    {
        Assert.That(
            GripFingerCuePolicy.ResolveHandStatusColor(false),
            Is.EqualTo(GripFingerCuePolicy.NeutralColor).Using(ColorEqualityComparer.Instance));
        Assert.That(
            GripFingerCuePolicy.ResolveHandStatusColor(true),
            Is.EqualTo(GripFingerCuePolicy.ContactColor).Using(ColorEqualityComparer.Instance));
    }

    /// <summary>The recorder's column packs the right hand into bits 5-9. Handing that combined value
    /// to a per-hand marker would colour the left hand from the right hand's fingers, so it throws
    /// rather than resolving to something plausible.</summary>
    [Test]
    public void CombinedRecorderMaskIsRejected()
    {
        int packed = ThumbMask | (IndexMask << 5);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripFingerCuePolicy.ResolveFingertipColor(packed, 0));
        Assert.That(GripFingerCuePolicy.IsFingerContacting(packed & AllFingersMask, 0), Is.True);
        Assert.That(GripFingerCuePolicy.IsFingerContacting((packed >> 5) & AllFingersMask, 1), Is.True);
    }

    [Test]
    public void FingerIndexIsBoundsChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripFingerCuePolicy.IsFingerContacting(AllFingersMask, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripFingerCuePolicy.IsFingerContacting(AllFingersMask, GripAffordancePolicy.FingerCount));
    }
}
