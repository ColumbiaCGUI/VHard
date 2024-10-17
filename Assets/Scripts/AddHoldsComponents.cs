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
                ProcessObject(child.gameObject);
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
            if (child.name.Length < 2 || !char.IsDigit(child.name[1]))
            {
                UnityEngine.Debug.Log($"Skipped object: {child.name}");
                skippedCount++;
                continue;
            }

            try
            {
                if (child.GetComponent<CoACD>() != null)
                {
                    AddcoACD(child.gameObject);
                }
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
        CoACD coACD = Undo.AddComponent<CoACD>(obj);
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

    static void ProcessObject(GameObject obj)
    {
        Undo.RecordObject(obj, "Process Moonboard Object");
        
        // Rename object if it has a period in its name
        string newName = obj.name.Split('.')[0];
        obj.name = newName;

        // Add COAcd
        AddcoACD(obj);

        // Add XR Grab Interactable component
        UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable grabInteractable = Undo.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>(obj);
        if (grabInteractable != null)
        {
            grabInteractable.trackPosition = false;
            grabInteractable.trackRotation = false;
            grabInteractable.trackScale = false;
            grabInteractable.throwOnDetach = false;
        }
        else
        {
            UnityEngine.Debug.LogWarning($"XRGrabInteractable component could not be added to {obj.name}");
        }

        // Modify RigidBody component
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Undo.RecordObject(rb, "Modify RigidBody");
            rb.useGravity = false;
        }
        else
        {
            UnityEngine.Debug.LogWarning($"RigidBody component not found on {obj.name}");
        }

        // Add Interactable component (as XRInteractableAffordanceStateProvider is not found)
        UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable interactable = Undo.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>(obj);
        if (interactable == null)
        {
            UnityEngine.Debug.LogWarning($"XRBaseInteractable component could not be added to {obj.name}");
        }

        // Check for existing Color Affordance object
        Transform existingAffordance = obj.transform.Find("ColorAffordance");
        if (existingAffordance == null)
        {
            existingAffordance = obj.transform.Find("Color Affordance");
            if (existingAffordance != null)
            {
                // Rename "Color Affordance" to "ColorAffordance"
                Undo.RecordObject(existingAffordance.gameObject, "Rename Color Affordance");
                existingAffordance.gameObject.name = "ColorAffordance";
                UnityEngine.Debug.Log($"Renamed 'Color Affordance' to 'ColorAffordance' for {obj.name}");
            }
        }

        GameObject colorAffordance;
        if (existingAffordance != null)
        {
            colorAffordance = existingAffordance.gameObject;
            Undo.RecordObject(colorAffordance, "Modify Existing Color Affordance");
            UnityEngine.Debug.Log($"Using existing ColorAffordance object for {obj.name}");
        }
        else
        {
            colorAffordance = new GameObject("ColorAffordance");
            Undo.RegisterCreatedObjectUndo(colorAffordance, "Create Color Affordance");
            Undo.SetTransformParent(colorAffordance.transform, obj.transform, "Set Color Affordance Parent");
            UnityEngine.Debug.Log($"Created new ColorAffordance object for {obj.name}");
        }

        // Add MaterialPropertyBlockHelper component
        var materialPropertyBlockHelper = AddComponentByName(colorAffordance, "UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Rendering.MaterialPropertyBlockHelper");

        // Add ColorMaterialPropertyAffordanceReceiver component
        var colorAffordanceReceiver = AddComponentByName(colorAffordance, "UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Receiver.Rendering.ColorMaterialPropertyAffordanceReceiver");

        if (colorAffordanceReceiver != null)
        {
            // Set MaterialPropertyBlockHelper
            SetFieldValue(colorAffordanceReceiver, "m_MaterialPropertyBlockHelper", materialPropertyBlockHelper);

            // Set color property name (optional, as it has a default value)
            SetFieldValue(colorAffordanceReceiver, "m_ColorPropertyName", "_Color");

            // Add XRInteractableAffordanceStateProvider to the parent object
            var stateProvider = AddComponentByName(obj, "UnityEngine.XR.Interaction.Toolkit.XRInteractableAffordanceStateProvider");
            if (stateProvider != null)
            {
                // Set the affordance state provider
                SetFieldValue(colorAffordanceReceiver, "m_AffordanceStateProvider", stateProvider);
            }

            // Set replaceIdleStateValueWithInitialValue
            SetFieldValue(colorAffordanceReceiver, "m_ReplaceIdleStateValueWithInitialValue", true);

            // Manually call OnValidate to ensure proper setup
            InvokeMethod(colorAffordanceReceiver, "OnValidate");
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