using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// Runtime-built experimenter console with hand-ray interaction. All study transitions are
/// explicit button actions; clocks on this panel report elapsed time only.
/// </summary>
public sealed class StudyControlPanel
{
    private const float PanelDistanceMeters = 0.72f;
    private const float PointerLengthMeters = 1.5f;
    private const float ConfirmationWindowSeconds = 4f;
    private const float TextTransformScale = 0.01f;
    private const float PostDragSettleSeconds = 0.25f;
    private const float PanelWidthMeters = 0.82f;
    private const float PanelHeightMeters = 1.02f;
    private const float PanelViewportMargin = 0.04f;
    private const float MinimumPanelDepthMeters = 0.55f;
    private const float MaximumPanelDepthMeters = 1.5f;
    private const float PanelBottomMeters = -0.51f;
    private const float PanelTopWithTimerMeters = 0.62f;
    private const int PanelClampSearchIterations = 10;
    private const string ConfirmStartBlock = "start-block";
    private const string ConfirmCompleteBlock = "complete-block";
    private const string ConfirmStartPractice = "start-practice";
    private const string ConfirmEndPractice = "end-practice";
    private const string ConfirmStartAdhoc = "start-adhoc";
    private const string ConfirmClearAlignment = "clear-alignment";

    private static readonly Color PanelColor = new(0.035f, 0.06f, 0.095f, 0.99f);
    private static readonly Color AccentColor = new(0.19f, 0.78f, 0.92f, 1f);
    private static readonly Color MutedTextColor = new(0.90f, 0.93f, 0.97f, 1f);
    private static readonly Color PointerColor = new(0.10f, 0.72f, 0.92f, 0.92f);
    private static readonly Color PointerHoverColor = new(1f, 0.68f, 0.18f, 1f);
    private static readonly Color PointerMissColor = new(0.30f, 0.42f, 0.52f, 0.65f);
    private static readonly Color SessionButtonColor = new(0.055f, 0.32f, 0.50f, 1f);
    private static readonly Color SessionHoverColor = new(0.08f, 0.62f, 0.80f, 1f);
    private static readonly Color PracticeButtonColor = new(0.27f, 0.20f, 0.55f, 1f);
    private static readonly Color PracticeHoverColor = new(0.46f, 0.34f, 0.82f, 1f);
    private static readonly Color AdhocButtonColor = new(0.055f, 0.36f, 0.36f, 1f);
    private static readonly Color AdhocHoverColor = new(0.08f, 0.62f, 0.60f, 1f);
    private static readonly Color UtilityButtonColor = new(0.17f, 0.24f, 0.33f, 1f);
    private static readonly Color UtilityHoverColor = new(0.28f, 0.42f, 0.56f, 1f);
    private static readonly Color SelectedColor = new(0.93f, 0.58f, 0.12f, 1f);

    private readonly Transform panelParent;
    private readonly Camera userCamera;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudySessionState state;
    private readonly Func<float> panelSettleSeconds;
    private readonly MaterialPropertyBlock pointerProperties = new();

    private SummonGestureDetector summonGesture;
    private BlockRunController blockRun;
    private PracticeController practice;
    private EstimationController estimation;

    private GameObject panelRoot;
    private GameObject timerChipRoot;
    private TextMeshPro panelText;
    private TextMeshPro timerText;
    private Material panelMaterial;
    private Material buttonMaterial;
    private Material pointerMaterial;
    private Material textMaterial;
    private TMP_FontAsset fontAsset;
    private int uiLayer;
    private int uiLayerMask;

    private TextMeshPro adhocConditionLabel;
    private TextMeshPro adhocRouteLabel;
    private TextMeshPro blockActionLabel;
    private TextMeshPro practiceBLabel;
    private TextMeshPro practiceCLabel;
    private TextMeshPro practiceEndLabel;
    private TextMeshPro adhocStartLabel;
    private TextMeshPro clearAlignmentLabel;

    private StudyPanelButton previousParticipantButton;
    private StudyPanelButton nextParticipantButton;
    private StudyPanelButton previousBlockButton;
    private StudyPanelButton nextBlockButton;
    private StudyPanelButton blockActionButton;
    private StudyPanelButton practiceBButton;
    private StudyPanelButton practiceCButton;
    private StudyPanelButton practiceEndButton;
    private StudyPanelButton adhocConditionButton;
    private StudyPanelButton adhocRouteButton;
    private StudyPanelButton adhocStartButton;
    private StudyPanelButton estimationStartButton;
    private StudyPanelButton estimationNextButton;
    private StudyPanelButton alignBoardButton;
    private StudyPanelButton clearAlignmentButton;
    private StudyPanelButton panelGrabHandleVisual;
    private Collider panelGrabHandleCollider;

    private GameObject leftPointerRoot;
    private GameObject rightPointerRoot;
    private LineRenderer leftPointerLine;
    private LineRenderer rightPointerLine;
    private Renderer leftPointerReticle;
    private Renderer rightPointerReticle;
    private StudyPanelButton leftHoveredButton;
    private StudyPanelButton rightHoveredButton;

    private string lastRoutesStatusLine;
    private string lastBoardAlignmentStatus;
    private string pendingConfirmationKey = string.Empty;
    private float confirmationDeadline = -1f;
    private int confirmationArmedFrame = -1;
    private int lastPanelPressFrame = -1;
    private float panelPressableAt;
    private bool leftWasPinching;
    private bool rightWasPinching;
    private bool leftPinchArmed;
    private bool rightPinchArmed;
    private int lastBlockElapsedSecond = -1;
    private int lastPracticeElapsedSecond = -1;
    private bool timerWaitingTextShown;
    private PanelGrabHand activePanelGrabHand;
    private float panelGrabRayDistance;
    private Vector3 panelGrabWorldOffset;

    private enum PanelGrabHand
    {
        None,
        Left,
        Right,
    }

    private struct PanelPointerTarget
    {
        public StudyPanelButton button;
        public bool isGrabHandle;
        public Vector3 hitPoint;
        public float hitDistance;
    }

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

        uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            throw new InvalidOperationException("The study panel requires the project UI layer.");
        }
        uiLayerMask = 1 << uiLayer;

        panelMaterial = CreateOverlayMaterial(PanelColor);
        buttonMaterial = CreateButtonMaterial();
        pointerMaterial = CreateOverlayMaterial(PointerColor);
        fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        UnityEngine.Shader textShader = UnityEngine.Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
        if (fontAsset == null || textShader == null)
        {
            throw new InvalidOperationException(
                "The study panel requires LiberationSans SDF and the TMP mobile overlay shader.");
        }
        textMaterial = new Material(fontAsset.material)
        {
            shader = textShader,
            renderQueue = 4000,
        };
        panelMaterial.renderQueue = 3000;
        buttonMaterial.renderQueue = 3050;
        pointerMaterial.renderQueue = 4100;

        panelRoot = new GameObject("Study Experimenter Console");
        panelRoot.layer = uiLayer;
        panelRoot.transform.SetParent(panelParent, false);

        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "Console Background";
        background.layer = uiLayer;
        background.transform.SetParent(panelRoot.transform, false);
        background.transform.localScale = new Vector3(PanelWidthMeters, PanelHeightMeters, 0.012f);
        background.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;

        BuildGrabHandle();

        TextMeshPro title = CreateText(
            panelRoot.transform,
            "Console Title",
            new Vector3(0f, 0.435f, -0.014f),
            new Vector2(0.76f, 0.05f),
            0.027f,
            Color.white,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        title.text = "VHARD STUDY CONSOLE";
        TextMeshPro subtitle = CreateText(
            panelRoot.transform,
            "Console Subtitle",
            new Vector3(0f, 0.392f, -0.014f),
            new Vector2(0.72f, 0.032f),
            0.012f,
            AccentColor,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        subtitle.text = "MANUAL CONTROL  |  NO AUTOMATIC TRANSITIONS";

        panelText = CreateText(
            panelRoot.transform,
            "Session Summary",
            new Vector3(0f, 0.24f, -0.014f),
            new Vector2(0.72f, 0.23f),
            0.014f,
            MutedTextColor,
            TextAlignmentOptions.Top,
            FontStyles.Normal);
        panelText.lineSpacing = 0f;

        CreateSectionLabel("SESSION", 0.105f);
        previousParticipantButton = CreateButton(
            "Previous Participant",
            new Vector3(-0.19f, 0.055f, -0.02f),
            new Vector2(0.30f, 0.055f),
            "< PARTICIPANT",
            PreviousParticipant,
            out _);
        nextParticipantButton = CreateButton(
            "Next Participant",
            new Vector3(0.19f, 0.055f, -0.02f),
            new Vector2(0.30f, 0.055f),
            "PARTICIPANT >",
            NextParticipant,
            out _);
        previousBlockButton = CreateButton(
            "Previous Block",
            new Vector3(-0.27f, -0.015f, -0.02f),
            new Vector2(0.16f, 0.055f),
            "< BLOCK",
            PreviousBlock,
            out _);
        blockActionButton = CreateButton(
            "Block Action",
            new Vector3(0f, -0.015f, -0.02f),
            new Vector2(0.30f, 0.055f),
            "START BLOCK",
            HandleBlockAction,
            out blockActionLabel);
        nextBlockButton = CreateButton(
            "Next Block",
            new Vector3(0.27f, -0.015f, -0.02f),
            new Vector2(0.16f, 0.055f),
            "BLOCK >",
            NextBlock,
            out _);
        SetPalette(
            SessionButtonColor,
            SessionHoverColor,
            previousParticipantButton,
            nextParticipantButton,
            previousBlockButton,
            blockActionButton,
            nextBlockButton);

        CreateSectionLabel("PRACTICE", -0.075f);
        practiceBButton = CreateButton(
            "Practice B",
            new Vector3(-0.25f, -0.125f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "PRACTICE B",
            HandlePracticeB,
            out practiceBLabel);
        practiceCButton = CreateButton(
            "Practice C",
            new Vector3(0f, -0.125f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "MODE C",
            HandlePracticeC,
            out practiceCLabel);
        practiceEndButton = CreateButton(
            "End Practice",
            new Vector3(0.25f, -0.125f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "END PRACTICE",
            HandleEndPractice,
            out practiceEndLabel);
        SetPalette(
            PracticeButtonColor,
            PracticeHoverColor,
            practiceBButton,
            practiceCButton,
            practiceEndButton);
        practiceEndButton.SetDanger(true);

        CreateSectionLabel("AD HOC", -0.18f);
        adhocConditionButton = CreateButton(
            "Adhoc Condition",
            new Vector3(-0.25f, -0.23f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "COND: A",
            CycleAdhocCondition,
            out adhocConditionLabel);
        adhocStartButton = CreateButton(
            "Adhoc Start",
            new Vector3(0f, -0.23f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "START AD HOC",
            HandleAdhocStart,
            out adhocStartLabel);
        adhocRouteButton = CreateButton(
            "Adhoc Route",
            new Vector3(0.25f, -0.23f, -0.02f),
            new Vector2(0.21f, 0.058f),
            "ROUTE",
            CycleAdhocRoute,
            out adhocRouteLabel);
        SetPalette(
            AdhocButtonColor,
            AdhocHoverColor,
            adhocConditionButton,
            adhocStartButton,
            adhocRouteButton);

        CreateSectionLabel("TOOLS", -0.285f);
        estimationStartButton = CreateButton(
            "Estimation Start",
            new Vector3(-0.29f, -0.335f, -0.02f),
            new Vector2(0.16f, 0.052f),
            "EST START",
            HandleEstimationStart,
            out _);
        estimationNextButton = CreateButton(
            "Estimation Next",
            new Vector3(-0.097f, -0.335f, -0.02f),
            new Vector2(0.16f, 0.052f),
            "EST NEXT",
            HandleEstimationNext,
            out _);
        alignBoardButton = CreateButton(
            "Align Board",
            new Vector3(0.097f, -0.335f, -0.02f),
            new Vector2(0.16f, 0.052f),
            "ALIGN",
            BeginBoardAlignment,
            out _);
        clearAlignmentButton = CreateButton(
            "Clear Alignment",
            new Vector3(0.29f, -0.335f, -0.02f),
            new Vector2(0.16f, 0.052f),
            "CLEAR",
            HandleClearAlignment,
            out clearAlignmentLabel);
        SetPalette(
            UtilityButtonColor,
            UtilityHoverColor,
            estimationStartButton,
            estimationNextButton,
            alignBoardButton,
            clearAlignmentButton);
        clearAlignmentButton.SetDanger(true);
        CreateButton(
            "Recenter Panel",
            new Vector3(-0.125f, -0.435f, -0.02f),
            new Vector2(0.22f, 0.05f),
            "RECENTER",
            RecenterPanel,
            out _).SetPalette(
                UtilityButtonColor,
                UtilityHoverColor,
                SelectedColor);
        CreateButton(
            "Hide Panel",
            new Vector3(0.125f, -0.435f, -0.02f),
            new Vector2(0.22f, 0.05f),
            "HIDE",
            HidePanel,
            out _).SetPalette(
                UtilityButtonColor,
                UtilityHoverColor,
                SelectedColor);

        BuildTimerChip();
        BuildPointer("Left Panel Pointer", out leftPointerRoot, out leftPointerLine, out leftPointerReticle);
        BuildPointer("Right Panel Pointer", out rightPointerRoot, out rightPointerLine, out rightPointerReticle);
        PositionPanelInFrontOfUser();
        RefreshPanelText();
    }

    private void BuildGrabHandle()
    {
        GameObject handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        handle.name = "Panel Grab Handle";
        handle.layer = uiLayer;
        handle.transform.SetParent(panelRoot.transform, false);
        handle.transform.localPosition = new Vector3(0f, 0.492f, -0.02f);
        Vector2 handleSize = new(0.24f, 0.026f);
        handle.transform.localScale = new Vector3(handleSize.x, handleSize.y, 0.018f);
        handle.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        panelGrabHandleCollider = handle.GetComponent<Collider>();
        panelGrabHandleVisual = handle.AddComponent<StudyPanelButton>();
        panelGrabHandleVisual.ConfigureSurface(handleSize);
        panelGrabHandleVisual.SetPalette(
            UtilityButtonColor,
            SessionHoverColor,
            SelectedColor);

        TextMeshPro label = CreateText(
            panelRoot.transform,
            "Panel Grab Handle Label",
            new Vector3(0f, 0.492f, -0.0305f),
            new Vector2(0.22f, 0.022f),
            0.0095f,
            MutedTextColor,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        label.text = "PINCH + DRAG";
    }

    private void BuildTimerChip()
    {
        timerChipRoot = new GameObject("Study Elapsed Chip");
        timerChipRoot.layer = uiLayer;
        timerChipRoot.transform.SetParent(panelParent, false);

        GameObject chipBackground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        chipBackground.name = "Elapsed Chip Background";
        chipBackground.layer = uiLayer;
        chipBackground.transform.SetParent(timerChipRoot.transform, false);
        Vector2 chipSize = new(0.28f, 0.09f);
        chipBackground.transform.localScale = new Vector3(chipSize.x, chipSize.y, 0.018f);
        chipBackground.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        StudyPanelButton chipButton = chipBackground.AddComponent<StudyPanelButton>();
        chipButton.ConfigureSurface(chipSize);
        chipButton.Pressed = ShowPanel;
        timerText = CreateText(
            timerChipRoot.transform,
            "Elapsed Chip Label",
            new Vector3(0f, 0f, -0.011f),
            new Vector2(0.26f, 0.08f),
            0.016f,
            Color.white,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        SetTimerChipVisible(false);
    }

    private void BuildPointer(
        string name,
        out GameObject root,
        out LineRenderer line,
        out Renderer reticleRenderer)
    {
        root = new GameObject(name);
        root.layer = uiLayer;
        root.transform.SetParent(panelParent, false);

        line = root.AddComponent<LineRenderer>();
        line.sharedMaterial = pointerMaterial;
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = 0.004f;
        line.endWidth = 0.002f;
        line.numCapVertices = 4;
        line.alignment = LineAlignment.View;

        GameObject reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        reticle.name = "Pointer Reticle";
        reticle.layer = uiLayer;
        reticle.transform.SetParent(root.transform, false);
        reticle.transform.localScale = Vector3.one * 0.014f;
        UnityEngine.Object.Destroy(reticle.GetComponent<Collider>());
        reticleRenderer = reticle.GetComponent<Renderer>();
        reticleRenderer.sharedMaterial = pointerMaterial;
        reticle.SetActive(false);
        root.SetActive(false);
    }

    private void CreateSectionLabel(string textValue, float y)
    {
        TextMeshPro label = CreateText(
            panelRoot.transform,
            textValue + " Section",
            new Vector3(-0.255f, y, -0.014f),
            new Vector2(0.22f, 0.026f),
            0.011f,
            AccentColor,
            TextAlignmentOptions.Left,
            FontStyles.Bold);
        label.text = textValue;
    }

    private StudyPanelButton CreateButton(
        string objectName,
        Vector3 localPosition,
        Vector2 size,
        string label,
        Action pressed,
        out TextMeshPro labelText)
    {
        GameObject buttonObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonObject.name = objectName;
        buttonObject.layer = uiLayer;
        buttonObject.transform.SetParent(panelRoot.transform, false);
        buttonObject.transform.localPosition = localPosition;
        buttonObject.transform.localScale = new Vector3(size.x, size.y, 0.018f);
        buttonObject.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        StudyPanelButton button = buttonObject.AddComponent<StudyPanelButton>();
        button.ConfigureSurface(size);
        button.Pressed = pressed;

        labelText = CreateText(
            panelRoot.transform,
            objectName + " Label",
            localPosition + new Vector3(0f, 0f, -0.0105f),
            new Vector2(size.x * 0.9f, size.y * 0.82f),
            0.0135f,
            Color.white,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        labelText.text = label;
        return button;
    }

    private TextMeshPro CreateText(
        Transform parent,
        string objectName,
        Vector3 localPosition,
        Vector2 worldSize,
        float worldFontSize,
        Color color,
        TextAlignmentOptions alignment,
        FontStyles style)
    {
        GameObject textObject = new(objectName);
        textObject.layer = uiLayer;
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localRotation = Quaternion.identity;
        textObject.transform.localScale = Vector3.one * TextTransformScale;
        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.font = fontAsset;
        text.fontSharedMaterial = textMaterial;
        text.rectTransform.sizeDelta = worldSize / TextTransformScale;
        text.alignment = alignment;
        text.fontSize = worldFontSize / TextTransformScale * 10f;
        text.fontStyle = style;
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.sortingOrder = 100;
        return text;
    }

    private static void SetPalette(
        Color enabled,
        Color hovered,
        params StudyPanelButton[] buttons)
    {
        foreach (StudyPanelButton button in buttons)
        {
            button?.SetPalette(enabled, hovered, SelectedColor);
        }
    }

    private static Material CreateOverlayMaterial(Color color)
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Oculus/Unlit Transparent Color") ??
                                     UnityEngine.Shader.Find("Interaction/UnlitTransparentColor");
        if (shader == null)
        {
            throw new InvalidOperationException("No always-visible unlit shader is available for the study panel.");
        }

        Material material = new(shader);
        material.SetColor("_Color", color);
        return material;
    }

    private static Material CreateButtonMaterial()
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Interaction/RoundedBoxUnlit") ??
                                     UnityEngine.Shader.Find("Oculus/Unlit Transparent Color");
        if (shader == null)
        {
            throw new InvalidOperationException("No compatible unlit shader is available for study buttons.");
        }

        Material material = new(shader);
        if (material.HasProperty("_ZTest"))
        {
            material.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        }
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", new Color(0.08f, 0.35f, 0.52f, 1f));
        }
        return material;
    }

    private void PreviousParticipant()
    {
        CancelConfirmation();
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
        CancelConfirmation();
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
        CancelConfirmation();
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 1 ? 3 : state.selectedBlock - 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void NextBlock()
    {
        CancelConfirmation();
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 3 ? 1 : state.selectedBlock + 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void HandleBlockAction()
    {
        if (state.blockRunning)
        {
            RequireConfirmation(
                ConfirmCompleteBlock,
                "Press COMPLETE BLOCK again to confirm manual completion.",
                blockRun.CompleteBlock);
            return;
        }

        RequireConfirmation(
            ConfirmStartBlock,
            "Press START BLOCK again to confirm the selected participant and block.",
            () => blockRun.StartSelectedBlock());
    }

    private void HandlePracticeB()
    {
        if (state.practiceActive)
        {
            CancelConfirmation();
            practice.SetPracticePhase("B");
            return;
        }

        RequireConfirmation(
            ConfirmStartPractice,
            "Press PRACTICE B again to begin unlimited practice.",
            () => practice.StartPractice());
    }

    private void HandlePracticeC()
    {
        CancelConfirmation();
        practice.SetPracticePhase("C");
    }

    private void HandleEndPractice()
    {
        RequireConfirmation(
            ConfirmEndPractice,
            "Press END PRACTICE again to confirm.",
            practice.EndPractice);
    }

    private void CycleAdhocCondition()
    {
        CancelConfirmation();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.adhocConditionIndex = (state.adhocConditionIndex + 1) % StudySessionState.AdhocConditions.Length;
        RefreshPanelText();
    }

    private void CycleAdhocRoute()
    {
        CancelConfirmation();
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

    private void HandleAdhocStart()
    {
        RequireConfirmation(
            ConfirmStartAdhoc,
            "Press START AD HOC again to confirm.",
            () => blockRun.StartAdhocBlock());
    }

    private void HandleEstimationStart()
    {
        CancelConfirmation();
        estimation.StartEstimation();
    }

    private void HandleEstimationNext()
    {
        CancelConfirmation();
        estimation.NextEstimation();
    }

    private void RecenterPanel()
    {
        CancelPanelGrab();
        CancelConfirmation();
        PositionPanelInFrontOfUser();
        PositionTimerChip();
        state.panelPinned = true;
        state.statusMessage = "Panel recentered.";
        RefreshPanelText();
    }

    private void HidePanel()
    {
        CancelConfirmation();
        SetPanelVisible(false);
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
            panelText.text = "ESTIMATION SET " + state.activeEstimationSet.setIndex + "\n" +
                             "PROBLEM " + (state.activeEstimationOrdinal + 1) + "/4  |  " + problem.apiId;
            return;
        }

        StringBuilder text = new();
        if (state.blockRunning && state.activeRow != null)
        {
            text.Append("RUNNING ").Append(state.activeRow.participant)
                .Append("  BLOCK ").Append(state.activeRow.block)
                .Append("  [").Append(state.activeRow.condition).Append("]  |  ")
                .Append(state.blockTimerStarted
                    ? "ELAPSED " + StudyRehearsalTiming.FormatElapsedSeconds(blockRun.ElapsedSeconds)
                    : "WAITING FOR FIRST INTERACTION")
                .AppendLine();
        }
        else if (state.practiceActive)
        {
            text.Append("PRACTICE ").Append(state.practicePhase)
                .Append("  |  ELAPSED ")
                .Append(StudyRehearsalTiming.FormatElapsedSeconds(practice.PhaseElapsedSeconds))
                .AppendLine();
        }
        else if (state.participants.Count > 0)
        {
            text.Append(state.participants[state.participantIndex])
                .Append("  |  SELECTED BLOCK ").Append(state.selectedBlock).AppendLine();
        }

        if (state.participants.Count > 0)
        {
            string participant = state.participants[state.participantIndex];
            foreach (StudyScheduleRow row in state.schedule.Where(row => row.participant == participant))
            {
                string routeName = sceneConfiguror != null
                    ? sceneConfiguror.GetRouteDisplayName(row.route)
                    : row.route;
                text.Append(row.block == state.selectedBlock ? "> " : "  ")
                    .Append("B").Append(row.block).Append("  ")
                    .Append(row.condition).Append("  ")
                    .Append(Truncate(routeName, 34))
                    .AppendLine();
            }
        }
        else
        {
            text.AppendLine("NO VALID SCHEDULE LOADED");
        }

        lastRoutesStatusLine = sceneConfiguror != null
            ? sceneConfiguror.GetRoutesLoadStatusLine()
            : "UNAVAILABLE";
        if (!lastRoutesStatusLine.StartsWith("READY", StringComparison.Ordinal))
        {
            text.Append("ROUTES: ").Append(Truncate(lastRoutesStatusLine, 56)).AppendLine();
        }
        if (sceneConfiguror != null && sceneConfiguror.IsGripFeedbackDegraded)
        {
            text.AppendLine("GRIP CUE OFF");
        }
        text.Append("STATUS: ").Append(Truncate(state.statusMessage, 58)).AppendLine();
        if (!string.IsNullOrEmpty(state.supplementalContentStatus))
        {
            text.Append(Truncate(state.supplementalContentStatus, 64)).AppendLine();
        }
        if (boardAlignment != null)
        {
            text.Append("ALIGN: ").Append(Truncate(boardAlignment.StatusMessage, 58));
        }
        panelText.text = text.ToString();
    }

    private void RefreshButtonStates()
    {
        bool hasParticipant = state.participants.Count > 0;
        bool idle = !state.blockRunning && !state.IsAuxiliaryActive;
        previousParticipantButton?.SetInteractable(idle && hasParticipant);
        nextParticipantButton?.SetInteractable(idle && hasParticipant);
        previousBlockButton?.SetInteractable(idle && hasParticipant);
        nextBlockButton?.SetInteractable(idle && hasParticipant);

        string blockConfirmation = state.blockRunning ? ConfirmCompleteBlock : ConfirmStartBlock;
        blockActionButton?.SetInteractable(state.blockRunning || (idle && hasParticipant));
        blockActionButton?.SetDanger(state.blockRunning);
        blockActionButton?.SetSelected(pendingConfirmationKey == blockConfirmation);
        if (blockActionLabel != null)
        {
            blockActionLabel.text = pendingConfirmationKey == blockConfirmation
                ? "CONFIRM"
                : state.blockRunning ? "COMPLETE BLOCK" : "START BLOCK";
        }

        bool practiceAvailable = false;
        if (practice != null && state.estimationCatalog != null && hasParticipant)
        {
            practiceAvailable = practice.CanStartPractice(state.participants[state.participantIndex]);
        }
        practiceBButton?.SetInteractable(state.practiceActive || (idle && practiceAvailable));
        practiceCButton?.SetInteractable(state.practiceActive);
        practiceEndButton?.SetInteractable(state.practiceActive);
        practiceBButton?.SetSelected(state.practiceActive && state.practicePhase == "B" ||
                                     pendingConfirmationKey == ConfirmStartPractice);
        practiceCButton?.SetSelected(state.practiceActive && state.practicePhase == "C");
        practiceEndButton?.SetSelected(pendingConfirmationKey == ConfirmEndPractice);
        if (practiceBLabel != null)
        {
            practiceBLabel.text = pendingConfirmationKey == ConfirmStartPractice
                ? "CONFIRM B"
                : state.practiceActive ? "MODE B" : "PRACTICE B";
        }
        if (practiceCLabel != null)
        {
            practiceCLabel.text = "MODE C";
        }
        if (practiceEndLabel != null)
        {
            practiceEndLabel.text = pendingConfirmationKey == ConfirmEndPractice
                ? "CONFIRM END"
                : "END PRACTICE";
        }

        bool hasAdhocRoute = sceneConfiguror != null && sceneConfiguror.GetAvailableRouteNames().Count > 0;
        adhocConditionButton?.SetInteractable(idle);
        adhocRouteButton?.SetInteractable(idle && hasAdhocRoute);
        adhocStartButton?.SetInteractable(idle && hasAdhocRoute);
        adhocStartButton?.SetSelected(pendingConfirmationKey == ConfirmStartAdhoc);
        if (adhocStartLabel != null)
        {
            adhocStartLabel.text = pendingConfirmationKey == ConfirmStartAdhoc
                ? "CONFIRM"
                : "START AD HOC";
        }
        if (adhocConditionLabel != null)
        {
            adhocConditionLabel.text = "COND: " + StudySessionState.AdhocConditions[state.adhocConditionIndex];
        }
        if (adhocRouteLabel != null)
        {
            string routeName = GetAdhocRouteName();
            adhocRouteLabel.text = string.IsNullOrEmpty(routeName) ? "NO ROUTES" : Truncate(routeName, 12);
        }

        bool estimationAvailable = false;
        if (estimation != null && state.estimationCatalog != null && state.lastEndedRow != null && hasParticipant &&
            StudyRehearsalTiming.IsEstimationSelectionMatch(
                state.participants[state.participantIndex],
                state.selectedBlock,
                state.lastEndedRow.participant,
                state.lastEndedRow.block))
        {
            estimationAvailable = !estimation.HasStartedEstimation(state.lastEndedRow);
        }
        estimationStartButton?.SetInteractable(estimationAvailable && idle);
        estimationNextButton?.SetInteractable(state.estimationActive);
        alignBoardButton?.SetInteractable(idle && boardAlignment != null);
        clearAlignmentButton?.SetInteractable(idle && boardAlignment != null);
        clearAlignmentButton?.SetSelected(pendingConfirmationKey == ConfirmClearAlignment);
        if (clearAlignmentLabel != null)
        {
            clearAlignmentLabel.text = pendingConfirmationKey == ConfirmClearAlignment ? "CONFIRM" : "CLEAR";
        }
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

    public void UpdateBlockElapsedText(float elapsedSeconds)
    {
        if (timerText == null || state.activeRow == null)
        {
            return;
        }

        int elapsedSecond = Mathf.FloorToInt(elapsedSeconds);
        if (elapsedSecond == lastBlockElapsedSecond && !timerWaitingTextShown)
        {
            return;
        }

        lastBlockElapsedSecond = elapsedSecond;
        timerWaitingTextShown = false;
        timerText.text = state.activeRow.participant + " B" + state.activeRow.block + "\nELAPSED " +
                         StudyRehearsalTiming.FormatElapsedSeconds(elapsedSeconds) +
                         (sceneConfiguror != null && sceneConfiguror.IsGripFeedbackDegraded
                             ? "\nGRIP CUE OFF"
                             : string.Empty);
        if (panelRoot != null && panelRoot.activeSelf)
        {
            RefreshPanelText();
        }
    }

    public void UpdateTimerWaitingText()
    {
        if (timerText == null || state.activeRow == null || timerWaitingTextShown)
        {
            return;
        }

        timerWaitingTextShown = true;
        timerText.text = state.activeRow.participant + " B" + state.activeRow.block +
                         "\nWAITING FOR INTERACTION";
    }

    public void ResetBlockTimerDisplay()
    {
        lastBlockElapsedSecond = -1;
        timerWaitingTextShown = false;
    }

    public void UpdatePracticeElapsedText(float elapsedSeconds)
    {
        int elapsedSecond = Mathf.FloorToInt(elapsedSeconds);
        if (elapsedSecond == lastPracticeElapsedSecond)
        {
            return;
        }

        lastPracticeElapsedSecond = elapsedSecond;
        if (panelRoot != null && panelRoot.activeSelf)
        {
            RefreshPanelText();
        }
    }

    public void ShowPanel()
    {
        CancelPanelGrab();
        CancelConfirmation();
        PositionPanelInFrontOfUser();
        SetPanelVisible(true);
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds());
        leftWasPinching = false;
        rightWasPinching = false;
        leftPinchArmed = false;
        rightPinchArmed = false;
        SetTimerChipVisible(ShouldShowTimerChip());
        PositionTimerChip();
        RefreshPanelText();
    }

    public void UpdateIdlePanelPosition()
    {
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
        panelRoot.transform.position = cameraTransform.position + cameraTransform.forward * PanelDistanceMeters;
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
                                               panelRoot.transform.up * 0.575f -
                                               panelRoot.transform.forward * 0.01f;
            timerChipRoot.transform.rotation = panelRoot.transform.rotation;
        }
        else
        {
            timerChipRoot.transform.position = cameraTransform.position +
                                               cameraTransform.forward * PanelDistanceMeters +
                                               cameraTransform.up * 0.575f;
            timerChipRoot.transform.rotation = Quaternion.LookRotation(
                timerChipRoot.transform.position - cameraTransform.position,
                cameraTransform.up);
        }
    }

    public void SetPanelVisible(bool visible)
    {
        panelRoot?.SetActive(visible);
        SetGameplayInputSuppressed(visible);
        if (!visible)
        {
            CancelPanelGrab();
            CancelConfirmation();
            ResetPointerAndHover();
            summonGesture?.ResetSummonDwell();
            SetTimerChipVisible(ShouldShowTimerChip());
        }
    }

    public void SetGameplayInputSuppressed(bool suppressed)
    {
        sceneConfiguror?.SetPanelInputSuppressed(suppressed);
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
        ExpireConfirmationIfNeeded();
        PanelPointerTarget leftTarget = UpdatePointer(
            leftHand,
            leftPointerRoot,
            leftPointerLine,
            leftPointerReticle);
        PanelPointerTarget rightTarget = UpdatePointer(
            rightHand,
            rightPointerRoot,
            rightPointerLine,
            rightPointerReticle);
        UpdateHoveredButtons(leftTarget.button, rightTarget.button);
        panelGrabHandleVisual?.SetHovered(
            activePanelGrabHand == PanelGrabHand.None &&
            (leftTarget.isGrabHandle || rightTarget.isGrabHandle));
        panelGrabHandleVisual?.SetSelected(activePanelGrabHand != PanelGrabHand.None);

        HandleHandInput(
            leftHand,
            leftSkeleton,
            ref leftWasPinching,
            ref leftPinchArmed,
            true,
            leftTarget);
        HandleHandInput(
            rightHand,
            rightSkeleton,
            ref rightWasPinching,
            ref rightPinchArmed,
            false,
            rightTarget);
    }

    private void HandleHandInput(
        OVRHand hand,
        OVRSkeleton skeleton,
        ref bool wasPinching,
        ref bool pinchArmed,
        bool isLeft,
        PanelPointerTarget target)
    {
        bool trackingConfident = hand != null && hand.IsTracked && hand.IsDataHighConfidence;
        bool pinching = trackingConfident && hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool pinchStarted = StudyRehearsalTiming.TryConsumeArmedPinch(
            trackingConfident,
            pinching,
            ref wasPinching,
            ref pinchArmed);
        PanelGrabHand handSide = isLeft ? PanelGrabHand.Left : PanelGrabHand.Right;
        if (activePanelGrabHand != PanelGrabHand.None)
        {
            if (activePanelGrabHand == handSide)
            {
                bool pointerValid = trackingConfident && hand.IsPointerPoseValid && hand.PointerPose != null;
                if (!pinching || !pointerValid)
                {
                    EndPanelGrab(ref pinchArmed);
                }
                else
                {
                    UpdatePanelGrab(hand);
                }
            }
            return;
        }

        bool summonConsumed = summonGesture != null && summonGesture.UpdateSummonGesture(
            hand,
            skeleton,
            pinching,
            pinchStarted,
            isLeft);
        if (!pinchStarted || summonConsumed ||
            Time.unscaledTime < panelPressableAt ||
            lastPanelPressFrame == Time.frameCount)
        {
            return;
        }

        if (target.isGrabHandle)
        {
            BeginPanelGrab(hand, handSide, target);
            return;
        }

        StudyPanelButton button = target.button;
        if (button == null || !button.gameObject.activeInHierarchy)
        {
            return;
        }

        if (button.Press())
        {
            lastPanelPressFrame = Time.frameCount;
            state.panelPinned = true;
        }
    }

    private PanelPointerTarget UpdatePointer(
        OVRHand hand,
        GameObject pointerRoot,
        LineRenderer line,
        Renderer reticle)
    {
        bool uiVisible = panelRoot != null && panelRoot.activeSelf ||
                         timerChipRoot != null && timerChipRoot.activeSelf;
        bool pointerValid = uiVisible && hand != null && hand.IsTracked && hand.IsDataHighConfidence &&
                            hand.IsPointerPoseValid && hand.PointerPose != null;
        if (!pointerValid)
        {
            pointerRoot?.SetActive(false);
            return default;
        }

        Vector3 origin = hand.PointerPose.position;
        Vector3 direction = hand.PointerPose.forward;
        bool hitUi = Physics.Raycast(
            origin,
            direction,
            out RaycastHit hit,
            5f,
            uiLayerMask,
            QueryTriggerInteraction.Ignore);
        PanelPointerTarget target = default;
        if (hitUi)
        {
            target.isGrabHandle = hit.collider == panelGrabHandleCollider;
            target.button = target.isGrabHandle
                ? null
                : hit.collider.GetComponentInParent<StudyPanelButton>();
            target.hitPoint = hit.point;
            target.hitDistance = hit.distance;
        }
        Vector3 end = hitUi ? hit.point : origin + direction * PointerLengthMeters;

        pointerRoot.SetActive(true);
        line.SetPosition(0, origin);
        line.SetPosition(1, end);
        reticle.gameObject.SetActive(hitUi);
        if (hitUi)
        {
            reticle.transform.position = hit.point - direction * 0.003f;
        }

        Color color = target.isGrabHandle || target.button != null && target.button.Interactable
            ? PointerHoverColor
            : hitUi ? PointerColor : PointerMissColor;
        SetRendererColor(line, color);
        SetRendererColor(reticle, color);
        return target;
    }

    private void BeginPanelGrab(OVRHand hand, PanelGrabHand handSide, PanelPointerTarget target)
    {
        if (hand == null || hand.PointerPose == null || panelRoot == null)
        {
            return;
        }

        Vector3 origin = hand.PointerPose.position;
        Vector3 direction = hand.PointerPose.forward;
        panelGrabRayDistance = Mathf.Clamp(target.hitDistance, 0.25f, 2f);
        Vector3 rayPoint = origin + direction.normalized * panelGrabRayDistance;
        panelGrabWorldOffset = panelRoot.transform.position - rayPoint;
        activePanelGrabHand = handSide;
        state.panelPinned = true;
        CancelConfirmation();
        panelGrabHandleVisual?.SetSelected(true);
    }

    private void UpdatePanelGrab(OVRHand hand)
    {
        if (hand == null || hand.PointerPose == null || panelRoot == null)
        {
            return;
        }

        Vector3 candidatePosition = StudyRehearsalTiming.ResolvePanelDragPosition(
            hand.PointerPose.position,
            hand.PointerPose.forward,
            panelGrabRayDistance,
            panelGrabWorldOffset);
        panelRoot.transform.position = ClampPanelPositionToViewport(candidatePosition);
        if (userCamera != null)
        {
            panelRoot.transform.rotation = GetPanelFacingRotation(panelRoot.transform.position);
        }
        PositionTimerChip();
    }

    private Vector3 ClampPanelPositionToViewport(Vector3 position)
    {
        if (userCamera == null)
        {
            return position;
        }

        Vector3 clampedViewportPosition = StudyRehearsalTiming.ClampPanelViewportPosition(
            userCamera.WorldToViewportPoint(position),
            Vector2.zero,
            PanelViewportMargin,
            MinimumPanelDepthMeters,
            MaximumPanelDepthMeters);
        float safeDepth = FindSafeCenteredPanelDepth(clampedViewportPosition.z);
        Vector3 centeredViewportPosition = new(0.5f, 0.5f, safeDepth);
        Vector3 centeredPosition = userCamera.ViewportToWorldPoint(centeredViewportPosition);
        clampedViewportPosition.z = safeDepth;
        Vector3 candidatePosition = userCamera.ViewportToWorldPoint(clampedViewportPosition);
        if (IsPanelPoseInsideViewport(candidatePosition))
        {
            return candidatePosition;
        }

        Vector3 bestPosition = centeredPosition;
        float safeFraction = 0f;
        float unsafeFraction = 1f;
        for (int i = 0; i < PanelClampSearchIterations; i++)
        {
            float fraction = (safeFraction + unsafeFraction) * 0.5f;
            Vector3 searchedViewportPosition = Vector3.Lerp(
                centeredViewportPosition,
                clampedViewportPosition,
                fraction);
            Vector3 searchedPosition = userCamera.ViewportToWorldPoint(searchedViewportPosition);
            if (IsPanelPoseInsideViewport(searchedPosition))
            {
                safeFraction = fraction;
                bestPosition = searchedPosition;
            }
            else
            {
                unsafeFraction = fraction;
            }
        }
        return bestPosition;
    }

    private float FindSafeCenteredPanelDepth(float requestedDepth)
    {
        Vector3 centeredViewportPosition = new(0.5f, 0.5f, requestedDepth);
        if (IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
        {
            return requestedDepth;
        }

        centeredViewportPosition.z = MaximumPanelDepthMeters;
        if (!IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
        {
            return MaximumPanelDepthMeters;
        }

        float unsafeDepth = requestedDepth;
        float safeDepth = MaximumPanelDepthMeters;
        for (int i = 0; i < PanelClampSearchIterations; i++)
        {
            float depth = (unsafeDepth + safeDepth) * 0.5f;
            centeredViewportPosition.z = depth;
            if (IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
            {
                safeDepth = depth;
            }
            else
            {
                unsafeDepth = depth;
            }
        }
        return safeDepth;
    }

    private bool IsPanelPoseInsideViewport(Vector3 position)
    {
        GetPanelViewportBounds(
            position,
            GetPanelFacingRotation(position),
            out float minimumX,
            out float maximumX,
            out float minimumY,
            out float maximumY);
        return minimumX >= PanelViewportMargin && maximumX <= 1f - PanelViewportMargin &&
               minimumY >= PanelViewportMargin && maximumY <= 1f - PanelViewportMargin;
    }

    private Quaternion GetPanelFacingRotation(Vector3 position)
    {
        Vector3 awayFromUser = position - userCamera.transform.position;
        return Quaternion.LookRotation(awayFromUser, userCamera.transform.up);
    }

    private void GetPanelViewportBounds(
        Vector3 position,
        Quaternion rotation,
        out float minimumX,
        out float maximumX,
        out float minimumY,
        out float maximumY)
    {
        minimumX = float.PositiveInfinity;
        maximumX = float.NegativeInfinity;
        minimumY = float.PositiveInfinity;
        maximumY = float.NegativeInfinity;
        if (userCamera.stereoEnabled)
        {
            AccumulatePanelViewportBounds(
                position,
                rotation,
                Camera.MonoOrStereoscopicEye.Left,
                ref minimumX,
                ref maximumX,
                ref minimumY,
                ref maximumY);
            AccumulatePanelViewportBounds(
                position,
                rotation,
                Camera.MonoOrStereoscopicEye.Right,
                ref minimumX,
                ref maximumX,
                ref minimumY,
                ref maximumY);
            return;
        }

        AccumulatePanelViewportBounds(
            position,
            rotation,
            Camera.MonoOrStereoscopicEye.Mono,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
    }

    private void AccumulatePanelViewportBounds(
        Vector3 position,
        Quaternion rotation,
        Camera.MonoOrStereoscopicEye eye,
        ref float minimumX,
        ref float maximumX,
        ref float minimumY,
        ref float maximumY)
    {
        AccumulatePanelViewportCorner(
            position,
            rotation,
            -PanelWidthMeters * 0.5f,
            PanelBottomMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            PanelWidthMeters * 0.5f,
            PanelBottomMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            -PanelWidthMeters * 0.5f,
            PanelTopWithTimerMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            PanelWidthMeters * 0.5f,
            PanelTopWithTimerMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
    }

    private void AccumulatePanelViewportCorner(
        Vector3 position,
        Quaternion rotation,
        float localX,
        float localY,
        Camera.MonoOrStereoscopicEye eye,
        ref float minimumX,
        ref float maximumX,
        ref float minimumY,
        ref float maximumY)
    {
        Vector3 worldCorner = position + rotation * new Vector3(localX, localY, 0f);
        Vector3 viewportCorner = userCamera.WorldToViewportPoint(worldCorner, eye);
        minimumX = Mathf.Min(minimumX, viewportCorner.x);
        maximumX = Mathf.Max(maximumX, viewportCorner.x);
        minimumY = Mathf.Min(minimumY, viewportCorner.y);
        maximumY = Mathf.Max(maximumY, viewportCorner.y);
    }

    private void EndPanelGrab(ref bool pinchArmed)
    {
        activePanelGrabHand = PanelGrabHand.None;
        pinchArmed = false;
        panelPressableAt = Time.unscaledTime + PostDragSettleSeconds;
        panelGrabHandleVisual?.SetSelected(false);
        state.statusMessage = "Panel moved.";
        RefreshPanelText();
    }

    private void CancelPanelGrab()
    {
        activePanelGrabHand = PanelGrabHand.None;
        leftPinchArmed = false;
        rightPinchArmed = false;
        panelGrabHandleVisual?.SetSelected(false);
        panelGrabHandleVisual?.SetHovered(false);
    }

    private void UpdateHoveredButtons(StudyPanelButton leftTarget, StudyPanelButton rightTarget)
    {
        if (leftHoveredButton != null && leftHoveredButton != leftTarget && leftHoveredButton != rightTarget)
        {
            leftHoveredButton.SetHovered(false);
        }
        if (rightHoveredButton != null && rightHoveredButton != leftTarget && rightHoveredButton != rightTarget)
        {
            rightHoveredButton.SetHovered(false);
        }

        leftHoveredButton = leftTarget;
        rightHoveredButton = rightTarget;
        leftHoveredButton?.SetHovered(true);
        rightHoveredButton?.SetHovered(true);
    }

    private void SetRendererColor(Renderer renderer, Color color)
    {
        if (renderer == null)
        {
            return;
        }

        renderer.GetPropertyBlock(pointerProperties);
        pointerProperties.SetColor("_Color", color);
        pointerProperties.SetColor("_BaseColor", color);
        renderer.SetPropertyBlock(pointerProperties);
    }

    private void ResetPointerAndHover()
    {
        leftHoveredButton?.SetHovered(false);
        if (rightHoveredButton != leftHoveredButton)
        {
            rightHoveredButton?.SetHovered(false);
        }
        leftHoveredButton = null;
        rightHoveredButton = null;
        panelGrabHandleVisual?.SetHovered(false);
        leftPointerRoot?.SetActive(false);
        rightPointerRoot?.SetActive(false);
    }

    private void RequireConfirmation(string key, string prompt, Action confirmedAction)
    {
        bool confirmed = StudyRehearsalTiming.TryConfirmPanelAction(
            key,
            Time.unscaledTime,
            Time.frameCount,
            ConfirmationWindowSeconds,
            ref pendingConfirmationKey,
            ref confirmationDeadline,
            ref confirmationArmedFrame);
        if (confirmed)
        {
            confirmedAction();
            return;
        }

        state.statusMessage = prompt + " Confirmation expires in 4 seconds.";
        RefreshPanelText();
    }

    private void CancelConfirmation()
    {
        pendingConfirmationKey = string.Empty;
        confirmationDeadline = -1f;
        confirmationArmedFrame = -1;
    }

    private void ExpireConfirmationIfNeeded()
    {
        if (string.IsNullOrEmpty(pendingConfirmationKey) || Time.unscaledTime <= confirmationDeadline)
        {
            return;
        }

        CancelConfirmation();
        state.statusMessage = "Confirmation expired; no action was taken.";
        RefreshPanelText();
    }

    private void BeginBoardAlignment()
    {
        CancelConfirmation();
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

    private void HandleClearAlignment()
    {
        RequireConfirmation(
            ConfirmClearAlignment,
            "Press CLEAR again to remove the saved board alignment.",
            ClearBoardAlignment);
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

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value ?? string.Empty;
        }
        return value.Substring(0, maximumLength - 2) + "..";
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
        if (pointerMaterial != null)
        {
            UnityEngine.Object.Destroy(pointerMaterial);
        }
        if (textMaterial != null)
        {
            UnityEngine.Object.Destroy(textMaterial);
        }
    }
}
