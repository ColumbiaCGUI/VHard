using UnityEngine;

[CreateAssetMenu(fileName = "GripScoreConfig", menuName = "VHard/Grip Score Config")]
public sealed class GripScoreConfig : ScriptableObject
{
    [Header("Contact")]
    [Min(0.001f)] public float proximityThreshold = 0.025f;
    [Min(0.001f)] public float contactThreshold = 0.008f;
    [Min(0.00001f)] public float referenceContactArea = 0.0015f;
    [Min(1000f)] public float fixedPointScale = 100000000f;

    [Header("Score Weights")]
    [Range(0f, 1f)] public float contactWeight = 0.30f;
    [Range(0f, 1f)] public float areaWeight = 0.20f;
    [Range(0f, 1f)] public float oppositionWeight = 0.25f;
    [Range(0f, 1f)] public float loadAlignmentWeight = 0.25f;

    [Header("Display")]
    [Min(0.01f)] public float smoothingSeconds = 0.15f;
    [Range(0f, 0.25f)] public float hysteresis = 0.05f;
    public bool rimGlow;
    [Range(0f, 1f)] public float rimGlowThreshold = 0.5f;
    [Range(0f, 1f)] public float rimGlowAlpha = 0.35f;
    [Range(0.5f, 8f)] public float rimGlowPower = 3f;
    public Color lowScoreColor = new(0.9f, 0.08f, 0.06f, 1f);
    public Color mediumScoreColor = new(1f, 0.55f, 0.05f, 1f);
    public Color highScoreColor = new(0.1f, 0.85f, 0.2f, 1f);
    public Material contactPatchMaterial;

    public float WeightSum => contactWeight + areaWeight + oppositionWeight + loadAlignmentWeight;

    public Color EvaluateScoreColor(float score)
    {
        score = Mathf.Clamp01(score);
        return score < 0.5f
            ? Color.Lerp(lowScoreColor, mediumScoreColor, score * 2f)
            : Color.Lerp(mediumScoreColor, highScoreColor, (score - 0.5f) * 2f);
    }

    private void OnValidate()
    {
        proximityThreshold = Mathf.Max(proximityThreshold, contactThreshold);
        referenceContactArea = Mathf.Max(referenceContactArea, 0.00001f);
        fixedPointScale = Mathf.Max(fixedPointScale, 1000f);
        hysteresis = Mathf.Min(hysteresis, 0.5f);
    }
}
