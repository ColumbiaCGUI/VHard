using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI; // For UI elements

public class GhostHoldManager : MonoBehaviour
{
    [Header("Ghost Hold Settings")]
    public GameObject holdsParentGameObject; // The parent object containing all climbing holds
    public Canvas canvasUI; // The Canvas where ghost holds will be displayed (World Space)
    public GameObject holdPrefab; // The hold prefab (your existing climbing hold) for ghost holds in the Canvas
    public Dictionary<string, GameObject> holdsDictionary; // Hold name to GameObject dictionary
    public bool ghostHoldMode = false; // Whether ghost mode is active
    public float ghostHoldAlpha = 0.3f; // Transparency of ghost holds when active
    public float ghostHoldDistance = 5f; // Distance to move holds away from the climbing wall for ghost mode

    private List<GameObject> activeGhostHolds = new List<GameObject>(); // List of active ghost holds
    private GameObject selectedHoldUI; // The UI copy of the selected hold

    void Start()
    {
        // Initialize holdsDictionary
        holdsDictionary = new Dictionary<string, GameObject>();
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0]; // Get the hold's name (before any extensions like .001)
            holdsDictionary[holdName] = child.gameObject;
        }
    }

    void Update()
    {
        // Raycast to detect if the player clicks on a hold
        HandleHoldSelection();

        // Toggle ghost hold mode (for example, could be linked to input)
        if (Input.GetKeyDown(KeyCode.G)) // Example: press 'G' to toggle ghost mode
        {
            ghostHoldMode = !ghostHoldMode;
            SetGhostHoldMode(ghostHoldMode);
        }
    }

    // Handle selection of a climbing hold
    void HandleHoldSelection()
    {
        if (Input.GetMouseButtonDown(0)) // Left click
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit))
            {
                // Check if the clicked object is a climbing hold
                if (holdsDictionary.ContainsValue(hit.collider.gameObject))
                {
                    string holdName = hit.collider.gameObject.name.Split('.')[0];
                    GameObject selectedHold = holdsDictionary[holdName];
                    CreateHoldUI(selectedHold);
                }
            }
        }
    }

    // Create a UI copy of the selected hold
    void CreateHoldUI(GameObject selectedHold)
    {
        // If a previous UI copy exists, destroy it
        if (selectedHoldUI != null)
        {
            Destroy(selectedHoldUI);
        }

        // Instantiate a copy of the selected hold as a UI element
        selectedHoldUI = Instantiate(selectedHold);
        selectedHoldUI.transform.SetParent(canvasUI.transform, false); // Set it under the Canvas

        // Position it closer to the user (in the canvas)
        selectedHoldUI.transform.localPosition = Vector3.zero; // Center in the canvas (adjust as needed)

        // Optionally, adjust its scale to fit the UI (UI elements are often scaled differently)
        selectedHoldUI.transform.localScale = Vector3.one;

        // Make the UI copy semi-transparent
        Renderer holdRenderer = selectedHoldUI.GetComponent<Renderer>();
        if (holdRenderer != null)
        {
            Material material = holdRenderer.material;
            if (material.HasProperty("_Color"))
            {
                Color color = material.color;
                color.a = ghostHoldAlpha;
                material.color = color;
            }
        }
    }

    // Method to toggle ghost hold mode
    void SetGhostHoldMode(bool isActive)
    {
        if (isActive)
        {
            // Clear any previous ghost holds before creating new ones
            foreach (GameObject ghostHold in activeGhostHolds)
            {
                Destroy(ghostHold);
            }
            activeGhostHolds.Clear();

            // Show ghost holds in the 3D world (near the climbing wall) AND in the Canvas (World Space)
            foreach (var holdEntry in holdsDictionary)
            {
                GameObject originalHold = holdEntry.Value;

                // Create a ghost hold in the 3D world (near the climbing wall)
                GameObject ghostHold = Instantiate(originalHold, originalHold.transform.position + Vector3.up * ghostHoldDistance, originalHold.transform.rotation);

                // Add it to the active ghost holds list
                activeGhostHolds.Add(ghostHold);

                // Optionally adjust scale or position to fit within the Canvas
                ghostHold.transform.localScale = originalHold.transform.localScale; // Adjust size if necessary

                // Now create the same ghost hold in the Canvas as a UI element (3D world-space UI)
                CreateGhostHoldInCanvas(ghostHold);
            }
        }
        else
        {
            // Destroy ghost holds in the world and canvas
            foreach (GameObject ghostHold in activeGhostHolds)
            {
                Destroy(ghostHold);
            }
            activeGhostHolds.Clear();
        }
    }

    // Method to create ghost holds in the Canvas (as a 3D object in world space)
    void CreateGhostHoldInCanvas(GameObject worldHold)
    {
        // Convert the world position of the 3D hold to the Canvas's local space
        Vector3 worldPosition = worldHold.transform.position;
        Vector3 viewportPosition = Camera.main.WorldToViewportPoint(worldPosition); // Get viewport position
        Vector3 localPosition = canvasUI.transform.InverseTransformPoint(worldPosition); // Convert to local position in canvas

        // Instantiate the ghost hold in the Canvas (as a 3D object in world space)
        GameObject ghostHoldInCanvas = Instantiate(worldHold, localPosition, Quaternion.identity);
        ghostHoldInCanvas.transform.SetParent(canvasUI.transform, false); // Set it under the Canvas

        // Optionally adjust scale for better UI appearance
        ghostHoldInCanvas.transform.localScale = worldHold.transform.localScale;

        // Optional: Adjust transparency of the hold
        Renderer holdRenderer = ghostHoldInCanvas.GetComponent<Renderer>();
        if (holdRenderer != null)
        {
            Material material = holdRenderer.material;
            if (material.HasProperty("_Color"))
            {
                Color color = material.color;
                color.a = ghostHoldAlpha; // Set transparency
                material.color = color;
            }
        }
    }

    // Optional: You could add a method to hide/show specific holds based on some logic or interaction
    public void SetGhostHoldAlpha(float alpha)
    {
        foreach (var ghostHold in activeGhostHolds)
        {
            Renderer ghostRenderer = ghostHold.GetComponent<Renderer>();
            if (ghostRenderer != null)
            {
                Material material = ghostRenderer.material;
                if (material.HasProperty("_Color"))
                {
                    Color color = material.color;
                    color.a = alpha;
                    material.color = color;
                }
            }
        }
    }
}
