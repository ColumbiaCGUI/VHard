using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

public class SceneConfiguror : MonoBehaviour
{
    [Header("Hands")]
    public GameObject leftHand;
    public GameObject leftHandNearFarInteractor;
    public GameObject rightHand;
    public GameObject rightHandNearFarInteractor;
    public float hoverRadiusOverride = 0.1f;
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;

    [Header("Holds")]
    public GameObject holdContainer;
    [Tooltip("List of active holds in the scene -- ensure they are children of the Hold Container!")]
    public List<GameObject> activeHolds;

    void Start()
    {
        // Override hover radius
        foreach (var nearFarInteractor in new GameObject[] { leftHandNearFarInteractor, rightHandNearFarInteractor })
        {
            IInteractionCaster nearInteractionCaster = nearFarInteractor.GetComponent<NearFarInteractor>().nearInteractionCaster;
            if (nearInteractionCaster is SphereInteractionCaster sphereInteractionCaster)
            {
                sphereInteractionCaster.castRadius = hoverRadiusOverride;
            }
        }
    }

    void Update()
    {
        if (leftHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = leftHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);
            meshRenderer.material.SetVector("_InteractingHandPosition", leftHand.transform.position);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            MeshRenderer meshRenderer = rightHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);
            meshRenderer.material.SetVector("_InteractingHandPosition", leftHand.transform.position);
        }
    }

    public void LeftHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(leftHand, args);
    }
    public void RightHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(rightHand, args);
    }
    void HandHoverEnter(GameObject hand, HoverEnterEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);

            if (hand == leftHand)
            {
                leftHandInteractingClimbingHold = hoveredGameObject;
            }
            else if (hand == rightHand)
            {
                rightHandInteractingClimbingHold = hoveredGameObject;
            }
        }
        else
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void LeftHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(leftHand, args);
    }
    public void RightHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(rightHand, args);
    }
    void HandHoverExit(GameObject hand, HoverExitEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 0);

            if (hand == leftHand)
            {
                leftHandInteractingClimbingHold = null;
            }
            else if (hand == rightHand)
            {
                rightHandInteractingClimbingHold = null;
            }
        }
        else
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with GameObject " + hoveredGameObject.name);
        }
    }
}
