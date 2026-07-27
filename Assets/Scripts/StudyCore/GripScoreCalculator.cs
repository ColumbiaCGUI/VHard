using System;
using UnityEngine;

[Serializable]
public struct GripContactAccumulator
{
    public int area;
    public int normalX;
    public int normalY;
    public int normalZ;
}

public readonly struct GripScoreResult
{
    public readonly float score;
    public readonly float contact;
    public readonly float area;
    public readonly float opposition;
    public readonly float loadAlignment;
    public readonly int contactMask;

    public GripScoreResult(
        float score,
        float contact,
        float area,
        float opposition,
        float loadAlignment,
        int contactMask)
    {
        this.score = score;
        this.contact = contact;
        this.area = area;
        this.opposition = opposition;
        this.loadAlignment = loadAlignment;
        this.contactMask = contactMask;
    }
}

public static class GripScoreCalculator
{
    public static GripScoreResult Calculate(
        GripContactAccumulator[] accumulators,
        int startIndex,
        GripScoreConfig config)
    {
        if (accumulators == null || accumulators.Length < startIndex + 5)
        {
            throw new ArgumentException("Five fingertip accumulators are required.", nameof(accumulators));
        }

        float inverseScale = 1f / config.fixedPointScale;
        float totalArea = 0f;
        int contactedFingers = 0;
        int contactMask = 0;
        Vector3 summedUnitNormals = Vector3.zero;
        int validNormalCount = 0;
        float strongestUpwardSupport = 0f;

        for (int finger = 0; finger < 5; finger++)
        {
            GripContactAccumulator accumulator = accumulators[startIndex + finger];
            if (accumulator.area <= 0)
            {
                continue;
            }

            float patchArea = accumulator.area * inverseScale;
            Vector3 areaWeightedNormal = new(
                accumulator.normalX * inverseScale,
                accumulator.normalY * inverseScale,
                accumulator.normalZ * inverseScale);
            totalArea += patchArea;
            contactedFingers++;
            contactMask |= 1 << finger;
            if (areaWeightedNormal.sqrMagnitude <= 0.0000000001f)
            {
                continue;
            }

            Vector3 patchNormal = areaWeightedNormal.normalized;
            summedUnitNormals += patchNormal;
            validNormalCount++;
            strongestUpwardSupport = Mathf.Max(
                strongestUpwardSupport,
                Mathf.Max(0f, Vector3.Dot(patchNormal, Vector3.up)));
        }

        float contact = contactedFingers / 5f;
        float area = Mathf.Clamp01(totalArea / config.referenceContactArea);
        float opposition = validNormalCount > 1
            ? Mathf.Clamp01(1f - summedUnitNormals.magnitude / validNormalCount)
            : 0f;
        float loadAlignment = strongestUpwardSupport;
        float weightSum = Mathf.Max(config.WeightSum, 0.0001f);
        float score = Mathf.Clamp01((
            config.contactWeight * contact +
            config.areaWeight * area +
            config.oppositionWeight * opposition +
            config.loadAlignmentWeight * loadAlignment) / weightSum);

        return new GripScoreResult(
            score,
            contact,
            area,
            opposition,
            loadAlignment,
            contactMask);
    }
}
