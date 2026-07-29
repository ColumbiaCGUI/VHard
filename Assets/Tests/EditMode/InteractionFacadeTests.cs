using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class InteractionFacadeTests
{
    [Test]
    public void OverlapResolverKeepsHoldUntilItsLastColliderExits()
    {
        OverlapContactResolver<string> resolver = new();

        resolver.Enter("A5");
        resolver.Enter("A5");
        resolver.Enter("B6");
        Assert.That(resolver.Current, Is.EqualTo("B6"));

        resolver.Exit("B6");
        Assert.That(resolver.Current, Is.EqualTo("A5"));
        resolver.Exit("A5");
        Assert.That(resolver.Current, Is.EqualTo("A5"));
        resolver.Exit("A5");
        Assert.That(resolver.Current, Is.Null);
    }

    [Test]
    public void OverlapResolverIgnoresUnmatchedExitAndFallsBackByEnterOrder()
    {
        OverlapContactResolver<string> resolver = new();
        resolver.Enter("A5");
        resolver.Enter("B6");
        resolver.Enter("C7");

        Assert.That(resolver.Exit("missing"), Is.False);
        resolver.Remove("C7");
        Assert.That(resolver.Current, Is.EqualTo("B6"));
        resolver.Remove("B6");
        Assert.That(resolver.Current, Is.EqualTo("A5"));
    }

    [Test]
    public void RouteCuePolicyKeepsConditionsBAndCSymmetricAndSurfacesBaseline()
    {
        RouteCuePresentation baseline = RouteCuePresentation.PhysicalBoardLeds;

        Assert.That(RouteCuePolicy.ForCondition("A", baseline), Is.EqualTo(baseline));
        Assert.That(RouteCuePolicy.ForCondition("B", baseline),
            Is.EqualTo(RouteCuePresentation.VirtualHalos));
        Assert.That(RouteCuePolicy.ForCondition("C", baseline),
            Is.EqualTo(RouteCuePresentation.VirtualHalos));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Start).RingCount, Is.EqualTo(2));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Intermediate).RingCount, Is.EqualTo(1));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Finish).RingCount, Is.EqualTo(2));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Start).Color, Is.EqualTo(RouteCuePolicy.StartColor));
        Assert.That(RouteCuePolicy.GetStyle(RouteCueRole.Finish).Color, Is.EqualTo(RouteCuePolicy.FinishColor));
    }

    [Test]
    public void RouteCueProjectionUsesGridAnchorRatherThanRendererCenter()
    {
        Vector3 gridAnchor = new(0.4f, 0.08f, 1.2f);
        Vector3 projected = RouteCuePolicy.ProjectGridAnchorOntoBoard(
            gridAnchor,
            Vector3.zero,
            Vector3.up,
            0.015f);

        Assert.That(projected, Is.EqualTo(new Vector3(0.4f, 0.015f, 1.2f)));
    }

    [Test]
    public void RouteCueProjectionUsesTheTiltedMainSurfaceFrame()
    {
        Quaternion mountRotation = Quaternion.Euler(50f, 0f, 180f);
        Vector3 boardNormal = mountRotation * Vector3.up;
        Vector3 boardVertical = RouteCuePolicy.GetBoardVertical(boardNormal);
        Vector3 planePoint = new(0f, 0.37f, 0f);
        Vector3 seatedHoldAnchor = planePoint + boardVertical * 1.2f + boardNormal * 0.04f;

        Vector3 projected = RouteCuePolicy.ProjectGridAnchorOntoBoard(
            seatedHoldAnchor,
            planePoint,
            boardNormal,
            0.015f);

        Assert.That(boardVertical.y, Is.GreaterThan(0f));
        Assert.That(Vector3.Distance(
                projected,
                planePoint + boardVertical * 1.2f + boardNormal * 0.015f),
            Is.LessThan(0.00001f));
    }

    [Test]
    public void CpuGripAcquisitionIsExplicitlyDegradedOnlyForBAndCContexts()
    {
        Assert.That(DegradedGripContactAcquisition.ShouldUseCpu(
            false,
            GripAcquisitionContext.WallGrip), Is.False);
        Assert.That(DegradedGripContactAcquisition.ShouldUseCpu(
            true,
            GripAcquisitionContext.None), Is.False);
        Assert.That(DegradedGripContactAcquisition.ShouldUseCpu(
            true,
            GripAcquisitionContext.WallGrip), Is.True);
        Assert.That(DegradedGripContactAcquisition.ShouldUseCpu(
            true,
            GripAcquisitionContext.DetachedInspection), Is.True);
        Assert.That(DegradedGripContactAcquisition.ShouldUseCpu(
            true,
            (GripAcquisitionContext)999), Is.False);
    }

    [Test]
    public void CpuGripAcquisitionUsesGpuEquivalentRootMeshVerticesNotHoverTrigger()
    {
        GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            UnityEngine.Object.DestroyImmediate(hold.GetComponent<BoxCollider>());
            SphereCollider hover = hold.AddComponent<SphereCollider>();
            hover.isTrigger = true;
            hover.radius = 10f;

            Assert.That(DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error), Is.True, error);
            Assert.That(geometry.VertexCount, Is.EqualTo(24));

            Vector3[] bones = new Vector3[GripEngagementGate.RequiredBoneDistanceCount];
            Array.Fill(bones, Vector3.one * 100f);
            bones[10] = new Vector3(0.6f, 0.5f, 0.5f);
            float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];

            Assert.That(DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                hold,
                geometry,
                bones,
                distances,
                out error), Is.True, error);
            Assert.That(distances[10], Is.EqualTo(0.1f).Within(0.001f));
            Assert.That(float.IsPositiveInfinity(distances[0]), Is.True);

            hold.transform.SetPositionAndRotation(
                new Vector3(2f, 3f, 4f),
                Quaternion.Euler(15f, 25f, 35f));
            hold.transform.localScale = new Vector3(-2f, 2f, 2f);
            Vector3 transformedVertex = hold.transform.TransformPoint(
                new Vector3(0.5f, 0.5f, 0.5f));
            bones[10] = transformedVertex + Vector3.up * 0.02f;

            Assert.That(DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                hold,
                geometry,
                bones,
                distances,
                out error), Is.True, error);
            Assert.That(distances[10], Is.EqualTo(0.02f).Within(0.0001f),
                "Cached local vertices must follow the hold's current world transform.");

            GameObject ghost = UnityEngine.Object.Instantiate(hold);
            try
            {
                ghost.transform.SetPositionAndRotation(
                    new Vector3(-4f, 1f, 2f),
                    Quaternion.Euler(7f, 11f, 13f));
                bones[10] = ghost.transform.TransformPoint(new Vector3(0.5f, 0.5f, 0.5f));
                Assert.That(DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                    ghost,
                    geometry,
                    bones,
                    distances,
                    out error), Is.True, error);
                Assert.That(distances[10], Is.EqualTo(0f).Within(0.00001f),
                    "A detached ghost must reuse its source mesh index with its own transform.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ghost);
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
        }
    }

    [Test]
    public void CpuGripAcquisitionRejectsNonUniformOrShearedTransforms()
    {
        GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            Assert.That(DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error), Is.True, error);
            hold.transform.localScale = new Vector3(2f, 0.5f, 1f);
            Vector3[] bones = new Vector3[GripEngagementGate.RequiredBoneDistanceCount];
            Array.Fill(bones, hold.transform.position);
            float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];

            Assert.That(DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                hold,
                geometry,
                bones,
                distances,
                out error), Is.False);
            Assert.That(error, Does.Contain("uniformly scaled"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
        }
    }

    [Test]
    public void PersistentUnsafeTransformReusesCachedCpuGripIndex()
    {
        const string scenePath = "Assets/Scenes/SampleScene.unity";
        Scene scene = SceneManager.GetSceneByPath(scenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
        }

        GameObject hold = new("UnsafeTransformHold");
        Mesh mesh = new();
        try
        {
            Type configurorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("SceneConfiguror"))
                .Single(type => type != null);
            Component configuror = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .Where(component => component.GetType() == configurorType)
                .Single();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            hold.AddComponent<MeshFilter>().sharedMesh = mesh;
            hold.transform.localScale = new Vector3(2f, 0.5f, 1f);

            PropertyInfo gripProperty = configurorType.GetProperty(
                "Grip",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(gripProperty, Is.Not.Null);
            object gripCoordinator = gripProperty.GetValue(configuror);
            Type coordinatorType = gripCoordinator.GetType();
            FieldInfo cacheField = coordinatorType.GetField(
                "degradedGripGeometry",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo failuresField = coordinatorType.GetField(
                "reportedDegradedGripGeometryFailures",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo positionsField = configurorType.GetField(
                "leftHandBonePositions",
                BindingFlags.Instance | BindingFlags.Public);
            MethodInfo buildMask = coordinatorType.GetMethod(
                "TryBuildDegradedGripContactMask",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(cacheField, Is.Not.Null);
            Assert.That(failuresField, Is.Not.Null);
            Assert.That(positionsField, Is.Not.Null);
            Assert.That(buildMask, Is.Not.Null);

            IDictionary cache = (IDictionary)cacheField.GetValue(gripCoordinator);
            object reportedFailures = failuresField.GetValue(gripCoordinator);
            MethodInfo removeFailure = reportedFailures.GetType().GetMethod("Remove");
            List<Vector3> originalPositions = (List<Vector3>)positionsField.GetValue(configuror);
            positionsField.SetValue(
                configuror,
                Enumerable.Repeat(
                    Vector3.zero,
                    GripEngagementGate.RequiredBoneDistanceCount).ToList());
            removeFailure.Invoke(reportedFailures, new object[] { hold.GetInstanceID() });
            cache.Remove(mesh);
            object leftHand = Enum.Parse(buildMask.GetParameters()[0].ParameterType, "Left");

            try
            {
                LogAssert.Expect(
                    LogType.Error,
                    new Regex("DEGRADED CPU grip acquisition rejected"));
                object[] firstArguments = { leftHand, hold, new float[5], 0 };
                Assert.That((bool)buildMask.Invoke(gripCoordinator, firstArguments), Is.False);
                Assert.That(cache.Contains(mesh), Is.True);
                object cached = cache[mesh];

                object[] secondArguments = { leftHand, hold, new float[5], 0 };
                Assert.That((bool)buildMask.Invoke(gripCoordinator, secondArguments), Is.False);
                Assert.That(cache.Contains(mesh), Is.True);
                Assert.That(cache[mesh], Is.SameAs(cached));
            }
            finally
            {
                positionsField.SetValue(configuror, originalPositions);
                cache.Remove(mesh);
                removeFailure.Invoke(reportedFailures, new object[] { hold.GetInstanceID() });
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
            UnityEngine.Object.DestroyImmediate(mesh);
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }

    [Test]
    public void CpuGripSpatialIndexMatchesBruteForceVertexDistance()
    {
        GameObject hold = new("IndexedHold");
        Mesh mesh = new();
        try
        {
            System.Random random = new(12345);
            Vector3[] vertices = Enumerable.Range(0, 2000)
                .Select(_ => new Vector3(
                    (float)random.NextDouble() * 2f - 1f,
                    (float)random.NextDouble() * 2f - 1f,
                    (float)random.NextDouble() * 2f - 1f))
                .ToArray();
            mesh.vertices = vertices;
            hold.AddComponent<MeshFilter>().sharedMesh = mesh;
            hold.transform.SetPositionAndRotation(
                new Vector3(2f, -3f, 4f),
                Quaternion.Euler(17f, 31f, 43f));
            hold.transform.localScale = new Vector3(-3f, 3f, 3f);

            Assert.That(DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error), Is.True, error);
            Vector3[] bones = new Vector3[GripEngagementGate.RequiredBoneDistanceCount];
            float[] distances = new float[GripEngagementGate.RequiredBoneDistanceCount];
            int[] tips = { 5, 10, 15, 20, 25 };
            for (int sample = 0; sample < 25; sample++)
            {
                Vector3 query = new(
                    (float)random.NextDouble() * 8f - 2f,
                    (float)random.NextDouble() * 8f - 4f,
                    (float)random.NextDouble() * 8f);
                Array.Fill(bones, query);
                float expected = vertices.Min(vertex =>
                    Vector3.Distance(hold.transform.TransformPoint(vertex), query));

                Assert.That(DegradedGripContactAcquisition.TryMeasureFingertipDistances(
                    hold,
                    geometry,
                    bones,
                    distances,
                    out error), Is.True, error);
                foreach (int tip in tips)
                {
                    Assert.That(distances[tip], Is.EqualTo(expected).Within(0.00001f),
                        "sample " + sample + ", tip " + tip);
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    [Test]
    public void CpuGripAcquisitionRejectsBroadHoverVolumeWithoutRootMesh()
    {
        GameObject hold = new("HoverOnlyHold");
        try
        {
            SphereCollider hover = hold.AddComponent<SphereCollider>();
            hover.isTrigger = true;

            Assert.That(DegradedGripContactAcquisition.TryCollectReliableGeometry(
                hold,
                out DegradedGripContactGeometry geometry,
                out string error), Is.False);
            Assert.That(geometry, Is.Null);
            Assert.That(error, Does.Contain("root MeshFilter"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hold);
        }
    }

    [Test]
    public void ShippedMoonBoardHoldsAllProvideReadableCpuFallbackGeometry()
    {
        const string scenePath = "Assets/Scenes/SampleScene.unity";
        Scene scene = SceneManager.GetSceneByPath(scenePath);
        bool openedForTest = !scene.isLoaded;
        if (openedForTest)
        {
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
        }

        try
        {
            Transform moonboard = scene.GetRootGameObjects()
                .Single(root => root.name == "Environment")
                .transform.Find("BoardAlignmentRoot/Moonboard");
            Assert.That(moonboard, Is.Not.Null);
            Transform holds = moonboard.GetComponentsInChildren<Transform>(true)
                .Single(candidate =>
                    candidate.childCount == 140 &&
                    candidate.Cast<Transform>().All(
                        child => child.GetComponent<MeshFilter>() != null));
            Assert.That(holds.childCount, Is.EqualTo(140));
            GameObject largestHold = null;
            int largestVertexCount = 0;
            foreach (Transform hold in holds)
            {
                MeshFilter meshFilter = hold.GetComponent<MeshFilter>();
                Assert.That(meshFilter.sharedMesh, Is.Not.Null, hold.name);
                Assert.That(meshFilter.sharedMesh.isReadable, Is.True, hold.name);
                if (meshFilter.sharedMesh.vertexCount > largestVertexCount)
                {
                    largestHold = hold.gameObject;
                    largestVertexCount = meshFilter.sharedMesh.vertexCount;
                }
            }

            Assert.That(DegradedGripContactAcquisition.TryCollectReliableGeometry(
                largestHold,
                out DegradedGripContactGeometry largestGeometry,
                out string error), Is.True, error);
            Assert.That(largestGeometry.VertexCount, Is.EqualTo(largestVertexCount));
        }
        finally
        {
            if (openedForTest && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
