using UnityEngine;
using UnityEditor;

using Unity.XR.CoreUtils;
using System.Linq;
using System.Diagnostics;
using System.Reflection;

using Debug = UnityEngine.Debug;
using System.ComponentModel;
using System;
using System.Net;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class RemoveCoACDComponents
{
    [MenuItem("Custom/Holds/Remove CoACD Components")]
    static void RemoveCoACDFromAll()
    {
        String holdsGroupPath = "Environment/Moonboard/New_Decimated_Holds"; //CAROLINE 7/7/2026: hardcoded here. changed from "~/Holds" to "~/New_Decimated_Holds"
        Transform holdsGroup = GameObject.Find(holdsGroupPath)?.transform;

        if (holdsGroup == null)
        {
            UnityEngine.Debug.LogError("Could not find " + holdsGroupPath + " you entered! Check holdsGroupPath in script.");
            return;
        }

        int processedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        foreach (Transform child in holdsGroup)
        {

            // Skip non-holds
            if (child.name.Length < 2 || !char.IsDigit(child.name[1]) || child.GetComponent<CoACD>() == null) // Skip if CoACD component doesn't exist
            {
                UnityEngine.Debug.Log($"Skipped object: {child.name}");
                skippedCount++;
                continue;
            }

            try
            {
                Debug.Log($"Processing object: {child.name}");
                bool changed = RemovecoACD(child.gameObject);

                if (changed)
                {
                    processedCount++;
                }
                else
                {
                    skippedCount++;
                    UnityEngine.Debug.Log($"Skipped object with no CoACD/MeshCollider cleanup needed: {child.name}");
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Error processing object {child.name}: {e.Message}");
                errorCount++;
            }
        }

        UnityEngine.Debug.Log(
        $"Remove CoACD complete. Processed {processedCount} objects. " +
        $"Skipped {skippedCount}. Encountered {errorCount} errors."
    );

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene()
        );
    }

    static bool RemovecoACD(GameObject obj)
    {
        bool changed = false;

        // Clean XRGrabInteractable collider references first, before deleting components.
        XRGrabInteractable grabInteractable = obj.GetComponent<XRGrabInteractable>();
        if (grabInteractable != null)
        {
            int before = grabInteractable.colliders.Count;

            grabInteractable.colliders.RemoveAll(c =>
                c == null || c is MeshCollider
            );

            int removed = before - grabInteractable.colliders.Count;
            if (removed > 0)
            {
                changed = true;
                Debug.Log($"Removed {removed} MeshCollider/null references from XRGrabInteractable on {obj.name}");
            }
        }

        // Remove all generated MeshCollider components from this hold.
        MeshCollider[] meshColliders = obj.GetComponents<MeshCollider>();
        foreach (MeshCollider meshCollider in meshColliders)
        {
            UnityEngine.Object.DestroyImmediate(meshCollider, true);
            changed = true;
        }

        if (meshColliders.Length > 0)
        {
            Debug.Log($"Removed {meshColliders.Length} MeshCollider components from {obj.name}");
        }

        // Remove CoACD component itself.
        CoACD coACDComponent = obj.GetComponent<CoACD>();
        if (coACDComponent != null)
        {
            UnityEngine.Object.DestroyImmediate(coACDComponent, true);
            changed = true;
            Debug.Log($"Removed CoACD component from {obj.name}");
        }

        // Remove missing script components, such as broken CoACDColliderData references.
        int removedMissingScripts = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);
        if (removedMissingScripts > 0)
        {
            changed = true;
            Debug.Log($"Removed {removedMissingScripts} missing script components from {obj.name}");
        }

        return changed;
    }
}
