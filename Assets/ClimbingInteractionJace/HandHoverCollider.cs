using UnityEngine;

public class HandHoverCollider : MonoBehaviour
{
    public int handIndex;
    public SceneConfiguror sceneConfiguror;

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.CompareTag("ClimbingHold"))
        {
            sceneConfiguror.HandHoverEnter(handIndex, other.gameObject);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.CompareTag("ClimbingHold"))
        {
            sceneConfiguror.HandHoverExit(handIndex, other.gameObject);
        }
    }
}
