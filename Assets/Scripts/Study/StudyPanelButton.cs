using System;
using UnityEngine;

public sealed class StudyPanelButton : MonoBehaviour
{
    private static readonly int BaseColor = UnityEngine.Shader.PropertyToID("_BaseColor");
    private static readonly int ColorProperty = UnityEngine.Shader.PropertyToID("_Color");
    private static readonly int BorderColor = UnityEngine.Shader.PropertyToID("_BorderColor");
    private static readonly int Dimensions = UnityEngine.Shader.PropertyToID("_Dimensions");
    private static readonly int Radii = UnityEngine.Shader.PropertyToID("_Radii");
    private static readonly Color DefaultEnabledColor = new(0.08f, 0.35f, 0.52f, 1f);
    private static readonly Color DefaultHoveredColor = new(0.08f, 0.62f, 0.78f, 1f);
    private static readonly Color DefaultSelectedColor = new(0.93f, 0.58f, 0.12f, 1f);
    private static readonly Color DangerColor = new(0.62f, 0.16f, 0.18f, 1f);
    private static readonly Color DangerHoveredColor = new(0.86f, 0.24f, 0.24f, 1f);
    private static readonly Color DisabledColor = new(0.12f, 0.14f, 0.17f, 1f);

    public Action Pressed;
    public bool Interactable { get; private set; } = true;

    private bool hovered;
    private bool selected;
    private bool danger;
    private Color enabledColor = DefaultEnabledColor;
    private Color hoveredColor = DefaultHoveredColor;
    private Color selectedColor = DefaultSelectedColor;
    private Renderer surfaceRenderer;
    private MaterialPropertyBlock surfaceProperties;

    public void ConfigureSurface(Vector2 size)
    {
        if (!EnsureSurface())
        {
            return;
        }

        surfaceRenderer.GetPropertyBlock(surfaceProperties);
        float radius = Mathf.Min(size.x, size.y) * 0.22f;
        surfaceProperties.SetVector(Dimensions, new Vector4(size.x * 0.5f, size.y * 0.5f, 0.002f, 0.004f));
        surfaceProperties.SetVector(Radii, new Vector4(radius, radius, radius, radius));
        surfaceRenderer.SetPropertyBlock(surfaceProperties);
        RefreshVisual();
    }

    public void SetInteractable(bool interactable)
    {
        if (Interactable == interactable)
        {
            return;
        }
        Interactable = interactable;
        RefreshVisual();
    }

    public void SetHovered(bool isHovered)
    {
        if (hovered == isHovered)
        {
            return;
        }
        hovered = isHovered;
        RefreshVisual();
    }

    public void SetSelected(bool isSelected)
    {
        if (selected == isSelected)
        {
            return;
        }
        selected = isSelected;
        RefreshVisual();
    }

    public void SetDanger(bool isDanger)
    {
        if (danger == isDanger)
        {
            return;
        }
        danger = isDanger;
        RefreshVisual();
    }

    public void SetPalette(Color enabled, Color hovered, Color selected)
    {
        if (enabledColor == enabled && hoveredColor == hovered && selectedColor == selected)
        {
            return;
        }
        enabledColor = enabled;
        hoveredColor = hovered;
        selectedColor = selected;
        RefreshVisual();
    }

    public bool Press()
    {
        if (!Interactable || !isActiveAndEnabled || !gameObject.activeInHierarchy)
        {
            return false;
        }

        Pressed?.Invoke();
        return true;
    }

    private void RefreshVisual()
    {
        if (!EnsureSurface())
        {
            return;
        }

        Color color = !Interactable
            ? DisabledColor
            : selected
                ? selectedColor
                : danger
                    ? hovered ? DangerHoveredColor : DangerColor
                    : hovered ? hoveredColor : enabledColor;
        surfaceRenderer.GetPropertyBlock(surfaceProperties);
        surfaceProperties.SetColor(BaseColor, color);
        surfaceProperties.SetColor(ColorProperty, color);
        surfaceProperties.SetColor(BorderColor, Color.Lerp(color, Color.white, 0.22f));
        surfaceRenderer.SetPropertyBlock(surfaceProperties);
    }

    private bool EnsureSurface()
    {
        surfaceRenderer ??= GetComponent<Renderer>();
        surfaceProperties ??= new MaterialPropertyBlock();
        return surfaceRenderer != null;
    }
}
