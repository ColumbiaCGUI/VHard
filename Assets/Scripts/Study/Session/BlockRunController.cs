using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Owns the block lifecycle: validating a schedule row, creating the block directory and
/// manifest, tracking rehearsal elapsed time, and finalizing the manifest on an explicit end.
/// </summary>
public sealed class BlockRunController
{
    private const string NullRoutesHashSentinel = "__NULL_ROUTES_JSON_SHA256__";

    private readonly StudySessionState state;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ActionRecorder actionRecorder;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudyControlPanel panel;
    private readonly HeadsetPresenceTracker headsetPresence;
    private readonly EstimationController estimation;
    private readonly Action ensureScheduleLoadedForRuntime;

    private StudySessionManifest activeManifest;
    private string manifestPath;
    private MoonBoardRouteDefinition activeRouteDefinition;
    private float blockStartRealtime;
    private string retryConfirmationKey;
    private float retryConfirmationDeadline;

    public float ElapsedSeconds => state.blockTimerStarted
        ? Mathf.Max(0f, Time.realtimeSinceStartup - blockStartRealtime)
        : 0f;

    public BlockRunController(
        StudySessionState state,
        SceneConfiguror sceneConfiguror,
        ActionRecorder actionRecorder,
        BoardAlignmentController boardAlignment,
        StudyControlPanel panel,
        HeadsetPresenceTracker headsetPresence,
        EstimationController estimation,
        Action ensureScheduleLoadedForRuntime)
    {
        this.state = state;
        this.sceneConfiguror = sceneConfiguror;
        this.actionRecorder = actionRecorder;
        this.boardAlignment = boardAlignment;
        this.panel = panel;
        this.headsetPresence = headsetPresence;
        this.estimation = estimation;
        this.ensureScheduleLoadedForRuntime = ensureScheduleLoadedForRuntime;
    }

    public bool StartSelectedBlock()
    {
        ensureScheduleLoadedForRuntime();
        if (state.participants.Count == 0)
        {
            state.statusMessage = "No valid schedule loaded.";
            panel.RefreshPanelText();
            return false;
        }
        return StartBlock(state.participants[state.participantIndex], state.selectedBlock);
    }

    public bool StartBlock(string participant, int block)
    {
        ensureScheduleLoadedForRuntime();
        if (state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the practice or estimation sequence first.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.blockRunning)
        {
            state.statusMessage = "End the current block first.";
            panel.RefreshPanelText();
            return false;
        }
        StudyScheduleRow row = state.schedule.FirstOrDefault(candidate =>
            candidate.participant == participant && candidate.block == block);
        if (row == null)
        {
            state.statusMessage = $"No schedule row for {participant} block {block}.";
            panel.RefreshPanelText();
            return false;
        }
        if (!TryValidateRowRuntime(row))
        {
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.TryGetRouteDefinition(row.route, out MoonBoardRouteDefinition routeDefinition))
        {
            state.statusMessage = "Authoritative route record is unavailable: " + row.route + ".";
            panel.RefreshPanelText();
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
                state.statusMessage = "Block data exists. Press Start again to create a retry.";
                panel.RefreshPanelText();
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
        if (state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the practice or estimation sequence first.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.blockRunning)
        {
            state.statusMessage = "End the current block first.";
            panel.RefreshPanelText();
            return false;
        }
        List<string> routes = sceneConfiguror != null
            ? sceneConfiguror.GetAvailableRouteNames()
            : new List<string>();
        if (routes.Count == 0)
        {
            state.statusMessage = "No routes are available.";
            panel.RefreshPanelText();
            return false;
        }

        state.adhocRouteIndex = Mathf.Clamp(state.adhocRouteIndex, 0, routes.Count - 1);
        StudyScheduleRow row = new()
        {
            participant = "ADHOC",
            block = 0,
            condition = StudySessionState.AdhocConditions[state.adhocConditionIndex],
            route = routes[state.adhocRouteIndex],
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
            state.statusMessage = "Study runtime references are unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (row.condition != "A" && !sceneConfiguror.IsGripFeedbackReady)
        {
            state.statusMessage = "Grip feedback is unavailable on this device.";
            panel.RefreshPanelText();
            return false;
        }
        bool routeReady = row.condition == "A"
            ? sceneConfiguror.TrySelectBaselineRoute(row.route, out string routeError)
            : sceneConfiguror.TryValidateRoute(row.route, out routeError);
        if (!routeReady)
        {
            state.statusMessage = routeError;
            panel.RefreshPanelText();
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
            state.participantsWithBlockRuns.Add(row.participant);
        }
        state.activeRow = row;
        state.activeDirectory = directory;
        manifestPath = Path.Combine(state.activeDirectory, "session.json");
        activeManifest = new StudySessionManifest
        {
            participant = row.participant,
            block = row.block,
            condition = row.condition,
            route = row.route,
            routeName = activeRouteDefinition != null ? activeRouteDefinition.name : row.route,
            routeSourceProblemId = activeRouteDefinition != null ? activeRouteDefinition.sourceProblemId : string.Empty,
            routeCatalogSha256 = state.routeCatalogSha256,
            boardSetup = state.routeCatalog != null ? state.routeCatalog.setupName : string.Empty,
            boardOverhangAngleDegrees = state.routeCatalog != null ? state.routeCatalog.overhangAngleDegrees : 0,
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
        actionRecorder.BeginBlock(state.activeDirectory, activeManifest);
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

        state.blockTimerStarted = false;
        panel.ResetBlockTimerDisplay();
        state.panelPinned = true;
        state.blockRunning = true;
        headsetPresence.InitializeBlockHeadsetWear();
        if (row.condition == "A")
        {
            StartRehearsalClock("BaselineStart", false);
        }
        state.statusMessage = row.condition == "A"
            ? adhoc
                ? $"Running adhoc {row.condition} / {row.route}."
                : $"Running {row.participant} block {row.block}."
            : $"Waiting for {row.condition} first interaction; rehearsal clock is stopped.";
        if (row.condition == "A")
        {
            panel.ShowPanel();
            panel.SetTimerChipVisible(true);
        }
        else
        {
            panel.SetPanelVisible(false);
        }
        panel.RefreshPanelText();
    }

    public void EndBlockEarly()
    {
        EndBlock(true, "completed_early");
    }

    public void CompleteBlock()
    {
        EndBlock(false, "completed_manual");
    }

    public void EndBlock(bool endedEarly, string reason)
    {
        if (!state.blockRunning)
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
        headsetPresence.FinalizeBlockHeadsetWear();
        actionRecorder.EndBlock();
        activeManifest.endUtc = DateTime.UtcNow.ToString("o");
        activeManifest.endedEarly = endedEarly;
        activeManifest.endReason = reason;
        activeManifest.droppedCaptureFrames = actionRecorder.DroppedCaptureFrames;
        activeManifest.holdAggregates = actionRecorder.GetHoldAggregates();
        activeManifest.boardAlignmentEnd = boardAlignment != null ? boardAlignment.GetSnapshot() : null;
        WriteManifest();

        state.blockRunning = false;
        state.blockTimerStarted = false;
        if (activeManifest != null && !activeManifest.adhoc && state.activeRow != null &&
            state.activeRow.block >= 1 && state.activeRow.block <= 3)
        {
            int endedParticipantIndex = state.participants.IndexOf(state.activeRow.participant);
            if (endedParticipantIndex < 0)
            {
                throw new InvalidOperationException(
                    "Ended participant is missing from the loaded schedule: " + state.activeRow.participant + ".");
            }
            state.lastEndedRow = state.activeRow;
            state.lastEndedDirectory = state.activeDirectory;
            state.lastEndedParticipantIndex = endedParticipantIndex;
        }
        else
        {
            estimation.ClearPendingEstimation();
        }
        state.statusMessage = $"Ended {state.activeRow.participant} block {state.activeRow.block}: {reason}.";
        panel.SetTimerChipVisible(false);
        panel.ShowPanel();
        panel.RefreshPanelText();
    }

    /// <summary>
    /// Advances the running block one frame. Elapsed time is informational only; this method
    /// never changes or ends the active condition.
    /// </summary>
    public void UpdateRunningBlock()
    {
        HandleGripFeedbackDegradation();
        if (!state.blockTimerStarted)
        {
            bool ghostDetached = sceneConfiguror != null &&
                                 sceneConfiguror.ghostHoldController != null &&
                                 sceneConfiguror.ghostHoldController.CurrentGhost != null;
            if (StudyRehearsalTiming.TryGetFirstInteraction(
                    state.activeRow.condition,
                    sceneConfiguror != null && sceneConfiguror.isGripLocomotionActive,
                    ghostDetached,
                    out string interaction))
            {
                if (!headsetPresence.BlockHeadsetDonnedRecorded)
                {
                    headsetPresence.InferHeadsetDonnedFromInteraction(interaction);
                }
                StartRehearsalClock(interaction, true);
            }
            else
            {
                panel.UpdateTimerWaitingText();
                panel.PositionTimerChip();
            }
        }
        if (state.blockTimerStarted)
        {
            panel.UpdateBlockElapsedText(ElapsedSeconds);
            panel.PositionTimerChip();
        }
    }

    private void StartRehearsalClock(string trigger, bool recordFirstInteraction)
    {
        if (!state.blockRunning || state.blockTimerStarted || state.activeRow == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        float latency = recordFirstInteraction
            ? Mathf.Max(0f, now - headsetPresence.DonningStartRealtime)
            : 0f;
        if (recordFirstInteraction)
        {
            actionRecorder.Record(
                "FirstInteraction",
                state.activeRow.condition,
                null,
                trigger + ";donningLatencySeconds=" + latency.ToString("F3", CultureInfo.InvariantCulture));
        }
        blockStartRealtime = now;
        state.blockTimerStarted = true;
        actionRecorder.Record(
            "RehearsalClockStarted",
            state.activeRow.condition,
            null,
            "block=" + state.activeRow.block.ToString(CultureInfo.InvariantCulture) + ";trigger=" + trigger);
        state.statusMessage = $"Running {state.activeRow.participant} block {state.activeRow.block}.";
        panel.UpdateBlockElapsedText(0f);
        panel.RefreshPanelText();
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
        state.statusMessage = "Grip feedback degraded; block continues.";
        WriteManifest();
        panel.RefreshPanelText();
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

    public static string GetUnusedDirectory(string requestedDirectory)
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
}
