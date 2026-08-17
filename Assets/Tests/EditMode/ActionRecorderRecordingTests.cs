using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class ActionRecorderRecordingTests
{
    private string temporaryDirectory;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "VHardRecordingTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }

    [Test]
    public void ActionRecorderFacadePreservesGuidAndPublicCaptureHeader()
    {
        Assert.That(
            AssetDatabase.AssetPathToGUID("Assets/Scripts/ActionRecorder.cs"),
            Is.EqualTo("13f6875c6d145614f9975fe73593035e"));

        Type facadeType = GetActionRecorderType();
        Assert.That(facadeType.GetField("playerHead"), Is.Not.Null);
        Assert.That(facadeType.GetField("sceneConfiguror"), Is.Not.Null);
        Assert.That(facadeType.GetField("recordToConsole"), Is.Not.Null);
        Assert.That(facadeType.GetField("recordToCsv"), Is.Not.Null);
        Assert.That(
            facadeType.GetMethod("BeginBlock", new[] { typeof(string), typeof(StudySessionManifest) }),
            Is.Not.Null);
        Assert.That(facadeType.GetMethod("EndBlock", Type.EmptyTypes), Is.Not.Null);
        Assert.That(
            facadeType.GetMethod(
                "Record",
                new[] { typeof(string), typeof(string), typeof(GameObject), typeof(string) }),
            Is.Not.Null);
        Assert.That(facadeType.GetMethod("GetHoldAggregates", Type.EmptyTypes), Is.Not.Null);
        Assert.That(facadeType.GetProperty("DroppedCaptureFrames"), Is.Not.Null);
        Assert.That(facadeType.GetProperty("IsRecording"), Is.Not.Null);
        Assert.That(facadeType.GetProperty("CurrentDirectory"), Is.Not.Null);

        string facadeHeader = (string)facadeType
            .GetMethod("BuildCaptureHeader", BindingFlags.Public | BindingFlags.Static)
            .Invoke(null, null);
        Assert.That(facadeHeader, Is.EqualTo(BuildLockedCaptureHeader()));
    }

    [Test]
    public void PartialTrackingDataRetainsLastKnownBoneTail()
    {
        Vector3[] positions = new Vector3[CaptureFrame.BoneCount];
        Quaternion[] rotations = new Quaternion[CaptureFrame.BoneCount];
        positions[1] = new Vector3(7f, 8f, 9f);
        rotations[1] = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f);
        MethodInfo copyBones = GetActionRecorderType().GetMethod(
            "CopyBones",
            BindingFlags.NonPublic | BindingFlags.Static);

        copyBones.Invoke(
            null,
            new object[]
            {
                new List<Vector3> { new(1f, 2f, 3f) },
                new List<Quaternion> { Quaternion.identity },
                positions,
                rotations,
            });

        Assert.That(positions[0], Is.EqualTo(new Vector3(1f, 2f, 3f)));
        Assert.That(rotations[0], Is.EqualTo(Quaternion.identity));
        Assert.That(positions[1], Is.EqualTo(new Vector3(7f, 8f, 9f)));
        Assert.That(rotations[1], Is.EqualTo(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f)));
    }

    [Test]
    public void CsvSerializersPreserveLockedEventAndCaptureSchemas()
    {
        Assert.That(
            RecordingCsvSerializer.EventHeader,
            Is.EqualTo("utcTime,sessionTime,frame,playerPosition,action,hand,hold,details"));
        string eventRow = RecordingCsvSerializer.BuildEventRow(
            new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc),
            4.5f,
            12,
            new Vector3(1.25f, -2.5f, 3f),
            "Grip,Start",
            "L",
            "A1",
            "say \"hi\"");
        Assert.That(eventRow, Is.EqualTo(
            "2026-07-28T01:02:03.0000000Z,4.500,12,\"(1.250,-2.500,3.000)\"," +
            "\"Grip,Start\",L,A1,\"say \"\"hi\"\"\""));
        Assert.That(ParseCsvRow(eventRow), Has.Count.EqualTo(8));

        string captureHeader = RecordingCsvSerializer.BuildCaptureHeader();
        Assert.That(captureHeader, Is.EqualTo(BuildLockedCaptureHeader()));
        Assert.That(captureHeader.Split(','), Has.Length.EqualTo(388));
    }

    [Test]
    public void CaptureSerializerPreservesEveryColumn()
    {
        CaptureFrame frame = CreateFrame("ROUTE, ONE");
        frame.hold = "A\"1";
        frame.leftHold = "A\"1";
        frame.rightHold = "B,2";
        frame.rightGripFlag = 1;
        frame.rightFingerMask = 17;
        frame.rightGripScore = 0.25f;
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            frame.leftPositions[i] = new Vector3(i + 0.1f, i + 0.2f, i + 0.3f);
            frame.leftRotations[i] = new Quaternion(i + 0.4f, i + 0.5f, i + 0.6f, i + 0.7f);
            frame.rightPositions[i] = new Vector3(-i - 0.1f, -i - 0.2f, -i - 0.3f);
            frame.rightRotations[i] = new Quaternion(-i - 0.4f, -i - 0.5f, -i - 0.6f, -i - 0.7f);
        }

        StringBuilder serialized = new();
        RecordingCsvSerializer.AppendCaptureRow(serialized, frame);
        string row = serialized.ToString().TrimEnd('\r', '\n');

        List<string> expected = new()
        {
            "2026-07-28T01:02:03.0000000Z",
            "1.25000",
            "42",
            "2.50000",
            "Grip",
            "ROUTE, ONE",
            "A\"1",
        };
        AddVectorColumns(expected, frame.headPosition);
        AddQuaternionColumns(expected, frame.headRotation);
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            AddVectorColumns(expected, frame.leftPositions[i]);
            AddQuaternionColumns(expected, frame.leftRotations[i]);
        }
        expected.Add("1");
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            AddVectorColumns(expected, frame.rightPositions[i]);
            AddQuaternionColumns(expected, frame.rightRotations[i]);
        }
        expected.Add("1");
        expected.Add("A\"1");
        expected.Add("1");
        expected.Add("3");
        expected.Add("0.75000");
        expected.Add("B,2");
        expected.Add("1");
        expected.Add("17");
        expected.Add("0.25000");

        Assert.That(ParseCsvRow(row), Is.EqualTo(expected));
        Assert.That(expected, Has.Count.EqualTo(388));
    }

    [Test]
    public void CaptureWriterProducesCompatibleEscapedRow()
    {
        MemoryStream output = new();
        CaptureWriter writer = CaptureWriter.Start(
            output,
            RecordingBlockSession.QueueCapacity,
            TimeSpan.FromHours(1));
        CaptureFrame frame = CreateFrame("ROUTE, ONE");
        frame.hold = "A\"1";
        frame.leftHold = frame.hold;

        writer.Enqueue(frame);
        writer.StopAndFinalize(1000);

        string[] lines = ReadGzip(output.ToArray())
            .Replace("\r\n", "\n")
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Is.EqualTo(BuildLockedCaptureHeader()));
        List<string> columns = ParseCsvRow(lines[1]);
        Assert.That(columns, Has.Count.EqualTo(388));
        Assert.That(columns[5], Is.EqualTo("ROUTE, ONE"));
        Assert.That(columns[6], Is.EqualTo("A\"1"));
        Assert.That(columns[1], Is.EqualTo("1.25000"));
        Assert.That(columns[3], Is.EqualTo("2.50000"));
        Assert.That(columns[380], Is.EqualTo("A\"1"));
        Assert.That(columns[383], Is.EqualTo("0.75000"));
        Assert.That(columns[387], Is.EqualTo("-1.00000"));
    }

    [Test]
    public void RecordingSessionWritesLockedBlockFileContract()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "contract");
        Directory.CreateDirectory(blockDirectory); // StudyManager creates the block directory first.
        RecordingBlockSession session = RecordingBlockSession.Begin(blockDirectory, true, 5d);
        string eventRow = RecordingCsvSerializer.BuildEventRow(
            new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc),
            1.25f,
            42,
            new Vector3(1f, 2f, 3f),
            "HoverEnter",
            "Left",
            "A1",
            string.Empty);
        session.WriteEvent(eventRow, "HoverEnter", "A1");
        Assert.That(session.TryScheduleCapture(5.04d), Is.True);
        CaptureFrame frame = CreateFrame("ROUTE");
        frame.blockTime = session.GetBlockTime(5.04d);
        session.EnqueueCapture(frame, "A1", 0.75f, null, -1f, false);

        session.End(1000, 5.04d);

        string[] files = Directory.GetFiles(blockDirectory);
        Array.Sort(files, StringComparer.Ordinal);
        Assert.That(files, Has.Length.EqualTo(2));
        Assert.That(Path.GetFileName(files[0]), Is.EqualTo("capture.csv.gz"));
        Assert.That(Path.GetFileName(files[1]), Is.EqualTo("events.csv"));
        byte[] eventBytes = File.ReadAllBytes(Path.Combine(blockDirectory, "events.csv"));
        Assert.That(
            eventBytes.Length >= 3 &&
            eventBytes[0] == 0xEF && eventBytes[1] == 0xBB && eventBytes[2] == 0xBF,
            Is.False,
            "events.csv must remain UTF-8 without a BOM.");
        string[] eventLines = Encoding.UTF8.GetString(eventBytes)
            .Replace("\r\n", "\n")
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(eventLines, Is.EqualTo(new[] { RecordingCsvSerializer.EventHeader, eventRow }));

        string[] captureLines = ReadGzip(File.ReadAllBytes(
                Path.Combine(blockDirectory, "capture.csv.gz")))
            .Replace("\r\n", "\n")
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(captureLines, Has.Length.EqualTo(2));
        Assert.That(captureLines[0], Is.EqualTo(BuildLockedCaptureHeader()));
        Assert.That(ParseCsvRow(captureLines[1]), Has.Count.EqualTo(388));
        Assert.That(session.DroppedCaptureFrames, Is.Zero);
    }

    [Test]
    public void ResumeSegmentPreservesTheInitialRecordingFiles()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "resumed-contract");
        RecordingBlockSession initial = RecordingBlockSession.Begin(blockDirectory, true, 1d);
        initial.End(1000, 1d);

        RecordingBlockSession resumed = RecordingBlockSession.Begin(
            blockDirectory,
            true,
            2d,
            "_resume1",
            120d);
        Assert.That(resumed.GetBlockTime(2.5d), Is.EqualTo(120.5f));
        resumed.End(1000, 2.5d);

        string[] files = Directory.GetFiles(blockDirectory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.That(files, Is.EqualTo(new[]
        {
            "capture.csv.gz",
            "capture_resume1.csv.gz",
            "events.csv",
            "events_resume1.csv",
        }));
    }

    [Test]
    public void FirstCaptureBecomesDurableWithoutWaitingForPeriodicFlush()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "first-capture-flush");
        RecordingBlockSession session = RecordingBlockSession.Begin(blockDirectory, true, 10d);
        try
        {
            Assert.That(session.TryScheduleCapture(10.04d), Is.True);
            CaptureFrame frame = CreateFrame("ROUTE");
            frame.blockTime = session.GetBlockTime(10.04d);
            session.EnqueueCapture(frame, null, -1f, null, -1f, false);

            Assert.That(
                SpinWait.SpinUntil(() => session.HasDurableCapture, 1000),
                Is.True,
                "The first capture was not flushed within one second.");
            byte[] captureBytes;
            using (FileStream stream = new(
                       Path.Combine(blockDirectory, "capture.csv.gz"),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite))
            using (MemoryStream copy = new())
            {
                stream.CopyTo(copy);
                captureBytes = copy.ToArray();
            }
            string[] captureLines = ReadGzip(captureBytes)
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(captureLines, Has.Length.EqualTo(2));
        }
        finally
        {
            session.End(1000, 10.04d);
        }
    }

    [Test]
    public void ManifestReplacementLeavesOneCompleteCanonicalFile()
    {
        string directory = Path.Combine(temporaryDirectory, "manifest-storage");
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "session.json");

        StudyManifestStorage.WriteAtomically(manifestPath, "{\"version\":1}", false);
        StudyManifestStorage.WriteAtomically(manifestPath, "{\"version\":2}", true);
        StudyManifestStorage.DeleteRecoveryFiles(manifestPath);

        Assert.That(File.ReadAllText(manifestPath), Is.EqualTo("{\"version\":2}"));
        Assert.That(StudyManifestStorage.GetRecoveryPaths(manifestPath), Is.EqualTo(new[] { manifestPath }));
    }

    [Test]
    public void ForcedEventFlushMakesLifecycleRowsImmediatelyDurable()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "lifecycle-flush");
        RecordingBlockSession session = RecordingBlockSession.Begin(blockDirectory, true, 5d);
        const string eventRow = "lifecycle event";
        try
        {
            session.WriteEvent(eventRow, "ApplicationPause", string.Empty);
            session.FlushEvents(5.1d);

            string eventText;
            using (FileStream stream = new(
                       Path.Combine(blockDirectory, "events.csv"),
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite))
            using (StreamReader reader = new(stream, Encoding.UTF8))
            {
                eventText = reader.ReadToEnd();
            }
            string[] eventLines = eventText
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.That(eventLines, Is.EqualTo(new[] { RecordingCsvSerializer.EventHeader, eventRow }));
        }
        finally
        {
            session.End(1000, 5.1d);
        }
    }

    [Test]
    public void HitchCountsSkippedIntervalsWithoutBackfillingHoldDuration()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "block");
        RecordingBlockSession session = RecordingBlockSession.Begin(blockDirectory, false, 10d);

        Assert.That(session.TryScheduleCapture(10.11d), Is.True);
        Assert.That(session.DroppedCaptureFrames, Is.EqualTo(2));
        session.EnqueueCapture(
            CreateFrame("ROUTE"),
            "A1",
            0.4f,
            null,
            -1f,
            false);
        session.WriteEvent("event", "GripLatched", "A1");
        session.End(1000, 10.11d);

        HoldAggregateData[] aggregates = session.GetHoldAggregates();
        Assert.That(aggregates, Has.Length.EqualTo(1));
        Assert.That(aggregates[0].hold, Is.EqualTo("A1"));
        Assert.That(
            aggregates[0].secondsTouched,
            Is.EqualTo((float)RecordingBlockSession.CaptureIntervalSeconds).Within(0.000001f));
        Assert.That(aggregates[0].gripsDetected, Is.EqualTo(1));
        Assert.That(aggregates[0].meanScore, Is.EqualTo(0.4f).Within(0.000001f));
        Assert.That(aggregates[0].scoreSamples, Is.EqualTo(1));
    }

    [Test]
    public void GripLatchedAndLegacyGripStartCountAsOneGrip()
    {
        RecordingBlockSession session = RecordingBlockSession.Begin(
            Path.Combine(temporaryDirectory, "grip-events"),
            false,
            0d);

        session.WriteEvent("latched event", "GripLatched", "A1");
        session.WriteEvent("legacy event", "GripStart", "A1");
        session.End(1000, 0d);

        HoldAggregateData[] aggregates = session.GetHoldAggregates();
        Assert.That(aggregates, Has.Length.EqualTo(1));
        Assert.That(aggregates[0].hold, Is.EqualTo("A1"));
        Assert.That(aggregates[0].gripsDetected, Is.EqualTo(1));
    }

    [Test]
    public void MissingAndTrailingCaptureOpportunitiesAreCounted()
    {
        RecordingBlockSession session = RecordingBlockSession.Begin(
            Path.Combine(temporaryDirectory, "missing-captures"),
            false,
            10d);

        Assert.That(session.TryScheduleCapture(10.11d), Is.True);
        Assert.That(session.DroppedCaptureFrames, Is.EqualTo(2));
        session.DropScheduledCapture();
        Assert.That(session.DroppedCaptureFrames, Is.EqualTo(3));

        session.End(1000, 10.18d);

        Assert.That(session.DroppedCaptureFrames, Is.EqualTo(5));
    }

    [Test]
    public void EndIsIdempotentAndRejectsPostEndMutation()
    {
        RecordingBlockSession session = RecordingBlockSession.Begin(
            Path.Combine(temporaryDirectory, "ended-session"),
            false,
            0d);
        Assert.That(session.TryScheduleCapture(0.04d), Is.True);
        session.EnqueueCapture(CreateFrame("ROUTE"), "A1", 0.5f, null, -1f, false);

        session.End(1000, 0.04d);
        Assert.That(session.IsFinalized, Is.True);
        Assert.DoesNotThrow(() => session.End(1000, 0.04d));
        Assert.That(
            () => session.WriteEvent("event", "GripStart", "A1"),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(
            () => session.TryScheduleCapture(0.08d),
            Throws.TypeOf<InvalidOperationException>());

        HoldAggregateData aggregate = session.GetHoldAggregates()[0];
        Assert.That(aggregate.gripsDetected, Is.Zero);
        Assert.That(
            aggregate.secondsTouched,
            Is.EqualTo((float)RecordingBlockSession.CaptureIntervalSeconds).Within(0.000001f));
    }

    [Test]
    public void BeginRollsBackEventFileWhenCaptureCannotOpen()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "blocked-capture");
        Directory.CreateDirectory(blockDirectory);
        string capturePath = Path.Combine(blockDirectory, "capture.csv.gz");
        File.WriteAllText(capturePath, "existing capture");

        Assert.That(
            () => RecordingBlockSession.Begin(blockDirectory, true, 0d),
            Throws.TypeOf<IOException>());
        Assert.That(File.Exists(Path.Combine(blockDirectory, "events.csv")), Is.False);
        Assert.That(File.ReadAllText(capturePath), Is.EqualTo("existing capture"));
        Assert.That(Directory.GetFiles(blockDirectory), Is.EqualTo(new[] { capturePath }));
    }

    [Test]
    public void BeginPreservesExistingEventFile()
    {
        string blockDirectory = Path.Combine(temporaryDirectory, "blocked-events");
        Directory.CreateDirectory(blockDirectory);
        string eventPath = Path.Combine(blockDirectory, "events.csv");
        File.WriteAllText(eventPath, "existing events");

        Assert.That(
            () => RecordingBlockSession.Begin(blockDirectory, true, 0d),
            Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllText(eventPath), Is.EqualTo("existing events"));
        Assert.That(Directory.GetFiles(blockDirectory), Is.EqualTo(new[] { eventPath }));
    }

    [Test]
    public void EndReportsWriterExceptionWithBackgroundStackTrace()
    {
        CaptureWriter writer = CaptureWriter.Start(
            new ThrowingWriteStream(),
            2,
            TimeSpan.FromHours(1));
        RecordingBlockSession session = new("memory", 0d, null, writer);
        writer.Enqueue(CreateFrame("FAULT"));

        IOException exception = Assert.Throws<IOException>(() => session.End(1000, 0d));
        Assert.That(exception.Message, Does.Contain("forced capture write failure"));
        Assert.That(exception.StackTrace, Does.Contain(nameof(ThrowingWriteStream.Write)));
        Assert.That(() => session.ThrowIfFaulted(), Throws.TypeOf<IOException>());
    }

    [Test]
    public void TimedOutSessionCanFinalizeAfterWriterUnblocks()
    {
        BlockingWriteStream output = new();
        CaptureWriter writer = CaptureWriter.Start(
            output,
            2,
            TimeSpan.FromHours(1));
        RecordingBlockSession session = new("memory", 0d, null, writer);
        writer.Enqueue(CreateFrame("BLOCKED"));

        try
        {
            Assert.That(
                () => session.End(50, 0d),
                Throws.TypeOf<TimeoutException>());
            Assert.That(session.IsFinalized, Is.False);
            Assert.That(output.WaitUntilWriteStarts(1000), Is.True);

            output.ReleaseWrite();
            Assert.DoesNotThrow(() => session.End(2000, 0d));
            Assert.That(session.IsFinalized, Is.True);
            Assert.DoesNotThrow(session.ThrowIfFaulted);
        }
        finally
        {
            output.ReleaseWrite();
            if (writer.IsAlive)
            {
                writer.StopAndFinalize(2000);
            }
        }
    }

    [Test]
    public void TimedOutSessionReportsKnownWriterFailureBeforeAndAfterCleanup()
    {
        ThrowingWriteBlockingDisposeStream output = new();
        CaptureWriter writer = CaptureWriter.Start(
            output,
            2,
            TimeSpan.FromMilliseconds(1));
        RecordingBlockSession session = new("memory", 0d, null, writer);
        writer.Enqueue(CreateFrame("FAULT"));

        try
        {
            Assert.That(output.WaitUntilDisposeStarts(1000), Is.True);
            AggregateException attemptFailure = Assert.Throws<AggregateException>(
                () => session.End(50, 0d));
            Assert.That(
                attemptFailure.Flatten().InnerExceptions,
                Has.Some.TypeOf<TimeoutException>());
            Assert.That(
                attemptFailure.Flatten().InnerExceptions,
                Has.Some.TypeOf<IOException>());

            output.ReleaseDispose();
            IOException exception = Assert.Throws<IOException>(
                () => session.End(2000, 0d));
            Assert.That(exception.StackTrace, Does.Contain(
                nameof(ThrowingWriteBlockingDisposeStream.Write)));
            Assert.That(session.IsFinalized, Is.True);
            Assert.That(() => session.ThrowIfFaulted(), Throws.TypeOf<IOException>());
        }
        finally
        {
            output.ReleaseDispose();
            if (writer.IsAlive)
            {
                Assert.That(
                    () => writer.StopAndFinalize(2000),
                    Throws.TypeOf<IOException>());
            }
        }
    }

    [Test]
    public void TimeoutDoesNotContaminateDurableFinalizationFailure()
    {
        BlockingWriteStream captureOutput = new();
        CaptureWriter writer = CaptureWriter.Start(
            captureOutput,
            2,
            TimeSpan.FromHours(1));
        StreamWriter eventWriter = new(
            new ThrowingFlushStream(),
            new UTF8Encoding(false));
        RecordingBlockSession session = new("memory", 0d, eventWriter, writer);
        writer.Enqueue(CreateFrame("BLOCKED"));

        try
        {
            AggregateException attemptFailure = Assert.Throws<AggregateException>(
                () => session.End(50, 0d));
            Assert.That(
                attemptFailure.Flatten().InnerExceptions,
                Has.Some.TypeOf<TimeoutException>());
            Assert.That(captureOutput.WaitUntilWriteStarts(1000), Is.True);

            captureOutput.ReleaseWrite();
            Exception durableFailure = Assert.Catch(
                () => session.End(2000, 0d));
            IReadOnlyList<Exception> durableErrors = Flatten(durableFailure);
            Assert.That(durableErrors, Has.None.TypeOf<TimeoutException>());
            Assert.That(durableErrors, Has.Some.TypeOf<IOException>());
            Assert.That(session.IsFinalized, Is.True);

            Exception storedFailure = Assert.Catch(session.ThrowIfFaulted);
            Assert.That(Flatten(storedFailure), Has.None.TypeOf<TimeoutException>());
        }
        finally
        {
            captureOutput.ReleaseWrite();
            if (writer.IsAlive)
            {
                writer.StopAndFinalize(2000);
            }
        }
    }

    [Test]
    public void AggregateWriterFailureRetainsProducerThreadStack()
    {
        CaptureWriter writer = CaptureWriter.Start(
            new ThrowingWriteAndDisposeStream(),
            2,
            TimeSpan.FromHours(1));
        writer.Enqueue(CreateFrame("FAULT"));

        AggregateException exception = Assert.Throws<AggregateException>(
            () => writer.StopAndFinalize(2000));

        Assert.That(exception.StackTrace, Does.Contain("CaptureWriter.WriterLoop"));
        Assert.That(exception.Flatten().InnerExceptions, Has.Count.EqualTo(2));
        foreach (Exception innerException in exception.Flatten().InnerExceptions)
        {
            Assert.That(innerException.StackTrace, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public void WriterFaultRejectsEnqueueBeforeBlockedDisposeReturns()
    {
        ThrowingWriteBlockingDisposeStream output = new();
        CaptureWriter writer = CaptureWriter.Start(
            output,
            2,
            TimeSpan.FromMilliseconds(1));
        writer.Enqueue(CreateFrame("FAULT"));

        try
        {
            Assert.That(output.WaitUntilDisposeStarts(1000), Is.True);
            IOException exception = Assert.Throws<IOException>(
                () => writer.Enqueue(CreateFrame("MUST_NOT_BE_ACCEPTED")));
            Assert.That(exception.StackTrace, Does.Contain(
                nameof(ThrowingWriteBlockingDisposeStream.Write)));
        }
        finally
        {
            output.ReleaseDispose();
        }

        Assert.That(
            () => writer.StopAndFinalize(2000),
            Throws.TypeOf<IOException>());
    }

    [Test]
    public void WriterFlushesAtHardChunkLimitIndependentlyOfTime()
    {
        BlockingWriteStream output = new();
        CaptureWriter writer = CaptureWriter.Start(
            output,
            2,
            TimeSpan.FromHours(1));
        CaptureFrame frame = CreateFrame(new string('R', CaptureWriter.MaxChunkCharacters));

        try
        {
            writer.Enqueue(frame);
            Assert.That(output.WaitUntilWriteStarts(2000), Is.True);
        }
        finally
        {
            output.ReleaseWrite();
        }

        writer.StopAndFinalize(2000);
        Assert.That(ReadGzip(output.ToArray()), Does.Contain(frame.route));
    }

    [Test]
    public void CaptureQueueDropsExactlyOneOldestFramePerOverflow()
    {
        CaptureFrameQueue queue = new(2);
        Assert.That(queue.Enqueue(CreateFrame("ONE")), Is.False);
        Assert.That(queue.Enqueue(CreateFrame("TWO")), Is.False);
        Assert.That(queue.Enqueue(CreateFrame("THREE")), Is.True);
        Assert.That(queue.Enqueue(CreateFrame("FOUR")), Is.True);
        queue.CompleteAdding();

        CaptureFrame frame = new();
        Assert.That(queue.Take(frame, 0), Is.EqualTo(CaptureQueueReadResult.Item));
        Assert.That(frame.route, Is.EqualTo("THREE"));
        Assert.That(queue.Take(frame, 0), Is.EqualTo(CaptureQueueReadResult.Item));
        Assert.That(frame.route, Is.EqualTo("FOUR"));
        Assert.That(queue.Take(frame, 0), Is.EqualTo(CaptureQueueReadResult.Completed));
    }

    [Test]
    public void ActionRecorderPreventsNewBlockAfterWriterFailure()
    {
        CaptureWriter writer = CaptureWriter.Start(
            new ThrowingWriteStream(),
            2,
            TimeSpan.FromHours(1));
        RecordingBlockSession session = new("memory", 0d, null, writer);
        writer.Enqueue(CreateFrame("FAULT"));
        Type facadeType = GetActionRecorderType();
        GameObject gameObject = new("ActionRecorder failure test");
        Component recorder = gameObject.AddComponent(facadeType);
        FieldInfo sessionField = facadeType.GetField(
            "recordingSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        sessionField.SetValue(recorder, session);
        facadeType.GetField(
                "<IsRecording>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(recorder, true);

        try
        {
            LogAssert.Expect(LogType.Exception, new Regex("forced capture write failure"));
            TargetInvocationException endException = Assert.Throws<TargetInvocationException>(
                () => facadeType.GetMethod("EndBlock").Invoke(recorder, null));
            Assert.That(endException.InnerException, Is.TypeOf<IOException>());
            Assert.That(sessionField.GetValue(recorder), Is.Null);

            TargetInvocationException repeatedEndException =
                Assert.Throws<TargetInvocationException>(
                    () => facadeType.GetMethod("EndBlock").Invoke(recorder, null));
            Assert.That(repeatedEndException.InnerException, Is.TypeOf<IOException>());

            string nextDirectory = Path.Combine(temporaryDirectory, "must-not-start");
            TargetInvocationException beginException = Assert.Throws<TargetInvocationException>(
                () => facadeType.GetMethod(
                        "BeginBlock",
                        new[] { typeof(string), typeof(StudySessionManifest) })
                    .Invoke(
                    recorder,
                    new object[] { nextDirectory, null }));
            Assert.That(beginException.InnerException, Is.TypeOf<IOException>());
            Assert.That(Directory.Exists(nextDirectory), Is.False);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
            if (writer.IsAlive)
            {
                Assert.That(
                    () => writer.StopAndFinalize(2000),
                    Throws.TypeOf<IOException>());
            }
        }
    }

    [Test]
    public void TimedOutWriterRemainsIsolatedFromLaterWriter()
    {
        BlockingWriteStream oldOutput = new();
        CaptureWriter oldWriter = CaptureWriter.Start(
            oldOutput,
            2,
            TimeSpan.FromHours(1));
        oldWriter.Enqueue(CreateFrame("OLD_BLOCK"));

        try
        {
            Assert.That(
                () => oldWriter.StopAndFinalize(50),
                Throws.TypeOf<TimeoutException>());
            Assert.That(oldOutput.WaitUntilWriteStarts(1000), Is.True);

            MemoryStream laterOutput = new();
            CaptureWriter laterWriter = CaptureWriter.Start(
                laterOutput,
                2,
                TimeSpan.FromHours(1));
            laterWriter.Enqueue(CreateFrame("LATER_BLOCK"));
            laterWriter.StopAndFinalize(1000);

            string laterCapture = ReadGzip(laterOutput.ToArray());
            Assert.That(laterCapture, Does.Contain("LATER_BLOCK"));
            Assert.That(laterCapture, Does.Not.Contain("OLD_BLOCK"));
        }
        finally
        {
            oldOutput.ReleaseWrite();
            oldWriter.StopAndFinalize(2000);
        }

        string oldCapture = ReadGzip(oldOutput.ToArray());
        Assert.That(oldCapture, Does.Contain("OLD_BLOCK"));
        Assert.That(oldCapture, Does.Not.Contain("LATER_BLOCK"));
    }

    private static CaptureFrame CreateFrame(string route)
    {
        return new CaptureFrame
        {
            utcTicks = new DateTime(2026, 7, 28, 1, 2, 3, DateTimeKind.Utc).Ticks,
            sessionTime = 1.25f,
            frame = 42,
            blockTime = 2.5f,
            mode = "Grip",
            route = route,
            hold = "A1",
            headPosition = new Vector3(1f, 2f, 3f),
            headRotation = Quaternion.identity,
            leftConfidence = 1,
            rightConfidence = 1,
            leftHold = "A1",
            leftGripFlag = 1,
            leftFingerMask = 3,
            leftGripScore = 0.75f,
        };
    }

    private static Type GetActionRecorderType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("ActionRecorder", false);
            if (type != null)
            {
                return type;
            }
        }
        Assert.Fail("ActionRecorder type was not loaded.");
        return null;
    }

    private static string BuildLockedCaptureHeader()
    {
        List<string> columns = new()
        {
            "utc", "sessionTime", "frame", "blockTime", "mode", "route", "hold",
            "headPosX", "headPosY", "headPosZ", "headRotX", "headRotY", "headRotZ", "headRotW",
        };
        foreach (char hand in "LR")
        {
            for (int bone = 0; bone < 26; bone++)
            {
                columns.Add($"{hand}{bone}PosX");
                columns.Add($"{hand}{bone}PosY");
                columns.Add($"{hand}{bone}PosZ");
                columns.Add($"{hand}{bone}RotX");
                columns.Add($"{hand}{bone}RotY");
                columns.Add($"{hand}{bone}RotZ");
                columns.Add($"{hand}{bone}RotW");
            }
            columns.Add($"{hand}Conf");
        }
        foreach (char hand in "LR")
        {
            columns.Add($"{hand}Hold");
            columns.Add($"{hand}GripFlag");
            columns.Add($"{hand}FingerMask");
            columns.Add($"{hand}GripScore");
        }
        return string.Join(",", columns);
    }

    private static void AddVectorColumns(List<string> columns, Vector3 value)
    {
        columns.Add(value.x.ToString("F5", CultureInfo.InvariantCulture));
        columns.Add(value.y.ToString("F5", CultureInfo.InvariantCulture));
        columns.Add(value.z.ToString("F5", CultureInfo.InvariantCulture));
    }

    private static void AddQuaternionColumns(List<string> columns, Quaternion value)
    {
        columns.Add(value.x.ToString("F5", CultureInfo.InvariantCulture));
        columns.Add(value.y.ToString("F5", CultureInfo.InvariantCulture));
        columns.Add(value.z.ToString("F5", CultureInfo.InvariantCulture));
        columns.Add(value.w.ToString("F5", CultureInfo.InvariantCulture));
    }

    private static List<string> ParseCsvRow(string row)
    {
        List<string> values = new();
        StringBuilder value = new();
        bool quoted = false;
        for (int i = 0; i < row.Length; i++)
        {
            char character = row[i];
            if (character == '"')
            {
                if (quoted && i + 1 < row.Length && row[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }
        values.Add(value.ToString());
        return values;
    }

    private static IReadOnlyList<Exception> Flatten(Exception exception)
    {
        return exception is AggregateException aggregateException
            ? aggregateException.Flatten().InnerExceptions
            : new[] { exception };
    }

    private static string ReadGzip(byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new IOException("forced capture write failure");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly MemoryStream output = new();
        private readonly ManualResetEventSlim writeStarted = new(false);
        private readonly ManualResetEventSlim releaseWrite = new(false);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position
        {
            get => output.Position;
            set => throw new NotSupportedException();
        }

        public bool WaitUntilWriteStarts(int timeoutMilliseconds)
        {
            return writeStarted.Wait(timeoutMilliseconds);
        }

        public void ReleaseWrite()
        {
            releaseWrite.Set();
        }

        public byte[] ToArray()
        {
            return output.ToArray();
        }

        public override void Flush()
        {
            output.Flush();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            writeStarted.Set();
            releaseWrite.Wait();
            output.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingWriteBlockingDisposeStream : Stream
    {
        private readonly ManualResetEventSlim disposeStarted = new(false);
        private readonly ManualResetEventSlim releaseDispose = new(false);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public bool WaitUntilDisposeStarts(int timeoutMilliseconds)
        {
            return disposeStarted.Wait(timeoutMilliseconds);
        }

        public void ReleaseDispose()
        {
            releaseDispose.Set();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new IOException("forced capture write failure before blocked dispose");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposeStarted.Set();
                releaseDispose.Wait();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingFlushStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new IOException("forced event flush failure");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingWriteAndDisposeStream : ThrowingWriteStream
    {
        protected override void Dispose(bool disposing)
        {
            throw new IOException("forced capture dispose failure");
        }
    }
}
