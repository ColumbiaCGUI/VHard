using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Draws the graded grip cue as a rim on the silhouette of the hold a hand is holding,
/// rather than recolouring the hold itself: the scanned mesh keeps its own colour and shape, which
/// is the thing the participant is there to read. One rim child per hold, created at runtime,
/// collider-free and flagged never to serialise, sharing the hold's own mesh so no extra geometry
/// is uploaded. At most one rim per hand is enabled at a time.</summary>
public sealed class GripAffordanceOutlinePresenter
{
    public const string OutlineName = "GripAffordanceRim";
    public const string OverlayMaterialResource = "ContactPatchOverlay";

    /// <summary>The rim carries no collider, so it cannot compete for hold selection; it is still
    /// kept off the study interaction layers, and off the ghost layer that GhostHoldController
    /// stamps recursively over a spawned ghost's children.</summary>
    public const int OutlineLayer = 0;

    private static readonly int AffordanceColorId = UnityEngine.Shader.PropertyToID("_AffordanceColor");
    private static readonly int AffordanceAlphaId = UnityEngine.Shader.PropertyToID("_AffordanceAlpha");
    private static readonly int AffordanceRimPowerId = UnityEngine.Shader.PropertyToID("_AffordanceRimPower");

    private readonly Dictionary<int, MeshRenderer> outlines = new();
    private readonly List<int> staleHoldIds = new();
    private Material outlineMaterial;
    private MaterialPropertyBlock outlineProperties;

    public int OutlineCount => outlines.Count;

    /// <summary>Applies both hands in one pass so a hold held by both reports the stronger of the
    /// two cues, and every other rim is switched off in the same frame.</summary>
    public void Apply(
        GameObject leftHold,
        GripAffordance leftAffordance,
        GameObject rightHold,
        GripAffordance rightAffordance)
    {
        PruneDestroyedOutlines();

        GameObject visibleLeft = leftAffordance.IsVisible ? leftHold : null;
        GameObject visibleRight = rightAffordance.IsVisible ? rightHold : null;
        if (visibleLeft != null && visibleLeft == visibleRight)
        {
            Show(visibleLeft, GripAffordancePolicy.Combine(leftAffordance, rightAffordance));
            visibleRight = null;
        }
        else
        {
            if (visibleLeft != null)
            {
                Show(visibleLeft, leftAffordance);
            }
            if (visibleRight != null)
            {
                Show(visibleRight, rightAffordance);
            }
        }

        int leftId = visibleLeft != null ? visibleLeft.GetInstanceID() : 0;
        int rightId = visibleRight != null ? visibleRight.GetInstanceID() : 0;
        foreach (KeyValuePair<int, MeshRenderer> outline in outlines)
        {
            if (outline.Key != leftId && outline.Key != rightId)
            {
                outline.Value.enabled = false;
            }
        }
    }

    public void HideAll()
    {
        PruneDestroyedOutlines();
        foreach (MeshRenderer outline in outlines.Values)
        {
            outline.enabled = false;
        }
    }

    public void Clear()
    {
        foreach (MeshRenderer outline in outlines.Values)
        {
            if (outline != null)
            {
                DestroyObject(outline.gameObject);
            }
        }
        outlines.Clear();
        if (outlineMaterial != null)
        {
            DestroyObject(outlineMaterial);
            outlineMaterial = null;
        }
    }

    private void Show(GameObject hold, GripAffordance affordance)
    {
        MeshRenderer outline = EnsureOutline(hold);
        outlineProperties ??= new MaterialPropertyBlock();
        outline.GetPropertyBlock(outlineProperties);
        outlineProperties.SetColor(AffordanceColorId, affordance.Color);
        outlineProperties.SetFloat(AffordanceAlphaId, affordance.Alpha);
        outlineProperties.SetFloat(AffordanceRimPowerId, affordance.RimPower);
        outline.SetPropertyBlock(outlineProperties);
        outline.enabled = true;
    }

    private MeshRenderer EnsureOutline(GameObject hold)
    {
        int holdId = hold.GetInstanceID();
        if (outlines.TryGetValue(holdId, out MeshRenderer cached) && cached != null)
        {
            // GhostHoldController re-stamps a spawned ghost's whole subtree onto the ghost layer,
            // so the rim reasserts its own layer rather than trusting the layer it was created on.
            cached.gameObject.layer = OutlineLayer;
            return cached;
        }

        if (!hold.TryGetComponent(out MeshFilter holdMesh) || holdMesh.sharedMesh == null)
        {
            throw new InvalidOperationException(
                "Grip affordance rim requires a hold mesh: " + hold.name + " has none.");
        }

        Transform existing = hold.transform.Find(OutlineName);
        GameObject outlineObject = existing != null ? existing.gameObject : new GameObject(OutlineName);
        outlineObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        outlineObject.layer = OutlineLayer;
        outlineObject.transform.SetParent(hold.transform, false);
        outlineObject.transform.localPosition = Vector3.zero;
        outlineObject.transform.localRotation = Quaternion.identity;
        outlineObject.transform.localScale = Vector3.one;

        if (!outlineObject.TryGetComponent(out MeshFilter outlineMesh))
        {
            outlineMesh = outlineObject.AddComponent<MeshFilter>();
        }
        outlineMesh.sharedMesh = holdMesh.sharedMesh;

        if (!outlineObject.TryGetComponent(out MeshRenderer renderer))
        {
            renderer = outlineObject.AddComponent<MeshRenderer>();
        }
        renderer.sharedMaterial = EnsureOutlineMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
        outlines[holdId] = renderer;
        return renderer;
    }

    /// <summary>One material for every rim, cloned from the Resources overlay asset so the shader
    /// survives build-time stripping and the shared asset is never mutated. Per-hold colour rides a
    /// MaterialPropertyBlock, so no material is created while the study is running.</summary>
    private Material EnsureOutlineMaterial()
    {
        if (outlineMaterial != null)
        {
            return outlineMaterial;
        }

        Material source = Resources.Load<Material>(OverlayMaterialResource);
        if (source == null)
        {
            throw new InvalidOperationException(
                "Grip affordance rim requires Resources/" + OverlayMaterialResource + ".mat.");
        }
        outlineMaterial = new Material(source) { name = OutlineName };
        return outlineMaterial;
    }

    private void PruneDestroyedOutlines()
    {
        staleHoldIds.Clear();
        foreach (KeyValuePair<int, MeshRenderer> outline in outlines)
        {
            if (outline.Value == null)
            {
                staleHoldIds.Add(outline.Key);
            }
        }
        foreach (int holdId in staleHoldIds)
        {
            outlines.Remove(holdId);
        }
    }

    private static void DestroyObject(UnityEngine.Object target)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(target);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
