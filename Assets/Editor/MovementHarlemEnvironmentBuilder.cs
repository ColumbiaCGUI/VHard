using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class MovementHarlemEnvironmentBuilder
{
    private const string ScenePath = "Assets/Scenes/VHardStudy.unity";
    private const string EnvironmentName = "Environment";
    private const string BoardName = "BoardAlignmentRoot";
    private const string SceneryRootName = "GripLocomotionSceneryRoot";
    private const string ReconstructionName = "Movement Harlem Reconstruction";
    private const string UpperBoardName = "Upper Vertical Board Wall";
    private const string MaterialFolder = "Assets/Materials/MovementHarlemEnvironment";
    private const string CmuTexturePath = MaterialFolder + "/MovementHarlemCMU.png";
    private const string UpperBoardTexturePath = MaterialFolder + "/MovementHarlemUpperBoard.png";
    private const string BoardTexturePath = "Assets/Materials/MovementHarlemMoonBoard.png";

    private const float FloorY = -0.57f;
    private const float BoardCenterX = -0.29f;
    private const float RoomHalfWidth = 4f;
    private const float FrontZ = -2.32f;
    private const float BackZ = 3.12f;
    private const float CeilingY = 4.2f;
    private const float WallThickness = 0.12f;

    private static readonly int[] BoardGridColumnPixels =
        { 46, 88, 130, 172, 214, 256, 297, 339, 381, 423, 465 };
    private static readonly int[] BoardGridRowPixels =
        { 67, 110, 152, 194, 236, 278, 320, 362, 405, 447, 489, 531, 573, 615, 657, 700, 742 };
    private static readonly string[][] DigitGlyphs =
    {
        new[] { "111", "101", "101", "101", "111" },
        new[] { "010", "110", "010", "010", "111" },
        new[] { "111", "001", "111", "100", "111" },
        new[] { "111", "001", "111", "001", "111" },
        new[] { "101", "101", "111", "001", "001" },
        new[] { "111", "100", "111", "001", "111" },
        new[] { "111", "100", "111", "101", "111" },
        new[] { "111", "001", "001", "001", "001" },
        new[] { "111", "101", "111", "101", "111" },
        new[] { "111", "101", "111", "001", "111" },
    };

    private static Mesh builtinCubeMesh;

    private sealed class TransformState
    {
        public Transform transform;
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    [MenuItem("VHard/Rebuild Movement Harlem Environment")]
    public static void Rebuild()
    {
        Scene previousActiveScene = SceneManager.GetActiveScene();
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool openedForRebuild = !scene.isLoaded;
        if (openedForRebuild)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        }
        if (scene.isDirty)
        {
            if (openedForRebuild)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            throw new InvalidOperationException(
                "VHardStudy has unsaved changes. Save or discard them before rebuilding the gym environment.");
        }

        try
        {
            if (SceneManager.GetActiveScene() != scene && !SceneManager.SetActiveScene(scene))
            {
                throw new InvalidOperationException("Unity failed to activate VHardStudy for reconstruction.");
            }

            GameObject environment = scene.GetRootGameObjects()
                .SingleOrDefault(root => root.name == EnvironmentName);
            if (environment == null)
            {
                throw new InvalidOperationException("VHardStudy is missing its Environment root.");
            }

            Transform board = environment.transform.Find(BoardName);
            if (board == null || board.Find("Moonboard") == null)
            {
                throw new InvalidOperationException("VHardStudy is missing BoardAlignmentRoot/Moonboard.");
            }

            TransformState[] boardState = board.Find("Moonboard").GetComponentsInChildren<Transform>(true)
                .Select(CaptureTransform)
                .ToArray();

            EnsureAssetFolder();
            Texture2D cmuTexture = GenerateCmuTexture();
            AddBoardLabelsAndLedSockets();
            Texture2D upperBoardTexture = GenerateUpperBoardTexture();
            UnityEngine.Shader shader = UnityEngine.Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException("The Universal Render Pipeline/Lit shader is unavailable.");
            }

            Material cmu = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemCMU.mat",
                Color.white,
                0.08f,
                0f,
                cmuTexture);
            Material cmuSide = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemCMUSide.mat",
                Color.white,
                0.08f,
                0f,
                cmuTexture);
            Vector2 sideCmuTiling = new((BackZ - FrontZ) / (RoomHalfWidth * 2f), 1f);
            cmuSide.SetTextureScale("_BaseMap", sideCmuTiling);
            cmuSide.SetTextureScale("_MainTex", sideCmuTiling);
            EditorUtility.SetDirty(cmuSide);
            Material ceiling = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemCeiling.mat",
                new Color(0.115f, 0.125f, 0.13f),
                0.16f,
                0.05f);
            Material steel = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemSteel.mat",
                new Color(0.055f, 0.065f, 0.07f),
                0.3f,
                0.65f);
            Material padSeam = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemPadSeam.mat",
                new Color(0.025f, 0.045f, 0.075f),
                0.06f,
                0f);
            Material lightPanel = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemLightPanel.mat",
                new Color(0.88f, 0.91f, 0.93f),
                0.42f,
                0f,
                null,
                new Color(2.4f, 2.55f, 2.65f));
            Material upperBoard = CreateMaterial(
                shader,
                MaterialFolder + "/MovementHarlemUpperBoard.mat",
                Color.white,
                0.08f,
                0f,
                upperBoardTexture);

            Transform sceneryRoot = MoveRequiredChild(
                environment.transform,
                board,
                SceneryRootName);
            sceneryRoot.localScale = Vector3.one;
            sceneryRoot.gameObject.SetActive(true);
            GameObjectUtility.SetStaticEditorFlags(sceneryRoot.gameObject, (StaticEditorFlags)0);
            Transform reconstruction = MoveRequiredChild(
                environment.transform,
                sceneryRoot,
                ReconstructionName);
            reconstruction = GetOrCreateGroup(sceneryRoot, ReconstructionName);
            Transform floor = MoveRequiredChild(environment.transform, sceneryRoot, "Floor");
            MoveRequiredChild(
                environment.transform,
                sceneryRoot,
                "Directional Light");
            Rigidbody floorBody = floor.GetComponent<Rigidbody>();
            if (floorBody == null)
            {
                throw new InvalidOperationException("The Movement Harlem floor is missing its Rigidbody.");
            }
            floorBody.isKinematic = true;

            Transform architecture = GetOrCreateGroup(reconstruction, "Architecture");
            Transform boardSurround = GetOrCreateGroup(reconstruction, "Board Surround");
            Transform ceilingFixtures = GetOrCreateGroup(reconstruction, "Ceiling Fixtures");
            Transform floorDetails = GetOrCreateGroup(reconstruction, "Floor Details");
            List<string> architectureChildren = new();
            List<string> boardSurroundChildren = new();
            List<string> ceilingFixtureChildren = new();
            List<string> floorDetailChildren = new();

            float roomWidth = RoomHalfWidth * 2f;
            float roomDepth = BackZ - FrontZ;
            float wallHeight = CeilingY - FloorY;
            float wallCenterY = FloorY + wallHeight * 0.5f;
            float roomCenterZ = (FrontZ + BackZ) * 0.5f;
            float leftX = BoardCenterX - RoomHalfWidth;
            float rightX = BoardCenterX + RoomHalfWidth;

            CreateCube(
                architecture,
                architectureChildren,
                "Back CMU Wall",
                new Vector3(BoardCenterX, wallCenterY, BackZ),
                new Vector3(roomWidth, wallHeight, WallThickness),
                Quaternion.identity,
                cmu);
            CreateCube(
                architecture,
                architectureChildren,
                "Front CMU Wall",
                new Vector3(BoardCenterX, wallCenterY, FrontZ),
                new Vector3(roomWidth, wallHeight, WallThickness),
                Quaternion.identity,
                cmu);
            CreateCube(
                architecture,
                architectureChildren,
                "Left CMU Wall",
                new Vector3(leftX, wallCenterY, roomCenterZ),
                new Vector3(WallThickness, wallHeight, roomDepth),
                Quaternion.identity,
                cmuSide);
            CreateCube(
                architecture,
                architectureChildren,
                "Right CMU Wall",
                new Vector3(rightX, wallCenterY, roomCenterZ),
                new Vector3(WallThickness, wallHeight, roomDepth),
                Quaternion.identity,
                cmuSide);
            CreateCube(
                architecture,
                architectureChildren,
                "Ceiling",
                new Vector3(BoardCenterX, CeilingY, roomCenterZ),
                new Vector3(roomWidth, WallThickness, roomDepth),
                Quaternion.identity,
                ceiling);

            const float basePadHeight = 0.42f;
            float basePadY = FloorY + basePadHeight * 0.5f;
            CreateCube(
                architecture,
                architectureChildren,
                "Back Wall Base Pad",
                new Vector3(BoardCenterX, basePadY, BackZ - 0.085f),
                new Vector3(roomWidth - 0.12f, basePadHeight, 0.05f),
                Quaternion.identity,
                padSeam);
            CreateCube(
                architecture,
                architectureChildren,
                "Front Wall Base Pad",
                new Vector3(BoardCenterX, basePadY, FrontZ + 0.085f),
                new Vector3(roomWidth - 0.12f, basePadHeight, 0.05f),
                Quaternion.identity,
                padSeam);
            CreateCube(
                architecture,
                architectureChildren,
                "Left Wall Base Pad",
                new Vector3(leftX + 0.085f, basePadY, roomCenterZ),
                new Vector3(0.05f, basePadHeight, roomDepth - 0.12f),
                Quaternion.identity,
                padSeam);
            CreateCube(
                architecture,
                architectureChildren,
                "Right Wall Base Pad",
                new Vector3(rightX - 0.085f, basePadY, roomCenterZ),
                new Vector3(0.05f, basePadHeight, roomDepth - 0.12f),
                Quaternion.identity,
                padSeam);

            float[] supportXs = { BoardCenterX - 1.32f, BoardCenterX + 1.32f };
            foreach (float x in supportXs)
            {
                string side = x < BoardCenterX ? "Left" : "Right";
                CreateCube(
                    boardSurround,
                    boardSurroundChildren,
                    side + " Main Support",
                    new Vector3(x, 1.241f, 0.748f),
                    new Vector3(0.075f, 3.64f, 0.075f),
                    Quaternion.Euler(-40f, 0f, 0f),
                    steel);
                CreateCube(
                    boardSurround,
                    boardSurroundChildren,
                    side + " Kicker Support",
                    new Vector3(x, -0.385f, 1.945f),
                    new Vector3(0.075f, 0.37f, 0.075f),
                    Quaternion.identity,
                    steel);
            }
            CreateCube(
                boardSurround,
                boardSurroundChildren,
                "Top Crossbar",
                new Vector3(BoardCenterX, 2.7f, -0.43f),
                new Vector3(2.78f, 0.075f, 0.075f),
                Quaternion.identity,
                steel);

            Transform mainSurface = board.Find("Moonboard/Main Surface");
            if (mainSurface == null)
            {
                throw new InvalidOperationException("VHardStudy is missing the MoonBoard main surface.");
            }
            Vector3 boardTopCenter = mainSurface.TransformPoint(new Vector3(0f, 0f, -5f));
            const float upperBoardThickness = 0.08f;
            float upperBoardBottom = boardTopCenter.y + 0.005f;
            float upperBoardHeight = CeilingY - upperBoardBottom;
            if (upperBoardHeight <= 0f)
            {
                throw new InvalidOperationException("The MoonBoard top is above the reconstructed ceiling.");
            }
            CreateCube(
                boardSurround,
                boardSurroundChildren,
                UpperBoardName,
                new Vector3(
                    boardTopCenter.x,
                    upperBoardBottom + upperBoardHeight * 0.5f,
                    boardTopCenter.z + upperBoardThickness * 0.5f),
                new Vector3(2.44f, upperBoardHeight, upperBoardThickness),
                Quaternion.identity,
                upperBoard);

            float[] ceilingRailXs = { BoardCenterX - 2.55f, BoardCenterX, BoardCenterX + 2.55f };
            foreach (float x in ceilingRailXs)
            {
                CreateCube(
                    ceilingFixtures,
                    ceilingFixtureChildren,
                    $"Ceiling Rail {FormatSigned(x - BoardCenterX)}",
                    new Vector3(x, CeilingY - 0.095f, roomCenterZ),
                    new Vector3(0.065f, 0.07f, roomDepth - 0.2f),
                    Quaternion.identity,
                    steel);
            }

            float[] lightXs =
            {
            BoardCenterX - 2.85f,
            BoardCenterX - 0.95f,
            BoardCenterX + 0.95f,
            BoardCenterX + 2.85f,
        };
            foreach (float x in lightXs)
            {
                CreateCube(
                    ceilingFixtures,
                    ceilingFixtureChildren,
                    $"LED Batten {FormatSigned(x - BoardCenterX)}",
                    new Vector3(x, CeilingY - 0.145f, 0.42f),
                    new Vector3(1.2f, 0.035f, 0.19f),
                    Quaternion.identity,
                    lightPanel,
                    false);
            }

            float seamY = FloorY + 0.004f;
            float[] transverseSeams = { -1.18f, 0.62f, 2.42f };
            foreach (float z in transverseSeams)
            {
                CreateCube(
                    floorDetails,
                    floorDetailChildren,
                    $"Floor Seam Z {FormatSigned(z)}",
                    new Vector3(BoardCenterX, seamY, z),
                    new Vector3(roomWidth - 0.18f, 0.007f, 0.018f),
                    Quaternion.identity,
                    padSeam,
                    false);
            }
            float[] longitudinalSeams = { BoardCenterX - 2f, BoardCenterX + 2f };
            foreach (float x in longitudinalSeams)
            {
                CreateCube(
                    floorDetails,
                    floorDetailChildren,
                    $"Floor Seam X {FormatSigned(x)}",
                    new Vector3(x, seamY, roomCenterZ),
                    new Vector3(0.018f, 0.007f, roomDepth - 0.18f),
                    Quaternion.identity,
                    padSeam,
                    false);
            }

            PruneUnexpectedChildren(architecture, architectureChildren);
            PruneUnexpectedChildren(boardSurround, boardSurroundChildren);
            PruneUnexpectedChildren(ceilingFixtures, ceilingFixtureChildren);
            PruneUnexpectedChildren(floorDetails, floorDetailChildren);
            PruneUnexpectedChildren(
                reconstruction,
                new[] { "Architecture", "Board Surround", "Ceiling Fixtures", "Floor Details" });

            ValidateBoardState(boardState);
            ValidateReconstruction(environment.transform, board, sceneryRoot, reconstruction);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException("Unity failed to save VHardStudy after reconstruction.");
            }

            AssetDatabase.SaveAssets();
            Debug.Log(
                "[MovementHarlemEnvironmentBuilder] Reconciled 27 collider-free renderers in VHardStudy.");
        }
        finally
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded &&
                previousActiveScene != scene)
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
            if (openedForRebuild && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    private static TransformState CaptureTransform(Transform transform)
    {
        return new TransformState
        {
            transform = transform,
            parent = transform.parent,
            localPosition = transform.localPosition,
            localRotation = transform.localRotation,
            localScale = transform.localScale,
        };
    }

    private static void ValidateBoardState(IEnumerable<TransformState> states)
    {
        foreach (TransformState state in states)
        {
            if (state.transform == null || state.transform.parent != state.parent ||
                state.transform.localPosition != state.localPosition ||
                state.transform.localRotation != state.localRotation ||
                state.transform.localScale != state.localScale)
            {
                throw new InvalidOperationException(
                    "The reconstruction changed the BoardAlignmentRoot/Moonboard hierarchy.");
            }
        }
    }

    private static void ValidateReconstruction(
        Transform environment,
        Transform board,
        Transform sceneryRoot,
        Transform reconstruction)
    {
        if (sceneryRoot.parent != board || sceneryRoot.position != Vector3.zero ||
            sceneryRoot.rotation != Quaternion.identity || sceneryRoot.localScale != Vector3.one ||
            reconstruction.parent != sceneryRoot || reconstruction.localPosition != Vector3.zero ||
            reconstruction.localRotation != Quaternion.identity || reconstruction.localScale != Vector3.one)
        {
            throw new InvalidOperationException("The Movement Harlem reconstruction root is not stable.");
        }
        string[] sceneryChildren = sceneryRoot.Cast<Transform>()
            .Select(child => child.name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!sceneryChildren.SequenceEqual(new[]
            {
                "Directional Light",
                "Floor",
                ReconstructionName,
            }))
        {
            throw new InvalidOperationException("The grip-locomotion scenery root has unexpected children.");
        }
        Renderer[] renderers = reconstruction.GetComponentsInChildren<Renderer>(true);
        MeshFilter[] meshFilters = reconstruction.GetComponentsInChildren<MeshFilter>(true);
        if (renderers.Length != 27 ||
            meshFilters.Length != renderers.Length ||
            meshFilters.Any(filter => filter.sharedMesh != GetBuiltinCubeMesh()) ||
            reconstruction.GetComponentsInChildren<Collider>(true).Length != 0 ||
            reconstruction.GetComponentsInChildren<Rigidbody>(true).Length != 0 ||
            reconstruction.GetComponentsInChildren<MonoBehaviour>(true).Length != 0)
        {
            throw new InvalidOperationException(
                "The Movement Harlem reconstruction must contain 27 renderers and no physics/runtime components.");
        }
        if (renderers.Any(renderer =>
                GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) != (StaticEditorFlags)0))
        {
            throw new InvalidOperationException(
                "Movement Harlem renderers must remain movable for grip locomotion.");
        }
        if (renderers.Any(renderer =>
                !renderer.enabled ||
                renderer.shadowCastingMode != ShadowCastingMode.Off ||
                renderer.lightProbeUsage != LightProbeUsage.Off ||
                renderer.reflectionProbeUsage != ReflectionProbeUsage.Off ||
                renderer.motionVectorGenerationMode != MotionVectorGenerationMode.ForceNoMotion))
        {
            throw new InvalidOperationException(
                "Movement Harlem renderers contain avoidable Quest rendering work.");
        }
        if (renderers.Any(renderer =>
                renderer.sharedMaterial == null ||
                !AssetDatabase.GetAssetPath(renderer.sharedMaterial).StartsWith(
                    MaterialFolder + "/",
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A Movement Harlem renderer has a missing or external material reference.");
        }

        Transform moonboard = board.Find("Moonboard");
        Physics.SyncTransforms();
        foreach (Renderer renderer in renderers)
        {
            if (renderer.name == UpperBoardName)
            {
                // This collider-free panel intentionally meets the main board at its top seam.
                continue;
            }
            Transform rendererTransform = renderer.transform;
            Vector3 halfExtents = Vector3.Scale(rendererTransform.lossyScale, Vector3.one * 0.5f);
            bool overlapsBoard = Physics.OverlapBox(
                    rendererTransform.position,
                    halfExtents,
                    rendererTransform.rotation,
                    ~0,
                    QueryTriggerInteraction.Collide)
                .Any(collider =>
                    collider.transform == moonboard || collider.transform.IsChildOf(moonboard));
            if (overlapsBoard)
            {
                throw new InvalidOperationException(
                    $"Movement Harlem visual '{renderer.name}' overlaps a Moonboard collider.");
            }
        }

        Renderer floorRenderer = sceneryRoot.Find("Floor")?.GetComponent<Renderer>();
        if (AssetDatabase.GetAssetPath(floorRenderer?.sharedMaterial) !=
            "Assets/Materials/MovementHarlemFloor.mat")
        {
            throw new InvalidOperationException("The photo-derived Movement Harlem floor material is missing.");
        }
        Rigidbody floorBody = sceneryRoot.Find("Floor")?.GetComponent<Rigidbody>();
        if (floorBody == null || !floorBody.isKinematic)
        {
            throw new InvalidOperationException("The movable Movement Harlem floor must be kinematic.");
        }
    }

    private static Transform MoveRequiredChild(
        Transform currentParent,
        Transform destinationParent,
        string name)
    {
        Transform existingAtDestination = TakeUniqueChild(destinationParent, name);
        Transform existingAtCurrentParent = TakeUniqueChild(currentParent, name);
        if (existingAtDestination != null && existingAtCurrentParent != null &&
            existingAtDestination != existingAtCurrentParent)
        {
            throw new InvalidOperationException("Duplicate environment object: " + name + ".");
        }

        Transform child = existingAtDestination ?? existingAtCurrentParent;
        if (child == null)
        {
            throw new InvalidOperationException("VHardStudy is missing environment object: " + name + ".");
        }
        child.SetParent(destinationParent, true);
        return child;
    }

    private static Transform GetOrCreateGroup(Transform parent, string name)
    {
        Transform groupTransform = TakeUniqueChild(parent, name);
        if (groupTransform != null && groupTransform.GetComponents<Component>().Length != 1)
        {
            UnityEngine.Object.DestroyImmediate(groupTransform.gameObject);
            groupTransform = null;
        }

        GameObject group = groupTransform == null ? new GameObject(name) : groupTransform.gameObject;
        group.transform.SetParent(parent, false);
        group.transform.localPosition = Vector3.zero;
        group.transform.localRotation = Quaternion.identity;
        group.transform.localScale = Vector3.one;
        group.layer = 0;
        group.tag = "Untagged";
        group.SetActive(true);
        GameObjectUtility.SetStaticEditorFlags(group, (StaticEditorFlags)0);
        return group.transform;
    }

    private static Mesh GetBuiltinCubeMesh()
    {
        if (builtinCubeMesh != null)
        {
            return builtinCubeMesh;
        }

        GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            builtinCubeMesh = primitive.GetComponent<MeshFilter>()?.sharedMesh;
            if (builtinCubeMesh == null)
            {
                throw new InvalidOperationException("Unity failed to provide its built-in cube mesh.");
            }
            return builtinCubeMesh;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(primitive);
        }
    }

    private static GameObject CreateCube(
        Transform parent,
        ICollection<string> expectedChildren,
        string name,
        Vector3 position,
        Vector3 scale,
        Quaternion rotation,
        Material material,
        bool castShadows = false)
    {
        expectedChildren.Add(name);
        Transform existing = TakeUniqueChild(parent, name);
        GameObject cube = existing?.gameObject;
        MeshFilter filter = cube?.GetComponent<MeshFilter>();
        MeshRenderer renderer = cube?.GetComponent<MeshRenderer>();
        Mesh canonicalCubeMesh = GetBuiltinCubeMesh();
        bool reusable = cube != null && cube.transform.childCount == 0 && filter != null &&
            filter.sharedMesh == canonicalCubeMesh && renderer != null &&
            cube.GetComponents<Component>().All(component =>
                component is Transform || component is MeshFilter || component is MeshRenderer);
        if (!reusable)
        {
            if (cube != null)
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
            cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            filter = cube.GetComponent<MeshFilter>();
            renderer = cube.GetComponent<MeshRenderer>();
        }

        cube.name = name;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = position;
        cube.transform.localRotation = rotation;
        cube.transform.localScale = scale;
        cube.layer = 0;
        cube.tag = "Untagged";
        cube.SetActive(true);
        GameObjectUtility.SetStaticEditorFlags(cube, (StaticEditorFlags)0);

        filter.sharedMesh = canonicalCubeMesh;
        renderer.enabled = true;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        renderer.receiveShadows = true;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        return cube;
    }

    private static Transform TakeUniqueChild(Transform parent, string name)
    {
        Transform[] matches = parent.Cast<Transform>()
            .Where(child => child.name == name)
            .ToArray();
        for (int i = 1; i < matches.Length; i++)
        {
            UnityEngine.Object.DestroyImmediate(matches[i].gameObject);
        }
        return matches.FirstOrDefault();
    }

    private static void PruneUnexpectedChildren(Transform parent, IEnumerable<string> expectedNames)
    {
        HashSet<string> expected = new(expectedNames, StringComparer.Ordinal);
        foreach (Transform child in parent.Cast<Transform>().ToArray())
        {
            if (!expected.Contains(child.name))
            {
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }
    }

    private static string FormatSigned(float value)
    {
        return value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    }

    private static void EnsureAssetFolder()
    {
        if (!AssetDatabase.IsValidFolder(MaterialFolder))
        {
            string guid = AssetDatabase.CreateFolder("Assets/Materials", "MovementHarlemEnvironment");
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("Unity failed to create the environment material folder.");
            }
        }
    }

    private static void AddBoardLabelsAndLedSockets()
    {
        if (!File.Exists(BoardTexturePath))
        {
            throw new FileNotFoundException("The Movement Harlem board texture is missing.", BoardTexturePath);
        }

        Texture2D texture = new(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(BoardTexturePath), false) ||
                texture.width != 512 || texture.height != 768)
            {
                throw new InvalidOperationException(
                    "The Movement Harlem board texture must be a readable 512 x 768 PNG source.");
            }

            Color32[] pixels = texture.GetPixels32();
            Color32 socketColor = new(26, 31, 33, 255);
            Color32 lensColor = new(63, 82, 85, 255);
            Color32 labelColor = new(102, 105, 103, 255);
            for (int rowIndex = 0; rowIndex < BoardGridRowPixels.Length; rowIndex++)
            {
                int rowPixel = BoardGridRowPixels[rowIndex];
                int nextRowPixel = rowIndex + 1 < BoardGridRowPixels.Length
                    ? BoardGridRowPixels[rowIndex + 1]
                    : texture.height - 5;
                int ledPixel = Mathf.RoundToInt((rowPixel + nextRowPixel) * 0.5f);
                foreach (int columnPixel in BoardGridColumnPixels)
                {
                    DrawFilledCircleTop(
                        pixels,
                        texture.width,
                        texture.height,
                        columnPixel,
                        ledPixel,
                        3,
                        socketColor);
                    DrawFilledCircleTop(
                        pixels,
                        texture.width,
                        texture.height,
                        columnPixel,
                        ledPixel,
                        1,
                        lensColor);
                }
                DrawNumberTop(
                    pixels,
                    texture.width,
                    texture.height,
                    18 - rowIndex,
                    rowPixel,
                    labelColor);
            }

            texture.SetPixels32(pixels);
            SaveTexture(texture, BoardTexturePath, TextureWrapMode.Clamp);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    private static Texture2D GenerateUpperBoardTexture()
    {
        const int width = 512;
        const int height = 340;
        Texture2D generated = new(width, height, TextureFormat.RGBA32, false, false);
        try
        {
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int noise = HashNoise(x, y) % 5 - 2;
                    int gradient = Mathf.RoundToInt(4f * y / (height - 1f));
                    byte value = (byte)Mathf.Clamp(180 + gradient + noise, 0, 255);
                    pixels[y * width + x] = new Color32(value, (byte)(value + 1), value, 255);
                }
            }

            Color32 seam = new(151, 153, 152, 255);
            for (int y = 0; y < height; y++)
            {
                pixels[y * width + width / 2] = seam;
            }

            Color32 hole = new(34, 38, 39, 255);
            for (int distanceFromBottom = 21;
                 distanceFromBottom < height;
                 distanceFromBottom += 42)
            {
                int yTop = height - 1 - distanceFromBottom;
                foreach (int columnPixel in BoardGridColumnPixels)
                {
                    DrawFilledCircleTop(pixels, width, height, columnPixel, yTop, 2, hole);
                }
            }

            generated.SetPixels32(pixels);
            return SaveTexture(generated, UpperBoardTexturePath, TextureWrapMode.Clamp);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(generated);
        }
    }

    private static Texture2D SaveTexture(Texture2D texture, string path, TextureWrapMode wrapMode)
    {
        texture.Apply(false, false);
        byte[] png = ImageConversion.EncodeToPNG(texture);
        if (png == null || png.Length == 0)
        {
            throw new InvalidOperationException("Unity failed to encode generated texture " + path + ".");
        }

        bool textureChanged = !File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(png);
        if (textureChanged)
        {
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            throw new InvalidOperationException("Unity failed to import generated texture " + path + ".");
        }
        bool importSettingsChanged = importer.textureType != TextureImporterType.Default ||
            !importer.sRGBTexture || !importer.mipmapEnabled || importer.isReadable ||
            importer.wrapMode != wrapMode || importer.filterMode != FilterMode.Bilinear ||
            importer.npotScale != TextureImporterNPOTScale.None || importer.maxTextureSize != 512 ||
            importer.textureCompression != TextureImporterCompression.Compressed;
        if (importSettingsChanged)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.wrapMode = wrapMode;
            importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 512;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }

        Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (imported == null)
        {
            throw new InvalidOperationException("The generated texture is unavailable: " + path + ".");
        }
        return imported;
    }

    private static void DrawFilledCircleTop(
        Color32[] pixels,
        int width,
        int height,
        int centerX,
        int centerYTop,
        int radius,
        Color32 color)
    {
        for (int y = centerYTop - radius; y <= centerYTop + radius; y++)
        {
            for (int x = centerX - radius; x <= centerX + radius; x++)
            {
                int deltaX = x - centerX;
                int deltaY = y - centerYTop;
                if (deltaX * deltaX + deltaY * deltaY <= radius * radius)
                {
                    SetPixelTop(pixels, width, height, x, y, color);
                }
            }
        }
    }

    private static void DrawNumberTop(
        Color32[] pixels,
        int width,
        int height,
        int number,
        int centerYTop,
        Color32 color)
    {
        const int scale = 2;
        string text = number.ToString(CultureInfo.InvariantCulture);
        int textWidth = text.Length * 3 * scale + (text.Length - 1) * scale;
        int cursorX = 507 - textWidth;
        int top = centerYTop - 5;
        foreach (char character in text)
        {
            string[] glyph = DigitGlyphs[character - '0'];
            for (int glyphY = 0; glyphY < glyph.Length; glyphY++)
            {
                for (int glyphX = 0; glyphX < glyph[glyphY].Length; glyphX++)
                {
                    if (glyph[glyphY][glyphX] != '1')
                    {
                        continue;
                    }
                    for (int offsetY = 0; offsetY < scale; offsetY++)
                    {
                        for (int offsetX = 0; offsetX < scale; offsetX++)
                        {
                            SetPixelTop(
                                pixels,
                                width,
                                height,
                                cursorX + glyphX * scale + offsetX,
                                top + glyphY * scale + offsetY,
                                color);
                        }
                    }
                }
            }
            cursorX += 4 * scale;
        }
    }

    private static void SetPixelTop(
        Color32[] pixels,
        int width,
        int height,
        int x,
        int yTop,
        Color32 color)
    {
        if (x < 0 || x >= width || yTop < 0 || yTop >= height)
        {
            return;
        }
        pixels[(height - 1 - yTop) * width + x] = color;
    }

    private static Texture2D GenerateCmuTexture()
    {
        const int size = 512;
        const int blockWidth = 24;
        const int blockHeight = 21;
        const int mortarWidth = 2;
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            int row = y / blockHeight;
            int localY = y % blockHeight;
            int rowOffset = (row & 1) == 0 ? 0 : blockWidth / 2;
            for (int x = 0; x < size; x++)
            {
                int localX = (x + rowOffset) % blockWidth;
                bool mortar = localY < mortarWidth || localX < mortarWidth;
                if (mortar)
                {
                    pixels[y * size + x] = new Color32(33, 36, 38, 255);
                    continue;
                }

                int noise = HashNoise(x, y) % 15 - 7;
                byte value = (byte)Mathf.Clamp(76 + noise, 0, 255);
                pixels[y * size + x] = new Color32(value, (byte)(value + 4), (byte)(value + 5), 255);
            }
        }

        Texture2D generated = new(size, size, TextureFormat.RGBA32, false, false);
        generated.SetPixels32(pixels);
        generated.Apply(false, false);
        byte[] png = generated.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(generated);
        if (png == null || png.Length == 0)
        {
            throw new InvalidOperationException("Unity failed to encode the CMU texture.");
        }

        bool textureChanged = !File.Exists(CmuTexturePath) ||
            !File.ReadAllBytes(CmuTexturePath).SequenceEqual(png);
        if (textureChanged)
        {
            File.WriteAllBytes(CmuTexturePath, png);
            AssetDatabase.ImportAsset(CmuTexturePath, ImportAssetOptions.ForceSynchronousImport);
        }
        TextureImporter importer = AssetImporter.GetAtPath(CmuTexturePath) as TextureImporter;
        if (importer == null)
        {
            throw new InvalidOperationException("Unity failed to import the CMU texture.");
        }
        bool importSettingsChanged = importer.textureType != TextureImporterType.Default ||
            !importer.sRGBTexture || !importer.mipmapEnabled || importer.isReadable ||
            importer.wrapMode != TextureWrapMode.Repeat || importer.filterMode != FilterMode.Bilinear ||
            importer.maxTextureSize != size ||
            importer.textureCompression != TextureImporterCompression.Compressed;
        if (importSettingsChanged)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = size;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(CmuTexturePath);
        if (texture == null)
        {
            throw new InvalidOperationException("The imported CMU texture is unavailable.");
        }
        return texture;
    }

    private static int HashNoise(int x, int y)
    {
        unchecked
        {
            uint value = (uint)(x * 374761393 + y * 668265263);
            value = (value ^ (value >> 13)) * 1274126177;
            return (int)(value ^ (value >> 16)) & int.MaxValue;
        }
    }

    private static Material CreateMaterial(
        UnityEngine.Shader shader,
        string path,
        Color color,
        float smoothness,
        float metallic,
        Texture texture = null,
        Color? emission = null)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        material.SetColor("_BaseColor", color);
        material.SetColor("_Color", color);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", metallic);
        material.SetTexture("_BaseMap", texture);
        material.SetTexture("_MainTex", texture);
        material.SetTextureScale("_BaseMap", Vector2.one);
        material.SetTextureScale("_MainTex", Vector2.one);
        material.enableInstancing = false;

        if (emission.HasValue)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", emission.Value);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        else
        {
            material.DisableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", Color.black);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        EditorUtility.SetDirty(material);
        return material;
    }
}
