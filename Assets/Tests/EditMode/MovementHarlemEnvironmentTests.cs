using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed class MovementHarlemEnvironmentTests
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ReconstructionName = "Movement Harlem Reconstruction";
    private const string MaterialFolder = "Assets/Materials/MovementHarlemEnvironment/";

    [Test]
    public void ReconstructionIsStableAndDoesNotEnterTheBoardHierarchy()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        try
        {
            GameObject environment = scene.GetRootGameObjects().Single(root => root.name == "Environment");
            Transform board = environment.transform.Find("BoardAlignmentRoot");
            Transform moonboard = board?.Find("Moonboard");
            Transform reconstruction = environment.transform.Find(ReconstructionName);

            Assert.That(board, Is.Not.Null);
            Assert.That(moonboard, Is.Not.Null);
            Assert.That(reconstruction, Is.Not.Null);
            Assert.That(
                environment.transform.Cast<Transform>().Count(child => child.name == ReconstructionName),
                Is.EqualTo(1));
            Assert.That(reconstruction.parent, Is.EqualTo(environment.transform));
            Assert.That(reconstruction.IsChildOf(board), Is.False);
            Assert.That(reconstruction.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(reconstruction.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(reconstruction.localScale, Is.EqualTo(Vector3.one));

            Assert.That(board.localPosition, Is.EqualTo(new Vector3(-0.29f, -0.57f, 1.86219f)));
            Assert.That(board.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(board.localScale, Is.EqualTo(Vector3.one));
            Assert.That(moonboard.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(moonboard.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(moonboard.localScale, Is.EqualTo(Vector3.one));

            string[] expectedGroups =
            {
                "Architecture",
                "Board Surround",
                "Ceiling Fixtures",
                "Floor Details",
            };
            Assert.That(
                reconstruction.Cast<Transform>().Select(child => child.name),
                Is.EquivalentTo(expectedGroups));
        }
        finally
        {
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [Test]
    public void ReconstructionUsesOnlyStaticProjectOwnedVisuals()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        try
        {
            GameObject environment = scene.GetRootGameObjects().Single(root => root.name == "Environment");
            Transform reconstruction = environment.transform.Find(ReconstructionName);
            Assert.That(reconstruction, Is.Not.Null);

            Renderer[] renderers = reconstruction.GetComponentsInChildren<Renderer>(true);
            MeshFilter[] meshFilters = reconstruction.GetComponentsInChildren<MeshFilter>(true);
            Assert.That(renderers, Has.Length.EqualTo(27));
            Assert.That(meshFilters, Has.Length.EqualTo(renderers.Length));
            Assert.That(meshFilters[0].sharedMesh, Is.Not.Null);
            Assert.That(meshFilters[0].sharedMesh.name, Is.EqualTo("Cube"));
            Assert.That(
                meshFilters.All(filter => filter.sharedMesh == meshFilters[0].sharedMesh),
                Is.True);
            Assert.That(reconstruction.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(reconstruction.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(reconstruction.GetComponentsInChildren<MonoBehaviour>(true), Is.Empty);
            Assert.That(
                renderers.All(renderer =>
                    GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) ==
                    StaticEditorFlags.BatchingStatic),
                Is.True);
            Assert.That(
                reconstruction.Cast<Transform>().All(group =>
                    GameObjectUtility.GetStaticEditorFlags(group.gameObject) == (StaticEditorFlags)0),
                Is.True);
            Assert.That(
                renderers.All(renderer =>
                    renderer.enabled &&
                    renderer.shadowCastingMode == ShadowCastingMode.Off &&
                    renderer.lightProbeUsage == LightProbeUsage.Off &&
                    renderer.reflectionProbeUsage == ReflectionProbeUsage.Off &&
                    renderer.motionVectorGenerationMode == MotionVectorGenerationMode.ForceNoMotion),
                Is.True);
            Assert.That(
                renderers.All(renderer =>
                    AssetDatabase.GetAssetPath(renderer.sharedMaterial).StartsWith(MaterialFolder)),
                Is.True);

            Material[] materials = renderers.Select(renderer => renderer.sharedMaterial).Distinct().ToArray();
            Assert.That(materials, Has.Length.EqualTo(7));
            Assert.That(materials.All(material => material != null && material.shader != null), Is.True);
            Assert.That(materials.All(material => material.shader.isSupported), Is.True);
            Assert.That(materials.All(material => !material.enableInstancing), Is.True);
            Assert.That(
                materials.All(material =>
                    material.globalIlluminationFlags == MaterialGlobalIlluminationFlags.None),
                Is.True);

            TextureImporter cmuImporter = AssetImporter.GetAtPath(
                MaterialFolder + "MovementHarlemCMU.png") as TextureImporter;
            Assert.That(cmuImporter, Is.Not.Null);
            Assert.That(cmuImporter.maxTextureSize, Is.EqualTo(512));
            Assert.That(cmuImporter.mipmapEnabled, Is.True);
            Assert.That(cmuImporter.isReadable, Is.False);

            Material cmu = AssetDatabase.LoadAssetAtPath<Material>(
                MaterialFolder + "MovementHarlemCMU.mat");
            Material cmuSide = AssetDatabase.LoadAssetAtPath<Material>(
                MaterialFolder + "MovementHarlemCMUSide.mat");
            Assert.That(cmu, Is.Not.Null);
            Assert.That(cmuSide, Is.Not.Null);
            Assert.That(cmu.GetTextureScale("_BaseMap"), Is.EqualTo(Vector2.one));
            Assert.That(cmuSide.GetTextureScale("_BaseMap").x, Is.EqualTo(0.68f).Within(0.0001f));
            Assert.That(cmuSide.GetTextureScale("_BaseMap").y, Is.EqualTo(1f));

            int triangleCount = reconstruction.GetComponentsInChildren<MeshFilter>(true)
                .Sum(filter => filter.sharedMesh == null ? 0 : filter.sharedMesh.triangles.Length / 3);
            Assert.That(triangleCount, Is.LessThanOrEqualTo(324));

            Transform mainSurface = environment.transform.Find(
                "BoardAlignmentRoot/Moonboard/Main Surface");
            Transform upperBoard = reconstruction.Find("Board Surround/Upper Vertical Board Wall");
            Assert.That(mainSurface, Is.Not.Null);
            Assert.That(upperBoard, Is.Not.Null);
            Vector3 boardTopCenter = mainSurface.TransformPoint(new Vector3(0f, 0f, -5f));
            Assert.That(upperBoard.rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(upperBoard.lossyScale.x, Is.EqualTo(2.44f).Within(0.0001f));
            Assert.That(upperBoard.lossyScale.z, Is.EqualTo(0.08f).Within(0.0001f));
            Assert.That(
                upperBoard.position.y - upperBoard.lossyScale.y * 0.5f,
                Is.EqualTo(boardTopCenter.y + 0.005f).Within(0.0001f));
            Assert.That(
                upperBoard.position.y + upperBoard.lossyScale.y * 0.5f,
                Is.EqualTo(4.2f).Within(0.0001f));
            Assert.That(
                upperBoard.position.z - upperBoard.lossyScale.z * 0.5f,
                Is.EqualTo(boardTopCenter.z).Within(0.0001f));
            Assert.That(
                AssetDatabase.GetAssetPath(upperBoard.GetComponent<Renderer>().sharedMaterial),
                Is.EqualTo(MaterialFolder + "MovementHarlemUpperBoard.mat"));

            Texture2D boardTexture = LoadPng("Assets/Materials/MovementHarlemMoonBoard.png");
            Texture2D upperBoardTexture = LoadPng(
                MaterialFolder + "MovementHarlemUpperBoard.png");
            try
            {
                Assert.That(boardTexture.width, Is.EqualTo(512));
                Assert.That(boardTexture.height, Is.EqualTo(768));
                Color32[] boardPixels = boardTexture.GetPixels32();
                Assert.That(
                    boardPixels.Count(pixel => pixel.Equals(new Color32(63, 82, 85, 255))),
                    Is.EqualTo(187 * 5));
                Assert.That(
                    boardPixels.Count(pixel => pixel.Equals(new Color32(102, 105, 103, 255))),
                    Is.GreaterThan(100));
                Assert.That(upperBoardTexture.width, Is.EqualTo(512));
                Assert.That(upperBoardTexture.height, Is.EqualTo(340));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(boardTexture);
                UnityEngine.Object.DestroyImmediate(upperBoardTexture);
            }

            TextureImporter upperBoardImporter = AssetImporter.GetAtPath(
                MaterialFolder + "MovementHarlemUpperBoard.png") as TextureImporter;
            Assert.That(upperBoardImporter, Is.Not.Null);
            Assert.That(upperBoardImporter.maxTextureSize, Is.EqualTo(512));
            Assert.That(upperBoardImporter.mipmapEnabled, Is.True);
            Assert.That(upperBoardImporter.isReadable, Is.False);
            Assert.That(upperBoardImporter.npotScale, Is.EqualTo(TextureImporterNPOTScale.None));

            Renderer floor = environment.transform.Find("Floor")?.GetComponent<Renderer>();
            Assert.That(AssetDatabase.GetAssetPath(floor?.sharedMaterial),
                Is.EqualTo("Assets/Materials/MovementHarlemFloor.mat"));

            Light directional = environment.transform.Find("Directional Light")?.GetComponent<Light>();
            Assert.That(directional, Is.Not.Null);
            Assert.That(directional.enabled, Is.True);
            Assert.That(directional.type, Is.EqualTo(LightType.Directional));
        }
        finally
        {
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [Test]
    public void ReconstructionVisualsDoNotOverlapMoonboardColliders()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        try
        {
            GameObject environment = scene.GetRootGameObjects().Single(root => root.name == "Environment");
            Transform moonboard = environment.transform.Find("BoardAlignmentRoot/Moonboard");
            Transform reconstruction = environment.transform.Find(ReconstructionName);
            Assert.That(moonboard, Is.Not.Null);
            Assert.That(reconstruction, Is.Not.Null);

            Physics.SyncTransforms();
            foreach (Renderer renderer in reconstruction.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.name == "Upper Vertical Board Wall")
                {
                    continue;
                }
                Transform rendererTransform = renderer.transform;
                Vector3 halfExtents = Vector3.Scale(
                    rendererTransform.lossyScale,
                    Vector3.one * 0.5f);
                Collider[] boardHits = Physics.OverlapBox(
                        rendererTransform.position,
                        halfExtents,
                        rendererTransform.rotation,
                        ~0,
                        QueryTriggerInteraction.Collide)
                    .Where(collider =>
                        collider.transform == moonboard || collider.transform.IsChildOf(moonboard))
                    .ToArray();
                Assert.That(
                    boardHits,
                    Is.Empty,
                    $"{renderer.name} overlaps {string.Join(", ", boardHits.Select(hit => hit.name))}.");
            }
        }
        finally
        {
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [Test]
    public void RebuildIsIdentityIdempotentAndPreservesRenderSettings()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        Scene previousActiveScene = SceneManager.GetActiveScene();
        try
        {
            Assert.That(scene.isDirty, Is.False);
            if (SceneManager.GetActiveScene() != scene)
            {
                Assert.That(SceneManager.SetActiveScene(scene), Is.True);
            }
            Material skybox = RenderSettings.skybox;
            AmbientMode ambientMode = RenderSettings.ambientMode;
            Color ambientLight = RenderSettings.ambientLight;
            float ambientIntensity = RenderSettings.ambientIntensity;
            float reflectionIntensity = RenderSettings.reflectionIntensity;
            bool fog = RenderSettings.fog;

            Transform environment = scene.GetRootGameObjects()
                .Single(root => root.name == "Environment")
                .transform;
            string[] boardIds = GetGlobalIds(environment.Find("BoardAlignmentRoot"));
            string[] nonOwnedSnapshot = GetNonOwnedSceneSnapshot(scene);
            byte[] originalSceneBytes = File.ReadAllBytes(ScenePath);

            InvokeRebuild();
            Transform firstReconstruction = environment.Find(ReconstructionName);
            string[] firstReconstructionIds = GetGlobalIds(firstReconstruction);
            string[] firstNonOwnedSnapshot = GetNonOwnedSceneSnapshot(scene);
            byte[] firstSceneBytes = File.ReadAllBytes(ScenePath);

            InvokeRebuild();
            Transform secondReconstruction = environment.Find(ReconstructionName);
            string[] secondReconstructionIds = GetGlobalIds(secondReconstruction);
            string[] secondNonOwnedSnapshot = GetNonOwnedSceneSnapshot(scene);
            byte[] secondSceneBytes = File.ReadAllBytes(ScenePath);

            Assert.That(GetGlobalIds(environment.Find("BoardAlignmentRoot")), Is.EqualTo(boardIds));
            Assert.That(firstNonOwnedSnapshot, Is.EqualTo(nonOwnedSnapshot));
            Assert.That(secondNonOwnedSnapshot, Is.EqualTo(nonOwnedSnapshot));
            Assert.That(secondReconstructionIds, Is.EqualTo(firstReconstructionIds));
            Assert.That(firstSceneBytes, Is.EqualTo(originalSceneBytes));
            Assert.That(secondSceneBytes, Is.EqualTo(firstSceneBytes));
            Assert.That(RenderSettings.skybox, Is.SameAs(skybox));
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(ambientMode));
            Assert.That(RenderSettings.ambientLight, Is.EqualTo(ambientLight));
            Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(ambientIntensity));
            Assert.That(RenderSettings.reflectionIntensity, Is.EqualTo(reflectionIntensity));
            Assert.That(RenderSettings.fog, Is.EqualTo(fog));
        }
        finally
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded &&
                previousActiveScene != scene)
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [Test]
    public void RebuildDoesNotCloseOrDiscardADirtyActiveScene()
    {
        Scene sampleScene = SceneManager.GetSceneByPath(ScenePath);
        bool openedSampleForTest = !sampleScene.isLoaded;
        if (openedSampleForTest)
        {
            sampleScene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }

        Scene previousActiveScene = SceneManager.GetActiveScene();
        Scene scratchScene = default;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene candidate = SceneManager.GetSceneAt(i);
            if (candidate != sampleScene && string.IsNullOrEmpty(candidate.path))
            {
                scratchScene = candidate;
                break;
            }
        }

        bool createdScratchScene = !scratchScene.IsValid();
        GameObject marker = null;
        if (createdScratchScene)
        {
            scratchScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        }
        if (!scratchScene.isDirty)
        {
            marker = new GameObject("Movement Harlem rebuild preservation marker");
            SceneManager.MoveGameObjectToScene(marker, scratchScene);
            EditorSceneManager.MarkSceneDirty(scratchScene);
        }

        try
        {
            if (SceneManager.GetActiveScene() != scratchScene)
            {
                Assert.That(SceneManager.SetActiveScene(scratchScene), Is.True);
            }
            InvokeRebuild();

            Assert.That(scratchScene.isLoaded, Is.True);
            Assert.That(scratchScene.isDirty, Is.True);
            Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(scratchScene));
            if (marker != null)
            {
                Assert.That(marker.scene, Is.EqualTo(scratchScene));
            }
        }
        finally
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
            else if (sampleScene.isLoaded)
            {
                SceneManager.SetActiveScene(sampleScene);
            }
            if (marker != null && !createdScratchScene)
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
            if (openedSampleForTest && sampleScene.isLoaded)
            {
                EditorSceneManager.CloseScene(sampleScene, true);
            }
            if (createdScratchScene && scratchScene.isLoaded && SceneManager.sceneCount > 1)
            {
                EditorSceneManager.CloseScene(scratchScene, true);
            }
        }
    }

    private static string[] GetNonOwnedSceneSnapshot(Scene scene)
    {
        Transform reconstruction = scene.GetRootGameObjects()
            .Single(root => root.name == "Environment")
            .transform
            .Find(ReconstructionName);

        return scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Where(transform => transform != reconstruction && !transform.IsChildOf(reconstruction))
            .SelectMany(transform =>
                new UnityEngine.Object[] { transform.gameObject }
                    .Concat(transform.GetComponents<Component>()))
            .Where(sceneObject => sceneObject != null)
            .Select(sceneObject =>
                $"{GlobalObjectId.GetGlobalObjectIdSlow(sceneObject)}|" +
                $"{sceneObject.GetType().AssemblyQualifiedName}|" +
                EditorJsonUtility.ToJson(sceneObject))
            .OrderBy(snapshot => snapshot, StringComparer.Ordinal)
            .ToArray();
    }

    private static Texture2D LoadPng(string path)
    {
        Texture2D texture = new(2, 2, TextureFormat.RGBA32, false, false);
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
        {
            UnityEngine.Object.DestroyImmediate(texture);
            throw new InvalidOperationException("Could not decode test texture: " + path);
        }
        return texture;
    }

    private static string[] GetGlobalIds(Transform root)
    {
        Assert.That(root, Is.Not.Null);
        return root.GetComponentsInChildren<Transform>(true)
            .Select(transform => GlobalObjectId.GetGlobalObjectIdSlow(transform.gameObject).ToString())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void InvokeRebuild()
    {
        Type builderType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("MovementHarlemEnvironmentBuilder"))
            .Single(type => type != null);
        MethodInfo rebuild = builderType.GetMethod(
            "Rebuild",
            BindingFlags.Public | BindingFlags.Static);
        Assert.That(rebuild, Is.Not.Null);

        try
        {
            rebuild.Invoke(null, null);
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }
}
