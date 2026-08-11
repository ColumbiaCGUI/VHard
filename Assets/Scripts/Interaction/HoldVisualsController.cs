using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>Drives hold interaction state, per-hold alpha, and active-route membership.</summary>
public sealed class HoldVisualsController
{
    private readonly SceneConfiguror configuror;
    private MaterialPropertyBlock holdProperties;
    private MaterialPropertyBlock HoldProperties => holdProperties ??= new MaterialPropertyBlock();

    public HoldVisualsController(SceneConfiguror configuror)
    {
        this.configuror = configuror;
    }

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

    public void SetUpRouteByHoldList(RouteDefinition route)
    {
        Dictionary<string, GameObject> holdsDictionary = configuror.holdsDictionary;
        configuror.ActiveRouteDefinition = route;
        List<string> holdsList = new(route.holds);
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

}
