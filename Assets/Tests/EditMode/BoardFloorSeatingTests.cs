using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class BoardFloorSeatingTests
{
    private const string SceneryRootName = "GripLocomotionSceneryRoot";
    private const float AuthoredFloorDepthMeters = -0.57f;
    private const float AuthoredBoardDistanceMeters = 1.86219f;
    private const float AuthoredBoardCenterOffsetMeters = -0.29f;
    private const int LowestReachableRow = 6;

    [Test]
    public void BoardRootSeatsTheReconstructedFloorOnTheTrackingOrigin()
    {
        GameObject environment = new("Test Environment");
        try
        {
            BuildAuthoredHierarchy(
                environment,
                out Transform alignment,
                out Transform moonboard,
                out Transform floor);
            Component controller =
                alignment.gameObject.AddComponent(FindLoadedType("BoardAlignmentController"));
            InvokeAwake(controller);

            Assert.That(moonboard.position.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(floor.position.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(alignment.localPosition.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(
                alignment.localPosition.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void BoardRootStandsItsKickerAtTheStartStandoffAheadOfTheTrackingOrigin()
    {
        GameObject environment = new("Test Environment");
        try
        {
            BuildAuthoredHierarchy(
                environment,
                out Transform alignment,
                out Transform moonboard,
                out Transform floor);
            Component controller =
                alignment.gameObject.AddComponent(FindLoadedType("BoardAlignmentController"));
            InvokeAwake(controller);

            Assert.That(
                moonboard.position.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0001f));
            Assert.That(moonboard.position.z, Is.LessThan(AuthoredBoardDistanceMeters));
            Assert.That(moonboard.position.x, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(
                floor.position.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters - AuthoredBoardDistanceMeters)
                    .Within(0.0001f));
            Assert.That(
                floor.position.x,
                Is.EqualTo(-AuthoredBoardCenterOffsetMeters).Within(0.0001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void ClearingAlignmentRestoresTheFloorSeatedPose()
    {
        GameObject environment = new("Test Environment");
        try
        {
            BuildAuthoredHierarchy(
                environment,
                out Transform alignment,
                out Transform moonboard,
                out Transform floor);
            Type controllerType = FindLoadedType("BoardAlignmentController");
            Component controller = alignment.gameObject.AddComponent(controllerType);
            InvokeAwake(controller);

            alignment.position += new Vector3(0.4f, 1.2f, -0.3f);
            MethodInfo clearAlignment = controllerType.GetMethod("ClearAlignment");
            Assert.That(clearAlignment, Is.Not.Null);
            Assert.That(clearAlignment.Invoke(controller, null), Is.True);

            Assert.That(moonboard.position.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(floor.position.y, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(
                moonboard.position.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0001f));
            Assert.That(moonboard.position.x, Is.EqualTo(0f).Within(0.0001f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void StartStandoffClearsATallStandingParticipant()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        float resolved = BoardStandoffPolicy.GetBoardBaseDistanceMeters(
            catalog.overhangAngleDegrees,
            catalog.geometry.kickerHeightMeters,
            BoardStandoffPolicy.StandingHeightMeters,
            BoardStandoffPolicy.StandingHeadClearanceMeters);
        Assert.That(
            resolved,
            Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.005f));

        float crownGap = BoardStandoffPolicy.GetFaceDistanceMeters(
            BoardStandoffPolicy.DefaultBoardBaseDistanceMeters,
            catalog.overhangAngleDegrees,
            catalog.geometry.kickerHeightMeters,
            BoardStandoffPolicy.StandingHeightMeters);
        Assert.That(crownGap, Is.GreaterThan(0.2f));
        Assert.That(
            BoardStandoffPolicy.GetFaceDistanceMeters(
                BoardStandoffPolicy.DefaultBoardBaseDistanceMeters,
                catalog.overhangAngleDegrees,
                catalog.geometry.kickerHeightMeters,
                2f),
            Is.GreaterThan(0f));
        Assert.That(
            BoardStandoffPolicy.GetFaceDistanceMeters(
                BoardStandoffPolicy.DefaultBoardBaseDistanceMeters,
                catalog.overhangAngleDegrees,
                catalog.geometry.kickerHeightMeters,
                catalog.geometry.kickerHeightMeters),
            Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0001f));
    }

    [Test]
    public void SeatedBoardPutsTheLockedRoutesRehearsalBandInFrontOfTheParticipant()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        GameObject environment = new("Test Environment");
        try
        {
            BuildAuthoredHierarchy(
                environment,
                out Transform alignment,
                out Transform moonboard,
                out Transform _);
            Component controller =
                alignment.gameObject.AddComponent(FindLoadedType("BoardAlignmentController"));
            InvokeAwake(controller);

            float travel = AuthoredBoardDistanceMeters - BoardStandoffPolicy.DefaultBoardBaseDistanceMeters;
            float widestSeatedReach = 0f;
            float widestAuthoredReach = 0f;
            foreach (MoonBoardRouteDefinition route in catalog.routes)
            {
                foreach (MoonBoardRouteMove move in route.moves)
                {
                    Assert.That(
                        MoonBoardStudyCatalog.TryParseCoordinate(move.coordinate, out _, out int row),
                        Is.True);
                    Assert.That(catalog.TryGetHold(move.coordinate, out MoonBoardHoldDefinition hold), Is.True);
                    Vector3 boardLocal = catalog.GetSeatedBoardLocalPosition(hold);
                    Vector3 seated = moonboard.TransformPoint(boardLocal);

                    Assert.That(
                        boardLocal.z + AuthoredBoardDistanceMeters - seated.z,
                        Is.EqualTo(travel).Within(0.0001f),
                        move.coordinate + " did not travel with the board.");
                    Assert.That(
                        seated.x - boardLocal.x,
                        Is.EqualTo(0f).Within(0.0001f),
                        move.coordinate + " is not measured from the board centre column.");
                    widestSeatedReach = Mathf.Max(widestSeatedReach, Mathf.Abs(seated.x));
                    widestAuthoredReach = Mathf.Max(
                        widestAuthoredReach,
                        Mathf.Abs(boardLocal.x + AuthoredBoardCenterOffsetMeters));
                    if (row >= LowestReachableRow)
                    {
                        Assert.That(
                            seated.z,
                            Is.LessThan(0.95f),
                            move.coordinate + " is beyond a standing reach.");
                        Assert.That(
                            Mathf.Abs(seated.x),
                            Is.LessThan(0.85f),
                            move.coordinate + " is beyond a lateral standing reach.");
                    }
                }
            }

            Assert.That(
                widestSeatedReach,
                Is.LessThan(widestAuthoredReach - 0.25f),
                "Centring did not narrow the widest lateral reach on the locked routes.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void StandoffPolicyRejectsGeometryItCannotResolve()
    {
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetBoardBaseDistanceMeters(90f, 0.37f, 1.9f, 0.22f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetBoardBaseDistanceMeters(0f, 0.37f, 1.9f, 0.22f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetBoardBaseDistanceMeters(40f, 0.37f, 0.3f, 0.22f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetBoardBaseDistanceMeters(40f, 0.37f, 1.9f, -0.01f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetBoardBaseDistanceMeters(40f, float.NaN, 1.9f, 0.22f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetFaceDistanceMeters(float.NaN, 40f, 0.37f, 1.9f));
        Assert.Throws<ArgumentException>(() =>
            BoardStandoffPolicy.GetFaceDistanceMeters(1.5f, 40f, -0.01f, 1.9f));
    }

    private static void BuildAuthoredHierarchy(
        GameObject environment,
        out Transform alignment,
        out Transform moonboard,
        out Transform floor)
    {
        GameObject alignmentObject = new("BoardAlignmentRoot");
        GameObject moonboardObject = new("Moonboard");
        GameObject sceneryObject = new(SceneryRootName);
        GameObject floorObject = new("Floor");

        alignmentObject.transform.SetParent(environment.transform, false);
        alignmentObject.transform.localPosition = new Vector3(
            AuthoredBoardCenterOffsetMeters,
            AuthoredFloorDepthMeters,
            AuthoredBoardDistanceMeters);
        moonboardObject.transform.SetParent(alignmentObject.transform, false);
        sceneryObject.transform.SetParent(alignmentObject.transform, false);
        sceneryObject.transform.localPosition = new Vector3(
            -AuthoredBoardCenterOffsetMeters,
            -AuthoredFloorDepthMeters,
            -AuthoredBoardDistanceMeters);
        floorObject.transform.SetParent(sceneryObject.transform, false);
        floorObject.transform.localPosition = new Vector3(0f, AuthoredFloorDepthMeters, 0f);

        alignment = alignmentObject.transform;
        moonboard = moonboardObject.transform;
        floor = floorObject.transform;
    }

    private static MoonBoardStudyCatalog LoadCatalog()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        string json = File.ReadAllText(path);
        Assert.That(
            MoonBoardStudyCatalog.TryParse(json, out MoonBoardStudyCatalog catalog, out string error),
            Is.True,
            error);
        return catalog;
    }

    private static void InvokeAwake(Component controller)
    {
        MethodInfo awake = controller.GetType().GetMethod(
            "Awake",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(awake, Is.Not.Null);
        awake.Invoke(controller, null);
    }

    private static Type FindLoadedType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name))
            .Single(type => type != null);
    }
}
