using System;
using UnityEngine;
using static GripContactConstants;

/// <summary>Per-hold GPU residency for the grip pipeline: the mesh buffers the contact pass reads,
/// the readback slots it writes, and which hands have latched this hold. It owns no visual. The
/// graded cue is drawn by GripAffordanceOutlinePresenter from the hand scores this pipeline
/// publishes, so the hold's own scanned appearance is never overpainted.</summary>
internal sealed class GripHoldContactState : IDisposable
{
    private readonly GripContactOutputSet[] outputs;
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

    /// <summary>Kept as the dispatcher's and the store's per-frame seam. The per-vertex contact
    /// buffer stays bound to its output set, ready for spec 04's per-finger contact patches, but
    /// nothing renders from it today.</summary>
    public void SetOverlayVisible(bool visible)
    {
    }

    public void SetContactBuffer(ComputeBuffer contactBuffer, long epoch)
    {
    }

    public void InvalidateContactData(long epoch = -1)
    {
    }

    public void SetLatchedHand(int handMask, bool latched)
    {
        if (handMask == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handMask));
        }

        latchedHandMask = latched ? latchedHandMask | handMask : latchedHandMask & ~handMask;
    }

    public void ClearLatchFeedback()
    {
        latchedHandMask = 0;
    }

    public void Dispose()
    {
        ClearLatchFeedback();
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
}
