using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>Drives everything the participant sees on a hold: the interaction shader state, the
/// per-hold alpha, which holds are active for the current route, and the world-space route halos.</summary>
public sealed class HoldVisualsController
{
    private readonly SceneConfiguror configuror;
    private readonly List<GameObject> activeHighlightCircles = new();
    private Material highlightCircleMaterial;
    private MaterialPropertyBlock holdProperties;
    private MaterialPropertyBlock HoldProperties => holdProperties ??= new MaterialPropertyBlock();

    public HoldVisualsController(SceneConfiguror configuror)
    {
        this.configuror = configuror;
    }

    public int IndicatorLayer { get; set; }

    public void SetInteractionVisual(GameObject hold, bool active, float maxDistance = -1f)
    {
        if (hold != null && hold.TryGetComponent(out MeshRenderer meshRenderer))
        {
            meshRenderer.GetPropertyBlock(HoldProperties);
            HoldProperties.SetInt("_IsBeingInteracted", active ? 1 : 0);
            if (maxDistance >= 0f)
            {
                HoldProperties.SetFloat("_InteractionColorMaxDistance", maxDistance);
            }
            meshRenderer.SetPropertyBlock(HoldProperties);
        }
    }

    public void SetHoldAlpha(Renderer renderer, float alpha)
    {
        renderer.GetPropertyBlock(HoldProperties);
        HoldProperties.SetFloat("_HoldAlpha", alpha);
        renderer.SetPropertyBlock(HoldProperties);
    }

    public void SetHalosVisible(bool showVirtualHalos)
    {
        foreach (GameObject circle in activeHighlightCircles)
        {
            if (circle != null)
            {
                circle.SetActive(showVirtualHalos);
            }
        }
    }

    public void SetUpRouteByHoldList(RouteDefinition route)
    {
        Dictionary<string, GameObject> holdsDictionary = configuror.holdsDictionary;
        configuror.ActiveRouteDefinition = route;
        List<string> holdsList = new(route.holds);
        ClearHighlightCircles();
        // Disable all holds
        configuror.activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (configuror.disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    SetHoldAlpha(renderer, configuror.inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in holdsList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!configuror.disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                SetHoldAlpha(renderer, configuror.activeHoldAlpha);
            }

            CoACD coACD = holdsDictionary[holdName].GetComponent<CoACD>();
            if (coACD != null)
            {
                holdsDictionary[holdName].GetComponent<CoACD>().enabled = true;
                MeshCollider[] meshColliders = holdsDictionary[holdName].GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = true;
                }
            }
            SphereCollider sphere = holdsDictionary[holdName].GetComponent<SphereCollider>();
            if (sphere != null)
            {
                holdsDictionary[holdName].GetComponent<SphereCollider>().enabled = true;
            }

            configuror.activeHoldsList.Add(holdsDictionary[holdName]);
        }
        SpawnRouteHalos(route);
    }

    public void PreviewAllHolds()
    {
        Dictionary<string, GameObject> holdsDictionary = configuror.holdsDictionary;
        // Disable all holds
        configuror.activeHoldsList = new List<GameObject>();
        foreach (var hold in holdsDictionary.Values)
        {
            if (configuror.disableInactiveHolds)
            {
                hold.SetActive(false);
            }
            else
            {
                Renderer renderer = hold.GetComponent<Renderer>();
                if (renderer != null)
                {
                    SetHoldAlpha(renderer, configuror.inactiveHoldAlpha);
                }
                EnsureHoldInteractionComponents(hold).enabled = false;
            }

            CoACD coACD = hold.GetComponent<CoACD>();
            if (coACD != null)
            {
                hold.GetComponent<CoACD>().enabled = false;
                MeshCollider[] meshColliders = hold.GetComponent<CoACD>().GetComponents<MeshCollider>();
                foreach (var collider in meshColliders)
                {
                    collider.enabled = false;
                }
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                hold.GetComponent<SphereCollider>().enabled = false;
            }
        }

        // Enable holds in the list
        foreach (var holdName in configuror.activeRouteHoldsNamesList)
        {
            if (!holdsDictionary.ContainsKey(holdName))
            {
                UnityEngine.Debug.LogError("Hold " + holdName + " not found in holds dictionary!");
                continue;
            }

            holdsDictionary[holdName].SetActive(true);
            if (!configuror.disableInactiveHolds)
            {
                EnsureHoldInteractionComponents(holdsDictionary[holdName]).enabled = true;
                Renderer renderer = holdsDictionary[holdName].GetComponent<Renderer>();
                SetHoldAlpha(renderer, configuror.activeHoldAlpha);
            }

            configuror.activeHoldsList.Add(holdsDictionary[holdName]);
        }
    }

    private void SpawnRouteHalos(RouteDefinition route)
    {
        GameObject highlightCirclePrefab = configuror.highlightCirclePrefab;
        GameObject holdsParentGameObject = configuror.holdsParentGameObject;
        if (highlightCirclePrefab == null || holdsParentGameObject == null || route?.holds == null)
        {
            return;
        }

        Transform board = holdsParentGameObject.transform;
        Transform boardSurface = board.parent?.Find("Main Surface") ?? board.parent?.Find("Plane");
        if (boardSurface == null)
        {
            Debug.LogError("MoonBoard main surface is unavailable; route halos cannot be projected.");
            return;
        }

        Vector3 boardNormal = boardSurface.up.normalized;
        Vector3 boardHorizontal = boardSurface.right.normalized;
        Vector3 boardVertical = RouteCuePolicy.GetBoardVertical(boardNormal);
        Vector3 boardPlanePoint = boardSurface.position;
        Transform viewer = configuror.centerEyeAnchor != null
            ? configuror.centerEyeAnchor.transform
            : configuror.mainCamera?.transform;
        if (viewer != null && Vector3.Dot(boardNormal, viewer.position - boardPlanePoint) < 0f)
        {
            boardNormal = -boardNormal;
        }

        bool hasRoles = route.start != null && route.start.Length > 0 &&
                        route.finish != null && route.finish.Length > 0;
        HashSet<string> starts = hasRoles
            ? new HashSet<string>(route.start, StringComparer.OrdinalIgnoreCase)
            : null;
        HashSet<string> finishes = hasRoles
            ? new HashSet<string>(route.finish, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string holdName in route.holds)
        {
            if (!configuror.holdsDictionary.TryGetValue(holdName, out GameObject hold) ||
                !TryGetCombinedRendererBounds(hold, out Bounds bounds))
            {
                continue;
            }

            float width = ProjectedBoundsDiameter(bounds, boardHorizontal);
            float height = ProjectedBoundsDiameter(bounds, boardVertical);
            float outerDiameter = Mathf.Clamp(Mathf.Max(width, height) * 1.35f, 0.14f, 0.30f);
            // Renderer bounds are scan-frame data and can be off-center. The hold transform is
            // the calibrated MoonBoard grid anchor, so it is the only valid point to project.
            Vector3 position = RouteCuePolicy.ProjectGridAnchorOntoBoard(
                hold.transform.position,
                boardPlanePoint,
                boardNormal,
                0.015f);
            Quaternion rotation = Quaternion.LookRotation(boardNormal, boardVertical);

            bool isStart = hasRoles && starts.Contains(holdName);
            bool isFinish = hasRoles && finishes.Contains(holdName);
            RouteCueRole role = isStart
                ? RouteCueRole.Start
                : isFinish
                    ? RouteCueRole.Finish
                    : RouteCueRole.Intermediate;
            RouteCueStyle style = RouteCuePolicy.GetStyle(role);
            CreateHaloRing(holdName, position, rotation, outerDiameter, style.Color, 0);
            if (style.RingCount == 2)
            {
                CreateHaloRing(holdName, position, rotation, outerDiameter * 0.65f, style.Color, 1);
            }
        }
        configuror.SetRouteCuePresentation(configuror.CurrentRouteCuePresentation);
    }

    private void CreateHaloRing(
        string holdName,
        Vector3 position,
        Quaternion rotation,
        float diameter,
        Color color,
        int ringIndex)
    {
        GameObject circle = UnityEngine.Object.Instantiate(configuror.highlightCirclePrefab);
        circle.name = holdName + (ringIndex == 0 ? " Route Halo" : " Route Halo Inner");
        circle.transform.SetPositionAndRotation(position, rotation);
        if (circle.TryGetComponent(out SpriteRenderer spriteRenderer))
        {
            spriteRenderer.color = color;
            spriteRenderer.sharedMaterial = GetHighlightCircleMaterial();
            spriteRenderer.sortingLayerName = "Default";
            spriteRenderer.sortingOrder = -100 + ringIndex;
            if (spriteRenderer.sprite != null)
            {
                Vector3 spriteSize = spriteRenderer.sprite.bounds.size;
                float sourceDiameter = Mathf.Max(spriteSize.x, spriteSize.y);
                circle.transform.localScale = Vector3.one * (diameter / sourceDiameter);
            }
        }
        if (IndicatorLayer >= 0)
        {
            SceneConfiguror.SetLayerRecursively(circle, IndicatorLayer);
        }
        circle.transform.SetParent(configuror.holdsParentGameObject.transform, true);
        activeHighlightCircles.Add(circle);
    }

    private Material GetHighlightCircleMaterial()
    {
        if (highlightCircleMaterial == null)
        {
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Sprites/Default");
            if (shader != null)
            {
                highlightCircleMaterial = new Material(shader) { name = "Route Halo Material" };
            }
        }
        return highlightCircleMaterial;
    }

    private static float ProjectedBoundsDiameter(Bounds bounds, Vector3 axis)
    {
        Vector3 extents = bounds.extents;
        return 2f * (Mathf.Abs(axis.x) * extents.x +
                     Mathf.Abs(axis.y) * extents.y +
                     Mathf.Abs(axis.z) * extents.z);
    }

    private static bool TryGetCombinedRendererBounds(GameObject target, out Bounds bounds)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = renderers[0].bounds;
        for (int index = 1; index < renderers.Length; index++)
        {
            bounds.Encapsulate(renderers[index].bounds);
        }
        return true;
    }

    private void ClearHighlightCircles()
    {
        foreach (var circle in activeHighlightCircles)
        {
            if (circle != null)
            {
                circle.SetActive(false);
                UnityEngine.Object.Destroy(circle);
            }
        }
        activeHighlightCircles.Clear();
    }

    public static XRGrabInteractable EnsureHoldInteractionComponents(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        if (sphere == null)
        {
            sphere = hold.AddComponent<SphereCollider>();
        }
        MeshRenderer renderer = hold.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            sphere.center = renderer.localBounds.center;
            sphere.radius = renderer.localBounds.extents.magnitude;
        }
        sphere.isTrigger = true;

        XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
        if (grab == null)
        {
            grab = hold.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
        }
        if (!grab.colliders.Contains(sphere))
        {
            grab.colliders.Add(sphere);
        }
        return grab;
    }

    public void Dispose()
    {
        if (highlightCircleMaterial != null)
        {
            UnityEngine.Object.Destroy(highlightCircleMaterial);
        }
    }
}
