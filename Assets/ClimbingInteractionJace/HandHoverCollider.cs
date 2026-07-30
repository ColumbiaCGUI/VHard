using System.Collections.Generic;
using UnityEngine;

public class HandHoverCollider : MonoBehaviour
{
    public int handIndex;
    public SceneConfiguror sceneConfiguror;
    private readonly HashSet<Collider> overlappingColliders = new();
    private int observedHoverContactEpoch = -1;

    private void OnTriggerEnter(Collider other)
    {
        TrackEnter(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TrackEnter(other);
    }

    private void OnTriggerExit(Collider other)
    {
        SynchronizeContactEpoch();
        if (other != null && overlappingColliders.Remove(other) && sceneConfiguror != null)
        {
            sceneConfiguror.HandHoverExit(handIndex, other.gameObject);
        }
    }

    private void OnDisable()
    {
        SynchronizeContactEpoch();
        if (sceneConfiguror != null)
        {
            foreach (Collider overlap in overlappingColliders)
            {
                if (overlap != null)
                {
                    sceneConfiguror.HandHoverExit(handIndex, overlap.gameObject);
                }
            }
        }
        overlappingColliders.Clear();
    }

    private void TrackEnter(Collider other)
    {
        SynchronizeContactEpoch();
        if (other != null && other.gameObject.CompareTag("ClimbingHold") &&
            sceneConfiguror != null && !sceneConfiguror.IsPanelInputSuppressed &&
            overlappingColliders.Add(other))
        {
            sceneConfiguror.HandHoverEnter(handIndex, other.gameObject);
        }
    }

    private void SynchronizeContactEpoch()
    {
        if (sceneConfiguror != null && observedHoverContactEpoch != sceneConfiguror.HoverContactEpoch)
        {
            overlappingColliders.Clear();
            observedHoverContactEpoch = sceneConfiguror.HoverContactEpoch;
        }
    }
}
