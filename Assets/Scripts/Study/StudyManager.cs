using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class StudyManager : MonoBehaviour
{
    private const float ReleaseBlockMinutes = 20f;
    private const float PanelSummonArmSeconds = 0.75f;

    [Header("Study References")]
    [SerializeField] private SceneConfiguror sceneConfiguror;
    [SerializeField] private ActionRecorder actionRecorder;
    [SerializeField] private Camera userCamera;
    [SerializeField] private MoonBoard2016Layout boardLayout;
    [SerializeField] private BoardAlignmentController boardAlignment;

    [Header("Debug")]
    [SerializeField] private bool useMockSchedule;
    [SerializeField] private float debugBlockMinutes = 2f;

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
    private float leftPalmUpSince = -1f;
    private bool blockRunning;
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
    private MoonBoardStudyCatalog routeCatalog;
    private string routeCatalogSha256 = string.Empty;
    private string lastBoardAlignmentStatus = string.Empty;

    public bool IsBlockRunning => blockRunning;
    public string ActiveDirectory => activeDirectory;
    public IReadOnlyList<StudyScheduleRow> Schedule => schedule;

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
        if (sceneConfiguror == null || actionRecorder == null)
        {
            statusMessage = "Study runtime references are unavailable.";
            RefreshPanelText();
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
        Directory.CreateDirectory(requestedDirectory);
        activeRow = row;
        activeDirectory = requestedDirectory;
        manifestPath = Path.Combine(activeDirectory, "session.json");
        activeManifest = new StudySessionManifest
        {
            participant = row.participant,
            block = row.block,
            condition = row.condition,
            route = row.route,
            routeName = routeDefinition.name,
            routeSourceProblemId = routeDefinition.sourceProblemId,
            routeCatalogSha256 = routeCatalogSha256,
            boardSetup = routeCatalog.setupName,
            boardOverhangAngleDegrees = routeCatalog.overhangAngleDegrees,
            routeDefinition = routeDefinition,
            boardAlignment = boardAlignment != null ? boardAlignment.GetSnapshot() : null,
            boardAlignmentEnd = null,
            retry = retry,
            appVersion = Application.version,
            gitRevision = StudyBuildRevision.Current,
            startUtc = string.Empty,
            endUtc = string.Empty,
            endedEarly = false,
            endReason = "running",
        };

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
        activeManifest.startUtc = DateTime.UtcNow.ToString("o");
        actionRecorder.BeginBlock(activeDirectory, activeManifest);
        WriteManifest();

        blockDurationSeconds = GetBlockMinutes() * 60f;
        blockStartRealtime = Time.realtimeSinceStartup;
        blockRunning = true;
        statusMessage = $"Running {row.participant} block {row.block}.";
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
        return true;
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
        actionRecorder.EndBlock();
        activeManifest.endUtc = DateTime.UtcNow.ToString("o");
        activeManifest.endedEarly = endedEarly;
        activeManifest.endReason = reason;
        activeManifest.droppedCaptureFrames = actionRecorder.DroppedCaptureFrames;
        activeManifest.holdAggregates = actionRecorder.GetHoldAggregates();
        activeManifest.boardAlignmentEnd = boardAlignment != null ? boardAlignment.GetSnapshot() : null;
        WriteManifest();

        blockRunning = false;
        statusMessage = $"Ended {activeRow.participant} block {activeRow.block}: {reason}.";
        SetTimerChipVisible(false);
        ShowPanel();
        RefreshPanelText();
    }

    public void ShowPanel()
    {
        PositionPanelInFrontOfUser();
        SetPanelVisible(true);
        SetTimerChipVisible(blockRunning);
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

    private void Update()
    {
        if (boardAlignment != null && boardAlignment.StatusMessage != lastBoardAlignmentStatus)
        {
            lastBoardAlignmentStatus = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
        if (blockRunning)
        {
            if (activeRow.condition != "A" && !sceneConfiguror.IsGripFeedbackReady)
            {
                EndBlock(true, "grip_feedback_failed");
                statusMessage = "Block stopped: grip feedback failed.";
                RefreshPanelText();
                return;
            }
            float remaining = blockDurationSeconds - (Time.realtimeSinceStartup - blockStartRealtime);
            if (remaining <= 0f)
            {
                EndBlock(false, "timer_expired");
                return;
            }
            UpdateTimerText(remaining);
            PositionTimerChip();
        }

        HandlePanelInput(leftHand, leftSkeleton, ref leftWasPinching, true);
        HandlePanelInput(rightHand, rightSkeleton, ref rightWasPinching, false);
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
        bool summonArmed = false;
        if (isLeft)
        {
            bool palmUp = IsPalmUp(skeleton);
            if (!pinching && palmUp && leftPalmUpSince < 0f)
            {
                leftPalmUpSince = Time.unscaledTime;
            }
            else if (!palmUp)
            {
                leftPalmUpSince = -1f;
            }
            summonArmed = palmUp && leftPalmUpSince >= 0f &&
                          Time.unscaledTime - leftPalmUpSince >= PanelSummonArmSeconds;
            if (pinchStarted)
            {
                leftPalmUpSince = -1f;
            }
        }
        if (!pinchStarted)
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
                button.Press();
                return;
            }
            return;
        }

        if (summonArmed && panelRoot != null && !panelRoot.activeSelf)
        {
            ShowPanel();
        }
    }

    private static bool IsPalmUp(OVRSkeleton skeleton)
    {
        if (skeleton == null || skeleton.Bones.Count == 0)
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
        background.transform.localScale = new Vector3(0.64f, 0.58f, 0.012f);
        background.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
        Destroy(background.GetComponent<Collider>());

        panelText = CreateText(panelRoot.transform, new Vector3(0f, 0.12f, -0.008f), 0.0055f, 34);
        CreateButton("Previous Participant", new Vector3(-0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "PREV P", PreviousParticipant);
        CreateButton("Next Participant", new Vector3(0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT P", NextParticipant);
        CreateButton("Previous Block", new Vector3(-0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "PREV BLOCK", PreviousBlock);
        CreateButton("Next Block", new Vector3(0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT BLOCK", NextBlock);
        CreateButton("Start Block", new Vector3(0f, -0.08f, -0.02f), new Vector2(0.20f, 0.065f), "START", () => StartSelectedBlock());
        CreateButton("End Block", new Vector3(0f, -0.16f, -0.02f), new Vector2(0.20f, 0.065f), "END EARLY", EndBlockEarly);
        CreateButton("Align Board", new Vector3(-0.18f, -0.24f, -0.02f), new Vector2(0.20f, 0.055f), "ALIGN BOARD", BeginBoardAlignment);
        CreateButton("Clear Alignment", new Vector3(0.18f, -0.24f, -0.02f), new Vector2(0.20f, 0.055f), "CLEAR ALIGN", ClearBoardAlignment);
        CreateButton("Hide Panel", new Vector3(0f, -0.31f, -0.02f), new Vector2(0.16f, 0.05f), "HIDE", () => SetPanelVisible(false));

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
    }

    private void CreateButton(
        string objectName,
        Vector3 localPosition,
        Vector2 size,
        string label,
        Action pressed)
    {
        GameObject buttonObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonObject.name = objectName;
        buttonObject.transform.SetParent(panelRoot.transform, false);
        buttonObject.transform.localPosition = localPosition;
        buttonObject.transform.localScale = new Vector3(size.x, size.y, 0.02f);
        buttonObject.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        StudyPanelButton button = buttonObject.AddComponent<StudyPanelButton>();
        button.Pressed = pressed;
        CreateText(buttonObject.transform, new Vector3(0f, 0f, -0.56f), 0.055f, 26).text = label;
    }

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
        if (participants.Count == 0 || blockRunning)
        {
            return;
        }
        participantIndex = (participantIndex - 1 + participants.Count) % participants.Count;
        RefreshPanelText();
    }

    private void NextParticipant()
    {
        if (participants.Count == 0 || blockRunning)
        {
            return;
        }
        participantIndex = (participantIndex + 1) % participants.Count;
        RefreshPanelText();
    }

    private void PreviousBlock()
    {
        if (!blockRunning)
        {
            selectedBlock = selectedBlock == 1 ? 3 : selectedBlock - 1;
            RefreshPanelText();
        }
    }

    private void NextBlock()
    {
        if (!blockRunning)
        {
            selectedBlock = selectedBlock == 3 ? 1 : selectedBlock + 1;
            RefreshPanelText();
        }
    }

    private void RefreshPanelText()
    {
        if (panelText == null)
        {
            return;
        }

        StringBuilder text = new();
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
        text.AppendLine(statusMessage);
        if (boardAlignment != null)
        {
            text.AppendLine(boardAlignment.StatusMessage);
        }
        panelText.text = text.ToString();
    }

    private void UpdateTimerText(float remainingSeconds)
    {
        if (timerText == null || activeRow == null)
        {
            return;
        }

        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        timerText.text = $"{activeRow.participant} B{activeRow.block}  {totalSeconds / 60:00}:{totalSeconds % 60:00}";
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
        timerChipRoot.transform.position = cameraTransform.position + cameraTransform.forward * 0.65f +
                                           cameraTransform.right * 0.24f - cameraTransform.up * 0.20f;
        timerChipRoot.transform.rotation = Quaternion.LookRotation(
            timerChipRoot.transform.position - cameraTransform.position,
            cameraTransform.up);
    }

    private void SetPanelVisible(bool visible)
    {
        panelRoot?.SetActive(visible);
        if (!visible)
        {
            SetTimerChipVisible(false);
        }
    }

    private void SetTimerChipVisible(bool visible)
    {
        timerChipRoot?.SetActive(visible);
    }

    private float GetBlockMinutes()
    {
        return (Debug.isDebugBuild || Application.isEditor)
            ? Mathf.Clamp(debugBlockMinutes, 0.1f, ReleaseBlockMinutes)
            : ReleaseBlockMinutes;
    }

    private void WriteManifest()
    {
        if (activeManifest == null || string.IsNullOrEmpty(manifestPath))
        {
            return;
        }

        string temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonUtility.ToJson(activeManifest, true));
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

    private IEnumerator LoadStreamingAssetText(string fileName, Action<string> loaded)
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
            else
            {
                statusMessage = fileName + " load failed: " + request.error;
            }
            yield break;
        }
        if (File.Exists(path))
        {
            loaded(File.ReadAllText(path));
        }
        else
        {
            statusMessage = fileName + " not found.";
        }
    }

    private void BeginBoardAlignment()
    {
        if (blockRunning)
        {
            statusMessage = "End the current block before aligning the board.";
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
        if (!blockRunning && boardAlignment != null)
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
