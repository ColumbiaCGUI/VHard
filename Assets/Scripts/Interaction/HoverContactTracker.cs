using System.Collections.Generic;
using UnityEngine;

/// <summary>Tracks which hold each hand is hovering. Hover colliders overlap heavily on a dense
/// board, so per-hand contacts are resolved newest-first and the facade's interacting-hold fields
/// only change when the resolved winner changes.</summary>
public sealed class HoverContactTracker
{
    private readonly SceneConfiguror configuror;
    private readonly HoldVisualsController holdVisuals;
    private readonly OverlapContactResolver<GameObject> leftHoverContacts = new();
    private readonly OverlapContactResolver<GameObject> rightHoverContacts = new();

    public HoverContactTracker(SceneConfiguror configuror, HoldVisualsController holdVisuals)
    {
        this.configuror = configuror;
        this.holdVisuals = holdVisuals;
    }

    public void HandHoverEnter(int hand, GameObject hoveredGameObject)
    {
        GameObject hold = ResolveEligibleHoverHold(hoveredGameObject);
        if (hold == null)
        {
            return;
        }

        GetHoverResolver(hand)?.Enter(hold);
        RefreshHandHoverTarget(hand);
    }

    public void HandHoverExit(int hand, GameObject hoveredGameObject)
    {
        GameObject hold = ResolveCanonicalHoverHold(hoveredGameObject);
        if (hold == null)
        {
            return;
        }

        GetHoverResolver(hand)?.Exit(hold);
        RefreshHandHoverTarget(hand);
    }

    private OverlapContactResolver<GameObject> GetHoverResolver(int hand)
    {
        if (hand == 0)
        {
            return leftHoverContacts;
        }
        if (hand == 1)
        {
            return rightHoverContacts;
        }
        Debug.LogError("Hand index " + hand + " not found.");
        return null;
    }

    private GameObject ResolveEligibleHoverHold(GameObject candidate)
    {
        GameObject hold = ResolveCanonicalHoverHold(candidate);
        if (hold == null || configuror.gameMode == GameMode.Basic)
        {
            return null;
        }
        bool isGhost = configuror.IsGhostHold(hold);
        if ((configuror.gameMode == GameMode.Ghost && !isGhost) ||
            (configuror.gameMode == GameMode.Grip && isGhost))
        {
            return null;
        }
        return isGhost || configuror.IsActiveRouteHold(hold) ? hold : null;
    }

    private GameObject ResolveCanonicalHoverHold(GameObject candidate)
    {
        if (candidate == null)
        {
            return null;
        }
        // Several proxies can be detached at once, so a hovered ghost child has to resolve to the
        // proxy it actually belongs to; the most recently summoned one is not it.
        GameObject ghostRoot = configuror.ghostHoldController != null
            ? configuror.ghostHoldController.GetGhostRoot(candidate)
            : null;
        if (ghostRoot != null)
        {
            return ghostRoot;
        }

        GameObject activeHold = configuror.GetActiveRouteHold(candidate);
        if (activeHold != null)
        {
            return activeHold;
        }
        for (Transform current = candidate.transform; current != null; current = current.parent)
        {
            if (configuror.holdsParentGameObject != null &&
                current.parent == configuror.holdsParentGameObject.transform)
            {
                return current.gameObject;
            }
        }
        return null;
    }

    public void RefreshHandHoverTarget(int hand, string exitDetails = "")
    {
        OverlapContactResolver<GameObject> resolver = GetHoverResolver(hand);
        if (resolver == null)
        {
            return;
        }
        GameObject previous = hand == 0
            ? configuror.leftHandInteractingClimbingHold
            : configuror.rightHandInteractingClimbingHold;
        GameObject current = resolver.Current;
        if (previous == current)
        {
            return;
        }

        Hand handSide = hand == 0 ? Hand.Left : Hand.Right;
        configuror.InvalidateGripAcquisitionSample(handSide);
        configuror.NotifyGripTargetDiscontinuity(handSide);

        GameObject otherHandTarget = hand == 0
            ? configuror.rightHandInteractingClimbingHold
            : configuror.leftHandInteractingClimbingHold;
        if (previous != null)
        {
            configuror.actionRecorder?.Record(
                "HoverExit",
                hand == 0 ? "Left" : "Right",
                previous,
                exitDetails);
            if (otherHandTarget != previous)
            {
                holdVisuals.SetInteractionVisual(previous, false);
            }
        }

        if (hand == 0)
        {
            configuror.leftHandInteractingClimbingHold = current;
        }
        else
        {
            configuror.rightHandInteractingClimbingHold = current;
        }
        configuror.ResetHandDistances(hand);

        if (current != null)
        {
            configuror.actionRecorder?.Record("HoverEnter", hand == 0 ? "Left" : "Right", current);
            holdVisuals.SetInteractionVisual(current, configuror.IsLegacyInteractionShaderActive);
        }
    }

    public void Remove(GameObject hold)
    {
        leftHoverContacts.Remove(hold);
        rightHoverContacts.Remove(hold);
    }

    public void Clear()
    {
        leftHoverContacts.Clear();
        rightHoverContacts.Clear();
    }
}
