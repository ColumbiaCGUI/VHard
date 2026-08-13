using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class RouteRoleRingTests
{
    [Test]
    public void RingDiameterScalesWithTheHoldAndClampsAtBothEnds()
    {
        Assert.That(
            RouteCuePolicy.GetRingOuterDiameterMeters(0.15f),
            Is.EqualTo(0.15f * RouteCuePolicy.RingOuterDiameterFactor).Within(1e-5f));
        Assert.That(
            RouteCuePolicy.GetRingOuterDiameterMeters(0.02f),
            Is.EqualTo(RouteCuePolicy.MinimumRingOuterDiameterMeters).Within(1e-5f));
        Assert.That(
            RouteCuePolicy.GetRingOuterDiameterMeters(0.9f),
            Is.EqualTo(RouteCuePolicy.MaximumRingOuterDiameterMeters).Within(1e-5f));
    }

    [Test]
    public void RingDiameterRejectsNonPositiveHoldExtents()
    {
        Assert.Throws<ArgumentException>(() => RouteCuePolicy.GetRingOuterDiameterMeters(0f));
        Assert.Throws<ArgumentException>(() => RouteCuePolicy.GetRingOuterDiameterMeters(-0.1f));
        Assert.Throws<ArgumentException>(() => RouteCuePolicy.GetRingOuterDiameterMeters(float.NaN));
    }

    [Test]
    public void OutwardNormalFollowsTheHoldsRatherThanTheAuthoredWinding()
    {
        Vector3 planePoint = Vector3.zero;
        List<Vector3> holdsInFront = new() { new Vector3(0f, 0f, 0.05f), new Vector3(0.2f, 0.3f, 0.06f) };

        Assert.That(
            RouteCuePolicy.ResolveOutwardNormal(Vector3.forward, planePoint, holdsInFront),
            Is.EqualTo(Vector3.forward));
        Assert.That(
            RouteCuePolicy.ResolveOutwardNormal(Vector3.back, planePoint, holdsInFront),
            Is.EqualTo(Vector3.forward));
    }

    [Test]
    public void OutwardNormalIsNormalisedRegardlessOfInputLength()
    {
        List<Vector3> holds = new() { new Vector3(0f, 0f, 0.05f) };

        Vector3 resolved = RouteCuePolicy.ResolveOutwardNormal(
            Vector3.forward * 7.5f,
            Vector3.zero,
            holds);

        Assert.That(resolved.magnitude, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void OutwardNormalRefusesDegenerateInput()
    {
        List<Vector3> holds = new() { new Vector3(0f, 0f, 0.05f) };

        Assert.Throws<ArgumentException>(() =>
            RouteCuePolicy.ResolveOutwardNormal(Vector3.zero, Vector3.zero, holds));
        Assert.Throws<ArgumentException>(() =>
            RouteCuePolicy.ResolveOutwardNormal(Vector3.forward, Vector3.zero, new List<Vector3>()));
        Assert.Throws<ArgumentException>(() =>
            RouteCuePolicy.ResolveOutwardNormal(Vector3.forward, Vector3.zero, null));
        Assert.Throws<ArgumentException>(() =>
            RouteCuePolicy.ResolveOutwardNormal(
                Vector3.forward,
                Vector3.zero,
                new List<Vector3> { new(0f, 0f, 0.05f), new(0f, 0f, -0.05f) }));
    }

    [Test]
    public void RingsSitOffTheBoardPlaneByTheFixedOutwardOffset()
    {
        Vector3 planePoint = Vector3.zero;
        Vector3 normal = Vector3.forward;

        Vector3 projected = RouteCuePolicy.ProjectGridAnchorOntoBoard(
            new Vector3(0.4f, 1.2f, 0.08f),
            planePoint,
            normal,
            RouteCuePolicy.RingOutwardOffsetMeters);

        Assert.That(projected.x, Is.EqualTo(0.4f).Within(1e-5f));
        Assert.That(projected.y, Is.EqualTo(1.2f).Within(1e-5f));
        Assert.That(projected.z, Is.EqualTo(RouteCuePolicy.RingOutwardOffsetMeters).Within(1e-5f));
    }

    [Test]
    public void RingsCoverEveryRouteHoldUnderOneRootWithNoColliders()
    {
        using RoleRingFixture fixture = new(new[] { "A5", "B6", "C10", "D18" });
        fixture.Roles["A5"] = RouteCueRole.Start;
        fixture.Roles["B6"] = RouteCueRole.Start;
        fixture.Roles["D18"] = RouteCueRole.Finish;

        fixture.Presenter.Rebuild(fixture.Holds);

        int expectedRings = 0;
        foreach (string coordinate in fixture.Coordinates)
        {
            expectedRings += RouteCuePolicy.GetStyle(fixture.Roles[coordinate]).RingCount;
        }
        Assert.That(fixture.Presenter.RingCount, Is.EqualTo(expectedRings));
        Assert.That(fixture.Presenter.RingRoot.name, Is.EqualTo(RouteRoleRingPresenter.RingRootName));
        Assert.That(fixture.Presenter.RingRoot.parent, Is.EqualTo(fixture.BoardRoot.transform));
        Assert.That(fixture.Presenter.RingRoot.childCount, Is.EqualTo(expectedRings));
        foreach (Transform ring in fixture.Presenter.RingRoot)
        {
            Assert.That(ring.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(ring.gameObject.layer, Is.Not.EqualTo(LayerMask.NameToLayer("StudyHolds")));
            Assert.That(ring.gameObject.layer, Is.Not.EqualTo(LayerMask.NameToLayer("StudyGhostHolds")));
        }
    }

    [Test]
    public void RingColoursMatchTheRoleOfTheirHold()
    {
        using RoleRingFixture fixture = new(new[] { "A5", "B6", "C10", "D18" });
        fixture.Roles["A5"] = RouteCueRole.Start;
        fixture.Roles["B6"] = RouteCueRole.Start;
        fixture.Roles["D18"] = RouteCueRole.Finish;

        fixture.Presenter.Rebuild(fixture.Holds);

        MaterialPropertyBlock properties = new();
        foreach (Transform ring in fixture.Presenter.RingRoot)
        {
            string coordinate = ring.name
                .Substring(RouteRoleRingPresenter.RingNamePrefix.Length)
                .Split('_')[0];
            ring.GetComponent<Renderer>().GetPropertyBlock(properties);
            Assert.That(
                properties.GetColor("_BaseColor"),
                Is.EqualTo(RouteCuePolicy.GetStyle(fixture.Roles[coordinate]).Color)
                    .Using(UnityEngine.TestTools.Utils.ColorEqualityComparer.Instance),
                "Ring " + ring.name + " does not carry its role colour.");
        }
    }

    [Test]
    public void RebuildReplacesTheRingsOfThePreviousRoute()
    {
        using RoleRingFixture fixture = new(new[] { "A5", "B6" });

        fixture.Presenter.Rebuild(fixture.Holds);
        int firstCount = fixture.Presenter.RingCount;
        fixture.Presenter.Rebuild(fixture.Holds);

        Assert.That(fixture.Presenter.RingCount, Is.EqualTo(firstCount));
        Assert.That(fixture.Presenter.RingRoot.childCount, Is.EqualTo(firstCount));
    }

    [Test]
    public void ClearRemovesEveryRing()
    {
        using RoleRingFixture fixture = new(new[] { "A5", "B6" });
        fixture.Presenter.Rebuild(fixture.Holds);

        fixture.Presenter.Clear();

        Assert.That(fixture.Presenter.RingCount, Is.Zero);
        Assert.That(fixture.Presenter.RingRoot.childCount, Is.Zero);
    }

    [Test]
    public void RingsStayHiddenUntilTheConditionAsksForThem()
    {
        using RoleRingFixture fixture = new(new[] { "A5", "B6" });

        fixture.Presenter.Rebuild(fixture.Holds);
        Assert.That(fixture.Presenter.AreRingsVisible, Is.False);
        Assert.That(fixture.Presenter.RingRoot.gameObject.activeSelf, Is.False);

        fixture.Presenter.SetVisible(true);
        Assert.That(fixture.Presenter.RingRoot.gameObject.activeSelf, Is.True);

        fixture.Presenter.SetVisible(false);
        Assert.That(fixture.Presenter.RingRoot.gameObject.activeSelf, Is.False);
    }

    [Test]
    public void RoleStylesFollowTheMoonBoardLedConvention()
    {
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Start).Color, Is.EqualTo(RouteCuePolicy.StartColor));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Finish).Color, Is.EqualTo(RouteCuePolicy.FinishColor));
        Assert.That(
            RouteCuePolicy.GetStyle(RouteCueRole.Intermediate).Color,
            Is.EqualTo(RouteCuePolicy.IntermediateColor));
        Assert.That(RouteCuePolicy.RingInnerScale, Is.LessThan(1f).And.GreaterThan(0f));
    }

    /// <summary>A minimal overhanging board: a Main Surface whose normal faces the climber along
    /// +Z, with cube stand-ins for the route holds standing proud of that plane.</summary>
    private sealed class RoleRingFixture : IDisposable
    {
        public RoleRingFixture(IReadOnlyList<string> coordinates)
        {
            Coordinates = coordinates;
            BoardRoot = new GameObject("BoardRoot");
            GameObject surface = new("Main Surface");
            surface.transform.SetParent(BoardRoot.transform, false);
            surface.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            MainSurface = surface.transform;

            List<GameObject> holds = new(coordinates.Count);
            for (int index = 0; index < coordinates.Count; index++)
            {
                GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
                hold.name = coordinates[index];
                UnityEngine.Object.DestroyImmediate(hold.GetComponent<Collider>());
                hold.transform.SetParent(BoardRoot.transform, false);
                hold.transform.localPosition = new Vector3(index * 0.2f, index * 0.15f, 0.05f);
                hold.transform.localScale = Vector3.one * 0.1f;
                holds.Add(hold);
                Roles[coordinates[index]] = RouteCueRole.Intermediate;
            }
            Holds = holds;
            Presenter = new RouteRoleRingPresenter(BoardRoot.transform, MainSurface, GetStyle);
        }

        public IReadOnlyList<string> Coordinates { get; }
        public Dictionary<string, RouteCueRole> Roles { get; } = new();
        public GameObject BoardRoot { get; }
        public Transform MainSurface { get; }
        public IReadOnlyList<GameObject> Holds { get; }
        public RouteRoleRingPresenter Presenter { get; }

        private RouteCueStyle GetStyle(string coordinate)
        {
            return RouteCuePolicy.GetStyle(Roles[coordinate]);
        }

        public void Dispose()
        {
            if (BoardRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(BoardRoot);
            }
        }
    }
}
