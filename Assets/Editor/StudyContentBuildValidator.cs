using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class StudyContentBuildValidator : IPreprocessBuildWithReport
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string CatalogPath = "Assets/StreamingAssets/moonboard_2016_40.json";
    private const string SchedulePath = "Assets/StreamingAssets/study_schedule.csv";
    private const string OculusConfigPath = "Assets/Oculus/OculusProjectConfig.asset";
    private const string BoardMaterialPath = "Assets/Materials/MovementHarlemMoonBoard.mat";
    private const string BoardTexturePath = "Assets/Materials/MovementHarlemMoonBoard.png";
    private const string KickerMaterialPath = "Assets/Materials/MovementHarlemKicker.mat";

    public int callbackOrder => -1100;

    public void OnPreprocessBuild(BuildReport report)
    {
        ValidateOrThrow();
    }

    [MenuItem("VHard/Validate Study Content")]
    public static void ValidateFromMenu()
    {
        ValidateOrThrow();
        Debug.Log("[StudyContentBuildValidator] MoonBoard study content is valid.");
    }

    public static void ValidateOrThrow()
    {
        string catalogJson = File.ReadAllText(CatalogPath);
        if (MoonBoardStudyCatalog.ComputeSha256(catalogJson) != MoonBoardStudyCatalog.ApprovedCatalogSha256)
        {
            throw new BuildFailedException("MoonBoard catalog does not match the approved study content hash.");
        }
        if (!MoonBoardStudyCatalog.TryParse(
                catalogJson,
                out MoonBoardStudyCatalog catalog,
                out string error))
        {
            throw new BuildFailedException(error);
        }
        if (ComputeFileSha256(catalog.provenance.meshAsset) != MoonBoardStudyCatalog.ApprovedMeshSha256)
        {
            throw new BuildFailedException("MoonBoard mesh asset does not match its approved SHA-256.");
        }
        if (!StudySchedule.TryParse(
                File.ReadAllText(SchedulePath),
                out List<StudyScheduleRow> rows,
                out error) ||
            !StudySchedule.TryValidateRoutes(rows, catalog, out error))
        {
            throw new BuildFailedException(error);
        }

        EditorBuildSettingsScene[] enabledScenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).ToArray();
        if (enabledScenes.Length != 1 || enabledScenes[0].path != ScenePath)
        {
            throw new BuildFailedException("SampleScene must be the only enabled study build scene.");
        }

        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForValidation = !scene.isLoaded;
        if (openedForValidation)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }
        try
        {
            ValidateScene(scene, catalog);
        }
        finally
        {
            if (openedForValidation && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        ScriptableObject oculusConfig = AssetDatabase.LoadAssetAtPath<ScriptableObject>(OculusConfigPath);
        if (oculusConfig == null)
        {
            throw new BuildFailedException("Oculus project configuration is missing.");
        }
        SerializedObject serializedConfig = new(oculusConfig);
        if (serializedConfig.FindProperty("anchorSupport")?.intValue != 1)
        {
            throw new BuildFailedException("Spatial-anchor support must be enabled for board registration.");
        }
        string[] disabledBooleanProperties =
        {
            "insightPassthroughEnabled",
            "_insightPassthroughSupport",
            "isPassthroughCameraAccessEnabled",
        };
        foreach (string propertyName in disabledBooleanProperties)
        {
            SerializedProperty property = serializedConfig.FindProperty(propertyName);
            if (property != null && (property.propertyType == SerializedPropertyType.Boolean
                    ? property.boolValue
                    : property.intValue != 0))
            {
                throw new BuildFailedException("Participant passthrough must remain disabled: " + propertyName + ".");
            }
        }
    }

    private static void ValidateScene(Scene scene, MoonBoardStudyCatalog catalog)
    {
        MoonBoard2016Layout[] layouts = FindComponents<MoonBoard2016Layout>(scene);
        BoardAlignmentController[] alignments = FindComponents<BoardAlignmentController>(scene);
        SceneConfiguror[] configurors = FindComponents<SceneConfiguror>(scene);
        if (layouts.Length != 1 || alignments.Length != 1 || configurors.Length != 1)
        {
            throw new BuildFailedException(
                "Study scene must contain exactly one metric layout, alignment controller, and scene configurator.");
        }
        if (!layouts[0].TryValidateAppliedLayout(catalog, out string error))
        {
            throw new BuildFailedException(error);
        }

        Renderer boardRenderer = layouts[0].transform.Find("Main Surface")?.GetComponent<Renderer>();
        Renderer kickerRenderer = layouts[0].transform.Find("Kicker")?.GetComponent<Renderer>();
        Material boardMaterial = boardRenderer?.sharedMaterial;
        if (AssetDatabase.GetAssetPath(boardMaterial) != BoardMaterialPath ||
            AssetDatabase.GetAssetPath(boardMaterial.GetTexture("_BaseMap")) != BoardTexturePath ||
            AssetDatabase.GetAssetPath(kickerRenderer?.sharedMaterial) != KickerMaterialPath)
        {
            throw new BuildFailedException("Movement Harlem board surface materials are missing or invalid.");
        }

        SceneConfiguror configuror = configurors[0];
        if (configuror.environment == null || configuror.environment.name != "Environment" ||
            configuror.moonBoardEnv == null || configuror.moonBoardEnv != layouts[0].gameObject ||
            configuror.holdsParentGameObject == null ||
            configuror.holdsParentGameObject.transform.parent != layouts[0].transform ||
            configuror.mainCamera == null || configuror.mainCamera.gameObject.name != "CenterEyeAnchor" ||
            !configuror.mainCamera.gameObject.activeInHierarchy || !configuror.disableInactiveHolds)
        {
            throw new BuildFailedException(
                "Study scene references, camera, or inactive-hold performance policy are invalid.");
        }
        if (layouts[0].GetComponent<MeshCollider>() != null)
        {
            throw new BuildFailedException("Moonboard motion root contains a stale mesh collider.");
        }
        GameObject networkingRoot = scene.GetRootGameObjects()
            .FirstOrDefault(root => root.name == "SIGGRAPHNetworkingJace");
        if (networkingRoot != null && networkingRoot.activeSelf)
        {
            throw new BuildFailedException("Participant study builds must keep networking disabled.");
        }

        Transform holdsRoot = configuror.holdsParentGameObject.transform;
        if (holdsRoot.childCount != catalog.holds.Length)
        {
            throw new BuildFailedException("Study scene hold count does not match the catalog.");
        }
        foreach (Transform hold in holdsRoot)
        {
            if (!catalog.TryGetHold(hold.name, out MoonBoardHoldDefinition definition))
            {
                throw new BuildFailedException("Hold is absent from the authoritative catalog: " + hold.name + ".");
            }
            GameObject sourceHold = PrefabUtility.GetCorrespondingObjectFromSource(hold.gameObject);
            if (sourceHold == null || sourceHold.name != definition.scanId ||
                AssetDatabase.GetAssetPath(sourceHold) != catalog.provenance.meshAsset)
            {
                throw new BuildFailedException(
                    $"Hold {hold.name} does not use approved physical scan {definition.scanId}.");
            }
            MeshFilter sceneMesh = hold.GetComponent<MeshFilter>();
            MeshFilter sourceMesh = sourceHold.GetComponent<MeshFilter>();
            if (sceneMesh == null || sourceMesh == null || sceneMesh.sharedMesh != sourceMesh.sharedMesh)
            {
                throw new BuildFailedException("Hold mesh overrides its approved physical scan: " + hold.name + ".");
            }
            string expectedMaterial = definition.holdset switch
            {
                "Original School Holds" => "Assets/ClimbingInteractionJace/InteractableHoldYellow.mat",
                "Hold Set A" => "Assets/ClimbingInteractionJace/InteractableHoldWhite.mat",
                _ => "Assets/ClimbingInteractionJace/InteractableHold.mat",
            };
            if (AssetDatabase.GetAssetPath(hold.GetComponent<Renderer>().sharedMaterial) != expectedMaterial)
            {
                throw new BuildFailedException("Hold material is not shared or does not match its hold set: " + hold.name + ".");
            }
            SphereCollider sphere = hold.GetComponent<SphereCollider>();
            float worldRadius = sphere == null
                ? 0f
                : sphere.radius * Mathf.Max(
                    hold.lossyScale.x,
                    Mathf.Max(hold.lossyScale.y, hold.lossyScale.z));
            if (sphere == null || !sphere.isTrigger || worldRadius < 0.03f || worldRadius > 0.25f)
            {
                throw new BuildFailedException("Hold interaction collider is invalid: " + hold.name + ".");
            }
        }

        Camera[] activeCenterEyeCameras = FindComponents<Camera>(scene)
            .Where(camera => camera.gameObject.activeInHierarchy && camera.gameObject.name == "CenterEyeAnchor")
            .ToArray();
        AudioListener[] activeListeners = FindComponents<AudioListener>(scene)
            .Where(listener => listener.gameObject.activeInHierarchy && listener.enabled)
            .ToArray();
        if (activeCenterEyeCameras.Length != 1 || activeCenterEyeCameras[0] != configuror.mainCamera ||
            activeListeners.Length != 1 || activeListeners[0].gameObject != configuror.mainCamera.gameObject)
        {
            throw new BuildFailedException("Study scene must contain one active CenterEye camera and audio listener.");
        }
    }

    private static T[] FindComponents<T>(Scene scene) where T : Component
    {
        return scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true))
            .ToArray();
    }

    private static string ComputeFileSha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
    }
}
