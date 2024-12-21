using UnityEngine;

public class ObjectInfo : MonoBehaviour
{
    // This is the info that will be sent to the Main Camera
    public GameObject myself;

    void Start()
    {
        // Assign the GameObject the script is attached to to the 'myself' variable
        myself = gameObject;

        // You can now use the 'myself' GameObject reference
        Debug.Log("This GameObject is: " + myself.name);
    }

    // This method will be called to send the info to the camera
    public void SendInfoToCamera()
    {
        // Access the CameraInfoReceiver script attached to the Main Camera
        Camera.main.GetComponent<GhostHoldCreator>().SetObjectInfo(myself);
    }
}
