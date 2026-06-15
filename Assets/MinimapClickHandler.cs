using UnityEngine;
using UnityEngine.EventSystems;

public class MinimapClickHandler : MonoBehaviour, IPointerClickHandler, IScrollHandler
{
    [Header("References")]
    public Camera minimapCamera;   // Your orthographic minimap camera
    public Transform cameraOffset; // The parent/pivot of your main camera
    public Transform climbingWall; // The wall you’re clicking on

    [Header("Zoom Settings")]
    [Tooltip("Starting distance from the wall")]
    public float cameraDistance = 5f;
    public float minDistance = 1f;
    public float maxDistance = 10f;
    [Tooltip("How fast scroll wheel zooms")]
    public float zoomSpeed = 5f;

    // Internal state for the current “look‐at” point & normal
    private Vector3 _lastHitPoint;
    private Vector3 _lastWallNormal;

    void Start()
    {
        // Pick the center of the minimap as our initial click
        Vector2 initialUV = new Vector2(0.5f, 0.5f);

        // If you’ve got mirroring enabled, don’t forget to flip:
        initialUV.x = 1f - initialUV.x;
        // initialUV.y = 1f - initialUV.y; // if you also flipped Y

        // Compute world hit & normal exactly like OnPointerClick does
        _lastHitPoint   = MinimapUVToWallPoint(initialUV);
        _lastWallNormal = climbingWall.forward;

        // Snap camera into position
        RepositionCamera();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // 1) Get UV coords on your minimap
        RectTransform rt = GetComponent<RectTransform>();
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rt,
            eventData.position,
            eventData.pressEventCamera,
            out localPoint
        );
        Vector2 uv = Rect.PointToNormalized(rt.rect, localPoint);

        // 2) Fix mirroring if needed
        uv.x = 1f - uv.x;
        // uv.y = 1f - uv.y;

        // 3) Compute hit & normal
        _lastHitPoint   = MinimapUVToWallPoint(uv);
        _lastWallNormal = climbingWall.forward;

        // 4) Snap camera into place
        RepositionCamera();
    }

    public void OnScroll(PointerEventData eventData)
    {
        // Wheel scroll: zoom in/out
        cameraDistance = Mathf.Clamp(
            cameraDistance - eventData.scrollDelta.y * zoomSpeed * Time.unscaledDeltaTime,
            minDistance,
            maxDistance
        );
        RepositionCamera();
    }

    private void RepositionCamera()
    {
        // Move the camera pivot out along the wall’s normal
        cameraOffset.position = _lastHitPoint + _lastWallNormal * cameraDistance;
        // Always look directly at the hit point
        cameraOffset.LookAt(_lastHitPoint);
    }

    private Vector3 MinimapUVToWallPoint(Vector2 uv)
    {
        // Ensure the wall has a MeshRenderer for bounds
        MeshRenderer mr = climbingWall.GetComponent<MeshRenderer>();
        if (mr == null)
        {
            Debug.LogError("ClimbingWall needs a MeshRenderer!");
            return Vector3.zero;
        }

        // Convert world‐space bounds into local‐space size
        Vector3 worldSize = mr.bounds.size;
        Vector3 localSize = new Vector3(
            worldSize.x / climbingWall.lossyScale.x,
            worldSize.y / climbingWall.lossyScale.y,
            worldSize.z / climbingWall.lossyScale.z
        );

        // Build a local‐space point on the wall’s plane (Z=0)
        float halfW = localSize.x * 0.5f;
        float halfH = localSize.y * 0.5f;
        Vector3 localPoint = new Vector3(
            Mathf.Lerp(-halfW, halfW, uv.x),
            Mathf.Lerp(-halfH, halfH, uv.y),
            0f
        );

        // Transform it into world‐space
        return climbingWall.TransformPoint(localPoint);
    }
}
