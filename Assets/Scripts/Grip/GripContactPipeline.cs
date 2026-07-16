using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class GripContactPipeline : IDisposable
{
    private const int BoneCount = 26;
    private const int TipCount = 10;
    private const int ThreadGroupSize = 128;
    private const int LeftHandMask = 1;
    private const int RightHandMask = 2;

    private readonly SceneConfiguror sceneConfiguror;
    private readonly ComputeShader computeShader;
    private readonly GripScoreConfig config;
    private readonly int kernel;
    private readonly Dictionary<int, HoldContactState> holdStates = new();
    private readonly List<int> staleStateIds = new();
    private readonly Vector3[] leftBones = new Vector3[BoneCount];
    private readonly Vector3[] rightBones = new Vector3[BoneCount];
    private readonly bool supported;
    private bool disposed;
    private bool failed;
    private bool readbackFailureReported;
    private float lastLeftScoreTime;
    private float lastRightScoreTime;

    public GripContactPipeline(
        SceneConfiguror sceneConfiguror,
        ComputeShader computeShader,
        GripScoreConfig config)
    {
        this.sceneConfiguror = sceneConfiguror;
        this.computeShader = computeShader;
        this.config = config;
        supported = SystemInfo.supportsAsyncGPUReadback;
        if (!supported)
        {
            Debug.LogError("Grip feedback requires asynchronous GPU readback support.");
        }
        kernel = computeShader.FindKernel("CSMain");
    }

    public bool IsSupported => supported && !failed;

    public void Process(
        GameObject leftHold,
        GameObject rightHold,
        List<Vector3> leftHandBonePositions,
        List<Vector3> rightHandBonePositions,
        bool leftHandValid = true,
        bool rightHandValid = true)
    {
        if (disposed)
        {
            return;
        }
        EnsureDistanceArrays();
        if (!supported || failed)
        {
            ClearFeedback();
            return;
        }

        bool leftReady = supported && leftHandValid && leftHandBonePositions.Count >= BoneCount;
        bool rightReady = supported && rightHandValid && rightHandBonePositions.Count >= BoneCount;

        if (leftReady)
        {
            for (int i = 0; i < BoneCount; i++)
            {
                leftBones[i] = leftHandBonePositions[i];
            }
        }
        else
        {
            leftHold = null;
            ClearHandFeedback(LeftHandMask);
        }
        if (rightReady)
        {
            for (int i = 0; i < BoneCount; i++)
            {
                rightBones[i] = rightHandBonePositions[i];
            }
        }
        else
        {
            rightHold = null;
            ClearHandFeedback(RightHandMask);
        }

        foreach (HoldContactState state in holdStates.Values)
        {
            state.SetOverlayVisible(false);
        }

        if (leftHold == null)
        {
            ClearHandFeedback(LeftHandMask);
        }
        if (rightHold == null)
        {
            ClearHandFeedback(RightHandMask);
        }

        if (leftHold != null && rightHold == leftHold)
        {
            ProcessHold(leftHold, LeftHandMask | RightHandMask);
        }
        else
        {
            if (leftHold != null)
            {
                ProcessHold(leftHold, LeftHandMask);
            }
            if (rightHold != null)
            {
                ProcessHold(rightHold, RightHandMask);
            }
        }

        sceneConfiguror.perFingerContactMask = sceneConfiguror.leftFingerContactMask |
                                               (sceneConfiguror.rightFingerContactMask << 5);
        sceneConfiguror.currentGripScore = Mathf.Max(
            sceneConfiguror.leftHandGripScore,
            sceneConfiguror.rightHandGripScore);
        RemoveDestroyedStates();
    }

    public void ClearFeedback()
    {
        foreach (HoldContactState state in holdStates.Values)
        {
            state.InvalidateContactData();
        }
        sceneConfiguror.leftFingerContactMask = 0;
        sceneConfiguror.rightFingerContactMask = 0;
        sceneConfiguror.perFingerContactMask = 0;
        sceneConfiguror.leftHandGripScore = 0f;
        sceneConfiguror.rightHandGripScore = 0f;
        sceneConfiguror.currentGripScore = 0f;
        EnsureDistanceArrays();
        Array.Fill(sceneConfiguror.leftHandBoneToHoldMinDistances, float.PositiveInfinity);
        Array.Fill(sceneConfiguror.rightHandBoneToHoldMinDistances, float.PositiveInfinity);
        lastLeftScoreTime = 0f;
        lastRightScoreTime = 0f;
    }

    public void Prepare(IReadOnlyList<GameObject> holds)
    {
        HashSet<int> retainedIds = new();
        if (holds != null)
        {
            foreach (GameObject hold in holds)
            {
                if (hold != null)
                {
                    retainedIds.Add(hold.GetInstanceID());
                }
            }
        }

        staleStateIds.Clear();
        foreach (KeyValuePair<int, HoldContactState> pair in holdStates)
        {
            if (!retainedIds.Contains(pair.Key))
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }

        if (holds == null)
        {
            return;
        }
        foreach (GameObject hold in holds)
        {
            Prepare(hold);
        }
    }

    public void Prepare(GameObject hold)
    {
        if (disposed || hold == null || holdStates.ContainsKey(hold.GetInstanceID()) ||
            !hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return;
        }

        holdStates.Add(
            hold.GetInstanceID(),
            new HoldContactState(this, hold, meshFilter, config.contactPatchMaterial));
    }

    private void EnsureDistanceArrays()
    {
        if (sceneConfiguror.leftHandBoneToHoldMinDistances == null ||
            sceneConfiguror.leftHandBoneToHoldMinDistances.Length != BoneCount)
        {
            sceneConfiguror.leftHandBoneToHoldMinDistances = CreateInfinityDistanceArray();
        }
        if (sceneConfiguror.rightHandBoneToHoldMinDistances == null ||
            sceneConfiguror.rightHandBoneToHoldMinDistances.Length != BoneCount)
        {
            sceneConfiguror.rightHandBoneToHoldMinDistances = CreateInfinityDistanceArray();
        }
    }

    private static float[] CreateInfinityDistanceArray()
    {
        float[] values = new float[BoneCount];
        Array.Fill(values, float.PositiveInfinity);
        return values;
    }

    private void ClearHandFeedback(int handMask)
    {
        if ((handMask & LeftHandMask) != 0)
        {
            sceneConfiguror.leftFingerContactMask = 0;
            sceneConfiguror.leftHandGripScore = 0f;
            Array.Fill(sceneConfiguror.leftHandBoneToHoldMinDistances, float.PositiveInfinity);
            lastLeftScoreTime = 0f;
        }
        if ((handMask & RightHandMask) != 0)
        {
            sceneConfiguror.rightFingerContactMask = 0;
            sceneConfiguror.rightHandGripScore = 0f;
            Array.Fill(sceneConfiguror.rightHandBoneToHoldMinDistances, float.PositiveInfinity);
            lastRightScoreTime = 0f;
        }
        sceneConfiguror.perFingerContactMask = sceneConfiguror.leftFingerContactMask |
                                               (sceneConfiguror.rightFingerContactMask << 5);
        sceneConfiguror.currentGripScore = Mathf.Max(
            sceneConfiguror.leftHandGripScore,
            sceneConfiguror.rightHandGripScore);
    }

    private void HandleReadbackFailure(HoldContactState state, OutputSet output, string bufferName)
    {
        failed = true;
        state.InvalidateContactData();
        ClearFeedback();
        if (!readbackFailureReported)
        {
            Debug.LogError("Grip feedback GPU readback failed for " + bufferName + ".");
            readbackFailureReported = true;
        }
    }

    private void ProcessHold(GameObject hold, int handMask)
    {
        if (!hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return;
        }

        int id = hold.GetInstanceID();
        if (!holdStates.TryGetValue(id, out HoldContactState state))
        {
            Prepare(hold);
            state = holdStates[id];
        }

        OutputSet output = state.GetAvailableOutput();
        if (output == null)
        {
            return;
        }

        state.leftHandBones.SetData(leftBones);
        state.rightHandBones.SetData(rightBones);
        output.Reset(handMask);

        computeShader.SetFloat("_ContactThreshold", config.contactThreshold);
        computeShader.SetFloat("_FixedPointScale", config.fixedPointScale);
        computeShader.SetInt("_VertexCount", state.vertexCount);
        computeShader.SetInt("_BoneCount", BoneCount);
        computeShader.SetInt("_HandMask", handMask);
        computeShader.SetMatrix("_LocalToWorld", hold.transform.localToWorldMatrix);
        Transform normalReference = sceneConfiguror.GetGripNormalReference(hold);
        computeShader.SetMatrix("_NormalToWorld", normalReference.worldToLocalMatrix.transpose);
        computeShader.SetBuffer(kernel, "climbingHoldVertices", state.vertices);
        computeShader.SetBuffer(kernel, "climbingHoldNormals", state.normals);
        computeShader.SetBuffer(kernel, "climbingHoldVertexAreas", state.vertexAreas);
        computeShader.SetBuffer(kernel, "leftHandBones", state.leftHandBones);
        computeShader.SetBuffer(kernel, "rightHandBones", state.rightHandBones);
        computeShader.SetBuffer(kernel, "vertexContactData", output.vertexContactData);
        computeShader.SetBuffer(kernel, "tipContactStats", output.tipContactStats);
        computeShader.SetBuffer(kernel, "handBoneToHoldMinDistances", output.boneDistances);
        computeShader.Dispatch(kernel, (state.vertexCount + ThreadGroupSize - 1) / ThreadGroupSize, 1, 1);
        state.SetContactBuffer(output.vertexContactData);
        state.SetOverlayVisible(true);
        output.RequestReadback();
    }

    private void OnStatsReadback(HoldContactState state, OutputSet output, AsyncGPUReadbackRequest request)
    {
        try
        {
            if (disposed || failed || state.hold == null)
            {
                return;
            }
            if (request.hasError)
            {
                HandleReadbackFailure(state, output, "contact statistics");
                return;
            }

            NativeArray<GripContactAccumulator> data = request.GetData<GripContactAccumulator>();
            for (int i = 0; i < TipCount; i++)
            {
                output.accumulators[i] = data[i];
            }

            float holdScore = 0f;
            if ((output.handMask & LeftHandMask) != 0 &&
                sceneConfiguror.leftHandInteractingClimbingHold == state.hold)
            {
                GripScoreResult result = GripScoreCalculator.Calculate(output.accumulators, 0, config);
                sceneConfiguror.leftFingerContactMask = result.contactMask;
                sceneConfiguror.leftHandGripScore = SmoothScore(
                    sceneConfiguror.leftHandGripScore,
                    result.score,
                    ref lastLeftScoreTime);
                holdScore = Mathf.Max(holdScore, sceneConfiguror.leftHandGripScore);
            }
            if ((output.handMask & RightHandMask) != 0 &&
                sceneConfiguror.rightHandInteractingClimbingHold == state.hold)
            {
                GripScoreResult result = GripScoreCalculator.Calculate(output.accumulators, 5, config);
                sceneConfiguror.rightFingerContactMask = result.contactMask;
                sceneConfiguror.rightHandGripScore = SmoothScore(
                    sceneConfiguror.rightHandGripScore,
                    result.score,
                    ref lastRightScoreTime);
                holdScore = Mathf.Max(holdScore, sceneConfiguror.rightHandGripScore);
            }

            sceneConfiguror.perFingerContactMask = sceneConfiguror.leftFingerContactMask |
                                                   (sceneConfiguror.rightFingerContactMask << 5);
            sceneConfiguror.currentGripScore = Mathf.Max(
                sceneConfiguror.leftHandGripScore,
                sceneConfiguror.rightHandGripScore);
            state.SetGripScore(holdScore);
        }
        finally
        {
            output.CompleteRequest();
        }
    }

    private void OnBoneReadback(HoldContactState state, OutputSet output, AsyncGPUReadbackRequest request)
    {
        try
        {
            if (disposed || failed || state.hold == null)
            {
                return;
            }
            if (request.hasError)
            {
                HandleReadbackFailure(state, output, "bone distances");
                return;
            }

            NativeArray<uint> data = request.GetData<uint>();
            if ((output.handMask & LeftHandMask) != 0 &&
                sceneConfiguror.leftHandInteractingClimbingHold == state.hold)
            {
                for (int i = 0; i < BoneCount; i++)
                {
                    sceneConfiguror.leftHandBoneToHoldMinDistances[i] = UIntFloat.ToFloat(data[i]);
                }
            }
            if ((output.handMask & RightHandMask) != 0 &&
                sceneConfiguror.rightHandInteractingClimbingHold == state.hold)
            {
                for (int i = 0; i < BoneCount; i++)
                {
                    sceneConfiguror.rightHandBoneToHoldMinDistances[i] = UIntFloat.ToFloat(data[BoneCount + i]);
                }
            }
        }
        finally
        {
            output.CompleteRequest();
        }
    }

    private float SmoothScore(float current, float target, ref float lastUpdateTime)
    {
        float now = Time.unscaledTime;
        float delta = lastUpdateTime > 0f ? now - lastUpdateTime : Time.unscaledDeltaTime;
        lastUpdateTime = now;
        float alpha = config.smoothingSeconds <= 0f
            ? 1f
            : 1f - Mathf.Exp(-Mathf.Max(delta, 0f) / config.smoothingSeconds);
        return Mathf.Lerp(current, target, alpha);
    }

    private void RemoveDestroyedStates()
    {
        staleStateIds.Clear();
        foreach (KeyValuePair<int, HoldContactState> pair in holdStates)
        {
            if (pair.Value.hold == null)
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        foreach (HoldContactState state in holdStates.Values)
        {
            state.Dispose();
        }
        holdStates.Clear();
    }

    private sealed class HoldContactState : IDisposable
    {
        private readonly OutputSet[] outputs;
        private readonly Renderer overlayRenderer;
        private readonly MaterialPropertyBlock overlayProperties;
        private readonly GripScoreConfig config;
        private bool rimGlowActive;
        private bool contactBufferReady;
        private bool overlayRequested;
        public readonly GameObject hold;
        public readonly Mesh mesh;
        public readonly int vertexCount;
        public readonly ComputeBuffer vertices;
        public readonly ComputeBuffer normals;
        public readonly ComputeBuffer vertexAreas;
        public readonly ComputeBuffer leftHandBones;
        public readonly ComputeBuffer rightHandBones;

        public HoldContactState(
            GripContactPipeline owner,
            GameObject hold,
            MeshFilter meshFilter,
            Material overlayMaterial)
        {
            this.hold = hold;
            config = owner.config;
            mesh = meshFilter.sharedMesh;
            vertexCount = mesh.vertexCount;
            Vector3[] meshVertices = mesh.vertices;
            Vector3[] meshNormals = mesh.normals;
            if (meshNormals.Length != vertexCount)
            {
                mesh.RecalculateNormals();
                meshNormals = mesh.normals;
            }
            float[] areas = ComputeVertexAreas(mesh, hold.transform);

            vertices = new ComputeBuffer(vertexCount, sizeof(float) * 3);
            normals = new ComputeBuffer(vertexCount, sizeof(float) * 3);
            vertexAreas = new ComputeBuffer(vertexCount, sizeof(float));
            leftHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
            rightHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
            vertices.SetData(meshVertices);
            normals.SetData(meshNormals);
            vertexAreas.SetData(areas);
            outputs = new[]
            {
                new OutputSet(owner, this),
                new OutputSet(owner, this),
            };
            overlayRenderer = EnsureOverlay(hold, mesh, overlayMaterial);
            overlayProperties = new MaterialPropertyBlock();
            if (overlayRenderer != null)
            {
                overlayRenderer.enabled = false;
                overlayRenderer.GetPropertyBlock(overlayProperties);
                overlayProperties.SetFloat("_ContactThreshold", owner.config.contactThreshold);
                overlayProperties.SetFloat("_ProximityThreshold", owner.config.proximityThreshold);
                overlayProperties.SetFloat("_RimGlowEnabled", 0f);
                overlayProperties.SetFloat("_RimGlowAlpha", owner.config.rimGlowAlpha);
                overlayProperties.SetFloat("_RimGlowPower", owner.config.rimGlowPower);
                overlayRenderer.SetPropertyBlock(overlayProperties);
            }
        }

        public OutputSet GetAvailableOutput()
        {
            foreach (OutputSet output in outputs)
            {
                if (!output.IsPending)
                {
                    return output;
                }
            }
            return null;
        }

        public void SetOverlayVisible(bool visible)
        {
            overlayRequested = visible;
            if (overlayRenderer != null)
            {
                overlayRenderer.enabled = visible && contactBufferReady;
            }
        }

        public void SetContactBuffer(ComputeBuffer contactBuffer)
        {
            if (overlayRenderer == null)
            {
                return;
            }

            overlayRenderer.GetPropertyBlock(overlayProperties);
            overlayProperties.SetBuffer("_ContactData", contactBuffer);
            overlayRenderer.SetPropertyBlock(overlayProperties);
            contactBufferReady = true;
            overlayRenderer.enabled = overlayRequested;
        }

        public void InvalidateContactData()
        {
            contactBufferReady = false;
            overlayRequested = false;
            if (overlayRenderer != null)
            {
                overlayRenderer.enabled = false;
            }
        }

        public void SetGripScore(float score)
        {
            if (overlayRenderer == null)
            {
                return;
            }

            float lowerThreshold = Mathf.Clamp01(config.rimGlowThreshold - config.hysteresis);
            float upperThreshold = Mathf.Clamp01(config.rimGlowThreshold + config.hysteresis);
            rimGlowActive = rimGlowActive ? score > lowerThreshold : score >= upperThreshold;
            overlayRenderer.GetPropertyBlock(overlayProperties);
            overlayProperties.SetFloat("_GripScore", Mathf.Clamp01(score));
            overlayProperties.SetFloat("_RimGlowEnabled", config.rimGlow && rimGlowActive ? 1f : 0f);
            overlayProperties.SetColor("_RimColor", config.EvaluateScoreColor(score));
            overlayRenderer.SetPropertyBlock(overlayProperties);
        }

        public void Dispose()
        {
            InvalidateContactData();
            foreach (OutputSet output in outputs)
            {
                output.Dispose();
            }
            vertices.Release();
            normals.Release();
            vertexAreas.Release();
            leftHandBones.Release();
            rightHandBones.Release();
        }

        private static float[] ComputeVertexAreas(Mesh sourceMesh, Transform transform)
        {
            Vector3[] meshVertices = sourceMesh.vertices;
            int[] triangles = sourceMesh.triangles;
            float[] areas = new float[meshVertices.Length];
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                Vector3 edgeA = transform.TransformVector(meshVertices[b] - meshVertices[a]);
                Vector3 edgeB = transform.TransformVector(meshVertices[c] - meshVertices[a]);
                float thirdArea = Vector3.Cross(edgeA, edgeB).magnitude / 6f;
                areas[a] += thirdArea;
                areas[b] += thirdArea;
                areas[c] += thirdArea;
            }
            return areas;
        }

        private static Renderer EnsureOverlay(GameObject hold, Mesh sourceMesh, Material overlayMaterial)
        {
            Transform overlayTransform = hold.transform.Find("Contact Patch Overlay");
            GameObject overlay;
            if (overlayTransform == null)
            {
                overlay = new GameObject("Contact Patch Overlay");
                overlay.transform.SetParent(hold.transform, false);
                overlay.AddComponent<MeshFilter>();
                overlay.AddComponent<MeshRenderer>();
            }
            else
            {
                overlay = overlayTransform.gameObject;
            }

            overlay.transform.localPosition = Vector3.zero;
            overlay.transform.localRotation = Quaternion.identity;
            overlay.transform.localScale = Vector3.one;
            overlay.GetComponent<MeshFilter>().sharedMesh = sourceMesh;
            MeshRenderer renderer = overlay.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = overlayMaterial != null
                ? overlayMaterial
                : Resources.Load<Material>("ContactPatchOverlay");
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }
    }

    private sealed class OutputSet : IDisposable
    {
        private static readonly GripContactAccumulator[] EmptyStats = new GripContactAccumulator[TipCount];
        private static readonly uint[] InfinityDistances = CreateInfinityDistances();
        private readonly GripContactPipeline owner;
        private readonly HoldContactState state;
        private readonly Action<AsyncGPUReadbackRequest> statsCallback;
        private readonly Action<AsyncGPUReadbackRequest> bonesCallback;
        private int pendingRequests;
        private bool hasRequests;
        public readonly ComputeBuffer vertexContactData;
        public readonly ComputeBuffer tipContactStats;
        public readonly ComputeBuffer boneDistances;
        public readonly GripContactAccumulator[] accumulators = new GripContactAccumulator[TipCount];
        public int handMask;
        public AsyncGPUReadbackRequest statsRequest;
        public AsyncGPUReadbackRequest bonesRequest;

        public bool IsPending => pendingRequests > 0;

        public OutputSet(GripContactPipeline owner, HoldContactState state)
        {
            this.owner = owner;
            this.state = state;
            statsCallback = request => owner.OnStatsReadback(state, this, request);
            bonesCallback = request => owner.OnBoneReadback(state, this, request);
            vertexContactData = new ComputeBuffer(state.vertexCount, sizeof(float) * 4);
            tipContactStats = new ComputeBuffer(TipCount, sizeof(int) * 4);
            boneDistances = new ComputeBuffer(BoneCount * 2, sizeof(uint));
        }

        public void Reset(int requestedHandMask)
        {
            handMask = requestedHandMask;
            tipContactStats.SetData(EmptyStats);
            boneDistances.SetData(InfinityDistances);
            pendingRequests = 2;
        }

        public void RequestReadback()
        {
            hasRequests = true;
            statsRequest = AsyncGPUReadback.Request(tipContactStats, statsCallback);
            bonesRequest = AsyncGPUReadback.Request(boneDistances, bonesCallback);
        }

        public void CompleteRequest()
        {
            pendingRequests = Mathf.Max(0, pendingRequests - 1);
        }

        public void Dispose()
        {
            if (hasRequests && !statsRequest.done)
            {
                statsRequest.WaitForCompletion();
            }
            if (hasRequests && !bonesRequest.done)
            {
                bonesRequest.WaitForCompletion();
            }
            vertexContactData.Release();
            tipContactStats.Release();
            boneDistances.Release();
        }

        private static uint[] CreateInfinityDistances()
        {
            uint infinity = UIntFloat.ToUInt(float.PositiveInfinity);
            uint[] values = new uint[BoneCount * 2];
            Array.Fill(values, infinity);
            return values;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct UIntFloat
    {
        [FieldOffset(0)] private uint unsigned;
        [FieldOffset(0)] private float floating;

        public static float ToFloat(uint value)
        {
            return new UIntFloat { unsigned = value }.floating;
        }

        public static uint ToUInt(float value)
        {
            return new UIntFloat { floating = value }.unsigned;
        }
    }
}
