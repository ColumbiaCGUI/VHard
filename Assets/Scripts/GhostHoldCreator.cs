using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine;

public class GhostHoldCreator : MonoBehaviour
{
    public Canvas canvas; // Reference to the Canvas
    public GameObject holdPrefab; // Reference to the hold prefab (can be your existing climbing hold)

    public GameObject childObject;
    
    void Start()
    {
        // Example: Create a ghost hold at a position in the world space
        Vector3 canvasWorldPosition = canvas.transform.position;        
        CreateGhostHoldInCanvas(canvasWorldPosition);
    }

    void CreateGhostHoldInCanvas(Vector3 worldPosition)
    {


        Renderer holdRenderer = holdPrefab.GetComponent<Renderer>();
        Vector3 pivotOffset = Vector3.zero;

        Renderer renderer = childObject.GetComponent<Renderer>();
        Material material = renderer.material;
        material.SetFloat("_HoldAlpha", 1);
        childObject.GetComponent<XRGrabInteractable>().enabled = true;        

        if (holdRenderer != null)
        {
            // Get the offset between the pivot and the center of the object
            pivotOffset = holdRenderer.bounds.center - holdPrefab.transform.position;
        }
        // Convert the world position to the Canvas's local space
        Vector3 viewportPosition = Camera.main.WorldToViewportPoint(worldPosition); // Get viewport position
        Vector3 localPosition = canvas.transform.InverseTransformPoint(viewportPosition); // Convert to local position in canvas

        // Instantiate the ghost hold (the existing climbing hold) in the Canvas at the calculated position
        GameObject ghostHold = Instantiate(holdPrefab, localPosition, Quaternion.identity);
        
        // Set the parent to the Canvas so the ghost hold is part of the UI (world space)
        ghostHold.transform.SetParent(canvas.transform);

    }
}
