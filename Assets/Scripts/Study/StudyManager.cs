using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Meta updates OVRHand at -90 and OVRSkeleton at -80; panel input reads the refreshed poses next.
// Guarded summons suppress gameplay during their pre-pinch dwell, before XRI sees the final pinch.
[DefaultExecutionOrder(-70)]
public sealed class StudyManager : MonoBehaviour
{
    [Header("Study References")]
    [SerializeField] private SceneConfiguror sceneConfiguror;
    [SerializeField] private ActionRecorder actionRecorder;
    [SerializeField] private Camera userCamera;
    [SerializeField] private MoonBoard2016Layout boardLayout;
    [SerializeField] private BoardAlignmentController boardAlignment;

    [Header("Debug")]
    [SerializeField] private bool useMockSchedule;

    [Header("Panel Interaction")]
    [SerializeField] private float summonDwellSeconds = 1f;
    [SerializeField] private float summonCooldownSeconds = 2f;
    [SerializeField] private float panelSettleSeconds = 0.75f;

    private readonly StudySessionState state = new();
    private StudyControlPanel controlPanel;
    private SummonGestureDetector summonGesture;
    private HeadsetPresenceTracker headsetPresence;
    private BlockRunController blockRun;
    private PracticeController practice;
    private EstimationController estimation;
    private bool estimationCatalogLoadAttempted;
    private OVRHand leftHand;
    private OVRHand rightHand;
    private OVRSkeleton leftSkeleton;
    private OVRSkeleton rightSkeleton;
    private bool shutdownStarted;

    public bool IsBlockRunning => state.blockRunning;
    public bool IsPracticeActive => state.practiceActive;
    public bool IsEstimationActive => state.estimationActive;
    public bool IsRehearsalClockRunning => state.blockRunning && state.blockTimerStarted;
    public string ActiveDirectory => state.activeDirectory;
    public IReadOnlyList<StudyScheduleRow> Schedule => state.schedule;

    private IEnumerator Start()
    {
        ResolveReferences();
        string catalogText = null;
        yield return LoadStreamingAssetText("moonboard_2016_40.json", text => catalogText = text);
        if (!LoadCatalogText(catalogText))
        {
            controlPanel.BuildPanel();
            ShowPanel();
            yield break;
        }

        controlPanel.BuildPanel();
        while (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Waiting for board calibration or spatial-anchor loading.";
            controlPanel.RefreshPanelText();
            yield return null;
        }

        ManualRunRecoveryOutcome recoveryOutcome;
        try
        {
            recoveryOutcome = blockRun.TryRecoverManualRun();
        }
        catch (Exception exception)
        {
            state.manualRunRecoveryBlocked = true;
            state.statusMessage = "Previous manual run could not be recovered. See the Unity log.";
            Debug.LogException(exception, this);
            ShowPanel();
            yield break;
        }
        if (recoveryOutcome == ManualRunRecoveryOutcome.Resumed)
        {
            yield break;
        }
        string recoveryStatus = recoveryOutcome == ManualRunRecoveryOutcome.Expired
            ? state.statusMessage
            : null;

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
        if (!string.IsNullOrEmpty(recoveryStatus))
        {
            state.statusMessage = recoveryStatus;
        }
        ShowPanel();
        sceneConfiguror?.SetGameMode(GameMode.Basic);
    }

    public bool LoadScheduleText(string csv)
    {
        state.schedule.Clear();
        state.participants.Clear();
        estimation.ClearPendingEstimation();
        if (!StudySchedule.TryParse(csv, out List<StudyScheduleRow> parsed, out string error))
        {
            state.statusMessage = error;
            controlPanel.RefreshPanelText();
            return false;
        }
        if (!StudySchedule.TryValidateRoutes(parsed, state.routeCatalog, out error))
        {
            state.statusMessage = error;
            controlPanel.RefreshPanelText();
            return false;
        }

        state.schedule.AddRange(parsed);
        state.participants.AddRange(state.schedule.Select(row => row.participant).Distinct());
        state.participantIndex = Mathf.Clamp(state.participantIndex, 0, state.participants.Count - 1);
        state.statusMessage = "Schedule loaded.";
        estimation.TryRecoverSelectedCompletedBlock();
        controlPanel.RefreshPanelText();
        return true;
    }

    public bool StartSelectedBlock()
    {
        return blockRun.StartSelectedBlock();
    }

    public bool StartBlock(string participant, int block)
    {
        return blockRun.StartBlock(participant, block);
    }

    public bool StartManualRun()
    {
        return blockRun.StartManualRun();
    }

    public void EndBlockEarly()
    {
        blockRun.EndBlockEarly();
    }

    public void CompleteBlock()
    {
        blockRun.CompleteBlock();
    }

    public void EndBlock(bool endedEarly, string reason)
    {
        blockRun.EndBlock(endedEarly, reason);
    }

    public bool StartPractice()
    {
        return practice.StartPractice();
    }

    public bool SetPracticePhase(string phase)
    {
        return practice.SetPracticePhase(phase);
    }

    public void EndPractice()
    {
        practice.EndPractice();
    }

    public bool StartEstimation()
    {
        return estimation.StartEstimation();
    }

    public void NextEstimation()
    {
        estimation.NextEstimation();
    }

    public void ShowPanel()
    {
        controlPanel.ShowPanel();
    }

    private void ResolveReferences()
    {
        sceneConfiguror ??= FindAnyObjectByType<SceneConfiguror>();
        actionRecorder ??= FindAnyObjectByType<ActionRecorder>();
        boardLayout ??= FindAnyObjectByType<MoonBoard2016Layout>();
        boardAlignment ??= FindAnyObjectByType<BoardAlignmentController>();
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
        CreateSessionModules();
    }

    private void CreateSessionModules()
    {
        controlPanel = new StudyControlPanel(
            transform,
            userCamera,
            sceneConfiguror,
            boardAlignment,
            state,
            () => panelSettleSeconds);
        summonGesture = new SummonGestureDetector(
            state,
            controlPanel,
            () => summonDwellSeconds,
            () => summonCooldownSeconds);
        controlPanel.AttachSummonDetector(summonGesture);
        headsetPresence = new HeadsetPresenceTracker(state, actionRecorder);
        estimation = new EstimationController(
            state,
            sceneConfiguror,
            actionRecorder,
            boardAlignment,
            controlPanel,
            EnsureScheduleLoadedForRuntime,
            EnsureEstimationCatalogLoadedForRuntime);
        practice = new PracticeController(
            state,
            sceneConfiguror,
            actionRecorder,
            boardAlignment,
            controlPanel,
            estimation,
            EnsureScheduleLoadedForRuntime,
            EnsureEstimationCatalogLoadedForRuntime);
        blockRun = new BlockRunController(
            state,
            sceneConfiguror,
            actionRecorder,
            boardAlignment,
            controlPanel,
            headsetPresence,
            estimation,
            EnsureScheduleLoadedForRuntime);
        controlPanel.AttachControllers(blockRun, practice, estimation);
    }

    private void EnsureScheduleLoadedForRuntime()
    {
        if (state.routeCatalog == null)
        {
            string catalogPath = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/moonboard_2016_40.json";
            if (!catalogPath.Contains("://") && File.Exists(catalogPath))
            {
                LoadCatalogText(File.ReadAllText(catalogPath));
            }
        }
        if (state.schedule.Count > 0)
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
        if (state.routeCatalog == null)
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
        headsetPresence.UpdateHeadsetPresence();
        controlPanel.RefreshStatusLinesIfChanged();
        if (state.practiceActive)
        {
            practice.UpdatePractice();
        }
        controlPanel.HandlePanelInput(leftHand, leftSkeleton, rightHand, rightSkeleton);
        controlPanel.UpdateIdlePanelPosition();
#if UNITY_EDITOR
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.mKey.wasPressedThisFrame)
        {
            ShowPanel();
        }
#endif
    }

    private void LateUpdate()
    {
        if (state.blockRunning && blockRun.TryExpireRunningBlock())
        {
            return;
        }
        if (state.blockRunning)
        {
            blockRun.UpdateRunningBlock();
        }
    }

    private string BuildMockSchedule()
    {
        if (state.routeCatalog == null || state.routeCatalog.routes == null ||
            state.routeCatalog.routes.Length != 3)
        {
            return string.Empty;
        }
        return "participant,block,condition,route\n" +
               "P07,1,B," + state.routeCatalog.routes[0].id + "\n" +
               "P07,2,C," + state.routeCatalog.routes[1].id + "\n" +
               "P07,3,A," + state.routeCatalog.routes[2].id + "\n";
    }

    private bool LoadCatalogText(string json)
    {
        string catalogSha256 = MoonBoardStudyCatalog.ComputeSha256(json);
        if (catalogSha256 != MoonBoardStudyCatalog.ApprovedCatalogSha256)
        {
            state.statusMessage = "MoonBoard catalog does not match the approved study content.";
            return false;
        }
        if (!MoonBoardStudyCatalog.TryParse(json, out MoonBoardStudyCatalog parsed, out string error))
        {
            state.statusMessage = error;
            return false;
        }
        if (boardLayout == null || !boardLayout.ApplyCatalog(parsed, out error))
        {
            state.statusMessage = boardLayout == null ? "MoonBoard metric layout is unavailable." : error;
            return false;
        }
        if (sceneConfiguror == null || !sceneConfiguror.SetRouteCatalog(parsed, out error))
        {
            state.statusMessage = sceneConfiguror == null ? "Scene configurator is unavailable." : error;
            return false;
        }

        state.routeCatalog = parsed;
        state.routeCatalogSha256 = catalogSha256;
        boardAlignment?.SetCatalog(parsed);
        return true;
    }

    private bool LoadEstimationCatalogText(string json)
    {
        state.routeCatalog?.ClearSupplementalRoutes();
        state.estimationCatalog = null;
        if (!MoonBoardEstimationCatalog.TryParseApproved(
                json,
                state.routeCatalog,
                out MoonBoardEstimationCatalog parsed,
                out string error))
        {
            SetSupplementalContentUnavailable(error);
            return false;
        }
        if (!state.routeCatalog.TrySetSupplementalRoutes(parsed.GetSupplementalRoutes(), out error))
        {
            SetSupplementalContentUnavailable(error);
            return false;
        }

        state.estimationCatalog = parsed;
        state.supplementalContentStatus = string.Empty;
        controlPanel.RefreshPanelText();
        return true;
    }

    private void SetSupplementalContentUnavailable(string error)
    {
        state.supplementalContentStatus = "Supplemental content unavailable.";
        Debug.LogError("[StudyManager] Supplemental content unavailable: " + error);
        controlPanel.RefreshPanelText();
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
                state.statusMessage = fileName + " load failed: " + request.error;
            }
            yield break;
        }
        if (File.Exists(path))
        {
            loaded(File.ReadAllText(path));
        }
        else if (updateStatusOnFailure)
        {
            state.statusMessage = fileName + " not found.";
        }
    }

    private void OnApplicationQuit()
    {
        ShutdownRuntime(true);
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && state.blockRunning)
        {
            blockRun?.CheckpointRecordingProgress();
        }
    }

    private void OnDestroy()
    {
        ShutdownRuntime(false);
    }

    private void ShutdownRuntime(bool applicationQuitting)
    {
        if (shutdownStarted)
        {
            return;
        }
        shutdownStarted = true;
        try
        {
            if (applicationQuitting)
            {
                actionRecorder?.RecordApplicationQuit();
            }
            if (state.blockRunning)
            {
                if (blockRun == null || !blockRun.TryExpireRunningBlock())
                {
                    EndBlock(true, "app_closed");
                }
            }
            else if (state.IsAuxiliaryActive)
            {
                actionRecorder?.EndBlock();
            }
            controlPanel?.DestroyMaterials();
        }
        catch
        {
            shutdownStarted = false;
            throw;
        }
    }
}
