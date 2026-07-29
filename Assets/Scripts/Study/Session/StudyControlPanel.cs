using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// The experimenter's in-headset panel: its TextMesh widgets, buttons, timer chip, and the
/// pinch ray-cast that presses them.
/// </summary>
public sealed class StudyControlPanel
{
    private readonly Transform panelParent;
    private readonly Camera userCamera;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudySessionState state;
    private readonly Func<float> panelSettleSeconds;

    private SummonGestureDetector summonGesture;
    private BlockRunController blockRun;
    private PracticeController practice;
    private EstimationController estimation;

    private GameObject panelRoot;
    private GameObject timerChipRoot;
    private TextMesh panelText;
    private TextMesh timerText;
    private Material panelMaterial;
    private Material buttonMaterial;
    private TextMesh adhocConditionLabel;
    private TextMesh adhocRouteLabel;
    private StudyPanelButton practiceButton;
    private StudyPanelButton estimationStartButton;
    private StudyPanelButton estimationNextButton;
    private string lastRoutesStatusLine;
    private string lastBoardAlignmentStatus;
    private float panelPressableAt;
    private bool leftWasPinching;
    private bool rightWasPinching;

    public StudyControlPanel(
        Transform panelParent,
        Camera userCamera,
        SceneConfiguror sceneConfiguror,
        BoardAlignmentController boardAlignment,
        StudySessionState state,
        Func<float> panelSettleSeconds)
    {
        this.panelParent = panelParent;
        this.userCamera = userCamera;
        this.sceneConfiguror = sceneConfiguror;
        this.boardAlignment = boardAlignment;
        this.state = state;
        this.panelSettleSeconds = panelSettleSeconds;
        lastBoardAlignmentStatus = boardAlignment != null ? boardAlignment.StatusMessage : string.Empty;
    }

    public bool IsPanelHidden => panelRoot != null && !panelRoot.activeSelf;

    public void AttachSummonDetector(SummonGestureDetector summonGesture)
    {
        this.summonGesture = summonGesture;
    }

    public void AttachControllers(
        BlockRunController blockRun,
        PracticeController practice,
        EstimationController estimation)
    {
        this.blockRun = blockRun;
        this.practice = practice;
        this.estimation = estimation;
    }

    public void BuildPanel()
    {
        if (panelRoot != null)
        {
            return;
        }

        panelMaterial = CreateMaterial(new Color(0.04f, 0.055f, 0.08f, 0.96f));
        buttonMaterial = CreateMaterial(new Color(0.08f, 0.35f, 0.52f, 1f));

        panelRoot = new GameObject("Study Experimenter Panel");
        panelRoot.transform.SetParent(panelParent, false);
        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "Panel Background";
        background.transform.SetParent(panelRoot.transform, false);
        background.transform.localScale = new Vector3(0.64f, 1.00f, 0.012f);
        background.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
        UnityEngine.Object.Destroy(background.GetComponent<Collider>());

        panelText = CreateText(panelRoot.transform, new Vector3(0f, 0.12f, -0.008f), 0.006f, 36);
        CreateButton("Previous Participant", new Vector3(-0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "PREV P", PreviousParticipant);
        CreateButton("Next Participant", new Vector3(0.22f, -0.08f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT P", NextParticipant);
        CreateButton("Previous Block", new Vector3(-0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "PREV BLOCK", PreviousBlock);
        CreateButton("Next Block", new Vector3(0.22f, -0.16f, -0.02f), new Vector2(0.16f, 0.065f), "NEXT BLOCK", NextBlock);
        CreateButton("Start Block", new Vector3(0f, -0.08f, -0.02f), new Vector2(0.20f, 0.065f), "START", () => blockRun.StartSelectedBlock());
        CreateButton("End Block", new Vector3(0f, -0.16f, -0.02f), new Vector2(0.20f, 0.065f), "END EARLY", blockRun.EndBlockEarly);
        adhocConditionLabel = CreateButton("Adhoc Condition", new Vector3(-0.22f, -0.24f, -0.02f), new Vector2(0.16f, 0.065f), "COND: A", CycleAdhocCondition);
        CreateButton("Adhoc Start", new Vector3(0f, -0.24f, -0.02f), new Vector2(0.20f, 0.065f), "ADHOC START", () => blockRun.StartAdhocBlock());
        adhocRouteLabel = CreateButton("Adhoc Route", new Vector3(0.22f, -0.24f, -0.02f), new Vector2(0.16f, 0.065f), "ROUTE", CycleAdhocRoute);
        CreateButton("Practice", new Vector3(-0.22f, -0.32f, -0.02f), new Vector2(0.16f, 0.06f), "PRACTICE", () => practice.StartPractice(), out practiceButton);
        CreateButton("Estimation Start", new Vector3(0f, -0.32f, -0.02f), new Vector2(0.20f, 0.06f), "EST START", () => estimation.StartEstimation(), out estimationStartButton);
        CreateButton("Estimation Next", new Vector3(0.22f, -0.32f, -0.02f), new Vector2(0.16f, 0.06f), "EST NEXT", estimation.NextEstimation, out estimationNextButton);
        CreateButton("Align Board", new Vector3(-0.18f, -0.40f, -0.02f), new Vector2(0.20f, 0.055f), "ALIGN BOARD", BeginBoardAlignment);
        CreateButton("Clear Alignment", new Vector3(0.18f, -0.40f, -0.02f), new Vector2(0.20f, 0.055f), "CLEAR ALIGN", ClearBoardAlignment);
        CreateButton("Hide Panel", new Vector3(0f, -0.47f, -0.02f), new Vector2(0.16f, 0.05f), "HIDE", () => SetPanelVisible(false));

        timerChipRoot = new GameObject("Study Timer Chip");
        timerChipRoot.transform.SetParent(panelParent, false);
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
        if (state.participants.Count == 0 || state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.participantIndex = (state.participantIndex - 1 + state.participants.Count) % state.participants.Count;
        estimation.TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void NextParticipant()
    {
        if (state.participants.Count == 0 || state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.participantIndex = (state.participantIndex + 1) % state.participants.Count;
        estimation.TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void PreviousBlock()
    {
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 1 ? 3 : state.selectedBlock - 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void CycleAdhocCondition()
    {
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.adhocConditionIndex = (state.adhocConditionIndex + 1) % StudySessionState.AdhocConditions.Length;
        RefreshPanelText();
    }

    private void CycleAdhocRoute()
    {
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        int routeCount = sceneConfiguror != null ? sceneConfiguror.GetAvailableRouteNames().Count : 0;
        if (routeCount == 0)
        {
            return;
        }
        state.adhocRouteIndex = (state.adhocRouteIndex + 1) % routeCount;
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
        state.adhocRouteIndex = Mathf.Clamp(state.adhocRouteIndex, 0, routes.Count - 1);
        return routes[state.adhocRouteIndex];
    }

    private void NextBlock()
    {
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 3 ? 1 : state.selectedBlock + 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    public void RefreshPanelText()
    {
        RefreshButtonStates();
        if (panelText == null)
        {
            return;
        }

        if (state.estimationActive)
        {
            MoonBoardEstimationProblemDefinition problem =
                state.activeEstimationProblems[state.activeEstimationOrdinal];
            panelText.text = "Estimation " +
                             state.activeEstimationSet.setIndex.ToString(CultureInfo.InvariantCulture) + " " +
                             (state.activeEstimationOrdinal + 1).ToString(CultureInfo.InvariantCulture) + "/4\n" +
                             problem.apiId.ToString(CultureInfo.InvariantCulture);
            return;
        }

        StringBuilder text = new();
        if (state.practiceActive)
        {
            text.Append("Practice phase ").Append(state.practicePhase).AppendLine();
        }
        if (state.participants.Count > 0)
        {
            string participant = state.participants[state.participantIndex];
            text.Append(participant).Append("  |  selected block ").Append(state.selectedBlock).AppendLine();
            foreach (StudyScheduleRow row in state.schedule.Where(row => row.participant == participant))
            {
                text.Append(row.block == state.selectedBlock ? "> " : "  ")
                    .Append("Block ").Append(row.block).Append(": ")
                    .Append(row.condition).Append(" / ")
                    .Append(sceneConfiguror != null ? sceneConfiguror.GetRouteDisplayName(row.route) : row.route)
                    .AppendLine();
            }
        }

        string adhocCondition = StudySessionState.AdhocConditions[state.adhocConditionIndex];
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
        text.AppendLine(state.statusMessage);
        if (!string.IsNullOrEmpty(state.supplementalContentStatus))
        {
            text.AppendLine(state.supplementalContentStatus);
        }
        if (boardAlignment != null)
        {
            text.AppendLine(boardAlignment.StatusMessage);
        }
        panelText.text = text.ToString();
    }

    private void RefreshButtonStates()
    {
        bool catalogReady = state.estimationCatalog != null;
        bool practiceAvailable = false;
        if (catalogReady && state.participants.Count > 0)
        {
            practiceAvailable = practice.CanStartPractice(state.participants[state.participantIndex]);
        }
        practiceButton?.SetInteractable(
            practiceAvailable && !state.blockRunning && !state.IsAuxiliaryActive);

        bool estimationAvailable = false;
        if (catalogReady && state.lastEndedRow != null && state.participants.Count > 0 &&
            StudyRehearsalTiming.IsEstimationSelectionMatch(
                state.participants[state.participantIndex],
                state.selectedBlock,
                state.lastEndedRow.participant,
                state.lastEndedRow.block))
        {
            estimationAvailable = !estimation.HasStartedEstimation(state.lastEndedRow);
        }
        estimationStartButton?.SetInteractable(
            estimationAvailable && !state.blockRunning && !state.IsAuxiliaryActive);
        estimationNextButton?.SetInteractable(state.estimationActive);
    }

    public void RefreshStatusLinesIfChanged()
    {
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
    }

    public void UpdateTimerText(float remainingSeconds)
    {
        if (timerText == null || state.activeRow == null)
        {
            return;
        }

        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        timerText.text = $"{state.activeRow.participant} B{state.activeRow.block}  {totalSeconds / 60:00}:{totalSeconds % 60:00}" +
                         (sceneConfiguror != null && sceneConfiguror.IsGripFeedbackDegraded
                             ? "\nGRIP CUE OFF"
                              : string.Empty);
    }

    public void UpdateTimerWaitingText()
    {
        if (timerText != null && state.activeRow != null)
        {
            timerText.text = $"{state.activeRow.participant} B{state.activeRow.block}\nWAITING FOR INTERACTION";
        }
    }

    public void UpdatePracticeTimerText(float remainingSeconds)
    {
        if (timerText == null)
        {
            return;
        }
        int totalSeconds = Mathf.CeilToInt(remainingSeconds);
        timerText.text = $"PRACTICE {state.practicePhase}  {totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    public void ShowPanel()
    {
        PositionPanelInFrontOfUser();
        SetPanelVisible(true);
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds());
        SetTimerChipVisible(ShouldShowTimerChip());
        PositionTimerChip();
        RefreshPanelText();
    }

    public void UpdateIdlePanelPosition()
    {
        // The HMD pose is not valid yet when Start() places the panel (over Link the
        // headset may not even be worn), so keep the idle panel in front of the user
        // until the experimenter first uses it.
        if (!state.panelPinned && !state.blockRunning && panelRoot != null && panelRoot.activeSelf)
        {
            PositionPanelInFrontOfUser();
        }
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

    public void PositionTimerChip()
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

    public void SetPanelVisible(bool visible)
    {
        panelRoot?.SetActive(visible);
        if (!visible)
        {
            summonGesture.ResetSummonDwell();
            SetTimerChipVisible(ShouldShowTimerChip());
        }
    }

    public void SetTimerChipVisible(bool visible)
    {
        timerChipRoot?.SetActive(visible);
    }

    private bool ShouldShowTimerChip()
    {
        return state.blockRunning && state.activeRow != null && state.activeRow.condition == "A";
    }

    public void HandlePanelInput(
        OVRHand leftHand,
        OVRSkeleton leftSkeleton,
        OVRHand rightHand,
        OVRSkeleton rightSkeleton)
    {
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
        bool summonConsumed = summonGesture.UpdateSummonGesture(
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
                state.panelPinned = true;
                button.Press();
                return;
            }
            return;
        }
    }

    private void BeginBoardAlignment()
    {
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the current block or auxiliary sequence before aligning the board.";
        }
        else if (boardAlignment == null)
        {
            state.statusMessage = "Board alignment is unavailable.";
        }
        else if (!boardAlignment.BeginCalibration(out string error))
        {
            state.statusMessage = error;
        }
        else
        {
            state.statusMessage = "Board alignment started.";
        }
        RefreshPanelText();
    }

    private void ClearBoardAlignment()
    {
        if (!state.blockRunning && !state.IsAuxiliaryActive && boardAlignment != null)
        {
            if (!boardAlignment.ClearAlignment())
            {
                state.statusMessage = boardAlignment.StatusMessage;
                RefreshPanelText();
                return;
            }
            state.statusMessage = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
    }

    public void DestroyMaterials()
    {
        if (panelMaterial != null)
        {
            UnityEngine.Object.Destroy(panelMaterial);
        }
        if (buttonMaterial != null)
        {
            UnityEngine.Object.Destroy(buttonMaterial);
        }
    }
}
