
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

using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

public class GhostHoldCreator : MonoBehaviour
{
    public Canvas canvas; 
    public GameObject childObject; 
    public XRController rightController; // Using right controller to allow users to rotate hold

    private Vector3 lastHandPosition;
    private bool isRotating = false; // Flag to check if rotation is active
    private InputDevice rightHandDevice;

    public void Start()
    {
        // Get the right hand input device
        var inputDevices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, inputDevices);
        if (inputDevices.Count > 0)
        {
            rightHandDevice = inputDevices[0];
        }

        UnityEngine.Debug.Log("started");
    }

    public void CreateGhostHoldInCanvas()
    {
        UnityEngine.Debug.Log("creating ghost holds");
        Vector3 canvasWorldPosition = canvas.transform.position;

        // Set up ghost hold
        Renderer renderer = childObject.GetComponent<Renderer>();
        Material material = renderer.material;
        material.SetFloat("_HoldAlpha", 1);
        childObject.GetComponent<XRGrabInteractable>().enabled = true;

        GameObject ghostHold = Instantiate(childObject);

        // Rotate the ghostHold by 40 degrees before calculating bounds.center
        ghostHold.transform.Rotate(0, 40, 0, Space.World); // Rotate 40 degrees in world space

        // Calculate the centroid (center of bounds)
        Vector3 meshCenter = ghostHold.GetComponent<Renderer>().bounds.center;
        Vector3 pivot = ghostHold.transform.position;
        Vector3 offset = pivot - meshCenter;

        // Set the parent to the Canvas so the ghost hold is part of the UI (world space)
        ghostHold.transform.SetParent(canvas.transform);
        ghostHold.transform.position = offset + canvas.transform.position;
        ghostHold.transform.position = canvasWorldPosition + offset;

        UnityEngine.Debug.Log("Position before rotation: " + ghostHold.transform.position);
        UnityEngine.Debug.Log("Rotation after 40 degree rotation: " + ghostHold.transform.rotation.eulerAngles);

        // Get the right hand position from the InputDevice
        if (rightHandDevice.isValid)
        {
            Vector3 handPosition;
            if (rightHandDevice.TryGetFeatureValue(CommonUsages.devicePosition, out handPosition))
            {
                // If the hand position is valid, track its movement
                Vector3 deltaPosition = handPosition - lastHandPosition;

                // Start rotating if the hand is moving
                if (isRotating)
                {
                    RotateAroundCentroid(ghostHold, deltaPosition);
                }

                lastHandPosition = handPosition;
            }

            // Optionally, set the flag for rotating based on a condition (e.g., gesture detection)
            if (rightHandDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool triggerPressed) && triggerPressed)
            {
                isRotating = true;
            }
            else
            {
                isRotating = false;
            }
        }

        UnityEngine.Debug.Log("Position after rotation: " + ghostHold.transform.position);
    }

    // Function to rotate the object around its centroid based on hand movement
    private void RotateAroundCentroid(GameObject ghostHold, Vector3 delta)
    {
        // Calculate the centroid of the object
        Vector3 centroid = ghostHold.GetComponent<Renderer>().bounds.center;

        // Define how fast the rotation should happen based on the hand's movement
        float rotationSpeed = 1.0f; // You can adjust this value for speed
        float rotationAngle = delta.x * rotationSpeed; // Rotation along X-axis based on hand movement

        // Rotate the ghost hold around its centroid
        ghostHold.transform.RotateAround(centroid, Vector3.up, rotationAngle);

        UnityEngine.Debug.Log("Rotating around centroid by: " + rotationAngle);
    }
}