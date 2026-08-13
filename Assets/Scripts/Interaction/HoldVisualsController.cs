using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>Drives hold interaction state, per-hold alpha, and active-route membership.</summary>
public sealed class HoldVisualsController
{
    private readonly SceneConfiguror configuror;
    private readonly GripAffordanceOutlinePresenter gripAffordances = new();
    private RouteRoleRingPresenter roleRings;
    private MaterialPropertyBlock holdProperties;
    private GameObject leftLatchedHold;
    private GameObject rightLatchedHold;
    private bool gripAffordancesVisible = true;
    private MaterialPropertyBlock HoldProperties => holdProperties ??= new MaterialPropertyBlock();

    public HoldVisualsController(SceneConfiguror configuror)
    {
        this.configuror = configuror;
    }

    /// <summary>Role rings render in B and C only; Condition A, practice, alignment and the
    /// estimation battery leave them hidden, so B and C stay symmetric by construction.</summary>
    public void SetRoleRingsVisible(bool visible)
    {
        if (!visible)
        {
            roleRings?.SetVisible(false);
            return;
        }

        RouteRoleRingPresenter presenter = RoleRings;
        presenter.SetVisible(true);
        if (presenter.RingCount == 0)
        {
            presenter.Rebuild(configuror.activeHoldsList);
        }
    }

    public void ClearRoleRings()
    {
        roleRings?.Clear();
    }

    private RouteRoleRingPresenter RoleRings
    {
        get
        {
            if (roleRings != null)
            {
                return roleRings;
            }

            GameObject boardEnvironment = configuror.moonBoardEnv;
            if (boardEnvironment == null)
            {
                throw new InvalidOperationException("Route role rings require the Moonboard reference.");
            }
            Transform mainSurface = boardEnvironment.transform.Find("Main Surface");
            if (mainSurface == null)
            {
                throw new InvalidOperationException(
                    "Route role rings require the Moonboard's Main Surface for the board plane.");
            }
            roleRings = new RouteRoleRingPresenter(
                boardEnvironment.transform,
                mainSurface,
                configuror.GetRouteCueStyle);
            return roleRings;
        }
    }

    /// <summary>Records which hold each hand has latched, from the same pipeline callback that
    /// drives grip logging, so the rim reports engagement without restating the latch rule.</summary>
    public void SetGripLatchedHold(Hand hand, GameObject hold, bool latched)
    {
        if (hand == Hand.Left)
        {
            leftLatchedHold = latched ? hold : null;
        }
        else
        {
            rightLatchedHold = latched ? hold : null;
        }
    }

    public void SetGripAffordancesVisible(bool visible)
    {
        gripAffordancesVisible = visible;
        if (!visible)
        {
            gripAffordances.HideAll();
        }
    }

    public void ClearGripAffordances()
    {
        leftLatchedHold = null;
        rightLatchedHold = null;
        gripAffordances.Clear();
    }

    /// <summary>Per-frame rim update. Identical in B (wall holds) and C (ghost copy): both read the
    /// same per-hand contact mask, grip score and latch, so the cue cannot differ by condition.
    /// Condition A never reaches it, running in Basic mode with the twin disabled.</summary>
    public void UpdateGripAffordances()
    {
        if (!gripAffordancesVisible || configuror.IsGripFeedbackDegraded ||
            (configuror.gameMode != GameMode.Grip && configuror.gameMode != GameMode.Ghost))
        {
            gripAffordances.HideAll();
            return;
        }

        ResolveHandAffordance(Hand.Left, out GameObject leftHold, out GripAffordance leftAffordance);
        ResolveHandAffordance(Hand.Right, out GameObject rightHold, out GripAffordance rightAffordance);
        gripAffordances.Apply(leftHold, leftAffordance, rightHold, rightAffordance);
    }

    /// <summary>A latched hand keeps its cue on the hold it is holding; an unlatched one reports the
    /// hold the grip pipeline measured this epoch, so the mask, the score and the outlined hold
    /// always describe the same object.</summary>
    private void ResolveHandAffordance(Hand hand, out GameObject hold, out GripAffordance affordance)
    {
        bool isLeft = hand == Hand.Left;
        GameObject latchedHold = isLeft ? leftLatchedHold : rightLatchedHold;
        hold = latchedHold != null
            ? latchedHold
            : isLeft
                ? configuror.leftHandInteractingClimbingHold
                : configuror.rightHandInteractingClimbingHold;
        if (hold == null)
        {
            affordance = default;
            return;
        }

        bool engaged = latchedHold == hold &&
                       (isLeft ? configuror.leftHandIsGripping : configuror.rightHandIsGripping);
        affordance = GripAffordancePolicy.Resolve(
            engaged,
            isLeft ? configuror.leftFingerContactMask : configuror.rightFingerContactMask,
            isLeft ? configuror.leftHandGripScore : configuror.rightHandGripScore);
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

        RoleRings.Rebuild(configuror.activeHoldsList);
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

        ClearRoleRings();
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
