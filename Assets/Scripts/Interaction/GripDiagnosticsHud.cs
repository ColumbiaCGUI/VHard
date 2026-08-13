using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>World-space panel that shows, per hand and per finger, the joint flexion the tracker
/// is reporting (MCP/PIP/DIP, and MCP/FPL on the thumb), the fingertip distance to the hold, and
/// the engagement verdict — including the clause that is short whenever a grip is refused. Built
/// entirely at runtime, collider-free, on the UI layer, and driven from the same coordinator call
/// in both Condition B and Condition C so the two stay identical.</summary>
public sealed class GripDiagnosticsHud : MonoBehaviour
{
    public const string RootName = "Grip Diagnostics";

    private const float StaleReportSeconds = 0.25f;
    private const float MeasurementGraceSeconds = 0.4f;
    private const float PanelWidthMeters = 0.62f;
    private const float PanelHeightMeters = 0.26f;
    private const float TextTransformScale = 0.02f;
    private const string MonospaceTag = "<mspace=0.52em>";

    private static readonly Color PanelColor = new(0.05f, 0.07f, 0.09f, 0.86f);
    private static readonly Color TitleColor = new(0.45f, 0.82f, 1f, 1f);
    private static readonly Color BodyColor = new(0.92f, 0.94f, 0.96f, 1f);

    private readonly GripHandDiagnostics leftDiagnostics = new("LEFT");
    private readonly GripHandDiagnostics rightDiagnostics = new("RIGHT");
    private readonly float[] jointSamples = new float[FingerCurlEstimator.MaximumJointsPerFinger];
    private readonly StringBuilder textBuilder = new(1024);

    private SceneConfiguror configuror;
    private GripInteractionCoordinator coordinator;
    private GripEngagementSettings settings;
    private GameObject panelRoot;
    private TextMeshPro leftText;
    private TextMeshPro rightText;
    private Material panelMaterial;
    private Material textMaterial;
    private Mesh quadMesh;
    private float leftReportedAt = float.NegativeInfinity;
    private float rightReportedAt = float.NegativeInfinity;
    private float leftMeasuredAt = float.NegativeInfinity;
    private float rightMeasuredAt = float.NegativeInfinity;
    private float lastActivityAt = float.NegativeInfinity;
    private float lastRefreshAt = float.NegativeInfinity;
    private bool panelVisible;

    public bool IsPanelVisible => panelVisible;

    public void Bind(
        SceneConfiguror owner,
        GripInteractionCoordinator gripCoordinator,
        GripEngagementSettings gripSettings)
    {
        configuror = owner != null
            ? owner
            : throw new ArgumentNullException(nameof(owner));
        coordinator = gripCoordinator ?? throw new ArgumentNullException(nameof(gripCoordinator));
        settings = gripSettings != null
            ? gripSettings
            : throw new ArgumentNullException(nameof(gripSettings));
        if (configuror.centerEyeAnchor == null)
        {
            throw new InvalidOperationException(
                "The grip diagnostics panel needs the centre eye anchor to sit in front of the participant.");
        }
    }

    /// <summary>Contact is only measured on the frames a GPU readback lands, so a verdict of
    /// "no measurement yet" holds the previous finger evidence for a moment rather than blanking
    /// the panel between epochs. It is reported plainly once measurements really do stop.</summary>
    public void ReportHand(
        Hand hand,
        GripLatchPhase phase,
        GripEngagementBlock block,
        in GripAcquisitionMasks masks,
        in GripAcquisitionCriteria criteria,
        int countedFingers,
        int requiredFingers)
    {
        float now = Time.unscaledTime;
        GripHandDiagnostics diagnostics = hand == Hand.Left ? leftDiagnostics : rightDiagnostics;
        float measuredAt = hand == Hand.Left ? leftMeasuredAt : rightMeasuredAt;
        bool holdingLastMeasurement = block == GripEngagementBlock.NoContactSample &&
                                      now - measuredAt <= MeasurementGraceSeconds;
        diagnostics.Phase = phase;
        diagnostics.Criteria = criteria;
        if (!holdingLastMeasurement)
        {
            diagnostics.Block = block;
            diagnostics.Masks = masks;
            diagnostics.CountedFingers = countedFingers;
            diagnostics.RequiredFingers = requiredFingers;
        }

        if (hand == Hand.Left)
        {
            leftReportedAt = now;
            leftMeasuredAt = block == GripEngagementBlock.NoContactSample ? leftMeasuredAt : now;
        }
        else
        {
            rightReportedAt = now;
            rightMeasuredAt = block == GripEngagementBlock.NoContactSample ? rightMeasuredAt : now;
        }
    }

    private void LateUpdate()
    {
        if (configuror == null || settings == null)
        {
            return;
        }
        if (!settings.showDiagnosticsPanel)
        {
            SetPanelVisible(false);
            return;
        }

        bool examining = (configuror.gameMode is GameMode.Grip or GameMode.Ghost) &&
                         !configuror.IsPanelInputSuppressed;
        if (!examining)
        {
            SetPanelVisible(false);
            return;
        }

        float now = Time.unscaledTime;
        SampleHand(Hand.Left, leftDiagnostics, leftReportedAt, now);
        SampleHand(Hand.Right, rightDiagnostics, rightReportedAt, now);
        if (IsHandActive(leftDiagnostics) || IsHandActive(rightDiagnostics))
        {
            lastActivityAt = now;
        }

        bool wanted = settings.alwaysShowDiagnosticsPanel ||
                      now - lastActivityAt <= settings.diagnosticsLingerSeconds;
        SetPanelVisible(wanted);
        if (!wanted)
        {
            return;
        }

        FollowHead();
        if (now - lastRefreshAt < settings.diagnosticsRefreshSeconds)
        {
            return;
        }
        lastRefreshAt = now;
        Render(leftText, leftDiagnostics);
        Render(rightText, rightDiagnostics);
    }

    private void SampleHand(
        Hand hand,
        GripHandDiagnostics diagnostics,
        float reportedAt,
        float now)
    {
        bool trackingValid = hand == Hand.Left
            ? coordinator.LeftTrackingValid
            : coordinator.RightTrackingValid;
        GameObject hold = hand == Hand.Left
            ? configuror.leftHandInteractingClimbingHold
            : configuror.rightHandInteractingClimbingHold;
        IReadOnlyList<float> curls = hand == Hand.Left
            ? configuror.LeftFingerCurls
            : configuror.RightFingerCurls;
        IReadOnlyList<Quaternion> rotations = hand == Hand.Left
            ? configuror.leftHandBoneQuaternions
            : configuror.rightHandBoneQuaternions;
        float[] distances = hand == Hand.Left
            ? configuror.leftHandBoneToHoldMinDistances
            : configuror.rightHandBoneToHoldMinDistances;

        diagnostics.TrackingValid = trackingValid;
        diagnostics.HoldLabel = DescribeHold(hold);
        bool hasJoints = rotations != null && rotations.Count >= FingerCurlEstimator.RequiredBoneCount;
        for (int finger = 0; finger < FingerCurlEstimator.FingerCount; finger++)
        {
            int jointCount = hasJoints
                ? FingerCurlEstimator.SampleJointDegrees(rotations, finger, jointSamples)
                : 0;
            int tipBone = GripEngagementGate.GetFingertipBoneIndex(finger);
            diagnostics.SetFinger(
                finger,
                curls != null && curls.Count > finger ? curls[finger] : 0f,
                distances != null && distances.Length > tipBone
                    ? distances[tipBone]
                    : float.PositiveInfinity,
                jointSamples,
                jointCount);
        }

        if (now - reportedAt <= StaleReportSeconds)
        {
            return;
        }

        diagnostics.Phase = GripLatchPhase.Free;
        diagnostics.Masks = default;
        diagnostics.CountedFingers = 0;
        diagnostics.Block = !trackingValid
            ? GripEngagementBlock.TrackingLost
            : GripEngagementBlock.NoCandidateHold;
    }

    private static bool IsHandActive(GripHandDiagnostics diagnostics)
    {
        return diagnostics.Phase != GripLatchPhase.Free ||
               !string.IsNullOrEmpty(diagnostics.HoldLabel) ||
               diagnostics.Masks.Flexed != 0 ||
               diagnostics.Masks.Contact != 0;
    }

    private void Render(TextMeshPro text, GripHandDiagnostics diagnostics)
    {
        textBuilder.Clear();
        textBuilder.Append(MonospaceTag);
        GripDiagnosticsFormatter.AppendHand(textBuilder, diagnostics);
        text.SetText(textBuilder);
    }

    private void FollowHead()
    {
        Transform head = configuror.centerEyeAnchor.transform;
        Vector3 forward = head.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        Vector3 target = head.position +
                         forward * settings.diagnosticsForwardMeters +
                         Vector3.down * settings.diagnosticsDownMeters;
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up) *
                              Quaternion.Euler(-settings.diagnosticsTiltDegrees, 0f, 0f);
        float smoothing = settings.diagnosticsFollowSeconds <= 0f
            ? 1f
            : 1f - Mathf.Exp(-Time.unscaledDeltaTime / settings.diagnosticsFollowSeconds);
        panelRoot.transform.SetPositionAndRotation(
            Vector3.Lerp(panelRoot.transform.position, target, smoothing),
            Quaternion.Slerp(panelRoot.transform.rotation, rotation, smoothing));
    }

    private void SetPanelVisible(bool visible)
    {
        if (visible && panelRoot == null)
        {
            BuildPanel();
        }
        if (panelRoot == null || panelVisible == visible)
        {
            return;
        }

        panelVisible = visible;
        panelRoot.SetActive(visible);
        if (visible)
        {
            lastRefreshAt = float.NegativeInfinity;
            FollowHead();
        }
    }

    private void BuildPanel()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            throw new InvalidOperationException(
                "The grip diagnostics panel requires the project UI layer.");
        }

        UnityEngine.Shader panelShader = UnityEngine.Shader.Find("Oculus/Unlit Transparent Color") ??
                                         UnityEngine.Shader.Find("Interaction/UnlitTransparentColor");
        TMP_FontAsset fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        UnityEngine.Shader textShader =
            UnityEngine.Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
        if (panelShader == null || fontAsset == null || textShader == null)
        {
            throw new InvalidOperationException(
                "The grip diagnostics panel requires LiberationSans SDF, the TMP mobile overlay " +
                "shader and an always-visible unlit shader in the build.");
        }

        panelMaterial = new Material(panelShader) { renderQueue = 3900 };
        panelMaterial.SetColor("_Color", PanelColor);
        textMaterial = new Material(fontAsset.material)
        {
            shader = textShader,
            renderQueue = 4000,
        };

        panelRoot = new GameObject(RootName) { layer = uiLayer };
        panelRoot.transform.SetParent(transform, false);

        // Built from a bare mesh rather than a primitive so no collider ever exists to intercept
        // the console's pinch ray, which casts against this same UI layer.
        GameObject background = new("Grip Diagnostics Background") { layer = uiLayer };
        background.transform.SetParent(panelRoot.transform, false);
        background.transform.localPosition = new Vector3(0f, 0f, 0.002f);
        background.transform.localScale = new Vector3(PanelWidthMeters, PanelHeightMeters, 1f);
        background.AddComponent<MeshFilter>().sharedMesh = CreateUnitQuad();
        MeshRenderer backgroundRenderer = background.AddComponent<MeshRenderer>();
        backgroundRenderer.sharedMaterial = panelMaterial;
        backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        backgroundRenderer.receiveShadows = false;

        TextMeshPro title = CreateText(
            uiLayer,
            fontAsset,
            "Grip Diagnostics Title",
            new Vector3(0f, PanelHeightMeters * 0.5f - 0.018f, 0f),
            new Vector2(PanelWidthMeters - 0.02f, 0.024f),
            0.014f,
            TitleColor,
            TextAlignmentOptions.Center);
        title.text = "HAND GRIP  |  FLEXION AND CONTACT";

        float columnWidth = PanelWidthMeters * 0.5f - 0.015f;
        float columnX = PanelWidthMeters * 0.25f;
        leftText = CreateText(
            uiLayer,
            fontAsset,
            "Grip Diagnostics Left",
            new Vector3(-columnX, PanelHeightMeters * 0.5f - 0.042f, 0f),
            new Vector2(columnWidth, PanelHeightMeters - 0.05f),
            0.0092f,
            BodyColor,
            TextAlignmentOptions.TopLeft);
        rightText = CreateText(
            uiLayer,
            fontAsset,
            "Grip Diagnostics Right",
            new Vector3(columnX, PanelHeightMeters * 0.5f - 0.042f, 0f),
            new Vector2(columnWidth, PanelHeightMeters - 0.05f),
            0.0092f,
            BodyColor,
            TextAlignmentOptions.TopLeft);
        panelRoot.SetActive(false);
    }

    private TextMeshPro CreateText(
        int uiLayer,
        TMP_FontAsset fontAsset,
        string objectName,
        Vector3 localPosition,
        Vector2 worldSize,
        float worldFontSize,
        Color color,
        TextAlignmentOptions alignment)
    {
        GameObject textObject = new(objectName) { layer = uiLayer };
        textObject.transform.SetParent(panelRoot.transform, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localScale = Vector3.one * TextTransformScale;
        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.font = fontAsset;
        text.fontSharedMaterial = textMaterial;
        text.rectTransform.pivot = new Vector2(0.5f, 1f);
        text.rectTransform.sizeDelta = worldSize / TextTransformScale;
        text.alignment = alignment;
        text.fontSize = worldFontSize / TextTransformScale * 10f;
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.sortingOrder = 120;
        return text;
    }

    private Mesh CreateUnitQuad()
    {
        quadMesh = new Mesh { name = "Grip Diagnostics Quad" };
        quadMesh.SetVertices(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
        });
        quadMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        quadMesh.RecalculateNormals();
        quadMesh.RecalculateBounds();
        return quadMesh;
    }

    private static string DescribeHold(GameObject hold)
    {
        if (hold == null)
        {
            return string.Empty;
        }

        string name = hold.name.Split('.')[0];
        int ghostMarker = name.IndexOf('#');
        return ghostMarker >= 0 ? name.Substring(0, ghostMarker) : name;
    }

    private void OnDestroy()
    {
        if (panelMaterial != null)
        {
            Destroy(panelMaterial);
        }
        if (textMaterial != null)
        {
            Destroy(textMaterial);
        }
        if (quadMesh != null)
        {
            Destroy(quadMesh);
        }
    }
}
