using System;
using UnityEngine;

public sealed class StudyPanelButton : MonoBehaviour
{
    private static readonly int BaseColor = UnityEngine.Shader.PropertyToID("_BaseColor");
    private static readonly int ColorProperty = UnityEngine.Shader.PropertyToID("_Color");
    private static readonly Color EnabledColor = new(0.08f, 0.35f, 0.52f, 1f);
    private static readonly Color DisabledColor = new(0.12f, 0.14f, 0.17f, 1f);

    public Action Pressed;
    public bool Interactable { get; private set; } = true;

    public void SetInteractable(bool interactable)
    {
        if (Interactable == interactable)
        {
            return;
        }
        Interactable = interactable;

        if (TryGetComponent(out Renderer renderer))
        {
            MaterialPropertyBlock properties = new();
            renderer.GetPropertyBlock(properties);
            Color color = interactable ? EnabledColor : DisabledColor;
            properties.SetColor(BaseColor, color);
            properties.SetColor(ColorProperty, color);
            renderer.SetPropertyBlock(properties);
        }
    }

    public void Press()
    {
        if (Interactable)
        {
            Pressed?.Invoke();
        }
    }
}
