using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class StudyRehearsalTimingTests
{
    private string recoveryStudyRoot;

    [SetUp]
    public void SetUp()
    {
        recoveryStudyRoot = Path.Combine(
            Path.GetTempPath(),
            "vhard-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recoveryStudyRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(recoveryStudyRoot))
        {
            Directory.Delete(recoveryStudyRoot, true);
        }
    }

    [Test]
    public void ConditionBStartsOnlyAtGripLocomotionEngagement()
    {
        Assert.That(
            StudyRehearsalTiming.TryGetFirstInteraction("B", false, true, out _),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.TryGetFirstInteraction("B", true, false, out string interaction),
            Is.True);
        Assert.That(interaction, Is.EqualTo("GripLocomotionEngaged"));
    }

    [Test]
    public void ConditionCStartsOnlyAtFirstDetachedHold()
    {
        Assert.That(
            StudyRehearsalTiming.TryGetFirstInteraction("C", true, false, out _),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.TryGetFirstInteraction("C", false, true, out string interaction),
            Is.True);
        Assert.That(interaction, Is.EqualTo("HoldDetached"));
    }

    [Test]
    public void BaselineDoesNotWaitForVrInteraction()
    {
        Assert.That(
            StudyRehearsalTiming.TryGetFirstInteraction("A", true, true, out string interaction),
            Is.False);
        Assert.That(interaction, Is.Empty);
    }

    [Test]
    public void PanelPinchMustBeReleasedAfterTrackingBecomesConfident()
    {
        bool wasPinching = false;
        bool pinchArmed = false;

        Assert.That(
            StudyRehearsalTiming.TryConsumeArmedPinch(
                true, true, ref wasPinching, ref pinchArmed),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.TryConsumeArmedPinch(
                true, false, ref wasPinching, ref pinchArmed),
            Is.False);
        Assert.That(pinchArmed, Is.True);
        Assert.That(
            StudyRehearsalTiming.TryConsumeArmedPinch(
                true, true, ref wasPinching, ref pinchArmed),
            Is.True);
        Assert.That(
            StudyRehearsalTiming.TryConsumeArmedPinch(
                true, true, ref wasPinching, ref pinchArmed),
            Is.False);

        Assert.That(
            StudyRehearsalTiming.TryConsumeArmedPinch(
                false, false, ref wasPinching, ref pinchArmed),
            Is.False);
        Assert.That(pinchArmed, Is.False);
    }

    [Test]
    public void ElapsedDisplayContinuesPastFormerBlockLimit()
    {
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(0f), Is.EqualTo("00:00"));
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(1200f), Is.EqualTo("20:00"));
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(3661.9f), Is.EqualTo("61:01"));
    }

    [Test]
    public void PanelConfirmationRequiresASecondFrameWithinTheWindow()
    {
        string pendingAction = string.Empty;
        float deadline = -1f;
        int armedFrame = -1;

        Assert.That(
            StudyRehearsalTiming.TryConfirmPanelAction(
                "complete-block", 10f, 100, 4f,
                ref pendingAction, ref deadline, ref armedFrame),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.TryConfirmPanelAction(
                "complete-block", 10f, 100, 4f,
                ref pendingAction, ref deadline, ref armedFrame),
            Is.False,
            "Two hands must not satisfy confirmation in one frame.");
        Assert.That(
            StudyRehearsalTiming.TryConfirmPanelAction(
                "complete-block", 10.1f, 101, 4f,
                ref pendingAction, ref deadline, ref armedFrame),
            Is.True);
        Assert.That(pendingAction, Is.Empty);
    }

    [Test]
    public void ExpiredPanelConfirmationRearmsInsteadOfExecuting()
    {
        string pendingAction = string.Empty;
        float deadline = -1f;
        int armedFrame = -1;

        StudyRehearsalTiming.TryConfirmPanelAction(
            "end-practice", 2f, 10, 4f,
            ref pendingAction, ref deadline, ref armedFrame);

        Assert.That(
            StudyRehearsalTiming.TryConfirmPanelAction(
                "end-practice", 7f, 11, 4f,
                ref pendingAction, ref deadline, ref armedFrame),
            Is.False);
        Assert.That(pendingAction, Is.EqualTo("end-practice"));
        Assert.That(deadline, Is.EqualTo(11f));
    }

    [Test]
    public void PracticeAndEstimationUseTheGuardedSummonGesture()
    {
        Assert.That(StudyRehearsalTiming.RequiresPanelSummonDwell(false, false), Is.False);
        Assert.That(StudyRehearsalTiming.RequiresPanelSummonDwell(true, false), Is.True);
        Assert.That(StudyRehearsalTiming.RequiresPanelSummonDwell(false, true), Is.True);
    }

    [Test]
    public void PanelDragPreservesGrabOffsetAlongNormalizedPointerRay()
    {
        Vector3 position = StudyRehearsalTiming.ResolvePanelDragPosition(
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 0f, 2f),
            0.75f,
            new Vector3(0.1f, -0.2f, 0f));

        Assert.That(position, Is.EqualTo(new Vector3(1.1f, 1.8f, 3.75f)));
    }

    [Test]
    public void PanelDragRejectsInvalidPointerPose()
    {
        Assert.That(
            () => StudyRehearsalTiming.ResolvePanelDragPosition(
                Vector3.zero,
                Vector3.zero,
                0.75f,
                Vector3.zero),
            Throws.ArgumentException);
        Assert.That(
            () => StudyRehearsalTiming.ResolvePanelDragPosition(
                Vector3.zero,
                Vector3.forward,
                float.NaN,
                Vector3.zero),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void PanelViewportClampKeepsPanelExtentsAndDepthVisible()
    {
        Vector3 position = StudyRehearsalTiming.ClampPanelViewportPosition(
            new Vector3(1.2f, -0.3f, -1f),
            new Vector2(0.2f, 0.3f),
            0.05f,
            0.55f,
            1.5f);

        Assert.That(position.x, Is.EqualTo(0.75f).Within(0.0001f));
        Assert.That(position.y, Is.EqualTo(0.35f).Within(0.0001f));
        Assert.That(position.z, Is.EqualTo(0.55f).Within(0.0001f));
    }

    [Test]
    public void PanelViewportClampCentersAnExtentTooLargeForTheView()
    {
        Vector3 position = StudyRehearsalTiming.ClampPanelViewportPosition(
            new Vector3(0.9f, 0.1f, 2f),
            new Vector2(0.6f, 0.7f),
            0.05f,
            0.55f,
            1.5f);

        Assert.That(position.x, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(position.y, Is.EqualTo(0.5f).Within(0.0001f));
        Assert.That(position.z, Is.EqualTo(1.5f).Within(0.0001f));
    }

    [Test]
    public void PanelViewportClampContainsFinalFacingPanelAndTimerBounds()
    {
        GameObject cameraObject = new("Panel Clamp Test Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.fieldOfView = 80f;
        camera.aspect = 16f / 9f;
        camera.nearClipPlane = 0.01f;
        try
        {
            Type panelType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("StudyControlPanel"))
                .Single(type => type != null);
            Type stateType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("StudySessionState"))
                .Single(type => type != null);
            ConstructorInfo constructor = panelType.GetConstructors()
                .Single(candidateConstructor => candidateConstructor.GetParameters().Length == 6);
            object panel = constructor.Invoke(new object[]
            {
                null,
                camera,
                null,
                null,
                Activator.CreateInstance(stateType),
                new Func<float>(() => 0f),
            });
            MethodInfo clampMethod = panelType.GetMethod(
                "ClampPanelPositionToViewport",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(clampMethod, Is.Not.Null);
            Vector3 candidate = camera.ViewportToWorldPoint(new Vector3(1.8f, -0.8f, 0.7f));
            Vector3 clamped = (Vector3)clampMethod.Invoke(panel, new object[] { candidate });
            Quaternion rotation = Quaternion.LookRotation(clamped - camera.transform.position, camera.transform.up);
            Vector3[] localCorners =
            {
                new(-0.41f, -0.51f, 0f),
                new(0.41f, -0.51f, 0f),
                new(-0.41f, 0.62f, 0f),
                new(0.41f, 0.62f, 0f),
            };
            foreach (Vector3 localCorner in localCorners)
            {
                Vector3 viewportCorner = camera.WorldToViewportPoint(clamped + rotation * localCorner);
                Assert.That(viewportCorner.x, Is.InRange(0.039f, 0.961f));
                Assert.That(viewportCorner.y, Is.InRange(0.039f, 0.961f));
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
    }

    [Test]
    public void PracticeEligibilityIsScopedPerParticipant()
    {
        HashSet<string> practiced = new() { "P08" };
        HashSet<string> blocksStarted = new() { "P07" };

        Assert.That(
            StudyRehearsalTiming.CanStartPractice("P07", practiced, blocksStarted),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.CanStartPractice("P08", practiced, blocksStarted),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.CanStartPractice("P09", practiced, blocksStarted),
            Is.True);
    }

    [Test]
    public void EstimationSelectionMustMatchJustEndedParticipantAndBlock()
    {
        Assert.That(
            StudyRehearsalTiming.IsEstimationSelectionMatch("P07", 2, "P07", 2),
            Is.True);
        Assert.That(
            StudyRehearsalTiming.IsEstimationSelectionMatch("P08", 2, "P07", 2),
            Is.False);
        Assert.That(
            StudyRehearsalTiming.IsEstimationSelectionMatch("P07", 3, "P07", 2),
            Is.False);
    }

    [Test]
    public void RecordedEstimationIsDetectedAcrossBlockRetries()
    {
        string participantRoot = Path.Combine(
            Path.GetTempPath(),
            "vhard-estimation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(
                participantRoot,
                "block1_A_MB2016_19215_retry2",
                "estimation_retry1"));
            Directory.CreateDirectory(Path.Combine(
                participantRoot,
                "block2_B_MB2016_21329",
                "estimation_notes"));

            Assert.That(StudyRehearsalTiming.HasRecordedEstimation(participantRoot, 1), Is.True);
            Assert.That(StudyRehearsalTiming.HasRecordedEstimation(participantRoot, 2), Is.False);
            Assert.That(StudyRehearsalTiming.HasRecordedEstimation(participantRoot, 3), Is.False);
        }
        finally
        {
            if (Directory.Exists(participantRoot))
            {
                Directory.Delete(participantRoot, true);
            }
        }
    }

    [Test]
    public void CompletedBlockRecoveryAcceptsMatchingCompletedManifest()
    {
        StudyScheduleRow row = RecoveryRow("P07", 1, "B", "MB2016_21329");
        string expectedDirectory = WriteRecoveryManifest(
            "P07",
            "block1_B_MB2016_21329",
            "P07",
            1,
            "B",
            "MB2016_21329",
            0,
            false,
            "2026-07-28T10:15:30.0000000Z",
            "completed_manual");

        bool recovered = StudyRehearsalTiming.TryRecoverCompletedBlock(
            recoveryStudyRoot,
            row,
            out string directory,
            out string diagnostic);

        Assert.That(recovered, Is.True);
        Assert.That(directory, Is.EqualTo(expectedDirectory));
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void CompletedBlockRecoveryChoosesHighestValidRetryDeterministically()
    {
        StudyScheduleRow row = RecoveryRow("P07", 2, "C", "MB2016_19215");
        string expectedDirectory = WriteRecoveryManifest(
            "P07",
            "block2_C_MB2016_19215_retry2",
            "P07",
            2,
            "C",
            "MB2016_19215",
            2,
            false,
            "2026-07-28T09:00:00.0000000Z",
            "completed_early");
        WriteRecoveryManifest(
            "P07",
            "block2_C_MB2016_19215",
            "P07",
            2,
            "C",
            "MB2016_19215",
            0,
            false,
            "2026-07-28T11:00:00.0000000Z",
            "timer_expired");

        bool recovered = StudyRehearsalTiming.TryRecoverCompletedBlock(
            recoveryStudyRoot,
            row,
            out string directory,
            out string diagnostic);

        Assert.That(recovered, Is.True);
        Assert.That(directory, Is.EqualTo(expectedDirectory));
        Assert.That(diagnostic, Is.Empty);
    }

    [Test]
    public void CompletedBlockRecoveryRejectsMalformedAndIncompleteManifests()
    {
        StudyScheduleRow row = RecoveryRow("P07", 3, "A", "MB2016_19215");
        string malformedDirectory = Path.Combine(
            recoveryStudyRoot,
            "P07",
            "block3_A_MB2016_19215");
        Directory.CreateDirectory(malformedDirectory);
        File.WriteAllText(Path.Combine(malformedDirectory, "session.json"), "{ malformed");
        WriteRecoveryManifest(
            "P07",
            "block3_A_MB2016_19215_retry1",
            "P07",
            3,
            "A",
            "MB2016_19215",
            1,
            false,
            string.Empty,
            string.Empty);
        WriteRecoveryManifest(
            "P07",
            "block3_A_MB2016_19215_retry2",
            "P07",
            3,
            "A",
            "MB2016_19215",
            2,
            false,
            "2026-07-28T12:00:00.0000000Z",
            "running");

        bool recovered = StudyRehearsalTiming.TryRecoverCompletedBlock(
            recoveryStudyRoot,
            row,
            out string directory,
            out string diagnostic);

        Assert.That(recovered, Is.False);
        Assert.That(directory, Is.Empty);
        Assert.That(diagnostic, Does.Contain("System.FormatException"));
        Assert.That(diagnostic, Does.Contain("manifest endUtc is empty"));
        Assert.That(diagnostic, Does.Contain("manifest endReason is still running"));
    }

    [Test]
    public void CompletedBlockRecoveryDoesNotCrossParticipantDirectories()
    {
        StudyScheduleRow row = RecoveryRow("P07", 1, "B", "MB2016_21329");
        WriteRecoveryManifest(
            "P08",
            "block1_B_MB2016_21329",
            "P07",
            1,
            "B",
            "MB2016_21329",
            0,
            false,
            "2026-07-28T12:00:00.0000000Z",
            "timer_expired");
        WriteRecoveryManifest(
            "P07",
            "block1_B_MB2016_21329",
            "P08",
            1,
            "B",
            "MB2016_21329",
            0,
            false,
            "2026-07-28T12:05:00.0000000Z",
            "timer_expired");

        bool recovered = StudyRehearsalTiming.TryRecoverCompletedBlock(
            recoveryStudyRoot,
            row,
            out string directory,
            out string diagnostic);

        Assert.That(recovered, Is.False);
        Assert.That(directory, Is.Empty);
        Assert.That(diagnostic, Does.Contain("manifest participant does not match"));
    }

    [Test]
    public void PreBlockHeadsetPresenceStartsDonningLatencyBeforeBlockWear()
    {
        Assert.That(
            StudyRehearsalTiming.ResolveDonningStartRealtime(12f, 40f),
            Is.EqualTo(12f));
        Assert.That(
            StudyRehearsalTiming.ResolveDonningStartRealtime(-1f, 40f),
            Is.EqualTo(40f));
    }

    private static StudyScheduleRow RecoveryRow(
        string participant,
        int block,
        string condition,
        string route)
    {
        return new StudyScheduleRow
        {
            participant = participant,
            block = block,
            condition = condition,
            route = route,
        };
    }

    private string WriteRecoveryManifest(
        string directoryParticipant,
        string directoryName,
        string manifestParticipant,
        int block,
        string condition,
        string route,
        int retry,
        bool adhoc,
        string endUtc,
        string endReason)
    {
        string directory = Path.Combine(recoveryStudyRoot, directoryParticipant, directoryName);
        Directory.CreateDirectory(directory);
        string json = "{\n" +
                      "  \"participant\": \"" + manifestParticipant + "\",\n" +
                      "  \"block\": " + block + ",\n" +
                      "  \"condition\": \"" + condition + "\",\n" +
                      "  \"route\": \"" + route + "\",\n" +
                      "  \"retry\": " + retry + ",\n" +
                      "  \"adhoc\": " + (adhoc ? "true" : "false") + ",\n" +
                      "  \"endUtc\": \"" + endUtc + "\",\n" +
                      "  \"endReason\": \"" + endReason + "\"\n" +
                      "}";
        File.WriteAllText(Path.Combine(directory, "session.json"), json);
        return directory;
    }

}
