using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Runs the once-per-participant familiarization sequence (spec 06): a timed B phase
/// followed by a timed C phase on the catalog's practice problem, before any block starts.
/// </summary>
public sealed class PracticeController
{
    private const float ReleasePracticePhaseSeconds = 60f;

    private readonly StudySessionState state;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ActionRecorder actionRecorder;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudyControlPanel panel;
    private readonly EstimationController estimation;
    private readonly Action ensureScheduleLoadedForRuntime;
    private readonly Action ensureEstimationCatalogLoadedForRuntime;
    private readonly Func<float> debugPracticePhaseSeconds;

    private float practicePhaseStartRealtime;

    public PracticeController(
        StudySessionState state,
        SceneConfiguror sceneConfiguror,
        ActionRecorder actionRecorder,
        BoardAlignmentController boardAlignment,
        StudyControlPanel panel,
        EstimationController estimation,
        Action ensureScheduleLoadedForRuntime,
        Action ensureEstimationCatalogLoadedForRuntime,
        Func<float> debugPracticePhaseSeconds)
    {
        this.state = state;
        this.sceneConfiguror = sceneConfiguror;
        this.actionRecorder = actionRecorder;
        this.boardAlignment = boardAlignment;
        this.panel = panel;
        this.estimation = estimation;
        this.ensureScheduleLoadedForRuntime = ensureScheduleLoadedForRuntime;
        this.ensureEstimationCatalogLoadedForRuntime = ensureEstimationCatalogLoadedForRuntime;
        this.debugPracticePhaseSeconds = debugPracticePhaseSeconds;
    }

    public bool StartPractice()
    {
        ensureScheduleLoadedForRuntime();
        ensureEstimationCatalogLoadedForRuntime();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the current sequence before starting practice.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.estimationCatalog == null)
        {
            state.statusMessage = "Practice content is unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.participants.Count == 0 || sceneConfiguror == null || actionRecorder == null)
        {
            state.statusMessage = "Practice runtime references are unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        string participant = state.participants[state.participantIndex];
        if (!CanStartPractice(participant))
        {
            state.statusMessage = "Practice is available only once, before this participant's first block starts.";
            panel.RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.IsGripFeedbackReady)
        {
            state.statusMessage = "Grip feedback is unavailable on this device.";
            panel.RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.TryValidateRoute(state.estimationCatalog.practiceProblem.id, out string error))
        {
            state.statusMessage = error;
            Debug.LogError("[StudyManager] " + error);
            panel.RefreshPanelText();
            return false;
        }

        string participantRoot = Path.Combine(Application.persistentDataPath, "study", participant);
        string directory = BlockRunController.GetUnusedDirectory(
            Path.Combine(participantRoot, "practice_block0"));
        actionRecorder.BeginBlock(directory, null);
        state.participantsWithPracticeRuns.Add(participant);
        state.practiceActive = true;
        state.panelPinned = true;
        actionRecorder.Record("PracticeStarted");
        BeginPracticePhase("B");
        panel.SetPanelVisible(false);
        panel.SetTimerChipVisible(false);
        return true;
    }

    private void BeginPracticePhase(string phase)
    {
        state.practicePhase = phase;
        actionRecorder.Record("PracticePhase", phase);
        sceneConfiguror.SetGameMode(GameMode.Basic);
        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetUpRouteByName(state.estimationCatalog.practiceProblem.id);
        sceneConfiguror.SetGameMode(phase == "B" ? GameMode.Grip : GameMode.Ghost);
        sceneConfiguror.SetStudyFeedbackVisible(true);
        practicePhaseStartRealtime = Time.realtimeSinceStartup;
        state.statusMessage = "Practice phase " + phase + " running.";
        panel.UpdatePracticeTimerText(GetPracticePhaseSeconds());
        panel.RefreshPanelText();
    }

    public void UpdatePractice()
    {
        float remaining = GetPracticePhaseSeconds() -
                          (Time.realtimeSinceStartup - practicePhaseStartRealtime);
        if (remaining > 0f)
        {
            panel.UpdatePracticeTimerText(remaining);
            return;
        }
        if (state.practicePhase == "B")
        {
            BeginPracticePhase("C");
            return;
        }
        EndPractice();
    }

    private void EndPractice()
    {
        if (!state.practiceActive)
        {
            return;
        }
        actionRecorder.Record("PracticeEnded");
        actionRecorder.EndBlock();
        state.practiceActive = false;
        state.practicePhase = string.Empty;
        estimation.RestoreScheduledDisplay(null);
        state.statusMessage = "Practice completed.";
        panel.SetTimerChipVisible(false);
        panel.ShowPanel();
        panel.RefreshPanelText();
    }

    public bool CanStartPractice(string participant)
    {
        string participantRoot = Path.Combine(Application.persistentDataPath, "study", participant);
        if (Directory.Exists(participantRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(participantRoot))
            {
                string name = Path.GetFileName(directory);
                if (name.StartsWith("practice_block0", StringComparison.Ordinal))
                {
                    state.participantsWithPracticeRuns.Add(participant);
                }
                else if (name.StartsWith("block1_", StringComparison.Ordinal) ||
                         name.StartsWith("block2_", StringComparison.Ordinal) ||
                         name.StartsWith("block3_", StringComparison.Ordinal))
                {
                    state.participantsWithBlockRuns.Add(participant);
                }
            }
        }

        return StudyRehearsalTiming.CanStartPractice(
            participant,
            state.participantsWithPracticeRuns,
            state.participantsWithBlockRuns);
    }

    private float GetPracticePhaseSeconds()
    {
        return (Debug.isDebugBuild || Application.isEditor)
            ? Mathf.Clamp(debugPracticePhaseSeconds(), 1f, ReleasePracticePhaseSeconds)
            : ReleasePracticePhaseSeconds;
    }
}
