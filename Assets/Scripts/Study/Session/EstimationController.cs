using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Runs the grade-estimation battery (spec 05). The scheduled path recovers the just-ended block,
/// shows its yoked problem set once, and restores the scheduled board display afterwards. The
/// manual path walks the whole battery from the experimenter console, which has no participant,
/// block or yoked set to work from.
/// </summary>
public sealed class EstimationController
{
    private const string ManualEstimationMarker = "manual";
    private readonly StudySessionState state;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ActionRecorder actionRecorder;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudyControlPanel panel;
    private readonly Action ensureScheduleLoadedForRuntime;
    private readonly Action ensureEstimationCatalogLoadedForRuntime;

    private readonly HashSet<string> startedEstimationBlocks = new(StringComparer.Ordinal);

    private bool manualEstimationActive;

    public EstimationController(
        StudySessionState state,
        SceneConfiguror sceneConfiguror,
        ActionRecorder actionRecorder,
        BoardAlignmentController boardAlignment,
        StudyControlPanel panel,
        Action ensureScheduleLoadedForRuntime,
        Action ensureEstimationCatalogLoadedForRuntime)
    {
        this.state = state;
        this.sceneConfiguror = sceneConfiguror;
        this.actionRecorder = actionRecorder;
        this.boardAlignment = boardAlignment;
        this.panel = panel;
        this.ensureScheduleLoadedForRuntime = ensureScheduleLoadedForRuntime;
        this.ensureEstimationCatalogLoadedForRuntime = ensureEstimationCatalogLoadedForRuntime;
    }

    public bool StartEstimation()
    {
        ensureScheduleLoadedForRuntime();
        ensureEstimationCatalogLoadedForRuntime();
        if (state.blockRunning || state.practiceActive || state.estimationActive)
        {
            state.statusMessage = "End the current sequence before starting estimation.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.estimationCatalog == null)
        {
            state.statusMessage = "Estimation content is unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        TryRecoverSelectedCompletedBlock();
        if (state.lastEndedRow == null || string.IsNullOrEmpty(state.lastEndedDirectory))
        {
            state.statusMessage = "End a scheduled block before starting estimation.";
            panel.RefreshPanelText();
            return false;
        }

        if (state.participants.Count == 0 ||
            !StudyRehearsalTiming.IsEstimationSelectionMatch(
                state.participants[state.participantIndex],
                state.selectedBlock,
                state.lastEndedRow.participant,
                state.lastEndedRow.block))
        {
            state.statusMessage = "Select the just-ended participant and block before starting estimation.";
            panel.RefreshPanelText();
            return false;
        }

        string blockKey = GetEstimationBlockKey(state.lastEndedRow);
        if (HasStartedEstimation(state.lastEndedRow))
        {
            state.statusMessage = "This block's estimation set has already been started.";
            panel.RefreshPanelText();
            return false;
        }
        string error = string.Empty;
        if (!state.estimationCatalog.TryGetSetForRoute(
                state.lastEndedRow.route,
                out MoonBoardEstimationSetDefinition set) ||
            !state.estimationCatalog.TryGetRotatedProblems(
                set,
                state.lastEndedParticipantIndex,
                out MoonBoardEstimationProblemDefinition[] problems,
                out error))
        {
            state.statusMessage = "The estimation set for this block is unavailable; see the log.";
            Debug.LogError(
                "[StudyManager] Estimation set unavailable for " + state.lastEndedRow.route + ": " +
                (string.IsNullOrEmpty(error) ? "no set is yoked to the just-ended route." : error));
            panel.RefreshPanelText();
            return false;
        }
        if (sceneConfiguror == null || actionRecorder == null)
        {
            state.statusMessage = "Estimation runtime references are unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }
        foreach (MoonBoardEstimationProblemDefinition problem in problems)
        {
            if (!sceneConfiguror.TryValidateRoute(problem.id, out error))
            {
                state.statusMessage = "An estimation problem is unavailable; see the log.";
                Debug.LogError("[StudyManager] " + error);
                panel.RefreshPanelText();
                return false;
            }
        }

        string directory = BlockRunController.GetUnusedDirectory(
            Path.Combine(state.lastEndedDirectory, "estimation"));
        actionRecorder.BeginBlock(directory, null);
        state.activeEstimationSet = set;
        state.activeEstimationProblems = problems;
        state.activeEstimationOrdinal = 0;
        state.estimationActive = true;
        state.panelPinned = true;
        startedEstimationBlocks.Add(blockKey);
        actionRecorder.Record(
            "EstimationStarted",
            set.setIndex.ToString(CultureInfo.InvariantCulture),
            null,
            state.lastEndedRow.block.ToString(CultureInfo.InvariantCulture));
        ShowEstimationProblem();
        panel.ShowPanel();
        return true;
    }

    /// <summary>
    /// Starts the estimation battery from the experimenter console. Manual runs are ad hoc, so
    /// there is no ended block to recover, no participant to rotate within a set and no set to
    /// yoke: the cycle walks all approved estimation problems in catalog order. The practice
    /// problem is familiarization content and stays out of it. Recordings land under their own
    /// root, outside both the participant directories the scheduled-estimation guard reads and the
    /// manual-run root that startup recovery scans.
    /// </summary>
    public bool StartManualEstimation()
    {
        ensureEstimationCatalogLoadedForRuntime();
        if (state.blockRunning || state.practiceActive || state.estimationActive)
        {
            state.statusMessage = "End the current sequence before starting estimation.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.estimationCatalog == null || state.estimationCatalog.problems == null ||
            state.estimationCatalog.problems.Length == 0)
        {
            state.statusMessage = "Estimation content is unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (sceneConfiguror == null || actionRecorder == null)
        {
            state.statusMessage = "Estimation runtime references are unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }

        MoonBoardEstimationProblemDefinition[] problems =
            (MoonBoardEstimationProblemDefinition[])state.estimationCatalog.problems.Clone();
        foreach (MoonBoardEstimationProblemDefinition problem in problems)
        {
            if (!sceneConfiguror.TryValidateRoute(problem.id, out string error))
            {
                state.statusMessage = "An estimation problem is unavailable; see the log.";
                Debug.LogError("[StudyManager] " + error);
                panel.RefreshPanelText();
                return false;
            }
        }

        string directory = BlockRunController.GetUnusedDirectory(Path.Combine(
            Application.persistentDataPath,
            "study",
            ManualEstimationPolicy.RecordingRootName,
            DateTimeOffset.UtcNow.UtcDateTime.ToString(
                "yyyyMMdd_HHmmss_fff",
                CultureInfo.InvariantCulture)));
        actionRecorder.BeginBlock(directory, null);
        state.activeEstimationSet = null;
        state.activeEstimationProblems = problems;
        state.activeEstimationOrdinal = 0;
        state.estimationActive = true;
        state.panelPinned = true;
        manualEstimationActive = true;
        actionRecorder.Record(
            "EstimationStarted",
            ManualEstimationMarker,
            null,
            "problems=" + problems.Length.ToString(CultureInfo.InvariantCulture));
        ShowEstimationProblem();
        panel.ShowPanel();
        return true;
    }

    /// <summary>Console label for the control that starts the cycle and then advances it.</summary>
    public string GetAdvanceLabel()
    {
        return state.estimationActive
            ? ManualEstimationPolicy.FormatAdvanceLabel(
                state.activeEstimationOrdinal,
                state.activeEstimationProblems.Length)
            : ManualEstimationPolicy.StartLabel;
    }

    /// <summary>Console readout for the problem currently on the board, empty when none is.</summary>
    public string GetProgressReadout()
    {
        if (!state.estimationActive || state.activeEstimationProblems.Length == 0)
        {
            return string.Empty;
        }

        int ordinal = Mathf.Clamp(
            state.activeEstimationOrdinal,
            0,
            state.activeEstimationProblems.Length - 1);
        return ManualEstimationPolicy.FormatProgressReadout(
            state.activeEstimationProblems[ordinal].id,
            ordinal,
            state.activeEstimationProblems.Length);
    }

    public void NextEstimation()
    {
        if (!state.estimationActive)
        {
            return;
        }
        if (state.activeEstimationOrdinal + 1 >= state.activeEstimationProblems.Length)
        {
            EndEstimation();
            return;
        }
        state.activeEstimationOrdinal++;
        ShowEstimationProblem();
    }

    private void ShowEstimationProblem()
    {
        MoonBoardEstimationProblemDefinition problem =
            state.activeEstimationProblems[state.activeEstimationOrdinal];
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetStudyFeedbackVisible(false);
        sceneConfiguror.SetUpRouteByName(problem.id);
        sceneConfiguror.SetRouteCuePresentation(RouteCuePresentation.Hidden);
        actionRecorder.Record(
            "EstimationShown",
            problem.apiId.ToString(CultureInfo.InvariantCulture),
            null,
            (state.activeEstimationOrdinal + 1).ToString(CultureInfo.InvariantCulture));
        panel.RefreshPanelText();
    }

    /// <summary>
    /// Ends whichever estimation cycle is showing. The console calls this before a rehearsal run
    /// or a reset, so it has to close the recording rather than leave the manual cycle open.
    /// </summary>
    public void EndEstimation()
    {
        if (!state.estimationActive)
        {
            return;
        }

        bool manual = manualEstimationActive;
        int completedSet = state.activeEstimationSet != null ? state.activeEstimationSet.setIndex : 0;
        actionRecorder.Record(
            "EstimationEnded",
            manual
                ? ManualEstimationMarker
                : completedSet.ToString(CultureInfo.InvariantCulture));
        actionRecorder.EndBlock();
        state.estimationActive = false;
        manualEstimationActive = false;
        state.activeEstimationSet = null;
        state.activeEstimationProblems = Array.Empty<MoonBoardEstimationProblemDefinition>();
        state.activeEstimationOrdinal = 0;
        if (manual)
        {
            RestoreManualDisplay();
            state.statusMessage = "Estimation cycle ended.";
        }
        else
        {
            RestoreScheduledDisplay(state.lastEndedRow);
            state.statusMessage = "Estimation set " + completedSet.ToString(CultureInfo.InvariantCulture) +
                                  " completed.";
        }
        panel.ShowPanel();
        panel.RefreshPanelText();
    }

    /// <summary>Returns the board to the route the console has selected, the manual counterpart of
    /// restoring the scheduled display.</summary>
    private void RestoreManualDisplay()
    {
        if (sceneConfiguror == null)
        {
            return;
        }
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetStudyFeedbackVisible(true);

        List<string> routes = sceneConfiguror.GetStudyRouteNames();
        if (routes.Count > 0)
        {
            sceneConfiguror.SetUpRouteByName(
                routes[Mathf.Clamp(state.adhocRouteIndex, 0, routes.Count - 1)]);
        }
    }

    public void RestoreScheduledDisplay(StudyScheduleRow preferredRow)
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
        if (row == null && state.participants.Count > 0)
        {
            string participant = state.participants[state.participantIndex];
            row = state.schedule.FirstOrDefault(candidate =>
                candidate.participant == participant && candidate.block == state.selectedBlock);
        }
        if (row != null && state.routeCatalog != null && state.routeCatalog.TryGetRoute(row.route, out _))
        {
            sceneConfiguror.SetUpRouteByName(row.route);
        }
    }

    private static string GetEstimationBlockKey(StudyScheduleRow row)
    {
        return row.participant + ":" + row.block.ToString(CultureInfo.InvariantCulture);
    }

    public bool HasStartedEstimation(StudyScheduleRow row)
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

    public bool TryRecoverSelectedCompletedBlock()
    {
        if (state.participants.Count == 0)
        {
            return false;
        }

        string participant = state.participants[state.participantIndex];
        StudyScheduleRow row = state.schedule.FirstOrDefault(candidate =>
            candidate.participant == participant && candidate.block == state.selectedBlock);
        if (row == null)
        {
            state.statusMessage = $"No loaded schedule row for {participant} block {state.selectedBlock}.";
            Debug.LogError("[StudyManager] " + state.statusMessage);
            return false;
        }

        bool pendingMatchesSelection = state.lastEndedRow != null &&
                                       StudyRehearsalTiming.IsEstimationSelectionMatch(
                                           participant,
                                           state.selectedBlock,
                                           state.lastEndedRow.participant,
                                           state.lastEndedRow.block);
        if (pendingMatchesSelection && !string.IsNullOrEmpty(state.lastEndedDirectory) &&
            Directory.Exists(state.lastEndedDirectory))
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
                participant + " block " + state.selectedBlock.ToString(CultureInfo.InvariantCulture) +
                ":" + Environment.NewLine + diagnostic);
            if (!recovered)
            {
                state.statusMessage = "Stored block recovery rejected; see the console diagnostic.";
            }
        }
        if (!recovered)
        {
            return false;
        }

        int recoveredParticipantIndex = state.participants.IndexOf(row.participant);
        if (recoveredParticipantIndex < 0)
        {
            throw new InvalidOperationException(
                "Recovered participant is missing from the loaded schedule: " + row.participant + ".");
        }
        state.lastEndedRow = row;
        state.lastEndedDirectory = directory;
        state.lastEndedParticipantIndex = recoveredParticipantIndex;
        state.statusMessage = $"Recovered {row.participant} block {row.block} for estimation.";
        Debug.Log("[StudyManager] " + state.statusMessage + " Directory: " + directory);
        return true;
    }

    public void ClearPendingEstimation()
    {
        state.lastEndedRow = null;
        state.lastEndedDirectory = null;
        state.lastEndedParticipantIndex = 0;
    }
}
