using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class StudyManager : MonoBehaviour
{
    private const float ReleaseBlockMinutes = 20f;
    private const float ReleasePracticePhaseSeconds = 60f;
    private const string NullRoutesHashSentinel = "__NULL_ROUTES_JSON_SHA256__";

    [Header("Study References")]
    [SerializeField] private SceneConfiguror sceneConfiguror;
    [SerializeField] private ActionRecorder actionRecorder;
    [SerializeField] private Camera userCamera;
    [SerializeField] private MoonBoard2016Layout boardLayout;
    [SerializeField] private BoardAlignmentController boardAlignment;

    [Header("Debug")]
    [SerializeField] private bool useMockSchedule;
    [SerializeField] private float debugBlockMinutes = 2f;
    [SerializeField] private float debugPracticePhaseSeconds = 10f;

    [Header("Panel Interaction")]
    [SerializeField] private float summonDwellSeconds = 1f;
    [SerializeField] private float summonCooldownSeconds = 2f;
    [SerializeField] private float panelSettleSeconds = 0.75f;

    private readonly List<StudyScheduleRow> schedule = new();
    private readonly List<string> participants = new();
    private GameObject panelRoot;
    private GameObject timerChipRoot;
    private TextMesh panelText;
    private TextMesh timerText;
    private Material panelMaterial;
    private Material buttonMaterial;
    private int participantIndex;
    private int selectedBlock = 1;
    private bool leftWasPinching;
    private bool rightWasPinching;
    private bool blockRunning;
    private bool panelPinned;
    private float blockStartRealtime;
    private float blockDurationSeconds;
    private StudyScheduleRow activeRow;
    private StudySessionManifest activeManifest;
    private string activeDirectory;
    private string manifestPath;
    private string statusMessage = "Select a participant and block.";
    private string retryConfirmationKey;
    private float retryConfirmationDeadline;
    private OVRHand leftHand;
    private OVRHand rightHand;
    private OVRSkeleton leftSkeleton;
    private OVRSkeleton rightSkeleton;
    private static readonly string[] AdhocConditions = { "A", "B", "C" };
    private int adhocConditionIndex;
    private int adhocRouteIndex;
    private TextMesh adhocConditionLabel;
    private TextMesh adhocRouteLabel;
    private string lastRoutesStatusLine;
    private float summonDwellStart = -1f;
    private bool summonReadyForPinch;
    private float summonCooldownUntil;
    private float panelPressableAt;
    private MoonBoardStudyCatalog routeCatalog;
    private string routeCatalogSha256 = string.Empty;
    private string lastBoardAlignmentStatus = string.Empty;
    private MoonBoardRouteDefinition activeRouteDefinition;
    private MoonBoardEstimationCatalog estimationCatalog;
    private string supplementalContentStatus = string.Empty;
    private bool estimationCatalogLoadAttempted;
    private StudyPanelButton practiceButton;
    private StudyPanelButton estimationStartButton;
    private StudyPanelButton estimationNextButton;
    private readonly HashSet<string> participantsWithBlockRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> participantsWithPracticeRuns = new(StringComparer.Ordinal);
    private bool practiceActive;
    private string practicePhase = string.Empty;
    private float practicePhaseStartRealtime;
    private bool estimationActive;
    private MoonBoardEstimationSetDefinition activeEstimationSet;
    private MoonBoardEstimationProblemDefinition[] activeEstimationProblems =
        Array.Empty<MoonBoardEstimationProblemDefinition>();
    private int activeEstimationOrdinal;
    private StudyScheduleRow lastEndedRow;
    private string lastEndedDirectory;
    private int lastEndedParticipantIndex;
    private readonly HashSet<string> startedEstimationBlocks = new(StringComparer.Ordinal);
    private bool blockTimerStarted;
    private float donningStartRealtime;
    private bool headsetPresenceInitialized;
    private bool headsetWasPresent;
    private float headsetPresentSinceRealtime = -1f;
    private bool blockHeadsetDonnedRecorded;
    private bool blockHeadsetWearActive;
    private float blockHeadsetWearSegmentStartRealtime;
    private float blockHeadsetWearSeconds;
    private bool headsetPresenceMismatchLogged;

    public bool IsBlockRunning => blockRunning;
    public bool IsPracticeActive => practiceActive;
    public bool IsEstimationActive => estimationActive;
    public bool IsRehearsalClockRunning => blockRunning && blockTimerStarted;
    public string ActiveDirectory => activeDirectory;
    public IReadOnlyList<StudyScheduleRow> Schedule => schedule;

    private bool IsAuxiliaryActive => practiceActive || estimationActive;

    private IEnumerator Start()
    {
        ResolveReferences();
        string catalogText = null;
        yield return LoadStreamingAssetText("moonboard_2016_40.json", text => catalogText = text);
        if (!LoadCatalogText(catalogText))
        {
            BuildPanel();
            ShowPanel();
            yield break;
        }

        string estimationText = null;
        yield return LoadStreamingAssetText(
            "moonboard_2016_40_estimation.json",
            text => estimationText = text,
            false);
        estimationCatalogLoadAttempted = true;
        LoadEstimationCatalogText(estimationText);

        string scheduleText = null;
        if (useMockSchedule)
        {
            scheduleText = BuildMockSchedule();
        }
        else
        {
            yield return LoadStreamingAssetText("study_schedule.csv", text => scheduleText = text);
        }

        LoadScheduleText(scheduleText);
        BuildPanel();
        ShowPanel();
        sceneConfiguror?.SetGameMode(GameMode.Basic);
    }

    public bool LoadScheduleText(string csv)
    {
        schedule.Clear();
        participants.Clear();
        ClearPendingEstimation();
        if (!StudySchedule.TryParse(csv, out List<StudyScheduleRow> parsed, out string error))
        {
            statusMessage = error;
            RefreshPanelText();
            return false;
        }
        if (!StudySchedule.TryValidateRoutes(parsed, routeCatalog, out error))
        {
            statusMessage = error;
            RefreshPanelText();
            return false;
        }

        schedule.AddRange(parsed);
        participants.AddRange(schedule.Select(row => row.participant).Distinct());
        participantIndex = Mathf.Clamp(participantIndex, 0, participants.Count - 1);
        statusMessage = "Schedule loaded.";
        TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
        return true;
    }

    public bool StartSelectedBlock()
    {
        EnsureScheduleLoadedForRuntime();
        if (participants.Count == 0)
        {
            statusMessage = "No valid schedule loaded.";
            RefreshPanelText();
            return false;
        }
        return StartBlock(participants[participantIndex], selectedBlock);
    }

    public bool StartBlock(string participant, int block)
    {
        EnsureScheduleLoadedForRuntime();
        if (IsAuxiliaryActive)
        {
            statusMessage = "End the practice or estimation sequence first.";
            RefreshPanelText();
            return false;
        }
        if (blockRunning)
        {
            statusMessage = "End the current block first.";
            RefreshPanelText();
            return false;
        }
        StudyScheduleRow row = schedule.FirstOrDefault(candidate =>
            candidate.participant == participant && candidate.block == block);
        if (row == null)
        {
            statusMessage = $"No schedule row for {participant} block {block}.";
            RefreshPanelText();
            return false;
        }
        if (!TryValidateRowRuntime(row))
        {
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.TryGetRouteDefinition(row.route, out MoonBoardRouteDefinition routeDefinition))
        {
            statusMessage = "Authoritative route record is unavailable: " + row.route + ".";
            RefreshPanelText();
            return false;
        }
        activeRouteDefinition = routeDefinition;

        string routeToken = SanitizePathToken(row.route);
        string baseName = $"block{row.block}_{row.condition}_{routeToken}";
        string participantRoot = Path.Combine(Application.persistentDataPath, "study", row.participant);
        string requestedDirectory = Path.Combine(participantRoot, baseName);
        int retry = 0;
        if (Directory.Exists(requestedDirectory))
        {
            string confirmationKey = row.participant + ":" + row.block;
            if (retryConfirmationKey != confirmationKey || Time.unscaledTime > retryConfirmationDeadline)
            {
                retryConfirmationKey = confirmationKey;
                retryConfirmationDeadline = Time.unscaledTime + 10f;
                statusMessage = "Block data exists. Press Start again to create a retry.";
                RefreshPanelText();
                return false;
            }

            retry = 1;
            while (Directory.Exists(requestedDirectory + "_retry" + retry))
            {
                retry++;
            }
            requestedDirectory += "_retry" + retry;
        }

        retryConfirmationKey = null;
        BeginValidatedRow(row, requestedDirectory, retry, false);
        return true;
    }

    /// <summary>
    /// Starts a one-off block outside the schedule for testing: the panel's condition and
    /// route cyclers pick any (condition, route) pair, including routes.json entries.
    /// Data lands in study/ADHOC/ (never a participant folder) and the manifest is marked
    /// adhoc so tools/check_session.py-audited study data stays unambiguous.
    /// </summary>
    public bool StartAdhocBlock()
    {
        if (IsAuxiliaryActive)
        {
            statusMessage = "End the practice or estimation sequence first.";
            RefreshPanelText();
            return false;
        }
        if (blockRunning)
        {
            statusMessage = "End the current block first.";
            RefreshPanelText();
            return false;
        }
        List<string> routes = sceneConfiguror != null
            ? sceneConfiguror.GetAvailableRouteNames()
            : new List<string>();
        if (routes.Count == 0)
        {
            statusMessage = "No routes are available.";
            RefreshPanelText();
            return false;
        }

        adhocRouteIndex = Mathf.Clamp(adhocRouteIndex, 0, routes.Count - 1);
        StudyScheduleRow row = new()
        {
            participant = "ADHOC",
            block = 0,
            condition = AdhocConditions[adhocConditionIndex],
            route = routes[adhocRouteIndex],
        };
        if (!TryValidateRowRuntime(row))
        {
            return false;
        }
        // Ad-hoc routes may come from routes.json with no catalog record; nulls are fine
        // because the manifest is marked adhoc and never lands in a participant folder.
        sceneConfiguror.TryGetRouteDefinition(row.route, out activeRouteDefinition);

        string directory = Path.Combine(
            Application.persistentDataPath,
            "study",
            "ADHOC",
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{row.condition}_{SanitizePathToken(row.route)}");
        BeginValidatedRow(row, directory, 0, true);
        return true;
    }

    private bool TryValidateRowRuntime(StudyScheduleRow row)
    {
        if (sceneConfiguror == null || actionRecorder == null)
        {
            statusMessage = "Study runtime references are unavailable.";
            RefreshPanelText();
            return false;
        }
        if (row.condition != "A" && !sceneConfiguror.IsGripFeedbackReady)
        {
            statusMessage = "Grip feedback is unavailable on this device.";
            RefreshPanelText();
            return false;
        }
        bool routeReady = row.condition == "A"
            ? sceneConfiguror.TrySelectBaselineRoute(row.route, out string routeError)
            : sceneConfiguror.TryValidateRoute(row.route, out routeError);
        if (!routeReady)
        {
            statusMessage = routeError;
            RefreshPanelText();
            Debug.LogError("[StudyManager] " + routeError);
            return false;
        }
        return true;
    }

    private void BeginValidatedRow(StudyScheduleRow row, string directory, int retry, bool adhoc)
    {
        Directory.CreateDirectory(directory);
        if (!adhoc)
        {
            participantsWithBlockRuns.Add(row.participant);
        }
        activeRow = row;
        activeDirectory = directory;
        manifestPath = Path.Combine(activeDirectory, "session.json");
        activeManifest = new StudySessionManifest
        {
            participant = row.participant,
            block = row.block,
            condition = row.condition,
            route = row.route,
            routeName = activeRouteDefinition != null ? activeRouteDefinition.name : row.route,
            routeSourceProblemId = activeRouteDefinition != null ? activeRouteDefinition.sourceProblemId : string.Empty,
            routeCatalogSha256 = routeCatalogSha256,
            boardSetup = routeCatalog != null ? routeCatalog.setupName : string.Empty,
            boardOverhangAngleDegrees = routeCatalog != null ? routeCatalog.overhangAngleDegrees : 0,
            routeDefinition = activeRouteDefinition,
            boardAlignment = boardAlignment != null ? boardAlignment.GetSnapshot() : null,
            boardAlignmentEnd = null,
            retry = retry,
            adhoc = adhoc,
            appVersion = Application.version,
            gitRevision = StudyBuildRevision.Current,
            startUtc = string.Empty,
            endUtc = string.Empty,
            endedEarly = false,
            endReason = "running",
            routesJsonSha256 = sceneConfiguror.IsBuiltInRoute(row.route)
                ? null
                : sceneConfiguror.RoutesJsonSha256,
            gripFeedback = sceneConfiguror.IsGripFeedbackDegraded
                ? "degraded_at_" + sceneConfiguror.GripFeedbackDegradedUtc
                : "ok",
        };

        activeManifest.startUtc = DateTime.UtcNow.ToString("o");
        actionRecorder.BeginBlock(activeDirectory, activeManifest);
        WriteManifest();

        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetUpRouteByName(row.route);
        sceneConfiguror.SetGameMode(row.condition switch
        {
            "B" => GameMode.Grip,
            "C" => GameMode.Ghost,
            _ => GameMode.Basic,
        });
        sceneConfiguror.SetStudyEnvironmentVisible(row.condition != "A");
        sceneConfiguror.SetStudyFeedbackVisible(row.condition != "A");
        GC.Collect();

        blockDurationSeconds = GetBlockMinutes() * 60f;
        blockTimerStarted = false;
        panelPinned = true;
        blockRunning = true;
        InitializeBlockHeadsetWear();
        if (row.condition == "A")
        {
            StartRehearsalClock("BaselineStart", false);
        }
        statusMessage = row.condition == "A"
            ? adhoc
                ? $"Running adhoc {row.condition} / {row.route}."
                : $"Running {row.participant} block {row.block}."
            : $"Waiting for {row.condition} first interaction; rehearsal clock is stopped.";
        if (row.condition == "A")
        {
            ShowPanel();
            SetTimerChipVisible(true);
        }
        else
        {
            SetPanelVisible(false);
        }
        RefreshPanelText();
    }

    public void EndBlockEarly()
    {
        EndBlock(true, "completed_early");
    }

    public void EndBlock(bool endedEarly, string reason)
    {
        if (!blockRunning)
        {
            return;
        }

        if (sceneConfiguror != null)
        {
            sceneConfiguror.SetGameMode(GameMode.Basic);
            sceneConfiguror.ResetMoonBoardTransform();
            sceneConfiguror.SetStudyEnvironmentVisible(true);
            sceneConfiguror.SetStudyFeedbackVisible(true);
        }
        FinalizeBlockHeadsetWear();
        actionRecorder.EndBlock();
        activeManifest.endUtc = DateTime.UtcNow.ToString("o");
        activeManifest.endedEarly = endedEarly;
        activeManifest.endReason = reason;
        activeManifest.droppedCaptureFrames = actionRecorder.DroppedCaptureFrames;
        activeManifest.holdAggregates = actionRecorder.GetHoldAggregates();
        activeManifest.boardAlignmentEnd = boardAlignment != null ? boardAlignment.GetSnapshot() : null;
        WriteManifest();

        blockRunning = false;
        blockTimerStarted = false;
        if (activeManifest != null && !activeManifest.adhoc && activeRow != null &&
            activeRow.block >= 1 && activeRow.block <= 3)
        {
            int endedParticipantIndex = participants.IndexOf(activeRow.participant);
            if (endedParticipantIndex < 0)
            {
                throw new InvalidOperationException(
                    "Ended participant is missing from the loaded schedule: " + activeRow.participant + ".");
            }
            lastEndedRow = activeRow;
            lastEndedDirectory = activeDirectory;
            lastEndedParticipantIndex = endedParticipantIndex;
        }
        else
        {
            ClearPendingEstimation();
        }
        statusMessage = $"Ended {activeRow.participant} block {activeRow.block}: {reason}.";
        SetTimerChipVisible(false);
        ShowPanel();
        RefreshPanelText();
    }

    public bool StartPractice()
    {
        EnsureScheduleLoadedForRuntime();
        EnsureEstimationCatalogLoadedForRuntime();
        if (blockRunning || IsAuxiliaryActive)
        {
            statusMessage = "End the current sequence before starting practice.";
            RefreshPanelText();
            return false;
        }
        if (estimationCatalog == null)
        {
            statusMessage = "Practice content is unavailable.";
            RefreshPanelText();
            return false;
        }
        if (participants.Count == 0 || sceneConfiguror == null || actionRecorder == null)
        {
            statusMessage = "Practice runtime references are unavailable.";
            RefreshPanelText();
            return false;
        }
        string participant = participants[participantIndex];
        if (!CanStartPractice(participant))
        {
            statusMessage = "Practice is available only once, before this participant's first block starts.";
            RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.IsGripFeedbackReady)
        {
            statusMessage = "Grip feedback is unavailable on this device.";
            RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.TryValidateRoute(estimationCatalog.practiceProblem.id, out string error))
        {
            statusMessage = error;
            Debug.LogError("[StudyManager] " + error);
            RefreshPanelText();
            return false;
        }

        string participantRoot = Path.Combine(Application.persistentDataPath, "study", participant);
        string directory = GetUnusedDirectory(Path.Combine(participantRoot, "practice_block0"));
        actionRecorder.BeginBlock(directory, null);
        participantsWithPracticeRuns.Add(participant);
        practiceActive = true;
        panelPinned = true;
        actionRecorder.Record("PracticeStarted");
        BeginPracticePhase("B");
        SetPanelVisible(false);
        SetTimerChipVisible(false);
        return true;
    }

    private void BeginPracticePhase(string phase)
    {
        practicePhase = phase;
        actionRecorder.Record("PracticePhase", phase);
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetUpRouteByName(estimationCatalog.practiceProblem.id);
        sceneConfiguror.SetGameMode(phase == "B" ? GameMode.Grip : GameMode.Ghost);
        sceneConfiguror.SetStudyFeedbackVisible(true);
        practicePhaseStartRealtime = Time.realtimeSinceStartup;
        statusMessage = "Practice phase " + phase + " running.";
        UpdatePracticeTimerText(GetPracticePhaseSeconds());
        RefreshPanelText();
    }

    private void UpdatePractice()
    {
        float remaining = GetPracticePhaseSeconds() -
                          (Time.realtimeSinceStartup - practicePhaseStartRealtime);
        if (remaining > 0f)
        {
            UpdatePracticeTimerText(remaining);
            return;
        }
        if (practicePhase == "B")
        {
            BeginPracticePhase("C");
            return;
        }
        EndPractice();
    }

    private void EndPractice()
    {
        if (!practiceActive)
        {
            return;
        }
        actionRecorder.Record("PracticeEnded");
        actionRecorder.EndBlock();
        practiceActive = false;
        practicePhase = string.Empty;
        RestoreScheduledDisplay(null);
        statusMessage = "Practice completed.";
        SetTimerChipVisible(false);
        ShowPanel();
        RefreshPanelText();
    }

    public bool StartEstimation()
    {
        EnsureScheduleLoadedForRuntime();
        EnsureEstimationCatalogLoadedForRuntime();
        if (blockRunning || practiceActive || estimationActive)
        {
            statusMessage = "End the current sequence before starting estimation.";
            RefreshPanelText();
            return false;
        }
        if (estimationCatalog == null)
        {
            statusMessage = "Estimation content is unavailable.";
            RefreshPanelText();
            return false;
        }
        TryRecoverSelectedCompletedBlock();
        if (lastEndedRow == null || string.IsNullOrEmpty(lastEndedDirectory))
        {
            statusMessage = "End a scheduled block before starting estimation.";
            RefreshPanelText();
            return false;
        }

        if (participants.Count == 0 ||
            !StudyRehearsalTiming.IsEstimationSelectionMatch(
                participants[participantIndex],
                selectedBlock,
                lastEndedRow.participant,
                lastEndedRow.block))
        {
            statusMessage = "Select the just-ended participant and block before starting estimation.";
            RefreshPanelText();
            return false;
        }

        string blockKey = GetEstimationBlockKey(lastEndedRow);
        if (HasStartedEstimation(lastEndedRow))
        {
            statusMessage = "This block's estimation set has already been started.";
            RefreshPanelText();
            return false;
        }
        string error = string.Empty;
        if (!estimationCatalog.TryGetSetForRoute(lastEndedRow.route, out MoonBoardEstimationSetDefinition set) ||
            !estimationCatalog.TryGetRotatedProblems(
                set,
                lastEndedParticipantIndex,
                out MoonBoardEstimationProblemDefinition[] problems,
                out error))
        {
            statusMessage = string.IsNullOrEmpty(error)
                ? "No estimation set is yoked to the just-ended route."
                : error;
            RefreshPanelText();
            return false;
        }
        if (sceneConfiguror == null || actionRecorder == null)
        {
            statusMessage = "Estimation runtime references are unavailable.";
            RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            RefreshPanelText();
            return false;
        }
        foreach (MoonBoardEstimationProblemDefinition problem in problems)
        {
            if (!sceneConfiguror.TryValidateRoute(problem.id, out error))
            {
                statusMessage = error;
                Debug.LogError("[StudyManager] " + error);
                RefreshPanelText();
                return false;
            }
        }

        string directory = GetUnusedDirectory(Path.Combine(lastEndedDirectory, "estimation"));
        actionRecorder.BeginBlock(directory, null);
        activeEstimationSet = set;
        activeEstimationProblems = problems;
        activeEstimationOrdinal = 0;
        estimationActive = true;
        panelPinned = true;
        startedEstimationBlocks.Add(blockKey);
        actionRecorder.Record(
            "EstimationStarted",
            set.setIndex.ToString(CultureInfo.InvariantCulture),
            null,
            lastEndedRow.block.ToString(CultureInfo.InvariantCulture));
        ShowEstimationProblem();
        ShowPanel();
        return true;
    }

    public void NextEstimation()
    {
        if (!estimationActive)
        {
            return;
        }
        if (activeEstimationOrdinal + 1 >= activeEstimationProblems.Length)
        {
            EndEstimation();
            return;
        }
        activeEstimationOrdinal++;
        ShowEstimationProblem();
    }

    private void ShowEstimationProblem()
    {
        MoonBoardEstimationProblemDefinition problem =
            activeEstimationProblems[activeEstimationOrdinal];
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetStudyFeedbackVisible(false);
        sceneConfiguror.SetUpRouteByName(problem.id);
        sceneConfiguror.SetRouteCuePresentation(RouteCuePresentation.VirtualHalos);
        actionRecorder.Record(
            "EstimationShown",
            problem.apiId.ToString(CultureInfo.InvariantCulture),
            null,
            (activeEstimationOrdinal + 1).ToString(CultureInfo.InvariantCulture));
        RefreshPanelText();
    }

    private void EndEstimation()
    {
        actionRecorder.Record(
            "EstimationEnded",
            activeEstimationSet.setIndex.ToString(CultureInfo.InvariantCulture));
        actionRecorder.EndBlock();
        int completedSet = activeEstimationSet.setIndex;
        estimationActive = false;
        activeEstimationSet = null;
        activeEstimationProblems = Array.Empty<MoonBoardEstimationProblemDefinition>();
        activeEstimationOrdinal = 0;
        RestoreScheduledDisplay(lastEndedRow);
        statusMessage = "Estimation set " + completedSet.ToString(CultureInfo.InvariantCulture) +
                        " completed.";
        ShowPanel();
        RefreshPanelText();
    }

    private void RestoreScheduledDisplay(StudyScheduleRow preferredRow)
    {
        if (sceneConfiguror == null)
        {
            return;
        }
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetStudyFeedbackVisible(true);

        StudyScheduleRow row = preferredRow;
        if (row == null && participants.Count > 0)
        {
            string participant = participants[participantIndex];
            row = schedule.FirstOrDefault(candidate =>
                candidate.participant == participant && candidate.block == selectedBlock);
        }
        if (row != null && routeCatalog != null && routeCatalog.TryGetRoute(row.route, out _))
        {
            sceneConfiguror.SetUpRouteByName(row.route);
        }
    }

    private static string GetEstimationBlockKey(StudyScheduleRow row)
    {
        return row.participant + ":" + row.block.ToString(CultureInfo.InvariantCulture);
    }

    private bool HasStartedEstimation(StudyScheduleRow row)
    {
        string blockKey = GetEstimationBlockKey(row);
        if (startedEstimationBlocks.Contains(blockKey))
        {
            return true;
        }

        string participantRoot = Path.Combine(Application.persistentDataPath, "study", row.participant);
        if (!StudyRehearsalTiming.HasRecordedEstimation(participantRoot, row.block))
        {
            return false;
        }
        startedEstimationBlocks.Add(blockKey);
        return true;
    }

    private bool TryRecoverSelectedCompletedBlock()
    {
        if (participants.Count == 0)
        {
            return false;
        }

        string participant = participants[participantIndex];
        StudyScheduleRow row = schedule.FirstOrDefault(candidate =>
            candidate.participant == participant && candidate.block == selectedBlock);
        if (row == null)
        {
            statusMessage = $"No loaded schedule row for {participant} block {selectedBlock}.";
            Debug.LogError("[StudyManager] " + statusMessage);
            return false;
        }

        bool pendingMatchesSelection = lastEndedRow != null &&
                                       StudyRehearsalTiming.IsEstimationSelectionMatch(
                                           participant,
                                           selectedBlock,
                                           lastEndedRow.participant,
                                           lastEndedRow.block);
        if (pendingMatchesSelection && !string.IsNullOrEmpty(lastEndedDirectory) &&
            Directory.Exists(lastEndedDirectory))
        {
            return true;
        }
        if (pendingMatchesSelection)
        {
            ClearPendingEstimation();
        }

        string studyRoot = Path.Combine(Application.persistentDataPath, "study");
        bool recovered = StudyRehearsalTiming.TryRecoverCompletedBlock(
            studyRoot,
            row,
            out string directory,
            out string diagnostic);
        if (!string.IsNullOrEmpty(diagnostic))
        {
            Debug.LogError(
                "[StudyManager] Completed-block recovery rejected persisted data for " +
                participant + " block " + selectedBlock.ToString(CultureInfo.InvariantCulture) +
                ":" + Environment.NewLine + diagnostic);
            if (!recovered)
            {
                statusMessage = "Stored block recovery rejected; see the console diagnostic.";
            }
        }
        if (!recovered)
        {
            return false;
        }

        int recoveredParticipantIndex = participants.IndexOf(row.participant);
        if (recoveredParticipantIndex < 0)
        {
            throw new InvalidOperationException(
                "Recovered participant is missing from the loaded schedule: " + row.participant + ".");
        }
        lastEndedRow = row;
        lastEndedDirectory = directory;
        lastEndedParticipantIndex = recoveredParticipantIndex;
        statusMessage = $"Recovered {row.participant} block {row.block} for estimation.";
        Debug.Log("[StudyManager] " + statusMessage + " Directory: " + directory);
        return true;
    }

    private void ClearPendingEstimation()
    {
        lastEndedRow = null;
        lastEndedDirectory = null;
        lastEndedParticipantIndex = 0;
    }

    private static string GetUnusedDirectory(string requestedDirectory)
    {
        if (!Directory.Exists(requestedDirectory))
        {
            return requestedDirectory;
        }
        int retry = 1;
        while (Directory.Exists(requestedDirectory + "_retry" + retry.ToString(CultureInfo.InvariantCulture)))
        {
            retry++;
        }
        return requestedDirectory + "_retry" + retry.ToString(CultureInfo.InvariantCulture);
    }

    public void ShowPanel()
    {
        PositionPanelInFrontOfUser();
        SetPanelVisible(true);
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds);
        SetTimerChipVisible(ShouldShowTimerChip());
        PositionTimerChip();
        RefreshPanelText();
    }

    private void ResolveReferences()
    {
        sceneConfiguror ??= FindAnyObjectByType<SceneConfiguror>();
        actionRecorder ??= FindAnyObjectByType<ActionRecorder>();
        boardLayout ??= FindAnyObjectByType<MoonBoard2016Layout>();
        boardAlignment ??= FindAnyObjectByType<BoardAlignmentController>();
        lastBoardAlignmentStatus = boardAlignment != null ? boardAlignment.StatusMessage : string.Empty;
        if (userCamera == null && sceneConfiguror != null && sceneConfiguror.centerEyeAnchor != null)
        {
            userCamera = sceneConfiguror.centerEyeAnchor.GetComponent<Camera>();
        }
        userCamera ??= Camera.main;
        if (sceneConfiguror != null)
        {
            leftSkeleton = sceneConfiguror.leftHandOVRSkeleton;
            rightSkeleton = sceneConfiguror.rightHandOVRSkeleton;
            leftHand = leftSkeleton != null ? leftSkeleton.GetComponent<OVRHand>() : null;
            rightHand = rightSkeleton != null ? rightSkeleton.GetComponent<OVRHand>() : null;
        }
    }

    private void EnsureScheduleLoadedForRuntime()
    {
        if (routeCatalog == null)
        {
            string catalogPath = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/moonboard_2016_40.json";
            if (!catalogPath.Contains("://") && File.Exists(catalogPath))
            {
                LoadCatalogText(File.ReadAllText(catalogPath));
            }
        }
        if (schedule.Count > 0)
        {
            return;
        }

        if (useMockSchedule)
        {
            LoadScheduleText(BuildMockSchedule());
            return;
        }

        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/study_schedule.csv";
        if (!path.Contains("://") && File.Exists(path))
        {
            LoadScheduleText(File.ReadAllText(path));
        }
    }

    private void EnsureEstimationCatalogLoadedForRuntime()
    {
        if (estimationCatalogLoadAttempted)
        {
            return;
        }
        estimationCatalogLoadAttempted = true;
        if (routeCatalog == null)
        {
            SetSupplementalContentUnavailable("Main catalog is not loaded.");
            return;
        }

        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') +
                      "/moonboard_2016_40_estimation.json";
        string json = !path.Contains("://") && File.Exists(path)
            ? File.ReadAllText(path)
            : null;
        LoadEstimationCatalogText(json);
    }

    private void Update()
    {
        UpdateHeadsetPresence();
        string routesStatusLine = sceneConfiguror != null
            ? sceneConfiguror.GetRoutesLoadStatusLine()
            : "UNAVAILABLE";
        if (routesStatusLine != lastRoutesStatusLine && panelRoot != null && panelRoot.activeSelf)
        {
            RefreshPanelText();
        }
        if (boardAlignment != null && boardAlignment.StatusMessage != lastBoardAlignmentStatus)
        {
            lastBoardAlignmentStatus = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
        if (practiceActive)
        {
            UpdatePractice();
        }
        if (blockRunning)
        {
            HandleGripFeedbackDegradation();
            if (!blockTimerStarted)
            {
                bool ghostDetached = sceneConfiguror != null &&
                                     sceneConfiguror.ghostHoldController != null &&
                                     sceneConfiguror.ghostHoldController.CurrentGhost != null;
                if (StudyRehearsalTiming.TryGetFirstInteraction(
                        activeRow.condition,
                        sceneConfiguror != null && sceneConfiguror.isGripLocomotionActive,
                        ghostDetached,
                        out string interaction))
                {
                    if (!blockHeadsetDonnedRecorded)
                    {
                        InferHeadsetDonnedFromInteraction(interaction);
                    }
                    StartRehearsalClock(interaction, true);
                }
                else
                {
                    UpdateTimerWaitingText();
                    PositionTimerChip();
                }
            }
            if (blockTimerStarted)
            {
                float remaining = blockDurationSeconds - (Time.realtimeSinceStartup - blockStartRealtime);
                if (remaining <= 0f)
                {
                    EndBlock(false, "timer_expired");
                    return;
                }
                UpdateTimerText(remaining);
                PositionTimerChip();
            }
        }

        HandlePanelInput(leftHand, leftSkeleton, ref leftWasPinching, true);
        HandlePanelInput(rightHand, rightSkeleton, ref rightWasPinching, false);

        // The HMD pose is not valid yet when Start() places the panel (over Link the
        // headset may not even be worn), so keep the idle panel in front of the user
        // until the experimenter first uses it.
        if (!panelPinned && !blockRunning && panelRoot != null && panelRoot.activeSelf)
        {
            PositionPanelInFrontOfUser();
        }
#if UNITY_EDITOR
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.mKey.wasPressedThisFrame)
        {
            ShowPanel();
        }
#endif
    }

    private void StartRehearsalClock(string trigger, bool recordFirstInteraction)
    {
        if (!blockRunning || blockTimerStarted || activeRow == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        float latency = recordFirstInteraction
            ? Mathf.Max(0f, now - donningStartRealtime)
            : 0f;
        if (recordFirstInteraction)
        {
            actionRecorder.Record(
                "FirstInteraction",
                activeRow.condition,
                null,
                trigger + ";donningLatencySeconds=" + latency.ToString("F3", CultureInfo.InvariantCulture));
        }
        blockStartRealtime = now;
        blockTimerStarted = true;
        actionRecorder.Record(
            "RehearsalClockStarted",
            activeRow.condition,
            null,
            "block=" + activeRow.block.ToString(CultureInfo.InvariantCulture) + ";trigger=" + trigger);
        statusMessage = $"Running {activeRow.participant} block {activeRow.block}.";
        UpdateTimerText(blockDurationSeconds);
        RefreshPanelText();
    }

    private void UpdateHeadsetPresence()
    {
        bool present = OVRPlugin.userPresent;
        float now = Time.realtimeSinceStartup;
        if (!headsetPresenceInitialized)
        {
            headsetPresenceInitialized = true;
            headsetWasPresent = present;
            headsetPresentSinceRealtime = present ? now : -1f;
            return;
        }
        if (present == headsetWasPresent)
        {
            return;
        }

        headsetWasPresent = present;
        if (present)
        {
            headsetPresentSinceRealtime = now;
            BeginBlockHeadsetWear(now, "sensor_transition");
        }
        else
        {
            EndBlockHeadsetWear(now);
            headsetPresentSinceRealtime = -1f;
        }
    }

    private void InitializeBlockHeadsetWear()
    {
        blockHeadsetDonnedRecorded = activeRow != null && activeRow.condition == "A";
        blockHeadsetWearActive = false;
        blockHeadsetWearSegmentStartRealtime = 0f;
        blockHeadsetWearSeconds = 0f;
        headsetPresenceMismatchLogged = false;
        if (blockHeadsetDonnedRecorded)
        {
            donningStartRealtime = 0f;
            return;
        }

        UpdateHeadsetPresence();
        if (headsetWasPresent)
        {
            float blockWearStart = Time.realtimeSinceStartup;
            float donningStart = StudyRehearsalTiming.ResolveDonningStartRealtime(
                headsetPresentSinceRealtime,
                blockWearStart);
            BeginBlockHeadsetWear(blockWearStart, "present_at_block_start", donningStart);
        }
    }

    private void BeginBlockHeadsetWear(
        float wearStartedAt,
        string source,
        float donningStartedAt = -1f)
    {
        if (!blockRunning || activeRow == null || activeRow.condition == "A" || blockHeadsetWearActive)
        {
            return;
        }

        blockHeadsetWearActive = true;
        blockHeadsetWearSegmentStartRealtime = wearStartedAt;
        string details = "block=" + activeRow.block.ToString(CultureInfo.InvariantCulture) +
                         ";source=" + source;
        if (!blockHeadsetDonnedRecorded)
        {
            blockHeadsetDonnedRecorded = true;
            donningStartRealtime = StudyRehearsalTiming.ResolveDonningStartRealtime(
                donningStartedAt,
                wearStartedAt);
            actionRecorder.Record("HeadsetDonned", activeRow.condition, null, details);
            return;
        }
        actionRecorder.Record("HeadsetRedonned", activeRow.condition, null, details);
    }

    private void EndBlockHeadsetWear(float removedAt)
    {
        if (!blockRunning || activeRow == null || activeRow.condition == "A" || !blockHeadsetWearActive)
        {
            return;
        }

        float segmentSeconds = Mathf.Max(0f, removedAt - blockHeadsetWearSegmentStartRealtime);
        blockHeadsetWearSeconds += segmentSeconds;
        blockHeadsetWearActive = false;
        actionRecorder.Record(
            "HeadsetRemoved",
            activeRow.condition,
            null,
            "segmentSeconds=" + segmentSeconds.ToString("F3", CultureInfo.InvariantCulture) +
            ";wearSeconds=" + blockHeadsetWearSeconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    private void FinalizeBlockHeadsetWear()
    {
        if (activeRow == null || activeRow.condition == "A")
        {
            return;
        }

        UpdateHeadsetPresence();
        if (blockHeadsetWearActive)
        {
            float now = Time.realtimeSinceStartup;
            blockHeadsetWearSeconds += Mathf.Max(0f, now - blockHeadsetWearSegmentStartRealtime);
            blockHeadsetWearActive = false;
        }
        actionRecorder.Record(
            "HeadsetWearSummary",
            activeRow.condition,
            null,
            "wearSeconds=" + blockHeadsetWearSeconds.ToString("F3", CultureInfo.InvariantCulture));
    }

    private void InferHeadsetDonnedFromInteraction(string interaction)
    {
        if (headsetPresenceMismatchLogged)
        {
            return;
        }

        headsetPresenceMismatchLogged = true;
        string details = "interaction=" + interaction + "; block=" +
                         activeRow.block.ToString(CultureInfo.InvariantCulture);
        actionRecorder.Record("HeadsetPresenceMismatch", activeRow.condition, null, details);
        Debug.LogWarning("[StudyManager] Interaction preceded the headset-presence signal; " +
                         "inferring donning. " + details);
        BeginBlockHeadsetWear(Time.realtimeSinceStartup, "inferred_from_interaction");
    }

    private void HandlePanelInput(
        OVRHand hand,
        OVRSkeleton skeleton,
        ref bool wasPinching,
        bool isLeft)
    {
        bool pinching = hand != null && hand.IsTracked && hand.IsDataHighConfidence &&
                        hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool pinchStarted = pinching && !wasPinching;
        wasPinching = pinching;
        bool summonConsumed = UpdateSummonGesture(
            hand,
            skeleton,
            pinching,
            pinchStarted,
            isLeft);
        if (!pinchStarted || summonConsumed)
        {
            return;
        }

        if (hand.IsPointerPoseValid && hand.PointerPose != null &&
            Physics.Raycast(
                hand.PointerPose.position,
                hand.PointerPose.forward,
                out RaycastHit hit,
                5f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore))
        {
            StudyPanelButton button = hit.collider.GetComponentInParent<StudyPanelButton>();
            if (button != null)
            {
                if (Time.unscaledTime < panelPressableAt)
                {
                    return;
                }
                panelPinned = true;
                button.Press();
                return;
            }
            return;
        }
    }

    private bool UpdateSummonGesture(
        OVRHand hand,
        OVRSkeleton skeleton,
        bool pinching,
        bool pinchStarted,
        bool isLeft)
    {
        if (!isLeft)
        {
            return false;
        }
        if (panelRoot == null || panelRoot.activeSelf)
        {
            ResetSummonDwell();
            return false;
        }

        bool trackingConfident = hand != null && hand.IsTracked && hand.IsDataHighConfidence;
        bool palmUp = trackingConfident && IsPalmUp(skeleton);
        if (!blockRunning)
        {
            ResetSummonDwell();
            if (palmUp && pinchStarted)
            {
                ShowPanel();
                return true;
            }
            return false;
        }

        float now = Time.unscaledTime;
        if (now < summonCooldownUntil || !palmUp)
        {
            ResetSummonDwell();
            return false;
        }

        if (summonDwellStart < 0f)
        {
            if (pinching)
            {
                return false;
            }
            summonDwellStart = now;
        }

        if (!summonReadyForPinch &&
            now - summonDwellStart >= Mathf.Max(0f, summonDwellSeconds))
        {
            summonReadyForPinch = true;
        }

        if (summonReadyForPinch && pinchStarted)
        {
            summonCooldownUntil = now + Mathf.Max(0f, summonCooldownSeconds);
            ResetSummonDwell();
            ShowPanel();
            return true;
        }

        if (pinching)
        {
            ResetSummonDwell();
        }
        return false;
    }

    private void ResetSummonDwell()
    {
        summonDwellStart = -1f;
        summonReadyForPinch = false;
    }

    private static bool IsPalmUp(OVRSkeleton skeleton)
    {
        if (skeleton == null || skeleton.Bones == null || skeleton.Bones.Count == 0 ||
            skeleton.Bones[0].Transform == null)
        {
            return false;
        }

        Transform palm = skeleton.Bones[0].Transform;
        return Vector3.Dot(palm.up, Vector3.up) > 0.55f ||
               Vector3.Dot(-palm.forward, Vector3.up) > 0.55f;
    }

    private void BuildPanel()
    {
        if (panelRoot != null)
        {
            return;
        }

        panelMaterial = CreateMaterial(new Color(0.04f, 0.055f, 0.08f, 0.96f));
        buttonMaterial = CreateMaterial(new Color(0.08f, 0.35f, 0.52f, 1f));

        panelRoot = new GameObject("Study Experimenter Panel");
        panelRoot.transform.SetParent(transform, false);
        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "Panel Background";
        background.transform.SetParent(panelRoot.transform, false);
        background.transform.localScale = new Vector3(0.64f, 1.00f, 0.012f);
        background.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
        Destroy(background.GetComponent<Collider>());

        panelText = CreateText(panelRoot.transform, new Vector3(0f, 0.12f, -0.008f), 0.006f, 36);
        CreateButton("Previous Participant", new Vector3(-0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "PREV P", PreviousParticipant);
        CreateButton("Next Participant", new Vector3(0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT P", NextParticipant);
        CreateButton("Previous Block", new Vector3(-0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "PREV BLOCK", PreviousBlock);
        CreateButton("Next Block", new Vector3(0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT BLOCK", NextBlock);
        CreateButton("Start Block", new Vector3(0f, -0.08f, -0.02f), new Vector2(0.20f, 0.065f), "START", () => StartSelectedBlock());
        CreateButton("End Block", new Vector3(0f, -0.16f, -0.02f), new Vector2(0.20f, 0.065f), "END EARLY", EndBlockEarly);
        adhocConditionLabel = CreateButton("Adhoc Condition", new Vector3(-0.22f, -0.24f, -0.02f), new Vector2(0.16f, 0.065f), "COND: A", CycleAdhocCondition);
        CreateButton("Adhoc Start", new Vector3(0f, -0.24f, -0.02f), new Vector2(0.20f, 0.065f), "ADHOC START", () => StartAdhocBlock());
        adhocRouteLabel = CreateButton("Adhoc Route", new Vector3(0.22f, -0.24f, -0.02f), new Vector2(0.16f, 0.065f), "ROUTE", CycleAdhocRoute);
        CreateButton("Practice", new Vector3(-0.22f, -0.32f, -0.02f), new Vector2(0.16f, 0.06f), "PRACTICE", () => StartPractice(), out practiceButton);
        CreateButton("Estimation Start", new Vector3(0f, -0.32f, -0.02f), new Vector2(0.20f, 0.06f), "EST START", () => StartEstimation(), out estimationStartButton);
        CreateButton("Estimation Next", new Vector3(0.22f, -0.32f, -0.02f), new Vector2(0.16f, 0.06f), "EST NEXT", NextEstimation, out estimationNextButton);
        CreateButton("Align Board", new Vector3(-0.18f, -0.40f, -0.02f), new Vector2(0.20f, 0.055f), "ALIGN BOARD", BeginBoardAlignment);
        CreateButton("Clear Alignment", new Vector3(0.18f, -0.40f, -0.02f), new Vector2(0.20f, 0.055f), "CLEAR ALIGN", ClearBoardAlignment);
        CreateButton("Hide Panel", new Vector3(0f, -0.47f, -0.02f), new Vector2(0.16f, 0.05f), "HIDE", () => SetPanelVisible(false));

        timerChipRoot = new GameObject("Study Timer Chip");
        timerChipRoot.transform.SetParent(transform, false);
        GameObject chipBackground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chipBackground.name = "Timer Background";
        chipBackground.transform.SetParent(timerChipRoot.transform, false);
        chipBackground.transform.localScale = new Vector3(0.20f, 0.075f, 0.012f);
        chipBackground.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
        StudyPanelButton chipButton = chipBackground.AddComponent<StudyPanelButton>();
        chipButton.Pressed = ShowPanel;
        timerText = CreateText(timerChipRoot.transform, new Vector3(0f, 0f, -0.008f), 0.006f, 36);
        SetTimerChipVisible(false);
        PositionPanelInFrontOfUser();
        RefreshButtonStates();
    }

    private TextMesh CreateButton(
        string objectName,
        Vector3 localPosition,
        Vector2 size,
        string label,
        Action pressed)
    {
        return CreateButton(objectName, localPosition, size, label, pressed, out _);
    }

    private TextMesh CreateButton(
        string objectName,
        Vector3 localPosition,
        Vector2 size,
        string label,
        Action pressed,
        out StudyPanelButton button)
    {
        GameObject buttonObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonObject.name = objectName;
        buttonObject.transform.SetParent(panelRoot.transform, false);
        buttonObject.transform.localPosition = localPosition;
        buttonObject.transform.localScale = new Vector3(size.x, size.y, 0.02f);
        buttonObject.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        button = buttonObject.AddComponent<StudyPanelButton>();
        button.Pressed = pressed;
        // Keep labels under the uniformly-scaled panel root. Parenting them to the flattened
        // cube would stretch glyphs by the button's non-uniform scale.
        TextMesh text = CreateText(
            panelRoot.transform,
            localPosition + new Vector3(0f, 0f, -0.0112f),
            0.006f,
            26);
        text.text = label;
        return text;
    }

    // No Y flip: PositionPanelInFrontOfUser points the panel's +Z away from the user, which
    // is exactly the orientation TextMesh glyphs read correctly from. The previous 180° flip
    // mirrored every label from the user's viewpoint (review finding C1, 2026-07-16).
    private static TextMesh CreateText(Transform parent, Vector3 localPosition, float characterSize, int fontSize)
    {
        GameObject textObject = new("Label");
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localRotation = Quaternion.identity;
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.characterSize = characterSize;
        text.fontSize = fontSize;
        text.color = Color.white;
        return text;
    }

    private Material CreateMaterial(Color color)
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Universal Render Pipeline/Unlit") ??
                                    UnityEngine.Shader.Find("Standard");
        if (shader == null)
        {
            throw new InvalidOperationException("No compatible unlit shader is available for the study panel.");
        }
        Material material = new(shader);
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        else
        {
            material.color = color;
        }
        return material;
    }

    private void PreviousParticipant()
    {
        if (participants.Count == 0 || blockRunning || IsAuxiliaryActive)
        {
            return;
        }
        participantIndex = (participantIndex - 1 + participants.Count) % participants.Count;
        TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void NextParticipant()
    {
        if (participants.Count == 0 || blockRunning || IsAuxiliaryActive)
        {
            return;
        }
        participantIndex = (participantIndex + 1) % participants.Count;
        TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void PreviousBlock()
    {
        if (!blockRunning && !IsAuxiliaryActive)
        {
            selectedBlock = selectedBlock == 1 ? 3 : selectedBlock - 1;
            TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void CycleAdhocCondition()
    {
        if (blockRunning || IsAuxiliaryActive)
        {
            return;
        }
        adhocConditionIndex = (adhocConditionIndex + 1) % AdhocConditions.Length;
        RefreshPanelText();
    }

    private void CycleAdhocRoute()
    {
        if (blockRunning || IsAuxiliaryActive)
        {
            return;
        }
        int routeCount = sceneConfiguror != null ? sceneConfiguror.GetAvailableRouteNames().Count : 0;
        if (routeCount == 0)
        {
            return;
        }
        adhocRouteIndex = (adhocRouteIndex + 1) % routeCount;
        RefreshPanelText();
    }

    private string GetAdhocRouteName()
    {
        List<string> routes = sceneConfiguror != null
            ? sceneConfiguror.GetAvailableRouteNames()
            : new List<string>();
        if (routes.Count == 0)
        {
            return string.Empty;
        }
        adhocRouteIndex = Mathf.Clamp(adhocRouteIndex, 0, routes.Count - 1);
        return routes[adhocRouteIndex];
    }

    private void NextBlock()
    {
        if (!blockRunning && !IsAuxiliaryActive)
        {
            selectedBlock = selectedBlock == 3 ? 1 : selectedBlock + 1;
            TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void RefreshPanelText()
    {
        RefreshButtonStates();
        if (panelText == null)
        {
            return;
        }

        if (estimationActive)
        {
            MoonBoardEstimationProblemDefinition problem =
                activeEstimationProblems[activeEstimationOrdinal];
            panelText.text = "Estimation " +
                             activeEstimationSet.setIndex.ToString(CultureInfo.InvariantCulture) + " " +
                             (activeEstimationOrdinal + 1).ToString(CultureInfo.InvariantCulture) + "/4\n" +
                             problem.apiId.ToString(CultureInfo.InvariantCulture);
            return;
        }

        StringBuilder text = new();
        if (practiceActive)
        {
            text.Append("Practice phase ").Append(practicePhase).AppendLine();
        }
        if (participants.Count > 0)
        {
            string participant = participants[participantIndex];
            text.Append(participant).Append("  |  selected block ").Append(selectedBlock).AppendLine();
            foreach (StudyScheduleRow row in schedule.Where(row => row.participant == participant))
            {
                text.Append(row.block == selectedBlock ? "> " : "  ")
                    .Append("Block ").Append(row.block).Append(": ")
                    .Append(row.condition).Append(" / ")
                    .Append(sceneConfiguror != null ? sceneConfiguror.GetRouteDisplayName(row.route) : row.route)
                    .AppendLine();
            }
        }

        string adhocCondition = AdhocConditions[adhocConditionIndex];
        string adhocRoute = GetAdhocRouteName();
        text.Append("Adhoc: ").Append(adhocCondition).Append(" / ")
            .Append(string.IsNullOrEmpty(adhocRoute) ? "(no routes)" : adhocRoute).AppendLine();
        if (adhocConditionLabel != null)
        {
            adhocConditionLabel.text = "COND: " + adhocCondition;
        }
        if (adhocRouteLabel != null)
        {
            adhocRouteLabel.text = adhocRoute.Length > 9 ? adhocRoute.Substring(0, 9) + ".." : adhocRoute;
        }

        lastRoutesStatusLine = sceneConfiguror != null
            ? sceneConfiguror.GetRoutesLoadStatusLine()
            : "UNAVAILABLE";
        text.Append("Routes: ").Append(lastRoutesStatusLine).AppendLine();
        if (sceneConfiguror != null && sceneConfiguror.IsGripFeedbackDegraded)
        {
            text.AppendLine("GRIP CUE OFF");
        }
        text.AppendLine(statusMessage);
        if (!string.IsNullOrEmpty(supplementalContentStatus))
        {
            text.AppendLine(supplementalContentStatus);
        }
        if (boardAlignment != null)
        {
            text.AppendLine(boardAlignment.StatusMessage);
        }
        panelText.text = text.ToString();
    }

    private void RefreshButtonStates()
    {
        bool catalogReady = estimationCatalog != null;
        bool practiceAvailable = false;
        if (catalogReady && participants.Count > 0)
        {
            practiceAvailable = CanStartPractice(participants[participantIndex]);
        }
        practiceButton?.SetInteractable(
            practiceAvailable && !blockRunning && !IsAuxiliaryActive);

        bool estimationAvailable = false;
        if (catalogReady && lastEndedRow != null && participants.Count > 0 &&
            StudyRehearsalTiming.IsEstimationSelectionMatch(
                participants[participantIndex],
                selectedBlock,
                lastEndedRow.participant,
                lastEndedRow.block))
        {
            estimationAvailable = !HasStartedEstimation(lastEndedRow);
        }
        estimationStartButton?.SetInteractable(
            estimationAvailable && !blockRunning && !IsAuxiliaryActive);
        estimationNextButton?.SetInteractable(estimationActive);
    }

    private bool CanStartPractice(string participant)
    {
        string participantRoot = Path.Combine(Application.persistentDataPath, "study", participant);
        if (Directory.Exists(participantRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(participantRoot))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith("practice_block0", StringComparison.Ordinal))
                {
                    participantsWithPracticeRuns.Add(participant);
                }
                else if (name.StartsWith("block1_", StringComparison.Ordinal) ||
                         name.StartsWith("block2_", StringComparison.Ordinal) ||
                         name.StartsWith("block3_", StringComparison.Ordinal))
                {
                    participantsWithBlockRuns.Add(participant);
                }
            }
        }

        return StudyRehearsalTiming.CanStartPractice(
            participant,
            participantsWithPracticeRuns,
            participantsWithBlockRuns);
    }

    private void UpdateTimerText(float remainingSeconds)
    {
        if (timerText == null || activeRow == null)
        {
            return;
        }

        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        timerText.text = $"{activeRow.participant} B{activeRow.block}  {totalSeconds / 60:00}:{totalSeconds % 60:00}" +
                         (sceneConfiguror != null && sceneConfiguror.IsGripFeedbackDegraded
                             ? "\nGRIP CUE OFF"
                              : string.Empty);
    }

    private void UpdateTimerWaitingText()
    {
        if (timerText != null && activeRow != null)
        {
            timerText.text = $"{activeRow.participant} B{activeRow.block}\nWAITING FOR INTERACTION";
        }
    }

    private void UpdatePracticeTimerText(float remainingSeconds)
    {
        if (timerText == null)
        {
            return;
        }
        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        timerText.text = $"PRACTICE {practicePhase}  {totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private void HandleGripFeedbackDegradation()
    {
        if (activeManifest == null || sceneConfiguror == null ||
            !sceneConfiguror.IsGripFeedbackDegraded ||
            !string.Equals(activeManifest.gripFeedback, "ok", StringComparison.Ordinal))
        {
            return;
        }

        string degradedUtc = string.IsNullOrEmpty(sceneConfiguror.GripFeedbackDegradedUtc)
            ? DateTime.UtcNow.ToString("o")
            : sceneConfiguror.GripFeedbackDegradedUtc;
        activeManifest.gripFeedback = "degraded_at_" + degradedUtc;
        actionRecorder?.Record(
            "GripFeedbackDegraded",
            "",
            null,
            "GRIP CUE OFF at " + degradedUtc + "; block continues");
        statusMessage = "Grip feedback degraded; block continues.";
        WriteManifest();
        RefreshPanelText();
    }

    private void PositionPanelInFrontOfUser()
    {
        if (panelRoot == null || userCamera == null)
        {
            return;
        }

        Transform cameraTransform = userCamera.transform;
        panelRoot.transform.position = cameraTransform.position + cameraTransform.forward * 0.75f;
        panelRoot.transform.rotation = Quaternion.LookRotation(
            panelRoot.transform.position - cameraTransform.position,
            cameraTransform.up);
    }

    private void PositionTimerChip()
    {
        if (timerChipRoot == null || userCamera == null)
        {
            return;
        }

        Transform cameraTransform = userCamera.transform;
        if (panelRoot != null)
        {
            timerChipRoot.transform.position = panelRoot.transform.position +
                                               panelRoot.transform.up * 0.515f -
                                               panelRoot.transform.forward * 0.01f;
            timerChipRoot.transform.rotation = panelRoot.transform.rotation;
        }
        else
        {
            timerChipRoot.transform.position = cameraTransform.position + cameraTransform.forward * 0.75f +
                                               cameraTransform.up * 0.515f;
            timerChipRoot.transform.rotation = Quaternion.LookRotation(
                timerChipRoot.transform.position - cameraTransform.position,
                cameraTransform.up);
        }
    }

    private void SetPanelVisible(bool visible)
    {
        panelRoot?.SetActive(visible);
        if (!visible)
        {
            ResetSummonDwell();
            SetTimerChipVisible(ShouldShowTimerChip());
        }
    }

    private void SetTimerChipVisible(bool visible)
    {
        timerChipRoot?.SetActive(visible);
    }

    private bool ShouldShowTimerChip()
    {
        return blockRunning && activeRow != null && activeRow.condition == "A";
    }

    private float GetBlockMinutes()
    {
        return (Debug.isDebugBuild || Application.isEditor)
            ? Mathf.Clamp(debugBlockMinutes, 0.1f, ReleaseBlockMinutes)
            : ReleaseBlockMinutes;
    }

    private float GetPracticePhaseSeconds()
    {
        return (Debug.isDebugBuild || Application.isEditor)
            ? Mathf.Clamp(debugPracticePhaseSeconds, 1f, ReleasePracticePhaseSeconds)
            : ReleasePracticePhaseSeconds;
    }

    private void WriteManifest()
    {
        if (activeManifest == null || string.IsNullOrEmpty(manifestPath))
        {
            return;
        }

        string temporaryPath = manifestPath + ".tmp";
        string routesHash = activeManifest.routesJsonSha256;
        string manifestJson = string.Empty;
        try
        {
            if (routesHash == null)
            {
                activeManifest.routesJsonSha256 = NullRoutesHashSentinel;
            }
            manifestJson = JsonUtility.ToJson(activeManifest, true);
        }
        finally
        {
            activeManifest.routesJsonSha256 = routesHash;
        }
        if (routesHash == null)
        {
            manifestJson = manifestJson.Replace('"' + NullRoutesHashSentinel + '"', "null");
        }
        File.WriteAllText(temporaryPath, manifestJson);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }
        File.Move(temporaryPath, manifestPath);
    }

    private static string SanitizePathToken(string value)
    {
        StringBuilder output = new(value.Length);
        foreach (char character in value.ToUpperInvariant())
        {
            output.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        return output.ToString().Trim('_');
    }

    private string BuildMockSchedule()
    {
        if (routeCatalog == null || routeCatalog.routes == null || routeCatalog.routes.Length != 3)
        {
            return string.Empty;
        }
        return "participant,block,condition,route\n" +
               "P07,1,B," + routeCatalog.routes[0].id + "\n" +
               "P07,2,C," + routeCatalog.routes[1].id + "\n" +
               "P07,3,A," + routeCatalog.routes[2].id + "\n";
    }

    private bool LoadCatalogText(string json)
    {
        string catalogSha256 = MoonBoardStudyCatalog.ComputeSha256(json);
        if (catalogSha256 != MoonBoardStudyCatalog.ApprovedCatalogSha256)
        {
            statusMessage = "MoonBoard catalog does not match the approved study content.";
            return false;
        }
        if (!MoonBoardStudyCatalog.TryParse(json, out MoonBoardStudyCatalog parsed, out string error))
        {
            statusMessage = error;
            return false;
        }
        if (boardLayout == null || !boardLayout.ApplyCatalog(parsed, out error))
        {
            statusMessage = boardLayout == null ? "MoonBoard metric layout is unavailable." : error;
            return false;
        }
        if (sceneConfiguror == null || !sceneConfiguror.SetRouteCatalog(parsed, out error))
        {
            statusMessage = sceneConfiguror == null ? "Scene configurator is unavailable." : error;
            return false;
        }

        routeCatalog = parsed;
        routeCatalogSha256 = catalogSha256;
        boardAlignment?.SetCatalog(parsed);
        return true;
    }

    private bool LoadEstimationCatalogText(string json)
    {
        routeCatalog?.ClearSupplementalRoutes();
        estimationCatalog = null;
        if (!MoonBoardEstimationCatalog.TryParseApproved(
                json,
                routeCatalog,
                out MoonBoardEstimationCatalog parsed,
                out string error))
        {
            SetSupplementalContentUnavailable(error);
            return false;
        }
        if (!routeCatalog.TrySetSupplementalRoutes(parsed.GetSupplementalRoutes(), out error))
        {
            SetSupplementalContentUnavailable(error);
            return false;
        }

        estimationCatalog = parsed;
        supplementalContentStatus = string.Empty;
        RefreshPanelText();
        return true;
    }

    private void SetSupplementalContentUnavailable(string error)
    {
        supplementalContentStatus = "Supplemental content unavailable.";
        Debug.LogError("[StudyManager] Supplemental content unavailable: " + error);
        RefreshPanelText();
    }

    private IEnumerator LoadStreamingAssetText(
        string fileName,
        Action<string> loaded,
        bool updateStatusOnFailure = true)
    {
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/" + fileName;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                loaded(request.downloadHandler.text);
            }
            else if (updateStatusOnFailure)
            {
                statusMessage = fileName + " load failed: " + request.error;
            }
            yield break;
        }
        if (File.Exists(path))
        {
            loaded(File.ReadAllText(path));
        }
        else if (updateStatusOnFailure)
        {
            statusMessage = fileName + " not found.";
        }
    }

    private void BeginBoardAlignment()
    {
        if (blockRunning || IsAuxiliaryActive)
        {
            statusMessage = "End the current block or auxiliary sequence before aligning the board.";
        }
        else if (boardAlignment == null)
        {
            statusMessage = "Board alignment is unavailable.";
        }
        else if (!boardAlignment.BeginCalibration(out string error))
        {
            statusMessage = error;
        }
        else
        {
            statusMessage = "Board alignment started.";
        }
        RefreshPanelText();
    }

    private void ClearBoardAlignment()
    {
        if (!blockRunning && !IsAuxiliaryActive && boardAlignment != null)
        {
            if (!boardAlignment.ClearAlignment())
            {
                statusMessage = boardAlignment.StatusMessage;
                RefreshPanelText();
                return;
            }
            statusMessage = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
    }

    private void OnDestroy()
    {
        if (blockRunning)
        {
            EndBlock(true, "app_closed");
        }
        else if (IsAuxiliaryActive)
        {
            actionRecorder?.EndBlock();
        }
        if (panelMaterial != null)
        {
            Destroy(panelMaterial);
        }
        if (buttonMaterial != null)
        {
            Destroy(buttonMaterial);
        }
    }
}
