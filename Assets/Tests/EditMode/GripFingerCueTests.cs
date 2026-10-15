using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools.Utils;

public sealed class GripFingerCueTests
{
    private const int ThumbMask = 0b0_0001;
    private const int IndexMask = 0b0_0010;
    private const int AllFingersMask = 0b1_1111;

    private static readonly string[] PatchColorProperties =
    {
        "_ThumbColor", "_IndexColor", "_MiddleColor", "_RingColor", "_LittleColor",
    };

    private GripScoreConfig config;

    [SetUp]
    public void SetUp()
    {
        config = ScriptableObject.CreateInstance<GripScoreConfig>();
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(config);
    }

    [Test]
    public void OnlyTheContactingFingerReadsVivid()
    {
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Color hue = config.GetPatchColor(finger);
            Color expected = finger == 1 ? hue : GripFingerCuePolicy.DullFingertipColor(hue);
            Assert.That(
                GripFingerCuePolicy.ResolveFingertipColor(IndexMask, finger, hue),
                Is.EqualTo(expected).Using(ColorEqualityComparer.Instance),
                finger == 1
                    ? "The contacting finger must carry its patch hue at full strength."
                    : "Finger " + finger + " is off the hold and must read dull.");
        }
    }

    /// <summary>The regression this policy exists for: a hand can be latched on two fingers, and the
    /// three that never touched the hold have to stay dull. The fingertip colour therefore takes no
    /// latch input at all - only the mask decides.</summary>
    [Test]
    public void AnEmptyMaskLeavesEveryFingerDullEvenWhileLatched()
    {
        Assert.That(
            GripFingerCuePolicy.ResolveHandStatusColor(true),
            Is.EqualTo(GripFingerCuePolicy.ContactColor).Using(ColorEqualityComparer.Instance));
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Color hue = config.GetPatchColor(finger);
            Assert.That(
                GripFingerCuePolicy.ResolveFingertipColor(0, finger, hue),
                Is.EqualTo(GripFingerCuePolicy.DullFingertipColor(hue))
                    .Using(ColorEqualityComparer.Instance));
        }
    }

    /// <summary>Dulling encodes only the contact state: value and saturation drop, while the hue
    /// angle survives exactly, so the finger-to-patch legend stays readable before contact.</summary>
    [Test]
    public void DullingDimsAndDesaturatesButPreservesTheHueAngle()
    {
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Color hue = config.GetPatchColor(finger);
            Color dull = GripFingerCuePolicy.DullFingertipColor(hue);
            Color.RGBToHSV(hue, out float hueH, out float hueS, out float hueV);
            Color.RGBToHSV(dull, out float dullH, out float dullS, out float dullV);
            Assert.That(dullV, Is.LessThan(hueV), "Finger " + finger + " must darken.");
            Assert.That(dullS, Is.LessThan(hueS), "Finger " + finger + " must desaturate.");
            Assert.That(dullH, Is.EqualTo(hueH).Within(0.01f),
                "Finger " + finger + " must keep its hue angle.");
            Assert.That(dull.a, Is.EqualTo(hue.a));
        }
    }

    /// <summary>A dulled marker still names its finger, so no two fingers' dull colours may collapse
    /// into the same grey.</summary>
    [Test]
    public void DullColorsStayDistinguishableAcrossFingers()
    {
        for (int first = 0; first < GripAffordancePolicy.FingerCount; first++)
        {
            for (int second = first + 1; second < GripAffordancePolicy.FingerCount; second++)
            {
                Color a = GripFingerCuePolicy.DullFingertipColor(config.GetPatchColor(first));
                Color b = GripFingerCuePolicy.DullFingertipColor(config.GetPatchColor(second));
                float separation = Mathf.Max(
                    Mathf.Abs(a.r - b.r),
                    Mathf.Abs(a.g - b.g),
                    Mathf.Abs(a.b - b.b));
                Assert.That(separation, Is.GreaterThan(0.05f),
                    "Dull colours for fingers " + first + " and " + second + " are too close.");
            }
        }
    }

    /// <summary>The material asset is what the editor shows before GripHoldContactStore pushes the
    /// config's palette at runtime; the two must agree or the editor lies about the build.</summary>
    [Test]
    public void ContactPatchMaterialCarriesTheConfigHues()
    {
        Material material =
            AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/ContactPatchField.mat");
        Assert.That(material, Is.Not.Null, "ContactPatchField.mat is missing from Resources.");
        for (int finger = 0; finger < GripAffordancePolicy.FingerCount; finger++)
        {
            Assert.That(
                material.GetColor(PatchColorProperties[finger]),
                Is.EqualTo(config.GetPatchColor(finger)).Using(ColorEqualityComparer.Instance),
                PatchColorProperties[finger] + " must match GripScoreConfig's default palette.");
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
            () => GripFingerCuePolicy.ResolveFingertipColor(packed, 0, Color.white));
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
