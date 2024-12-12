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

public class ProcessMoonboardObjects : MonoBehaviour
{
    [MenuItem("Custom/Process Moonboard Objects")]
    static void ProcessAllObjects()
    {
        // Load shader
        // Hardcoded path
        string materialPath = "Assets/ClimbingInteractionJace/InteractableHold.mat";

        // Load the material using AssetDatabase
        Material materialToApply = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (materialToApply == null)
        {
            Debug.LogError("Material not found at the specified path: " + materialPath);
            return;
        }

        // Find holds
        Transform holdsGroup = GameObject.Find("Environment/Moonboard/Holds")?.transform;
        if (holdsGroup == null)
        {
            UnityEngine.Debug.LogError("Could not find Moonboard/Environment/Moonboard/Holds group!");
            return;
        }

        int processedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;
        foreach (Transform child in holdsGroup)
        {

            // Skip non-holds
            if (child.name.Length < 2 || !char.IsDigit(child.name[1]))
            {
                UnityEngine.Debug.Log($"Skipped object: {child.name}");
                skippedCount++;
                continue;
            }

            try
            {
                ProcessObject(child.gameObject, materialToApply);
                processedCount++;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Error processing object {child.name}: {e.Message}");
                errorCount++;
            }
        }

        UnityEngine.Debug.Log($"Processing complete. Processed {processedCount} objects. Encountered {errorCount} errors.");

        // Save the changes
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    [MenuItem("Custom/Add CoACD to remaining meshes")]
    static void AddcoACDToAll()
    {
        Transform holdsGroup = GameObject.Find("Environment/Moonboard/Holds")?.transform;

        if (holdsGroup == null)
        {
            UnityEngine.Debug.LogError("Could not find Moonboard/Environment/Moonboard/Holds group!");
            return;
        }

        int processedCount = 0;
        int skippedCount = 0;
        int errorCount = 0;

        foreach (Transform child in holdsGroup)
        {

            // Skip non-holds
            if (child.name.Length < 2 || !char.IsDigit(child.name[1]) || child.GetComponent<CoACD>() != null) // Skip if CoACD component already exists
            {
                UnityEngine.Debug.Log($"Skipped object: {child.name}");
                skippedCount++;
                continue;
            }

            try
            {
                Debug.Log($"Processing object: {child.name}");
                AddcoACD(child.gameObject);
                processedCount++;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"Error processing object {child.name}: {e.Message}");
                errorCount++;
            }
        }

        UnityEngine.Debug.Log($"Processing complete. Processed {processedCount} objects. Encountered {errorCount} errors.");

        // Save the changes
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    static void AddcoACD(GameObject obj)
    {
        // Attach Co ACD script and calculate colliders
        CoACD coACD = obj.AddComponent<CoACD>();
        if (coACD != null)
        {
            // Set any necessary parameters
            coACD.parameters = CoACD.Parameters.Init(); // Use default parameters or customize as needed
            coACD.target = obj.GetComponent<MeshFilter>();
            coACD.hideColliders = true; // or false, depending on your preference
            coACD.isTrigger = false; // or true, depending on your needs

            // Use reflection to call the CalculateColliders method
            MethodInfo calculateCollidersMethod = typeof(CoACD).GetMethod("CalculateColliders", BindingFlags.NonPublic | BindingFlags.Instance);
            if (calculateCollidersMethod != null)
            {
                calculateCollidersMethod.Invoke(coACD, null);
            }
            else
            {
                Debug.LogWarning($"CalculateColliders method not found on CoACD component for {obj.name}");
            }
        }
        else
        {
            Debug.LogWarning($"CoACD component could not be added to {obj.name}");
        }
    }

    static void ProcessObject(GameObject obj, Material materialToApply)
    {
        Undo.RecordObject(obj, "Process Moonboard Object");

        // Add custom shader to material
        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer != null)
        {
            // Apply the material
            renderer.sharedMaterial = materialToApply;
        }

        UnityEngine.Debug.Log($"Processed object: {obj.name}");
    }

    static UnityEngine.Component AddComponentByName(GameObject obj, string fullTypeName)
    {
        Type type = Type.GetType(fullTypeName, false, true);
        if (type != null)
        {
            return Undo.AddComponent(obj, type);
        }
        UnityEngine.Debug.LogWarning($"Could not find type {fullTypeName}");
        return null;
    }

    static void SetFieldValue(object obj, string fieldName, object value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(obj, value);
        }
        else
        {
            UnityEngine.Debug.LogWarning($"Could not find field {fieldName} on {obj.GetType().Name}");
        }
    }

    static void InvokeMethod(object obj, string methodName, params object[] parameters)
    {
        var method = obj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method != null)
        {
            method.Invoke(obj, parameters);
        }
        else
        {
            UnityEngine.Debug.LogWarning($"Could not find method {methodName} on {obj.GetType().Name}");
        }
    }

}