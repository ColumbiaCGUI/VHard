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

    [Header("Study References")]
    [SerializeField] private SceneConfiguror sceneConfiguror;
    [SerializeField] private ActionRecorder actionRecorder;
    [SerializeField] private Camera userCamera;

    [Header("Debug")]
    [SerializeField] private bool useMockSchedule;
    [SerializeField] private float debugBlockMinutes = 2f;

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

    public bool IsBlockRunning => blockRunning;
    public string ActiveDirectory => activeDirectory;
    public IReadOnlyList<StudyScheduleRow> Schedule => schedule;

    private IEnumerator Start()
    {
        ResolveReferences();
        string scheduleText = null;
        if (useMockSchedule)
        {
            scheduleText = BuildMockSchedule();
        }
        else
        {
            string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/study_schedule.csv";
            if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
            {
                using UnityWebRequest request = UnityWebRequest.Get(path);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    scheduleText = request.downloadHandler.text;
                }
                else
                {
                    statusMessage = "Schedule load failed: " + request.error;
                }
            }
            else if (File.Exists(path))
            {
                scheduleText = File.ReadAllText(path);
            }
            else
            {
                statusMessage = "Schedule file not found.";
            }
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
        if (!TryValidateRowRuntime(row))
        {
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
        activeRow = row;
        activeDirectory = directory;
        manifestPath = Path.Combine(activeDirectory, "session.json");
        activeManifest = new StudySessionManifest
        {
            participant = row.participant,
            block = row.block,
            condition = row.condition,
            route = row.route,
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
        };

        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        if (row.condition != "A")
        {
            sceneConfiguror.SetUpRouteByName(row.route);
        }
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
        panelPinned = true;
        blockRunning = true;
        statusMessage = adhoc
            ? $"Running adhoc {row.condition} / {row.route}."
            : $"Running {row.participant} block {row.block}.";
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
        actionRecorder.EndBlock();
        activeManifest.endUtc = DateTime.UtcNow.ToString("o");
        activeManifest.endedEarly = endedEarly;
        activeManifest.endReason = reason;
        activeManifest.droppedCaptureFrames = actionRecorder.DroppedCaptureFrames;
        activeManifest.holdAggregates = actionRecorder.GetHoldAggregates();
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
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds);
        SetTimerChipVisible(ShouldShowTimerChip());
        PositionTimerChip();
        RefreshPanelText();
    }

    private void ResolveReferences()
    {
        sceneConfiguror ??= FindAnyObjectByType<SceneConfiguror>();
        actionRecorder ??= FindAnyObjectByType<ActionRecorder>();
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
        string routesStatusLine = sceneConfiguror != null
            ? sceneConfiguror.GetRoutesLoadStatusLine()
            : "UNAVAILABLE";
        if (routesStatusLine != lastRoutesStatusLine && panelRoot != null && panelRoot.activeSelf)
        {
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
        background.transform.localScale = new Vector3(0.64f, 0.70f, 0.012f);
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
        CreateButton("Hide Panel", new Vector3(0f, -0.30f, -0.02f), new Vector2(0.16f, 0.05f), "HIDE", () => SetPanelVisible(false));

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

    private TextMesh CreateButton(
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

    private void CycleAdhocCondition()
    {
        if (blockRunning)
        {
            return;
        }
        adhocConditionIndex = (adhocConditionIndex + 1) % AdhocConditions.Length;
        RefreshPanelText();
    }

    private void CycleAdhocRoute()
    {
        if (blockRunning)
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
                    .Append(row.condition).Append(" / ").Append(row.route).AppendLine();
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
        text.AppendLine(statusMessage);
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
        if (panelRoot != null)
        {
            timerChipRoot.transform.position = panelRoot.transform.position +
                                               panelRoot.transform.up * 0.415f -
                                               panelRoot.transform.forward * 0.01f;
            timerChipRoot.transform.rotation = panelRoot.transform.rotation;
        }
        else
        {
            timerChipRoot.transform.position = cameraTransform.position + cameraTransform.forward * 0.75f +
                                               cameraTransform.up * 0.415f;
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

    private static string BuildMockSchedule()
    {
        return "participant,block,condition,route\n" +
               "P07,1,B,DEATH STAR\n" +
               "P07,2,C,SPEED\n" +
               "P07,3,A,THE CRUSH ALT\n";
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
