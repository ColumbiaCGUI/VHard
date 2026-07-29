using System;
using System.Collections.Generic;
using UnityEngine;

public static class GripLocomotionAnchor
{
    public const int OpenXrWristBoneIndex = 1;

    public static Vector3 GetWristPosition(IReadOnlyList<Vector3> handBonePositions)
    {
        if (handBonePositions == null || handBonePositions.Count <= OpenXrWristBoneIndex)
        {
            throw new ArgumentException(
                "OpenXR hand positions must contain the palm and wrist.",
                nameof(handBonePositions));
        }
        return handBonePositions[OpenXrWristBoneIndex];
    }
}

public enum GripLocomotionDriver
{
    None,
    Left,
    Right,
}

public static class GripLocomotionPolicy
{
    public static GripLocomotionDriver SelectDriver(
        GripLatchPhase leftPhase,
        bool leftTrackingValid,
        GripLatchPhase rightPhase,
        bool rightTrackingValid)
    {
        bool leftEngaged = leftPhase != GripLatchPhase.Free;
        bool rightEngaged = rightPhase != GripLatchPhase.Free;
        if (leftEngaged == rightEngaged)
        {
            return GripLocomotionDriver.None;
        }

        if (leftEngaged)
        {
            return leftPhase == GripLatchPhase.Latched && leftTrackingValid
                ? GripLocomotionDriver.Left
                : GripLocomotionDriver.None;
        }

        return rightPhase == GripLatchPhase.Latched && rightTrackingValid
            ? GripLocomotionDriver.Right
            : GripLocomotionDriver.None;
    }
}

public enum GripAcquisitionContext
{
    None,
    WallGrip,
    DetachedInspection,
}

public sealed class DegradedGripContactGeometry
{
    private const float TransformTolerance = 0.0001f;

    private readonly Mesh mesh;
    private readonly Vector3[] vertices;

    public DegradedGripContactGeometry(
        Mesh mesh,
        Vector3[] vertices)
    {
        this.mesh = mesh;
        this.vertices = vertices;
        BuildIndex(vertices, 0, vertices.Length, 0);
    }

    public int VertexCount => vertices.Length;
    public Mesh SourceMesh => mesh;

    public bool TryGetSnapshot(
        GameObject hold,
        out Matrix4x4 worldToLocal,
        out float uniformScale,
        out string error)
    {
        worldToLocal = default;
        uniformScale = 0f;
        if (hold == null ||
            !hold.TryGetComponent(out MeshFilter meshFilter) ||
            meshFilter.sharedMesh != mesh)
        {
            error = "The hold's root mesh geometry became unavailable.";
            return false;
        }

        Matrix4x4 localToWorld = hold.transform.localToWorldMatrix;
        Vector3 axisX = localToWorld.MultiplyVector(Vector3.right);
        Vector3 axisY = localToWorld.MultiplyVector(Vector3.up);
        Vector3 axisZ = localToWorld.MultiplyVector(Vector3.forward);
        float scaleX = axisX.magnitude;
        float scaleY = axisY.magnitude;
        float scaleZ = axisZ.magnitude;
        float maximumScale = Mathf.Max(scaleX, Mathf.Max(scaleY, scaleZ));
        if (maximumScale <= Mathf.Epsilon ||
            Mathf.Abs(scaleX - scaleY) > maximumScale * TransformTolerance ||
            Mathf.Abs(scaleX - scaleZ) > maximumScale * TransformTolerance ||
            Mathf.Abs(Vector3.Dot(axisX / scaleX, axisY / scaleY)) > TransformTolerance ||
            Mathf.Abs(Vector3.Dot(axisX / scaleX, axisZ / scaleZ)) > TransformTolerance ||
            Mathf.Abs(Vector3.Dot(axisY / scaleY, axisZ / scaleZ)) > TransformTolerance)
        {
            error = "CPU grip acquisition requires an orthogonal, uniformly scaled hold transform.";
            return false;
        }

        worldToLocal = hold.transform.worldToLocalMatrix;
        uniformScale = (scaleX + scaleY + scaleZ) / 3f;
        error = string.Empty;
        return true;
    }

    public float FindNearestLocalSquaredDistance(Vector3 point)
    {
        return FindNearestLocalSquaredDistance(
            point,
            0,
            vertices.Length,
            0,
            float.PositiveInfinity);
    }

    private float FindNearestLocalSquaredDistance(
        Vector3 point,
        int start,
        int end,
        int depth,
        float bestSquaredDistance)
    {
        if (start >= end)
        {
            return bestSquaredDistance;
        }

        int median = start + (end - start) / 2;
        Vector3 medianPoint = vertices[median];
        bestSquaredDistance = Mathf.Min(
            bestSquaredDistance,
            (medianPoint - point).sqrMagnitude);

        int axis = depth % 3;
        float difference = GetAxis(point, axis) - GetAxis(medianPoint, axis);
        int nearStart = difference <= 0f ? start : median + 1;
        int nearEnd = difference <= 0f ? median : end;
        int farStart = difference <= 0f ? median + 1 : start;
        int farEnd = difference <= 0f ? end : median;
        bestSquaredDistance = FindNearestLocalSquaredDistance(
            point,
            nearStart,
            nearEnd,
            depth + 1,
            bestSquaredDistance);
        if (difference * difference < bestSquaredDistance)
        {
            bestSquaredDistance = FindNearestLocalSquaredDistance(
                point,
                farStart,
                farEnd,
                depth + 1,
                bestSquaredDistance);
        }
        return bestSquaredDistance;
    }

    private static void BuildIndex(Vector3[] points, int start, int end, int depth)
    {
        if (end - start <= 1)
        {
            return;
        }

        int median = start + (end - start) / 2;
        Select(points, start, end - 1, median, depth % 3);
        BuildIndex(points, start, median, depth + 1);
        BuildIndex(points, median + 1, end, depth + 1);
    }

    private static void Select(
        Vector3[] points,
        int left,
        int right,
        int target,
        int axis)
    {
        while (left < right)
        {
            float pivot = GetAxis(points[left + (right - left) / 2], axis);
            int lower = left;
            int current = left;
            int upper = right;
            while (current <= upper)
            {
                float value = GetAxis(points[current], axis);
                if (value < pivot)
                {
                    Swap(points, lower++, current++);
                }
                else if (value > pivot)
                {
                    Swap(points, current, upper--);
                }
                else
                {
                    current++;
                }
            }

            if (target < lower)
            {
                right = lower - 1;
            }
            else if (target > upper)
            {
                left = upper + 1;
            }
            else
            {
                return;
            }
        }
    }

    private static void Swap(Vector3[] points, int first, int second)
    {
        if (first == second)
        {
            return;
        }
        (points[first], points[second]) = (points[second], points[first]);
    }

    private static float GetAxis(Vector3 point, int axis)
    {
        return axis switch
        {
            0 => point.x,
            1 => point.y,
            _ => point.z,
        };
    }
}

public static class DegradedGripContactAcquisition
{
    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };

    public static bool ShouldUseCpu(bool fallbackActivated, GripAcquisitionContext context)
    {
        return fallbackActivated &&
               (context == GripAcquisitionContext.WallGrip ||
                context == GripAcquisitionContext.DetachedInspection);
    }

    public static bool TryCollectReliableGeometry(
        GameObject hold,
        out DegradedGripContactGeometry geometry,
        out string error)
    {
        geometry = null;
        if (hold == null)
        {
            error = "Grip contact geometry requires a hold.";
            return false;
        }

        if (!hold.TryGetComponent(out MeshFilter meshFilter) ||
            meshFilter.sharedMesh == null ||
            !meshFilter.sharedMesh.isReadable)
        {
            error = "No readable root MeshFilter geometry is available.";
            return false;
        }

        Mesh mesh = meshFilter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        if (vertices.Length == 0)
        {
            error = "The hold's root mesh contains no vertices.";
            return false;
        }
        foreach (Vector3 vertex in vertices)
        {
            if (!IsFinite(vertex))
            {
                error = "The hold's root mesh contains a non-finite vertex.";
                return false;
            }
        }

        geometry = new DegradedGripContactGeometry(mesh, vertices);
        error = string.Empty;
        return true;
    }

    public static bool TryMeasureFingertipDistances(
        GameObject hold,
        DegradedGripContactGeometry geometry,
        IReadOnlyList<Vector3> handBonePositions,
        float[] boneDistances,
        out string error)
    {
        if (handBonePositions == null ||
            handBonePositions.Count < GripEngagementGate.RequiredBoneDistanceCount)
        {
            throw new ArgumentException(
                "OpenXR hand positions must contain all hand bones.",
                nameof(handBonePositions));
        }
        if (boneDistances == null ||
            boneDistances.Length < GripEngagementGate.RequiredBoneDistanceCount)
        {
            throw new ArgumentException(
                "Distance output must contain all OpenXR hand bones.",
                nameof(boneDistances));
        }
        if (geometry == null)
        {
            error = "Grip contact geometry is empty.";
            return false;
        }
        if (!geometry.TryGetSnapshot(
                hold,
                out Matrix4x4 worldToLocal,
                out float uniformScale,
                out error))
        {
            return false;
        }

        Array.Fill(boneDistances, float.PositiveInfinity);
        foreach (int boneIndex in FingertipBoneIndices)
        {
            Vector3 fingertip = handBonePositions[boneIndex];
            if (!IsFinite(fingertip))
            {
                error = "A tracked fingertip position is not finite.";
                return false;
            }

        }

        // The GPU shader measures every root-mesh vertex in world space. A similarity transform
        // preserves nearest-neighbour order, so the local kd-tree is exactly equivalent while
        // avoiding a full scan of high-resolution hold meshes on every degraded frame.
        foreach (int boneIndex in FingertipBoneIndices)
        {
            Vector3 localFingertip = worldToLocal.MultiplyPoint3x4(
                handBonePositions[boneIndex]);
            boneDistances[boneIndex] = Mathf.Sqrt(
                geometry.FindNearestLocalSquaredDistance(localFingertip)) * uniformScale;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}

public enum GripLocomotionDiscontinuityReason
{
    None,
    InvalidSample,
    NonMonotonicTime,
    SampleGap,
    MissingDeltaTime,
    ImplausibleSpeed,
}

public sealed class GripLocomotionFilter
{
    public const float MaximumSampleIntervalSeconds = 0.1f;
    public const float MaximumRawSpeedMetersPerSecond = 5f;

    private readonly float minimumCutoff;
    private readonly float beta;
    private readonly float maximumAcceleration;
    private bool initialized;
    private bool terminal;
    private float lastTime;
    private Vector3 lastRawPosition;
    private Vector3 filteredPosition;
    private Vector3 filteredDerivative;
    private Vector3 emittedPosition;
    private Vector3 appliedVelocity;

    public GripLocomotionFilter(
        float minimumCutoff = 1f,
        float beta = 0.007f,
        float maximumAcceleration = 12f)
    {
        if (minimumCutoff <= 0f || beta < 0f || maximumAcceleration <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCutoff), "Filter values must be positive and beta cannot be negative.");
        }
        this.minimumCutoff = minimumCutoff;
        this.beta = beta;
        this.maximumAcceleration = maximumAcceleration;
    }

    public GripLocomotionDiscontinuityReason LastDiscontinuityReason { get; private set; }

    public void Reset(Vector3 wristPosition, float now)
    {
        terminal = false;
        LastDiscontinuityReason = GripLocomotionDiscontinuityReason.None;
        if (!IsFinite(wristPosition) || !IsFinite(now))
        {
            initialized = false;
            appliedVelocity = Vector3.zero;
            LastDiscontinuityReason = GripLocomotionDiscontinuityReason.InvalidSample;
            return;
        }
        Reanchor(wristPosition, now);
    }

    public Vector3 Update(Vector3 wristPosition, float now)
    {
        LastDiscontinuityReason = GripLocomotionDiscontinuityReason.None;
        if (terminal)
        {
            return Vector3.zero;
        }
        if (!IsFinite(wristPosition) || !IsFinite(now))
        {
            initialized = false;
            appliedVelocity = Vector3.zero;
            LastDiscontinuityReason = GripLocomotionDiscontinuityReason.InvalidSample;
            return Vector3.zero;
        }
        if (!initialized)
        {
            Reanchor(wristPosition, now);
            return Vector3.zero;
        }

        return Step(wristPosition, now);
    }

    public void Complete()
    {
        initialized = false;
        terminal = true;
        appliedVelocity = Vector3.zero;
        LastDiscontinuityReason = GripLocomotionDiscontinuityReason.None;
    }

    public void Cancel()
    {
        initialized = false;
        terminal = true;
        appliedVelocity = Vector3.zero;
        LastDiscontinuityReason = GripLocomotionDiscontinuityReason.None;
    }

    private Vector3 Step(Vector3 wristPosition, float now)
    {
        float deltaTime = now - lastTime;
        if (deltaTime < 0f)
        {
            Reanchor(wristPosition, now);
            LastDiscontinuityReason = GripLocomotionDiscontinuityReason.NonMonotonicTime;
            return Vector3.zero;
        }
        if (deltaTime == 0f)
        {
            if ((wristPosition - lastRawPosition).sqrMagnitude > 0f)
            {
                Reanchor(wristPosition, now);
                LastDiscontinuityReason = GripLocomotionDiscontinuityReason.MissingDeltaTime;
            }
            return Vector3.zero;
        }

        if (deltaTime > MaximumSampleIntervalSeconds)
        {
            Reanchor(wristPosition, now);
            LastDiscontinuityReason = GripLocomotionDiscontinuityReason.SampleGap;
            return Vector3.zero;
        }

        Vector3 rawMovement = wristPosition - lastRawPosition;
        Vector3 rawDerivative = rawMovement / deltaTime;
        if (rawDerivative.magnitude > MaximumRawSpeedMetersPerSecond)
        {
            Reanchor(wristPosition, now);
            LastDiscontinuityReason = GripLocomotionDiscontinuityReason.ImplausibleSpeed;
            return Vector3.zero;
        }

        float derivativeAlpha = Alpha(1f, deltaTime);
        filteredDerivative = Vector3.Lerp(filteredDerivative, rawDerivative, derivativeAlpha);
        float cutoff = minimumCutoff + beta * filteredDerivative.magnitude;
        filteredPosition = Vector3.Lerp(filteredPosition, wristPosition, Alpha(cutoff, deltaTime));
        // Chase the filtered position rather than only its instantaneous velocity. Otherwise any
        // movement withheld by the acceleration limiter is discarded and steady-state gain drifts.
        Vector3 targetVelocity = (filteredPosition - emittedPosition) / deltaTime;
        appliedVelocity = Vector3.MoveTowards(
            appliedVelocity,
            targetVelocity,
            maximumAcceleration * deltaTime);
        lastRawPosition = wristPosition;
        lastTime = now;
        Vector3 movement = appliedVelocity * deltaTime;
        emittedPosition += movement;
        return movement;
    }

    private void Reanchor(Vector3 wristPosition, float now)
    {
        initialized = true;
        lastRawPosition = wristPosition;
        lastTime = now;
        filteredPosition = wristPosition;
        filteredDerivative = Vector3.zero;
        emittedPosition = wristPosition;
        appliedVelocity = Vector3.zero;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static float Alpha(float cutoff, float deltaTime)
    {
        float tau = 1f / (2f * Mathf.PI * cutoff);
        return 1f / (1f + tau / deltaTime);
    }
}
