using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

public sealed class GripAffordanceTests
{
    private const int ThumbAndIndexMask = 0b0_0011;
    private const int AllFingersMask = 0b1_1111;

    [Test]
    public void StateSeparatesTouchingFromHolding()
    {
        Assert.That(
            GripAffordancePolicy.ResolveState(false, 0),
            Is.EqualTo(GripAffordanceState.None));
        Assert.That(
            GripAffordancePolicy.ResolveState(false, ThumbAndIndexMask),
            Is.EqualTo(GripAffordanceState.Partial));
        Assert.That(
            GripAffordancePolicy.ResolveState(true, AllFingersMask),
            Is.EqualTo(GripAffordanceState.Latched));
    }

    /// <summary>The engagement rule belongs to the grip pipeline. Once it reports a latch the rim
    /// says so, even on the frame whose GPU epoch has not landed a contact mask yet, so a change to
    /// the minimum finger count cannot silently drop the cue.</summary>
    [Test]
    public void EngagementOutranksAnEmptyContactMask()
    {
        Assert.That(
            GripAffordancePolicy.ResolveState(true, 0),
            Is.EqualTo(GripAffordanceState.Latched));
        Assert.That(
            GripAffordancePolicy.Resolve(true, 0, 0.5f).IsVisible,
            Is.True);
    }

    [Test]
    public void QualityRampRunsRedThroughAmberToGreen()
    {
        Assert.That(
            GripAffordancePolicy.EvaluateQualityColor(0f),
            Is.EqualTo(GripAffordancePolicy.LowQualityColor).Using(ColorEqualityComparer.Instance));
        Assert.That(
            GripAffordancePolicy.EvaluateQualityColor(0.5f),
            Is.EqualTo(GripAffordancePolicy.MediumQualityColor).Using(ColorEqualityComparer.Instance));
        Assert.That(
            GripAffordancePolicy.EvaluateQualityColor(1f),
            Is.EqualTo(GripAffordancePolicy.HighQualityColor).Using(ColorEqualityComparer.Instance));
    }

    /// <summary>The cue has to read as a spectrum rather than three steps: green rises and red falls
    /// monotonically across the whole range, and no two distinct scores share a colour.</summary>
    [Test]
    public void QualityRampIsContinuousAndMonotonic()
    {
        List<Color> ramp = new();
        for (int step = 0; step <= 20; step++)
        {
            ramp.Add(GripAffordancePolicy.EvaluateQualityColor(step / 20f));
        }

        for (int index = 1; index < ramp.Count; index++)
        {
            Assert.That(
                ramp[index].g,
                Is.GreaterThan(ramp[index - 1].g),
                "Green must rise with quality at step " + index + ".");
            Assert.That(
                Vector4.Distance(ramp[index], ramp[index - 1]),
                Is.LessThan(0.25f),
                "The ramp must not step at " + index + ".");
        }

        Assert.That(
            ramp[^1].r,
            Is.LessThan(ramp[0].r - 0.5f),
            "A strong grip must lose the red the ramp starts on.");
    }

    [Test]
    public void QualityClampsRatherThanExtrapolatingOutsideTheRamp()
    {
        Assert.That(
            GripAffordancePolicy.EvaluateQualityColor(-3f),
            Is.EqualTo(GripAffordancePolicy.LowQualityColor).Using(ColorEqualityComparer.Instance));
        Assert.That(
            GripAffordancePolicy.EvaluateQualityColor(4f),
            Is.EqualTo(GripAffordancePolicy.HighQualityColor).Using(ColorEqualityComparer.Instance));
    }

    /// <summary>Quality also drives rim breadth, so the magnitude survives for a participant who
    /// cannot separate red from green.</summary>
    [Test]
    public void RimBroadensAsQualityRises()
    {
        float weak = GripAffordancePolicy.EvaluateRimPower(0f);
        float middling = GripAffordancePolicy.EvaluateRimPower(0.5f);
        float strong = GripAffordancePolicy.EvaluateRimPower(1f);

        Assert.That(weak, Is.EqualTo(GripAffordancePolicy.LowQualityRimPower).Within(1e-5f));
        Assert.That(strong, Is.EqualTo(GripAffordancePolicy.HighQualityRimPower).Within(1e-5f));
        Assert.That(middling, Is.LessThan(weak));
        Assert.That(strong, Is.LessThan(middling));
    }

    [Test]
    public void OpacityRisesFromTouchingToHolding()
    {
        Assert.That(GripAffordancePolicy.EvaluateAlpha(GripAffordanceState.None), Is.Zero);
        Assert.That(
            GripAffordancePolicy.EvaluateAlpha(GripAffordanceState.Latched),
            Is.GreaterThan(GripAffordancePolicy.EvaluateAlpha(GripAffordanceState.Partial)));
        Assert.That(
            GripAffordancePolicy.EvaluateAlpha(GripAffordanceState.Partial),
            Is.GreaterThan(0f));
    }

    /// <summary>SceneConfiguror also publishes a ten-bit both-hands mask; feeding that in would
    /// report the wrong hand's fingers, so the affordance refuses it outright.</summary>
    [Test]
    public void PolicyRefusesABothHandsContactMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripAffordancePolicy.ResolveState(false, 0b11_1111_1111));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GripAffordancePolicy.ResolveState(false, -1));
    }

    [Test]
    public void PolicyRefusesNonFiniteQuality()
    {
        Assert.Throws<ArgumentException>(
            () => GripAffordancePolicy.EvaluateQualityColor(float.NaN));
        Assert.Throws<ArgumentException>(
            () => GripAffordancePolicy.Resolve(true, AllFingersMask, float.PositiveInfinity));
    }

    [Test]
    public void CombineKeepsTheStrongerOfTwoHands()
    {
        GripAffordance touching = GripAffordancePolicy.Resolve(false, ThumbAndIndexMask, 0.95f);
        GripAffordance holding = GripAffordancePolicy.Resolve(true, AllFingersMask, 0.2f);

        Assert.That(
            GripAffordancePolicy.Combine(touching, holding).State,
            Is.EqualTo(GripAffordanceState.Latched));
        Assert.That(
            GripAffordancePolicy.Combine(holding, touching).State,
            Is.EqualTo(GripAffordanceState.Latched));

        GripAffordance weakHold = GripAffordancePolicy.Resolve(true, AllFingersMask, 0.3f);
        GripAffordance strongHold = GripAffordancePolicy.Resolve(true, AllFingersMask, 0.8f);
        Assert.That(
            GripAffordancePolicy.Combine(weakHold, strongHold).Quality,
            Is.EqualTo(0.8f).Within(1e-5f));
    }

    [Test]
    public void RimIsAColliderFreeRuntimeChildOffTheStudyLayers()
    {
        using AffordanceFixture fixture = new();

        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.8f),
            null,
            default);

        Transform rim = fixture.WallHold.transform.Find(GripAffordanceOutlinePresenter.OutlineName);
        Assert.That(rim, Is.Not.Null);
        Assert.That(rim.GetComponentsInChildren<Collider>(true), Is.Empty);
        Assert.That(rim.gameObject.layer, Is.EqualTo(GripAffordanceOutlinePresenter.OutlineLayer));
        Assert.That(rim.gameObject.layer, Is.Not.EqualTo(LayerMask.NameToLayer("StudyHolds")));
        Assert.That(rim.gameObject.layer, Is.Not.EqualTo(LayerMask.NameToLayer("StudyGhostHolds")));
        Assert.That(
            rim.gameObject.hideFlags.HasFlag(HideFlags.DontSaveInEditor),
            Is.True,
            "The rim must never be serialised into the study scene.");
        Assert.That(
            rim.gameObject.hideFlags.HasFlag(HideFlags.DontSaveInBuild),
            Is.True);
        Assert.That(
            rim.GetComponent<MeshFilter>().sharedMesh,
            Is.SameAs(fixture.WallHold.GetComponent<MeshFilter>().sharedMesh),
            "The rim must share the hold's mesh rather than upload its own.");
        Assert.That(rim.localScale, Is.EqualTo(Vector3.one));
        Assert.That(rim.GetComponent<MeshRenderer>().enabled, Is.True);
    }

    [Test]
    public void RimCarriesTheGradedColourRatherThanABinaryGreen()
    {
        using AffordanceFixture fixture = new();
        const float quality = 0.25f;

        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, quality),
            null,
            default);

        MaterialPropertyBlock properties = fixture.ReadRim(fixture.WallHold, out MeshRenderer rim);
        Assert.That(
            properties.GetColor("_AffordanceColor"),
            Is.EqualTo(GripAffordancePolicy.EvaluateQualityColor(quality))
                .Using(ColorEqualityComparer.Instance));
        Assert.That(
            properties.GetColor("_AffordanceColor"),
            Is.Not.EqualTo(GripAffordancePolicy.HighQualityColor)
                .Using(ColorEqualityComparer.Instance),
            "A middling grip must not read as a solid green.");
        Assert.That(
            properties.GetFloat("_AffordanceAlpha"),
            Is.EqualTo(GripAffordancePolicy.LatchedAlpha).Within(1e-5f));
        Assert.That(
            properties.GetFloat("_AffordanceRimPower"),
            Is.EqualTo(GripAffordancePolicy.EvaluateRimPower(quality)).Within(1e-5f));
        Assert.That(rim.sharedMaterial, Is.Not.Null);
    }

    /// <summary>Condition B holds a wall hold and condition C holds the detached ghost copy of one.
    /// Both arrive here as a hold GameObject carrying the same mesh, so the same inputs must produce
    /// byte-identical rim properties or the two conditions would not be comparable.</summary>
    [Test]
    public void GhostAndWallHoldsProduceTheSameRim()
    {
        using AffordanceFixture fixture = new();
        GripAffordance affordance = GripAffordancePolicy.Resolve(true, AllFingersMask, 0.62f);

        fixture.Presenter.Apply(fixture.WallHold, affordance, null, default);
        MaterialPropertyBlock wall = fixture.ReadRim(fixture.WallHold, out MeshRenderer wallRim);

        fixture.Presenter.Apply(fixture.GhostHold, affordance, null, default);
        MaterialPropertyBlock ghost = fixture.ReadRim(fixture.GhostHold, out MeshRenderer ghostRim);

        Assert.That(
            ghost.GetColor("_AffordanceColor"),
            Is.EqualTo(wall.GetColor("_AffordanceColor")).Using(ColorEqualityComparer.Instance));
        Assert.That(
            ghost.GetFloat("_AffordanceAlpha"),
            Is.EqualTo(wall.GetFloat("_AffordanceAlpha")).Within(0f));
        Assert.That(
            ghost.GetFloat("_AffordanceRimPower"),
            Is.EqualTo(wall.GetFloat("_AffordanceRimPower")).Within(0f));
        Assert.That(ghostRim.sharedMaterial, Is.SameAs(wallRim.sharedMaterial));
        Assert.That(wallRim.enabled, Is.False, "Only the hold in hand keeps its rim.");
    }

    [Test]
    public void BothHandsOnOneHoldShowASingleRimWithTheStrongerCue()
    {
        using AffordanceFixture fixture = new();

        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(false, ThumbAndIndexMask, 0.9f),
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.4f));

        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(1));
        MaterialPropertyBlock properties = fixture.ReadRim(fixture.WallHold, out MeshRenderer rim);
        Assert.That(rim.enabled, Is.True);
        Assert.That(
            properties.GetFloat("_AffordanceAlpha"),
            Is.EqualTo(GripAffordancePolicy.LatchedAlpha).Within(1e-5f));
        Assert.That(
            properties.GetColor("_AffordanceColor"),
            Is.EqualTo(GripAffordancePolicy.EvaluateQualityColor(0.4f))
                .Using(ColorEqualityComparer.Instance));
    }

    [Test]
    public void ReleasingHidesTheRimWithoutRebuildingIt()
    {
        using AffordanceFixture fixture = new();

        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.7f),
            null,
            default);
        MeshRenderer rim = fixture.WallHold.transform
            .Find(GripAffordanceOutlinePresenter.OutlineName)
            .GetComponent<MeshRenderer>();
        Assert.That(rim.enabled, Is.True);

        fixture.Presenter.Apply(fixture.WallHold, default, null, default);
        Assert.That(rim.enabled, Is.False);
        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(1));

        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(false, ThumbAndIndexMask, 0.1f),
            null,
            default);
        Assert.That(rim.enabled, Is.True);
        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(1));
    }

    [Test]
    public void HideAllClearsEveryRimWithoutDestroyingThem()
    {
        using AffordanceFixture fixture = new();
        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.7f),
            fixture.GhostHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.7f));
        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(2));

        fixture.Presenter.HideAll();

        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(2));
        foreach (GameObject hold in new[] { fixture.WallHold, fixture.GhostHold })
        {
            Assert.That(
                hold.transform.Find(GripAffordanceOutlinePresenter.OutlineName)
                    .GetComponent<MeshRenderer>().enabled,
                Is.False);
        }
    }

    /// <summary>GhostHoldController clones the wall hold and then stamps the whole subtree onto the
    /// ghost layer, so a rim that came along with the clone must be adopted and re-layered instead
    /// of duplicated.</summary>
    [Test]
    public void RimReclaimsAndRelayersAClonedGhostChild()
    {
        using AffordanceFixture fixture = new();
        GripAffordance affordance = GripAffordancePolicy.Resolve(true, AllFingersMask, 0.5f);

        fixture.Presenter.Apply(fixture.GhostHold, affordance, null, default);
        Transform rim = fixture.GhostHold.transform.Find(GripAffordanceOutlinePresenter.OutlineName);
        int ghostLayer = LayerMask.NameToLayer("StudyGhostHolds");
        Assert.That(ghostLayer, Is.GreaterThanOrEqualTo(0), "The project must define StudyGhostHolds.");
        rim.gameObject.layer = ghostLayer;

        fixture.Presenter.Apply(fixture.GhostHold, affordance, null, default);

        Assert.That(rim.gameObject.layer, Is.EqualTo(GripAffordanceOutlinePresenter.OutlineLayer));
        Assert.That(fixture.GhostHold.transform.childCount, Is.EqualTo(1), "The rim must not be duplicated.");
        Assert.That(fixture.Presenter.OutlineCount, Is.EqualTo(1));
    }

    [Test]
    public void ClearRemovesEveryRim()
    {
        using AffordanceFixture fixture = new();
        fixture.Presenter.Apply(
            fixture.WallHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.7f),
            fixture.GhostHold,
            GripAffordancePolicy.Resolve(true, AllFingersMask, 0.3f));

        fixture.Presenter.Clear();

        Assert.That(fixture.Presenter.OutlineCount, Is.Zero);
        Assert.That(
            fixture.WallHold.transform.Find(GripAffordanceOutlinePresenter.OutlineName),
            Is.Null);
        Assert.That(
            fixture.GhostHold.transform.Find(GripAffordanceOutlinePresenter.OutlineName),
            Is.Null);
    }

    [Test]
    public void RimRefusesAHoldWithoutAMesh()
    {
        using AffordanceFixture fixture = new();
        GameObject meshless = new("Meshless Hold");
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Presenter.Apply(
                meshless,
                GripAffordancePolicy.Resolve(true, AllFingersMask, 0.5f),
                null,
                default));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(meshless);
        }
    }

    /// <summary>A wall hold and a detached copy of it, the two things B and C put in the hand.</summary>
    private sealed class AffordanceFixture : IDisposable
    {
        public AffordanceFixture()
        {
            WallHold = CreateHold("A5", "StudyHolds");
            GhostHold = CreateHold("A5#ghost", "StudyGhostHolds");
            Presenter = new GripAffordanceOutlinePresenter();
        }

        public GameObject WallHold { get; }
        public GameObject GhostHold { get; }
        public GripAffordanceOutlinePresenter Presenter { get; }

        public MaterialPropertyBlock ReadRim(GameObject hold, out MeshRenderer rim)
        {
            Transform found = hold.transform.Find(GripAffordanceOutlinePresenter.OutlineName);
            Assert.That(found, Is.Not.Null, "Hold " + hold.name + " has no rim.");
            rim = found.GetComponent<MeshRenderer>();
            MaterialPropertyBlock properties = new();
            rim.GetPropertyBlock(properties);
            return properties;
        }

        public void Dispose()
        {
            Presenter.Clear();
            UnityEngine.Object.DestroyImmediate(WallHold);
            UnityEngine.Object.DestroyImmediate(GhostHold);
        }

        private static GameObject CreateHold(string name, string layerName)
        {
            GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hold.name = name;
            UnityEngine.Object.DestroyImmediate(hold.GetComponent<Collider>());
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0)
            {
                hold.layer = layer;
            }
            return hold;
        }
    }
}
