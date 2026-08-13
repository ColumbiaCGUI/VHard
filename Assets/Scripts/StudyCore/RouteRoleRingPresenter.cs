using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Draws the MoonBoard role rings for the active route on the board plane: green double
/// ring on the starts, blue single ring on the intermediates, red double ring on the finish, the
/// same roles the physical board marks with its LEDs in every condition. Rings are passive board
/// decoration — no colliders, never on the study interaction layers — so hold selection, hover and
/// grip raycasts are untouched.</summary>
public sealed class RouteRoleRingPresenter
{
    public const string RingRootName = "RouteRoleRings";
    public const string RingNamePrefix = "RoleRing_";
    private const int RingSegments = 48;
    private const float RingThicknessRatio = 0.12f;

    private readonly Transform boardRoot;
    private readonly Transform mainSurface;
    private readonly Func<string, RouteCueStyle> styleForHold;
    private readonly List<GameObject> rings = new();
    private Transform ringRoot;
    private Mesh ringMesh;
    private Material ringMaterial;
    private MaterialPropertyBlock ringProperties;

    public RouteRoleRingPresenter(
        Transform boardRoot,
        Transform mainSurface,
        Func<string, RouteCueStyle> styleForHold)
    {
        if (boardRoot == null)
        {
            throw new ArgumentNullException(nameof(boardRoot));
        }
        if (mainSurface == null)
        {
            throw new ArgumentNullException(nameof(mainSurface));
        }
        this.boardRoot = boardRoot;
        this.mainSurface = mainSurface;
        this.styleForHold = styleForHold ?? throw new ArgumentNullException(nameof(styleForHold));
    }

    public bool AreRingsVisible { get; private set; }

    public int RingCount => rings.Count;

    public Transform RingRoot => ringRoot;

    public void SetVisible(bool visible)
    {
        AreRingsVisible = visible;
        if (ringRoot != null)
        {
            ringRoot.gameObject.SetActive(visible);
        }
    }

    public void Clear()
    {
        foreach (GameObject ring in rings)
        {
            if (ring != null)
            {
                DestroyRing(ring);
            }
        }
        rings.Clear();
    }

    /// <summary>Rebuilds every ring for the route. Static after this call: no per-frame work.</summary>
    public void Rebuild(IReadOnlyList<GameObject> routeHolds)
    {
        Clear();
        if (routeHolds == null || routeHolds.Count == 0)
        {
            return;
        }

        List<GameObject> renderableHolds = new(routeHolds.Count);
        List<Vector3> holdCentres = new(routeHolds.Count);
        foreach (GameObject hold in routeHolds)
        {
            if (hold != null && hold.TryGetComponent(out Renderer holdRenderer))
            {
                renderableHolds.Add(hold);
                holdCentres.Add(holdRenderer.bounds.center);
            }
        }
        if (renderableHolds.Count == 0)
        {
            throw new InvalidOperationException(
                "Route role rings require at least one route hold with a renderer.");
        }

        Vector3 planePoint = mainSurface.position;
        Vector3 outwardNormal = RouteCuePolicy.ResolveOutwardNormal(
            mainSurface.up,
            planePoint,
            holdCentres);
        Quaternion ringRotation = Quaternion.LookRotation(
            outwardNormal,
            RouteCuePolicy.GetBoardVertical(outwardNormal));

        Transform root = EnsureRingRoot();
        for (int index = 0; index < renderableHolds.Count; index++)
        {
            GameObject hold = renderableHolds[index];
            RouteCueStyle style = styleForHold(hold.name);
            Bounds bounds = hold.GetComponent<Renderer>().bounds;
            Vector3 centre = RouteCuePolicy.ProjectGridAnchorOntoBoard(
                hold.transform.position,
                planePoint,
                outwardNormal,
                RouteCuePolicy.RingOutwardOffsetMeters);
            float outerDiameter = RouteCuePolicy.GetRingOuterDiameterMeters(
                Mathf.Max(bounds.size.x, bounds.size.y));

            for (int ring = 0; ring < style.RingCount; ring++)
            {
                float diameter = ring == 0
                    ? outerDiameter
                    : outerDiameter * RouteCuePolicy.RingInnerScale;
                rings.Add(CreateRing(root, hold.name, ring, centre, ringRotation, diameter, style.Color));
            }
        }

        root.gameObject.SetActive(AreRingsVisible);
    }

    private GameObject CreateRing(
        Transform root,
        string holdCoordinate,
        int ringIndex,
        Vector3 centre,
        Quaternion rotation,
        float diameter,
        Color color)
    {
        GameObject ring = new(RingNamePrefix + holdCoordinate + "_" + ringIndex);
        ring.layer = root.gameObject.layer;
        ring.transform.SetParent(root, false);
        ring.transform.SetPositionAndRotation(centre, rotation);
        ring.transform.localScale = new Vector3(diameter, diameter, 1f);

        ring.AddComponent<MeshFilter>().sharedMesh = EnsureRingMesh();
        MeshRenderer meshRenderer = ring.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = EnsureRingMaterial();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        ringProperties ??= new MaterialPropertyBlock();
        meshRenderer.GetPropertyBlock(ringProperties);
        ringProperties.SetColor("_BaseColor", color);
        ringProperties.SetColor("_Color", color);
        meshRenderer.SetPropertyBlock(ringProperties);
        return ring;
    }

    /// <summary>Rings inherit the board surface layer, never the study interaction layers: the
    /// ghost ray target and the hover/grip pipeline filter by those layers, and a ring on one of
    /// them would compete with the holds for selection.</summary>
    private Transform EnsureRingRoot()
    {
        if (ringRoot != null)
        {
            return ringRoot;
        }

        int ringLayer = mainSurface.gameObject.layer;
        if (ringLayer == LayerMask.NameToLayer("StudyHolds") ||
            ringLayer == LayerMask.NameToLayer("StudyGhostHolds"))
        {
            throw new InvalidOperationException(
                "Board surface sits on a study interaction layer; role rings would capture hold selection.");
        }

        Transform existing = boardRoot.Find(RingRootName);
        if (existing != null)
        {
            ringRoot = existing;
            return ringRoot;
        }

        GameObject root = new(RingRootName) { layer = ringLayer };
        root.transform.SetParent(boardRoot, false);
        ringRoot = root.transform;
        return ringRoot;
    }

    private Material EnsureRingMaterial()
    {
        if (ringMaterial != null)
        {
            return ringMaterial;
        }

        UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default") ??
                                    UnityEngine.Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Route role rings require the Sprites/Default or URP Unlit shader in the build.");
        }
        ringMaterial = new Material(shader) { name = "RouteRoleRing" };
        return ringMaterial;
    }

    /// <summary>Unit-diameter annulus in the local XY plane facing +Z, so a ring scales to its
    /// hold by setting localScale to the wanted outer diameter.</summary>
    private Mesh EnsureRingMesh()
    {
        if (ringMesh != null)
        {
            return ringMesh;
        }

        const float outerRadius = 0.5f;
        float innerRadius = outerRadius * (1f - RingThicknessRatio);
        Vector3[] vertices = new Vector3[RingSegments * 2];
        int[] triangles = new int[RingSegments * 6];
        for (int segment = 0; segment < RingSegments; segment++)
        {
            float angle = segment / (float)RingSegments * Mathf.PI * 2f;
            Vector2 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
            vertices[segment * 2] = direction * outerRadius;
            vertices[segment * 2 + 1] = direction * innerRadius;

            int next = (segment + 1) % RingSegments;
            int triangle = segment * 6;
            triangles[triangle] = segment * 2;
            triangles[triangle + 1] = next * 2;
            triangles[triangle + 2] = segment * 2 + 1;
            triangles[triangle + 3] = segment * 2 + 1;
            triangles[triangle + 4] = next * 2;
            triangles[triangle + 5] = next * 2 + 1;
        }

        ringMesh = new Mesh { name = "RouteRoleRing" };
        ringMesh.SetVertices(vertices);
        ringMesh.SetTriangles(triangles, 0);
        ringMesh.RecalculateNormals();
        ringMesh.RecalculateBounds();
        return ringMesh;
    }

    private static void DestroyRing(GameObject ring)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(ring);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(ring);
        }
    }
}
