using System.Collections.Generic;
using UnityEngine;
using static GripContactConstants;

internal sealed class GripContactDispatcher
{
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ComputeShader computeShader;
    private readonly GripScoreConfig config;
    private readonly GripContactReadbackProcessor readback;
    private readonly int kernel;
    private readonly Vector3[] leftBones = new Vector3[BoneCount];
    private readonly Vector3[] rightBones = new Vector3[BoneCount];
    private readonly float[] leftCurls = new float[FingerCurlEstimator.FingerCount];
    private readonly float[] rightCurls = new float[FingerCurlEstimator.FingerCount];
    private long nextEpoch;
    private int debugFailuresRemaining;
    private bool debugForceFailures;

    public GripContactDispatcher(
        SceneConfiguror sceneConfiguror,
        ComputeShader computeShader,
        GripScoreConfig config,
        GripContactReadbackProcessor readback)
    {
        this.sceneConfiguror = sceneConfiguror;
        this.computeShader = computeShader;
        this.config = config;
        this.readback = readback;
        kernel = computeShader.FindKernel("CSMain");
    }

    public void InjectReadbackFailures(int epochCount)
    {
        debugFailuresRemaining += epochCount;
    }

    public void SetForcedReadbackFailures(bool enabled)
    {
        debugForceFailures = enabled;
    }

    public void StageHand(Hand hand, List<Vector3> handBonePositions, IReadOnlyList<float> fingerCurls)
    {
        Vector3[] bones = hand == Hand.Left ? leftBones : rightBones;
        float[] curls = hand == Hand.Left ? leftCurls : rightCurls;
        for (int i = 0; i < BoneCount; i++)
        {
            bones[i] = handBonePositions[i];
        }
        for (int i = 0; i < FingerCurlEstimator.FingerCount; i++)
        {
            curls[i] = fingerCurls[i];
        }
    }

    public void Dispatch(
        GameObject hold,
        GripHoldContactState state,
        int handMask,
        int leftTargetGeneration,
        int rightTargetGeneration)
    {
        state.SetOverlayVisible(true);
        GripContactOutputSet output = state.GetAvailableOutput();
        if (output == null)
        {
            return;
        }

        state.leftHandBones.SetData(leftBones);
        state.rightHandBones.SetData(rightBones);
        bool forceFailure = debugForceFailures || debugFailuresRemaining > 0;
        if (!debugForceFailures && forceFailure)
        {
            debugFailuresRemaining--;
        }
        output.Reset(
            handMask,
            ++nextEpoch,
            Time.unscaledTime,
            forceFailure,
            leftTargetGeneration,
            rightTargetGeneration,
            leftCurls,
            rightCurls);

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
        state.SetContactBuffer(output.vertexContactData, output.epoch);
        output.RequestReadback();
        readback.Enqueue(output);
    }
}
