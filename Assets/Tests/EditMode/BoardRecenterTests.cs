using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class BoardRecenterTests
{
    private const float AuthoredFloorDepthMeters = -0.57f;
    private const float AuthoredBoardDistanceMeters = 1.86219f;
    private const float AuthoredBoardCenterOffsetMeters = -0.29f;

    [Test]
    public void StandingYawIsIdentityForAHeadFacingTheSeatingDirection()
    {
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(Vector3.forward, Vector3.up, out Quaternion yaw),
            Is.True);
        Assert.That(Quaternion.Angle(yaw, Quaternion.identity), Is.LessThan(0.05f));
    }

    [Test]
    public void StandingYawIgnoresPitchAndRoll()
    {
        Quaternion head = Quaternion.Euler(35f, 120f, -20f);
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(
                head * Vector3.forward,
                head * Vector3.up,
                out Quaternion yaw),
            Is.True);
        Assert.That(Quaternion.Angle(yaw, Quaternion.Euler(0f, 120f, 0f)), Is.LessThan(0.05f));
        Assert.That(Vector3.Angle(yaw * Vector3.up, Vector3.up), Is.LessThan(0.01f));
    }

    [Test]
    public void StandingYawFallsBackToTheUpAxisAtTheVerticalPoles()
    {
        // Looking straight down while facing +Z: forward is vertical, the head's up axis tips
        // toward the facing direction.
        Quaternion lookingDown = Quaternion.Euler(90f, 0f, 0f);
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(
                lookingDown * Vector3.forward,
                lookingDown * Vector3.up,
                out Quaternion downYaw),
            Is.True);
        Assert.That(Quaternion.Angle(downYaw, Quaternion.identity), Is.LessThan(0.05f));

        // Looking straight up while facing +Z: the up axis tips away from the facing direction.
        Quaternion lookingUp = Quaternion.Euler(-90f, 0f, 0f);
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(
                lookingUp * Vector3.forward,
                lookingUp * Vector3.up,
                out Quaternion upYaw),
            Is.True);
        Assert.That(Quaternion.Angle(upYaw, Quaternion.identity), Is.LessThan(0.05f));
    }

    [Test]
    public void StandingYawRejectsDegenerateAndNonFiniteAxes()
    {
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(Vector3.up, Vector3.down, out _),
            Is.False);
        Assert.That(
            BoardRecenterPolicy.TryGetStandingYaw(Vector3.zero, Vector3.zero, out _),
            Is.False);
        Assert.Throws<ArgumentException>(() =>
            BoardRecenterPolicy.TryGetStandingYaw(
                new Vector3(float.NaN, 0f, 1f), Vector3.up, out _));
        Assert.Throws<ArgumentException>(() =>
            BoardRecenterPolicy.TryGetStandingYaw(
                Vector3.forward, new Vector3(0f, float.PositiveInfinity, 0f), out _));
    }

    [Test]
    public void RecenteredPoseRebuildsTheSeatingInTheStandingFrame()
    {
        Quaternion standingYaw = Quaternion.Euler(0f, 90f, 0f);
        BoardRecenterPolicy.GetRecenteredPose(
            standingYaw,
            new Vector3(2f, 1.6f, -1f),
            new Vector3(0f, 0f, BoardStandoffPolicy.DefaultBoardBaseDistanceMeters),
            Quaternion.Euler(0f, 10f, 0f),
            out Vector3 position,
            out Quaternion rotation);

        Assert.That(
            position.x,
            Is.EqualTo(2f + BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0005f));
        Assert.That(position.y, Is.EqualTo(0f).Within(0.0005f));
        Assert.That(position.z, Is.EqualTo(-1f).Within(0.0005f));
        Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 100f, 0f)), Is.LessThan(0.05f));
    }

    [Test]
    public void RecenteredPoseKeepsTheSeatedHeightAndDropsTheHeadHeight()
    {
        BoardRecenterPolicy.GetRecenteredPose(
            Quaternion.identity,
            new Vector3(0.4f, 1.75f, 0.9f),
            new Vector3(0.3f, 0.12f, 1.4f),
            Quaternion.identity,
            out Vector3 position,
            out _);

        Assert.That(position.y, Is.EqualTo(0.12f).Within(0.0005f));
        Assert.That(position.x, Is.EqualTo(0.7f).Within(0.0005f));
        Assert.That(position.z, Is.EqualTo(2.3f).Within(0.0005f));
        Assert.Throws<ArgumentException>(() =>
            BoardRecenterPolicy.GetRecenteredPose(
                Quaternion.identity,
                new Vector3(float.NaN, 0f, 0f),
                Vector3.zero,
                Quaternion.identity,
                out _,
                out _));
    }

    [Test]
    public void RecenteringReseatsTheBoardAndRoomAheadOfTheParticipant()
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

            GameObject head = new("Test Head");
            head.transform.SetParent(environment.transform, false);
            head.transform.SetPositionAndRotation(
                new Vector3(1.2f, 1.68f, -0.8f),
                Quaternion.Euler(15f, 90f, 5f));

            Assert.That(InvokeRecenter(controller, head.transform), Is.True);

            // The board base lands one policy standoff ahead of the head's floor point, along
            // the head's yaw, standing upright on the tracking floor.
            Vector3 expectedBoard = new Vector3(1.2f, 0f, -0.8f) +
                Quaternion.Euler(0f, 90f, 0f) *
                new Vector3(0f, 0f, BoardStandoffPolicy.DefaultBoardBaseDistanceMeters);
            Assert.That(moonboard.position.x, Is.EqualTo(expectedBoard.x).Within(0.0005f));
            Assert.That(moonboard.position.y, Is.EqualTo(0f).Within(0.0005f));
            Assert.That(moonboard.position.z, Is.EqualTo(expectedBoard.z).Within(0.0005f));
            Assert.That(Vector3.Angle(moonboard.up, Vector3.up), Is.LessThan(0.01f));
            Assert.That(
                Vector3.Angle(moonboard.forward, Quaternion.Euler(0f, 90f, 0f) * Vector3.forward),
                Is.LessThan(0.05f));

            // The room comes along: the reconstructed floor stays on the tracking floor and its
            // authored offset from the board swings with the participant's yaw.
            Vector3 expectedFloor = expectedBoard + Quaternion.Euler(0f, 90f, 0f) * new Vector3(
                -AuthoredBoardCenterOffsetMeters,
                0f,
                -AuthoredBoardDistanceMeters);
            Assert.That(floor.position.x, Is.EqualTo(expectedFloor.x).Within(0.0005f));
            Assert.That(floor.position.y, Is.EqualTo(0f).Within(0.0005f));
            Assert.That(floor.position.z, Is.EqualTo(expectedFloor.z).Within(0.0005f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void RecenteringClearsAManualAlignmentBeforeReseating()
    {
        GameObject environment = new("Test Environment");
        try
        {
            BuildAuthoredHierarchy(
                environment,
                out Transform alignment,
                out Transform moonboard,
                out _);
            Type controllerType = FindLoadedType("BoardAlignmentController");
            Component controller = alignment.gameObject.AddComponent(controllerType);
            InvokeAwake(controller);

            FieldInfo aligned = controllerType.GetField(
                "isAligned",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(aligned, Is.Not.Null);
            aligned.SetValue(controller, true);
            alignment.position += new Vector3(0.7f, 0.2f, -0.4f);

            GameObject head = new("Test Head");
            head.transform.SetParent(environment.transform, false);
            head.transform.position = new Vector3(0f, 1.7f, 0f);

            Assert.That(InvokeRecenter(controller, head.transform), Is.True);
            Assert.That((bool)aligned.GetValue(controller), Is.False);

            BoardAlignmentSnapshot snapshot = (BoardAlignmentSnapshot)controllerType
                .GetMethod("GetSnapshot")
                .Invoke(controller, null);
            Assert.That(snapshot.isAligned, Is.False);
            Assert.That(snapshot.recenterEpoch, Is.EqualTo(1));

            string status = (string)controllerType
                .GetProperty("StatusMessage")
                .GetValue(controller);
            StringAssert.Contains("alignment cleared", status);
            Assert.That(moonboard.position.y, Is.EqualTo(0f).Within(0.0005f));
            Assert.That(
                moonboard.position.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0005f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    [Test]
    public void ClearingAlignmentAfterRecenterRestoresTheLoadSeating()
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

            GameObject head = new("Test Head");
            head.transform.SetParent(environment.transform, false);
            head.transform.SetPositionAndRotation(
                new Vector3(-2.1f, 1.55f, 3.4f),
                Quaternion.Euler(0f, -135f, 0f));
            Assert.That(InvokeRecenter(controller, head.transform), Is.True);

            MethodInfo clearAlignment = controllerType.GetMethod("ClearAlignment");
            Assert.That(clearAlignment.Invoke(controller, null), Is.True);
            Assert.That(moonboard.position.x, Is.EqualTo(0f).Within(0.0005f));
            Assert.That(moonboard.position.y, Is.EqualTo(0f).Within(0.0005f));
            Assert.That(
                moonboard.position.z,
                Is.EqualTo(BoardStandoffPolicy.DefaultBoardBaseDistanceMeters).Within(0.0005f));
            Assert.That(floor.position.y, Is.EqualTo(0f).Within(0.0005f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(environment);
        }
    }

    private static bool InvokeRecenter(Component controller, Transform head)
    {
        MethodInfo recenter = controller.GetType().GetMethod("TryRecenterToParticipant");
        Assert.That(recenter, Is.Not.Null);
        object[] arguments = { head, null };
        bool result = (bool)recenter.Invoke(controller, arguments);
        if (!result)
        {
            Assert.Fail("TryRecenterToParticipant refused: " + arguments[1]);
        }
        return result;
    }

    private static void BuildAuthoredHierarchy(
        GameObject environment,
        out Transform alignment,
        out Transform moonboard,
        out Transform floor)
    {
        GameObject alignmentObject = new("BoardAlignmentRoot");
        GameObject moonboardObject = new("Moonboard");
        GameObject sceneryObject = new("GripLocomotionSceneryRoot");
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
