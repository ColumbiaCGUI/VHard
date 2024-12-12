
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using System.Collections;
using System.Collections.Generic;


public class GhostHoldCreator : MonoBehaviour
{
    public Canvas canvas; // Reference to the Canvas
    public GameObject childObject;
    
    public void Start()
    {
        // Example: Create a ghost hold at a position in the world space
        UnityEngine.Debug.Log("started");

    }

    public void CreateGhostHoldInCanvas()
    {


        UnityEngine.Debug.Log("creating ghost holds");
        Vector3 canvasWorldPosition = canvas.transform.position;   


        Renderer renderer = childObject.GetComponent<Renderer>();
        Material material = renderer.material;
        material.SetFloat("_HoldAlpha", 1);
        childObject.GetComponent<XRGrabInteractable>().enabled = true;

        GameObject ghostHold = Instantiate(childObject);

        // TODO: rotate 40 degrees (before calculating bounds.center)
        Vector3 meshCenter = ghostHold.GetComponent<Renderer>().bounds.center;
        Vector3 pivot = ghostHold.transform.position;
        Vector3 offset = pivot - meshCenter;

        // Set the parent to the Canvas so the ghost hold is part of the UI (world space)
        ghostHold.transform.SetParent(canvas.transform);
        ghostHold.transform.position = offset + canvas.transform.position;

    }
}
