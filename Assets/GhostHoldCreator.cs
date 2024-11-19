using UnityEngine;

public class GhostHoldCreator : MonoBehaviour
{
    public Canvas canvas; // Reference to the Canvas
    public GameObject holdPrefab; // Reference to the hold prefab (can be your existing climbing hold)
    
    void Start()
    {
        // Example: Create a ghost hold at a position in the world space
        Vector3 worldPosition = new Vector3(0f, 1f, 5f); // Example world position
        CreateGhostHoldInCanvas(worldPosition);
    }

    void CreateGhostHoldInCanvas(Vector3 worldPosition)
    {
        // Convert the world position to the Canvas's local space
        Vector3 viewportPosition = Camera.main.WorldToViewportPoint(worldPosition); // Get viewport position
        Vector3 localPosition = canvas.transform.InverseTransformPoint(viewportPosition); // Convert to local position in canvas

        // Instantiate the ghost hold (the existing climbing hold) in the Canvas at the calculated position
        GameObject ghostHold = Instantiate(holdPrefab, localPosition, Quaternion.identity);
        
        // Set the parent to the Canvas so the ghost hold is part of the UI (world space)
        ghostHold.transform.SetParent(canvas.transform);
        
        // Optionally adjust scale for better UI appearance
        ghostHold.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f); // Adjust scale as needed
    }
}
