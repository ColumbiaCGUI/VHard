using System;
using UnityEngine;
using UnityEngine.Rendering;
using static GripContactConstants;

internal sealed class GripHoldContactState : IDisposable
{
    private readonly GripContactOutputSet[] outputs;
    private readonly Renderer overlayRenderer;
    private readonly MaterialPropertyBlock overlayProperties;
    private int latchedHandMask;
    public readonly GameObject hold;
    public readonly Mesh mesh;
    public readonly int vertexCount;
    public readonly ComputeBuffer vertices;
    public readonly ComputeBuffer normals;
    public readonly ComputeBuffer vertexAreas;
    public readonly ComputeBuffer leftHandBones;
    public readonly ComputeBuffer rightHandBones;

    public GripHoldContactState(
        GripContactReadbackProcessor owner,
        GripScoreConfig config,
        GameObject hold,
        MeshFilter meshFilter,
        Material overlayMaterial)
    {
        this.hold = hold;
        mesh = meshFilter.sharedMesh;
        vertexCount = mesh.vertexCount;
        Vector3[] meshVertices = mesh.vertices;
        Vector3[] meshNormals = mesh.normals;
        if (meshNormals.Length != vertexCount)
        {
            mesh.RecalculateNormals();
            meshNormals = mesh.normals;
        }
        float[] areas = ComputeVertexAreas(mesh, hold.transform);

        vertices = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        normals = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        vertexAreas = new ComputeBuffer(vertexCount, sizeof(float));
        leftHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
        rightHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
        vertices.SetData(meshVertices);
        normals.SetData(meshNormals);
        vertexAreas.SetData(areas);
        outputs = new[]
        {
            new GripContactOutputSet(owner, this),
            new GripContactOutputSet(owner, this),
        };
        overlayRenderer = EnsureOverlay(hold, mesh, overlayMaterial);
        overlayProperties = new MaterialPropertyBlock();
        if (overlayRenderer != null)
        {
            overlayRenderer.enabled = false;
            overlayRenderer.GetPropertyBlock(overlayProperties);
            overlayProperties.SetFloat("_ContactThreshold", config.contactThreshold);
            overlayProperties.SetFloat("_ProximityThreshold", config.proximityThreshold);
            overlayProperties.SetFloat("_GripLatched", 0f);
            overlayProperties.SetColor("_LatchedColor", new Color(0.1f, 0.85f, 0.2f, 1f));
            overlayRenderer.SetPropertyBlock(overlayProperties);
        }
    }

    public int LatchedHandMask => latchedHandMask;

    public GripContactOutputSet GetAvailableOutput()
    {
        foreach (GripContactOutputSet output in outputs)
        {
            if (!output.IsPending)
            {
                return output;
            }
        }
        return null;
    }

    public void SetOverlayVisible(bool visible)
    {
        RefreshOverlayVisibility();
    }

    public void SetContactBuffer(ComputeBuffer contactBuffer, long epoch)
    {
        RefreshOverlayVisibility();
    }

    public void InvalidateContactData(long epoch = -1)
    {
        RefreshOverlayVisibility();
    }

    public void SetLatchedHand(int handMask, bool latched)
    {
        if (handMask == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handMask));
        }

        latchedHandMask = latched ? latchedHandMask | handMask : latchedHandMask & ~handMask;
        if (overlayRenderer != null)
        {
            overlayRenderer.GetPropertyBlock(overlayProperties);
            overlayProperties.SetFloat("_GripLatched", latchedHandMask != 0 ? 1f : 0f);
            overlayRenderer.SetPropertyBlock(overlayProperties);
            RefreshOverlayVisibility();
        }
    }

    public void ClearLatchFeedback()
    {
        latchedHandMask = 0;
        if (overlayRenderer != null)
        {
            overlayRenderer.GetPropertyBlock(overlayProperties);
            overlayProperties.SetFloat("_GripLatched", 0f);
            overlayRenderer.SetPropertyBlock(overlayProperties);
            RefreshOverlayVisibility();
        }
    }

    private void RefreshOverlayVisibility()
    {
        if (overlayRenderer != null)
        {
            overlayRenderer.enabled = latchedHandMask != 0;
        }
    }

    public void Dispose()
    {
        ClearLatchFeedback();
        InvalidateContactData();
        foreach (GripContactOutputSet output in outputs)
        {
            output.Dispose();
        }
        vertices.Release();
        normals.Release();
        vertexAreas.Release();
        leftHandBones.Release();
        rightHandBones.Release();
    }

    private static float[] ComputeVertexAreas(Mesh sourceMesh, Transform transform)
    {
        Vector3[] meshVertices = sourceMesh.vertices;
        int[] triangles = sourceMesh.triangles;
        float[] areas = new float[meshVertices.Length];
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int a = triangles[i];
            int b = triangles[i + 1];
            int c = triangles[i + 2];
            Vector3 edgeA = transform.TransformVector(meshVertices[b] - meshVertices[a]);
            Vector3 edgeB = transform.TransformVector(meshVertices[c] - meshVertices[a]);
            float thirdArea = Vector3.Cross(edgeA, edgeB).magnitude / 6f;
            areas[a] += thirdArea;
            areas[b] += thirdArea;
            areas[c] += thirdArea;
        }
        return areas;
    }

    private static Renderer EnsureOverlay(GameObject hold, Mesh sourceMesh, Material overlayMaterial)
    {
        Transform overlayTransform = hold.transform.Find("Contact Patch Overlay");
        GameObject overlay;
        if (overlayTransform == null)
        {
            overlay = new GameObject("Contact Patch Overlay");
            overlay.transform.SetParent(hold.transform, false);
            overlay.AddComponent<MeshFilter>();
            overlay.AddComponent<MeshRenderer>();
        }
        else
        {
            overlay = overlayTransform.gameObject;
        }

        overlay.layer = hold.layer;
        overlay.transform.localPosition = Vector3.zero;
        overlay.transform.localRotation = Quaternion.identity;
        overlay.transform.localScale = Vector3.one;
        overlay.GetComponent<MeshFilter>().sharedMesh = sourceMesh;
        MeshRenderer renderer = overlay.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = overlayMaterial != null
            ? overlayMaterial
            : Resources.Load<Material>("ContactPatchOverlay");
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return renderer;
    }
}
