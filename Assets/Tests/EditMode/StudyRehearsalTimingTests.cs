using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

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
