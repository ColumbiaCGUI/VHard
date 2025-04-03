using UnityEngine;
using UnityEngine.EventSystems;

public class MinimapClickHandler : MonoBehaviour, IPointerClickHandler
{
    public Camera minimapCamera;  // Assign the Minimap Camera in the Inspector
    public Transform cameraOffset;     // Assign the Main Camera in the Inspector
    public Transform climbingWall; // Assign the Climbing Wall in the Inspector

    public void OnPointerClick(PointerEventData eventData)
    {
        Debug.Log("Minimap clicked!");
        // Get the Minimap RectTransform
        RectTransform minimapRect = GetComponent<RectTransform>();

        // Convert screen click position to local minimap position
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(minimapRect, eventData.position, eventData.pressEventCamera, out localPoint);
        
        // Convert local minimap position to normalized coordinates (0 to 1)
        Vector2 normalizedPoint = Rect.PointToNormalized(minimapRect.rect, localPoint);

        // Convert minimap coordinates to world position on the climbing wall
        Vector3 worldPosition = ConvertMinimapToWorld(normalizedPoint);

        // Move the main camera to the new position
        cameraOffset.transform.position = new Vector3(worldPosition.x, worldPosition.y, cameraOffset.transform.position.z);
    }

    private Vector3 ConvertMinimapToWorld(Vector2 normalizedPoint)
    {
        Bounds wallBounds = climbingWall.GetComponent<Renderer>().bounds;

        float worldX = Mathf.Lerp(wallBounds.min.x, wallBounds.max.x, normalizedPoint.x);
        float worldY = Mathf.Lerp(wallBounds.min.y, wallBounds.max.y, normalizedPoint.y);

        return new Vector3(worldX, worldY, cameraOffset.transform.position.z);
    }
}
