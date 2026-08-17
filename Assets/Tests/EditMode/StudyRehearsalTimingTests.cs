using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
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
    public void ConditionBClassifiesGripLocomotionAsItsFirstInteraction()
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
    public void ConditionCClassifiesDetachedHoldAsItsFirstInteraction()
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
    public void RuntimeModeButtonsMapOnlyToCanonicalGripAndGhostConditions()
    {
        Type stateType = FindLoadedType("StudySessionState");
        FieldInfo runtimeConditions = stateType.GetField(
            "RuntimeConditions",
            BindingFlags.Public | BindingFlags.Static);

        Assert.That(runtimeConditions, Is.Not.Null);
        Assert.That((string[])runtimeConditions.GetValue(null), Is.EqualTo(new[] { "B", "C" }));
    }

    [Test]
    public void EnterPlayModeKeepsDomainReloadEnabled()
    {
        Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False);
        Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo((EnterPlayModeOptions)0));
        string settings = File.ReadAllText("ProjectSettings/EditorSettings.asset");
        StringAssert.Contains("m_EnterPlayModeOptionsEnabled: 0", settings);
        StringAssert.Contains("m_EnterPlayModeOptions: 0", settings);
    }

    [Test]
    public void RuntimePanelContainsOnlyTheRequestedControls()
    {
        GameObject parentObject = new("Panel Test Parent");
        GameObject cameraObject = new("Panel Test Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        Type panelType = FindLoadedType("StudyControlPanel");
        Type stateType = FindLoadedType("StudySessionState");
        Type buttonType = FindLoadedType("StudyPanelButton");
        ConstructorInfo constructor = panelType.GetConstructors()
            .Single(candidate => candidate.GetParameters().Length == 6);
        object panel = constructor.Invoke(new object[]
        {
            parentObject.transform,
            camera,
            null,
            null,
            Activator.CreateInstance(stateType),
            new Func<float>(() => 0f),
        });
        try
        {
            panelType.GetMethod("BuildPanel", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(panel, null);
            Transform console = parentObject.transform.Find("Study Experimenter Console");
            Assert.That(console, Is.Not.Null);
            // Seven manual-rehearsal controls; the estimation battery is reached through the
            // route cycle, which lists every supplemental problem alongside the climbed routes.
            (string objectName, string label)[] controls =
            {
                ("Mode A", "MODE A"),
                ("Mode B", "MODE B"),
                ("Previous Route", "NO ROUTES"),
                ("Next Route", "NO ROUTES"),
                ("Start Run", "START"),
                ("Complete Run", "COMPLETE"),
                ("Reset", "RESET"),
            };
            foreach ((string objectName, string label) control in controls)
            {
                Assert.That(console.Find(control.objectName), Is.Not.Null, control.objectName);
                Assert.That(
                    GetPanelText(console, control.objectName + " Label"),
                    Is.EqualTo(control.label),
                    control.objectName);
            }
            Assert.That(console.Find("Previous Participant"), Is.Null);
            Assert.That(console.Find("Previous Block"), Is.Null);
            Assert.That(console.Find("Practice B"), Is.Null);
            Assert.That(console.Find("Align Board"), Is.Null);
            Assert.That(console.Find("Hide Panel"), Is.Null);
            Assert.That(console.Find("Estimate"), Is.Null,
                "The estimation battery is reached through the route cycle, not a dedicated control.");
            Assert.That(parentObject.transform.Find("Study Countdown Chip"), Is.Null,
                "Runs are completed manually; there is no countdown timer.");
            Assert.That(console.Find("Panel Grab Handle"), Is.Null,
                "The panel is dragged by its background, not by a pinch-drag control.");
            Assert.That(console.GetComponentsInChildren(buttonType, true), Has.Length.EqualTo(controls.Length),
                "The console exposes exactly the requested controls.");
            Transform background = console.Find("Console Background");
            Assert.That(background, Is.Not.Null);
            Assert.That(background.GetComponent<Collider>(), Is.Not.Null,
                "The console background is the grab surface.");
            Assert.That(background.GetComponent(buttonType), Is.Null,
                "Grabbing the background must not press a control.");
            Assert.That(GetPanelText(console, "Route Readout"), Is.EqualTo("NO ROUTES"));
            Assert.That(console.Find("Route Identity"), Is.Null,
                "The console cannot reveal the route's identifying record.");
            Assert.That(console.Find("Route Identity Readout"), Is.Null,
                "The console cannot reveal the route's identifying record.");
        }
        finally
        {
            panelType.GetMethod("DestroyMaterials", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(panel, null);
            UnityEngine.Object.DestroyImmediate(parentObject);
            UnityEngine.Object.DestroyImmediate(cameraObject);
        }
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
    public void PanelPressPrefersTheDirectlyTargetedButton()
    {
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(true, false, true, 0.05f, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.PressTargetButton));
    }

    [Test]
    public void PanelPressFallsBackToTheRecentlyHoveredButtonWithinTheGraceWindow()
    {
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(false, true, true, 0.2f, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.PressRecentButton));
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(false, false, true, 0.2f, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.PressRecentButton));
    }

    [Test]
    public void PanelSurfacePinchGrabsOnlyAfterTheHoverGraceExpires()
    {
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(false, true, true, 0.31f, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.GrabPanel));
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(false, true, false, float.MaxValue, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.GrabPanel));
        Assert.That(
            StudyRehearsalTiming.ResolvePanelPress(false, false, false, float.MaxValue, 0.3f),
            Is.EqualTo(StudyRehearsalTiming.PanelPressResolution.None));
    }

    [Test]
    public void PointerSmoothingDampsHarderWhileThePinchCloses()
    {
        Vector3 previous = Vector3.forward;
        Vector3 current = Quaternion.Euler(0f, 30f, 0f) * Vector3.forward;

        Vector3 relaxed = StudyRehearsalTiming.SmoothPointerDirection(
            previous, current, 1f / 72f, 0f, 0.04f, 0.25f);
        Vector3 pinched = StudyRehearsalTiming.SmoothPointerDirection(
            previous, current, 1f / 72f, 1f, 0.04f, 0.25f);

        float relaxedRemaining = Vector3.Angle(relaxed, current);
        float pinchedRemaining = Vector3.Angle(pinched, current);
        Assert.That(relaxed.magnitude, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(pinchedRemaining, Is.GreaterThan(relaxedRemaining));
        Assert.That(relaxedRemaining, Is.GreaterThan(0f));
    }

    [Test]
    public void PointerSmoothingAdoptsTheFirstValidDirectionImmediately()
    {
        Vector3 current = new(0f, 0f, 2f);
        Assert.That(
            StudyRehearsalTiming.SmoothPointerDirection(
                Vector3.zero, current, 1f / 72f, 0f, 0.04f, 0.25f),
            Is.EqualTo(Vector3.forward));
        Assert.Throws<ArgumentException>(() =>
            StudyRehearsalTiming.SmoothPointerDirection(
                Vector3.forward, Vector3.zero, 1f / 72f, 0f, 0.04f, 0.25f));
    }

    [Test]
    public void ElapsedDisplayContinuesPastFormerBlockLimit()
    {
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(0f), Is.EqualTo("00:00"));
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(1200f), Is.EqualTo("20:00"));
        Assert.That(StudyRehearsalTiming.FormatElapsedSeconds(3661.9f), Is.EqualTo("61:01"));
    }

    [Test]
    public void CountdownDisplayUsesCeilingAtSecondBoundaries()
    {
        Assert.That(StudyRehearsalTiming.FormatRemainingSeconds(300f), Is.EqualTo("05:00"));
        Assert.That(StudyRehearsalTiming.FormatRemainingSeconds(299.1f), Is.EqualTo("05:00"));
        Assert.That(StudyRehearsalTiming.FormatRemainingSeconds(0.1f), Is.EqualTo("00:01"));
        Assert.That(StudyRehearsalTiming.FormatRemainingSeconds(0f), Is.EqualTo("00:00"));
    }

    [Test]
    public void ElapsedClockUsesPersistedOffsetAndCurrentProcessMonotonicTime()
    {
        Assert.That(
            StudyRehearsalTiming.ResolveElapsedSeconds(
                20d,
                100d,
                105d),
            Is.EqualTo(25f));
        Assert.That(
            StudyRehearsalTiming.ResolveElapsedSeconds(
                0d,
                100d,
                125d),
            Is.EqualTo(25f));
    }

    [Test]
    public void ActiveManualRunRecoveryHasNoDeadlineAndKeepsElapsedTimeUncapped()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "20260811_120000_000_B_MB2016_21329",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            null);

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329", "MB2016_19215" },
            start.AddMinutes(40),
            out StudyRehearsalTiming.ActiveManualRunRecovery recovery,
            out string diagnostic,
            out _);

        Assert.That(found, Is.True, diagnostic);
        Assert.That(recovery.Manifest.condition, Is.EqualTo("B"));
        Assert.That(recovery.RehearsalStartUtc, Is.EqualTo(start));
        Assert.That(recovery.GetElapsedSeconds(start.AddMinutes(2)), Is.EqualTo(120f));
        Assert.That(recovery.GetElapsedSeconds(start.AddMinutes(40)), Is.EqualTo(2400f));
    }

    [Test]
    public void ActiveManualRunRecoveryStillAcceptsLegacyDeadlineManifests()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "legacy-deadline",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddMinutes(20),
            out StudyRehearsalTiming.ActiveManualRunRecovery recovery,
            out string diagnostic,
            out _);

        Assert.That(found, Is.True, diagnostic);
        Assert.That(
            recovery.GetElapsedSeconds(start.AddMinutes(20)),
            Is.EqualTo(1200f),
            "A persisted legacy deadline is ignored; only the manual COMPLETE ends a run.");
    }

    [Test]
    public void ActiveManualRunRecoveryPreservesPendingStartTransaction()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "pending-start",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5),
            true);

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddSeconds(1),
            out StudyRehearsalTiming.ActiveManualRunRecovery recovery,
            out string diagnostic,
            out _);

        Assert.That(found, Is.True, diagnostic);
        Assert.That(recovery.Manifest.pendingStart, Is.True);
    }

    [Test]
    public void ActiveManualRunRecoveryRejectsFutureStartTimestamp()
    {
        DateTimeOffset now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset start = now.AddMinutes(1);
        WriteActiveManualManifest(
            "future-start",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            now,
            out _,
            out string diagnostic,
            out _);

        Assert.That(found, Is.False);
        Assert.That(diagnostic, Does.Contain("future"));
    }

    [Test]
    public void RecoveryPromotesNewestCompletedTemporaryManifestToCanonicalPath()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        string directory = WriteActiveManualManifest(
            "completed-temporary",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));
        string canonicalPath = Path.Combine(directory, "session.json");
        string temporaryPath = canonicalPath + ".interrupted.tmp";
        string completedJson = File.ReadAllText(canonicalPath)
            .Replace("\"endUtc\": \"\"", "\"endUtc\": \"" + start.AddMinutes(5).ToString("o") + "\"")
            .Replace("\"endReason\": \"running\"", "\"endReason\": \"timer_expired\"");
        File.WriteAllText(temporaryPath, completedJson);
        File.SetLastWriteTimeUtc(canonicalPath, start.UtcDateTime);
        File.SetLastWriteTimeUtc(temporaryPath, start.AddSeconds(1).UtcDateTime);

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddMinutes(6),
            out _,
            out _,
            out _);

        Assert.That(found, Is.False);
        Assert.That(File.ReadAllText(canonicalPath), Does.Contain("timer_expired"));
        Assert.That(File.Exists(temporaryPath), Is.False);
    }

    [Test]
    public void RecoveryDoesNotPromoteInconsistentTerminalTemporaryManifest()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        string directory = WriteActiveManualManifest(
            "invalid-terminal-temporary",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));
        string canonicalPath = Path.Combine(directory, "session.json");
        string temporaryPath = canonicalPath + ".interrupted.tmp";
        string invalidJson = File.ReadAllText(canonicalPath)
            .Replace("\"endReason\": \"running\"", "\"endReason\": \"timer_expired\"");
        File.WriteAllText(temporaryPath, invalidJson);
        File.SetLastWriteTimeUtc(canonicalPath, start.UtcDateTime);
        File.SetLastWriteTimeUtc(temporaryPath, start.AddSeconds(1).UtcDateTime);

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddMinutes(1),
            out _,
            out string diagnostic,
            out _);

        Assert.That(found, Is.False);
        Assert.That(diagnostic, Does.Contain("terminal state"));
        Assert.That(File.ReadAllText(canonicalPath), Does.Contain("\"endReason\": \"running\""));
        Assert.That(File.Exists(temporaryPath), Is.True);
    }

    [Test]
    public void HoldAggregateMergePreservesWholeRunScoreWeights()
    {
        HoldAggregateData[] merged = StudyRehearsalTiming.MergeHoldAggregates(
            new[]
            {
                new HoldAggregateData
                {
                    hold = "A1",
                    secondsTouched = 1f,
                    gripsDetected = 1,
                    meanScore = 0.4f,
                    maxScore = 0.8f,
                    scoreSamples = 30,
                },
            },
            new[]
            {
                new HoldAggregateData
                {
                    hold = "A1",
                    secondsTouched = 0.5f,
                    gripsDetected = 2,
                    meanScore = 0.8f,
                    maxScore = 0.9f,
                    scoreSamples = 15,
                },
            });

        Assert.That(merged, Has.Length.EqualTo(1));
        Assert.That(merged[0].secondsTouched, Is.EqualTo(1.5f).Within(0.000001f));
        Assert.That(merged[0].gripsDetected, Is.EqualTo(3));
        Assert.That(merged[0].meanScore, Is.EqualTo(0.533333f).Within(0.000001f));
        Assert.That(merged[0].maxScore, Is.EqualTo(0.9f));
        Assert.That(merged[0].scoreSamples, Is.EqualTo(45));
    }

    [Test]
    public void StaleCatalogRecordingsAreSetAsideWhileUnknownRoutesStillBlock()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "wrong-hash",
            "wrong-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));
        WriteActiveManualManifest(
            "unknown-route",
            "approved-hash",
            "NOT_APPROVED",
            "C",
            start.AddSeconds(1),
            start.AddMinutes(5).AddSeconds(1));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddMinutes(2),
            out _,
            out string diagnostic,
            out string staleNotice);

        Assert.That(found, Is.False);
        Assert.That(staleNotice, Does.Contain("wrong-hash"));
        Assert.That(staleNotice, Does.Contain("catalog hash"));
        Assert.That(diagnostic, Does.Contain("unknown-route"));
        Assert.That(diagnostic, Does.Not.Contain("catalog hash"));
    }

    [Test]
    public void StaleCatalogRecordingsAloneDoNotBlockRecovery()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "stale-only",
            "retired-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329" },
            start.AddMinutes(2),
            out _,
            out string diagnostic,
            out string staleNotice);

        Assert.That(found, Is.False);
        Assert.That(diagnostic, Is.Empty);
        Assert.That(staleNotice, Does.Contain("stale-only"));
    }

    [Test]
    public void StaleCatalogRecordingsDoNotHideACurrentActiveRun()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "stale-neighbour",
            "retired-hash",
            "MB2016_19215",
            "C",
            start.AddMinutes(-30),
            start.AddMinutes(-25));
        WriteActiveManualManifest(
            "current-active",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329", "MB2016_19215" },
            start.AddMinutes(2),
            out StudyRehearsalTiming.ActiveManualRunRecovery recovery,
            out string diagnostic,
            out string staleNotice);

        Assert.That(found, Is.True, diagnostic);
        Assert.That(recovery.Manifest.route, Is.EqualTo("MB2016_21329"));
        Assert.That(staleNotice, Does.Contain("stale-neighbour"));
    }

    [Test]
    public void ActiveManualRunRecoveryBlocksMultipleRunningCandidates()
    {
        DateTimeOffset start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        WriteActiveManualManifest(
            "first-active",
            "approved-hash",
            "MB2016_21329",
            "B",
            start,
            start.AddMinutes(5));
        WriteActiveManualManifest(
            "second-active",
            "approved-hash",
            "MB2016_19215",
            "C",
            start.AddSeconds(1),
            start.AddMinutes(5).AddSeconds(1));

        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            recoveryStudyRoot,
            "approved-hash",
            new[] { "MB2016_21329", "MB2016_19215" },
            start.AddMinutes(1),
            out _,
            out string diagnostic,
            out _);

        Assert.That(found, Is.False);
        Assert.That(diagnostic, Does.Contain("Multiple active manual runs"));
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
    public void PanelViewportClampContainsFinalFacingPanelBounds()
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
                new(-0.41f, 0.51f, 0f),
                new(0.41f, 0.51f, 0f),
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

    private string WriteActiveManualManifest(
        string directoryName,
        string catalogSha256,
        string route,
        string condition,
        DateTimeOffset start,
        DateTimeOffset? deadline,
        bool pendingStart = false)
    {
        string directory = Path.Combine(recoveryStudyRoot, directoryName);
        Directory.CreateDirectory(directory);
        string json = "{\n" +
                      "  \"participant\": \"UNASSIGNED\",\n" +
                      "  \"block\": 0,\n" +
                      "  \"condition\": \"" + condition + "\",\n" +
                      "  \"route\": \"" + route + "\",\n" +
                      "  \"routeCatalogSha256\": \"" + catalogSha256 + "\",\n" +
                      "  \"retry\": 0,\n" +
                      "  \"adhoc\": true,\n" +
                      "  \"startUtc\": \"" + start.ToString("o") + "\",\n" +
                      "  \"rehearsalStartUtc\": \"" + start.ToString("o") + "\",\n" +
                       "  \"rehearsalDeadlineUtc\": \"" +
                       (deadline.HasValue ? deadline.Value.ToString("o") : string.Empty) + "\",\n" +
                       "  \"resumeCount\": 0,\n" +
                       "  \"pendingStart\": " + (pendingStart ? "true" : "false") + ",\n" +
                       "  \"pendingResumeIndex\": 0,\n" +
                       "  \"firstInteractionRecorded\": false,\n" +
                       "  \"recordingSummaryComplete\": true,\n" +
                      "  \"endUtc\": \"\",\n" +
                      "  \"endReason\": \"running\"\n" +
                      "}";
        File.WriteAllText(Path.Combine(directory, "session.json"), json);
        return directory;
    }

    private static Type FindLoadedType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name))
            .Single(type => type != null);
    }

    private static string GetPanelText(Transform console, string objectName)
    {
        Transform textObject = console.Find(objectName);
        Assert.That(textObject, Is.Not.Null, objectName);
        Component text = textObject.GetComponents<Component>()
            .Single(component => component.GetType().Name == "TextMeshPro");
        return (string)text.GetType().GetProperty("text")?.GetValue(text);
    }
}
