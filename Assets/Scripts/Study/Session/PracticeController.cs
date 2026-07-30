using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Runs the once-per-participant familiarization sequence on the catalog's practice problem.
/// The experimenter explicitly selects B or C and explicitly ends the sequence.
/// </summary>
public sealed class PracticeController
{
    private readonly StudySessionState state;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ActionRecorder actionRecorder;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudyControlPanel panel;
    private readonly EstimationController estimation;
    private readonly Action ensureScheduleLoadedForRuntime;
    private readonly Action ensureEstimationCatalogLoadedForRuntime;

    private float practicePhaseStartRealtime;

    public float PhaseElapsedSeconds => state.practiceActive
        ? Mathf.Max(0f, Time.realtimeSinceStartup - practicePhaseStartRealtime)
        : 0f;

    public PracticeController(
        StudySessionState state,
        SceneConfiguror sceneConfiguror,
        ActionRecorder actionRecorder,
        BoardAlignmentController boardAlignment,
        StudyControlPanel panel,
        EstimationController estimation,
        Action ensureScheduleLoadedForRuntime,
        Action ensureEstimationCatalogLoadedForRuntime)
    {
        this.state = state;
        this.sceneConfiguror = sceneConfiguror;
        this.actionRecorder = actionRecorder;
        this.boardAlignment = boardAlignment;
        this.panel = panel;
        this.estimation = estimation;
        this.ensureScheduleLoadedForRuntime = ensureScheduleLoadedForRuntime;
        this.ensureEstimationCatalogLoadedForRuntime = ensureEstimationCatalogLoadedForRuntime;
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

    public bool SetPracticePhase(string phase)
    {
        if (phase != "B" && phase != "C")
        {
            throw new ArgumentException("Practice phase must be B or C.", nameof(phase));
        }
        if (!state.practiceActive)
        {
            state.statusMessage = "Start practice B before selecting another practice mode.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.practicePhase == phase)
        {
            state.statusMessage = "Practice phase " + phase + " remains active.";
            panel.RefreshPanelText();
            return true;
        }

        BeginPracticePhase(phase);
        panel.SetPanelVisible(false);
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
        state.statusMessage = "Practice phase " + phase + " running until manually changed or ended.";
        panel.RefreshPanelText();
    }

    public void UpdatePractice()
    {
        panel.UpdatePracticeElapsedText(PhaseElapsedSeconds);
    }

    public void EndPractice()
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
        state.statusMessage = "Practice completed manually.";
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

}
