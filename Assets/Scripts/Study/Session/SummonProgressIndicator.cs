using System;
using TMPro;
using UnityEngine;

/// <summary>Feedback for the guarded console summon: while the left palm faces up, a small bar
/// above the palm fills over the dwell, then flips to a PINCH TO OPEN prompt once a pinch would
/// toggle the console. Built at runtime on the UI layer, collider-free, hidden whenever the
/// gesture is not in progress; instant summons (no run active) never show it.</summary>
public sealed class SummonProgressIndicator : MonoBehaviour
{
    public const string RootName = "Summon Progress Indicator";

    private const float BarWidthMeters = 0.07f;
    private const float BarHeightMeters = 0.007f;
    private const float PalmUpOffsetMeters = 0.09f;
    private const float LabelUpOffsetMeters = 0.024f;
    private const float TextTransformScale = 0.01f;
    // Drawn just under the console pointer's queue so the cue reads over the hand and the board.
    private const int IndicatorQueue = 4050;

    private static readonly Color BarBackgroundColor = new(0.30f, 0.42f, 0.52f, 0.55f);
    private static readonly Color BarFillColor = new(0.10f, 0.72f, 0.92f, 0.92f);
    private static readonly Color ArmedColor = new(1f, 0.68f, 0.18f, 1f);

    private GameObject indicatorRoot;
    private Transform fillTransform;
    private Renderer fillRenderer;
    private TextMeshPro label;
    private Material backgroundMaterial;
    private Material fillMaterial;
    private Material textMaterial;
    private Mesh quadMesh;
    private MaterialPropertyBlock fillProperties;
    private bool indicatorVisible;
    private bool armedShown;

    public static SummonProgressIndicator Create(Transform parent)
    {
        GameObject indicatorObject = new(RootName);
        indicatorObject.transform.SetParent(parent, false);
        return indicatorObject.AddComponent<SummonProgressIndicator>();
    }

    public void Show(Vector3 palmPosition, Vector3 headPosition, float progress01, bool armed)
    {
        if (float.IsNaN(progress01) || float.IsInfinity(progress01))
        {
            throw new ArgumentOutOfRangeException(nameof(progress01));
        }
        if (indicatorRoot == null)
        {
            BuildIndicator();
        }

        Vector3 position = palmPosition + Vector3.up * PalmUpOffsetMeters;
        Vector3 awayFromHead = position - headPosition;
        Vector3 horizontal = new(awayFromHead.x, 0f, awayFromHead.z);
        if (horizontal.sqrMagnitude > 0.000001f)
        {
            indicatorRoot.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation(awayFromHead, Vector3.up));
        }
        else
        {
            indicatorRoot.transform.position = position;
        }

        float progress = armed ? 1f : Mathf.Clamp01(progress01);
        fillTransform.localScale = new Vector3(
            Mathf.Max(0.0001f, BarWidthMeters * progress),
            BarHeightMeters,
            1f);
        fillTransform.localPosition = new Vector3(
            -BarWidthMeters * 0.5f + BarWidthMeters * progress * 0.5f,
            0f,
            -0.0005f);
        if (armedShown != armed)
        {
            armedShown = armed;
            fillProperties ??= new MaterialPropertyBlock();
            fillRenderer.GetPropertyBlock(fillProperties);
            fillProperties.SetColor("_Color", armed ? ArmedColor : BarFillColor);
            fillRenderer.SetPropertyBlock(fillProperties);
            label.gameObject.SetActive(armed);
        }
        if (!indicatorVisible)
        {
            indicatorVisible = true;
            indicatorRoot.SetActive(true);
        }
    }

    public void Hide()
    {
        if (indicatorRoot == null || !indicatorVisible)
        {
            return;
        }
        indicatorVisible = false;
        indicatorRoot.SetActive(false);
    }

    private void BuildIndicator()
    {
        int uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            throw new InvalidOperationException(
                "The summon progress indicator requires the project UI layer.");
        }

        UnityEngine.Shader barShader = UnityEngine.Shader.Find("Oculus/Unlit Transparent Color") ??
                                       UnityEngine.Shader.Find("Interaction/UnlitTransparentColor");
        TMP_FontAsset fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        UnityEngine.Shader textShader =
            UnityEngine.Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
        if (barShader == null || fontAsset == null || textShader == null)
        {
            throw new InvalidOperationException(
                "The summon progress indicator requires LiberationSans SDF, the TMP mobile " +
                "overlay shader and an always-visible unlit shader in the build.");
        }

        backgroundMaterial = new Material(barShader) { renderQueue = IndicatorQueue };
        backgroundMaterial.SetColor("_Color", BarBackgroundColor);
        fillMaterial = new Material(barShader) { renderQueue = IndicatorQueue + 1 };
        fillMaterial.SetColor("_Color", BarFillColor);
        textMaterial = new Material(fontAsset.material)
        {
            shader = textShader,
            renderQueue = IndicatorQueue + 2,
        };

        indicatorRoot = new GameObject(RootName + " Root") { layer = uiLayer };
        indicatorRoot.transform.SetParent(transform, false);
        quadMesh = CreateUnitQuad();

        GameObject background = new(RootName + " Background") { layer = uiLayer };
        background.transform.SetParent(indicatorRoot.transform, false);
        background.transform.localScale = new Vector3(BarWidthMeters, BarHeightMeters, 1f);
        background.AddComponent<MeshFilter>().sharedMesh = quadMesh;
        MeshRenderer backgroundRenderer = background.AddComponent<MeshRenderer>();
        backgroundRenderer.sharedMaterial = backgroundMaterial;
        backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        backgroundRenderer.receiveShadows = false;

        GameObject fill = new(RootName + " Fill") { layer = uiLayer };
        fill.transform.SetParent(indicatorRoot.transform, false);
        fill.AddComponent<MeshFilter>().sharedMesh = quadMesh;
        MeshRenderer fillMeshRenderer = fill.AddComponent<MeshRenderer>();
        fillMeshRenderer.sharedMaterial = fillMaterial;
        fillMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        fillMeshRenderer.receiveShadows = false;
        fillTransform = fill.transform;
        fillRenderer = fillMeshRenderer;

        GameObject labelObject = new(RootName + " Label") { layer = uiLayer };
        labelObject.transform.SetParent(indicatorRoot.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, LabelUpOffsetMeters, 0f);
        labelObject.transform.localScale = Vector3.one * TextTransformScale;
        label = labelObject.AddComponent<TextMeshPro>();
        label.font = fontAsset;
        label.fontSharedMaterial = textMaterial;
        label.rectTransform.sizeDelta = new Vector2(0.2f, 0.02f) / TextTransformScale;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 0.011f / TextTransformScale * 10f;
        label.fontStyle = FontStyles.Bold;
        label.color = ArmedColor;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        label.sortingOrder = 130;
        label.text = "PINCH TO OPEN";
        label.gameObject.SetActive(false);
        indicatorRoot.SetActive(false);
    }

    private Mesh CreateUnitQuad()
    {
        Mesh mesh = new() { name = RootName + " Quad" };
        mesh.SetVertices(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
        });
        mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void OnDestroy()
    {
        if (backgroundMaterial != null)
        {
            Destroy(backgroundMaterial);
        }
        if (fillMaterial != null)
        {
            Destroy(fillMaterial);
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
