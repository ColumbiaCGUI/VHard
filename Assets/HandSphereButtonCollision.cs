using UnityEngine;
using UnityEngine.UI;

public class HandSphereButtonCollision : MonoBehaviour
{
    public Button targetButton;  // Reference to the button
    private bool isHandPressed = false; // Track the button press state

    void OnTriggerEnter(Collider other)
    {
        // Check if the hand sphere collides with the button (using tag or name)
        if (other.CompareTag("Button"))
        {
            if (!isHandPressed)
            {
                Debug.Log("Hand pressed the button!");
                targetButton.onClick.Invoke();  // Simulate button press
                isHandPressed = true; // Mark the button as pressed
            }
        }
    }

    void OnTriggerExit(Collider other)
    {
        // Check if the hand sphere leaves the button
        if (other.CompareTag("Button"))
        {
            if (isHandPressed)
            {
                Debug.Log("Hand released the button!");
                // You can add logic to simulate button release here, if needed.
                isHandPressed = false; // Mark the button as released
            }
        }
    }
}
