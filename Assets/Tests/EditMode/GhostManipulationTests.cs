using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Amplified proxy rotation and the align-to-wall return. The gain math must scale the shortest
/// rotation path and the align step must move at its configured rate and land exactly on the
/// target, because both write straight into the proxy transform participants are inspecting.
/// </summary>
public sealed class GhostManipulationTests
{
    private static readonly Vector3 Axis = Vector3.up;

    [Test]
    public void FactorZeroCollapsesAnyRotationToIdentity()
    {
        Quaternion scaled = GhostRotationAmplification.ScaleRotation(
            Quaternion.AngleAxis(37f, Axis),
            0f);
        Assert.That(Quaternion.Angle(scaled, Quaternion.identity), Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void FactorOneReturnsTheRotationUnchanged()
    {
        Quaternion rotation = Quaternion.AngleAxis(63f, new Vector3(0.3f, 0.8f, -0.5f).normalized);
        Quaternion scaled = GhostRotationAmplification.ScaleRotation(rotation, 1f);
        Assert.That(Quaternion.Angle(scaled, rotation), Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void FactorTwoDoublesASmallTwistAboutTheSameAxis()
    {
        Quaternion scaled = GhostRotationAmplification.ScaleRotation(
            Quaternion.AngleAxis(25f, Axis),
            2f);
        Assert.That(
            Quaternion.Angle(scaled, Quaternion.AngleAxis(50f, Axis)),
            Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void ScalingMeasuresTheShortestPathNotTheWindingRepresentation()
    {
        // A quaternion built as 350 degrees is a 10-degree turn the other way. A non-integer
        // factor exposes any implementation that scales the winding representation instead:
        // 350 * 1.5 = 525 = 165 degrees, while the correct -10 * 1.5 = -15 degrees.
        Quaternion scaled = GhostRotationAmplification.ScaleRotation(
            Quaternion.AngleAxis(350f, Axis),
            1.5f);
        Assert.That(
            Quaternion.Angle(scaled, Quaternion.AngleAxis(-15f, Axis)),
            Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void IdentityStaysIdentityForAnyFactor()
    {
        Quaternion scaled = GhostRotationAmplification.ScaleRotation(Quaternion.identity, 3f);
        Assert.That(Quaternion.Angle(scaled, Quaternion.identity), Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void ScaleRotationRejectsNonFiniteAndNegativeFactors()
    {
        Quaternion rotation = Quaternion.AngleAxis(20f, Axis);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostRotationAmplification.ScaleRotation(rotation, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostRotationAmplification.ScaleRotation(rotation, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostRotationAmplification.ScaleRotation(rotation, -1f));
    }

    [Test]
    public void AlignStepAdvancesAtTheConfiguredAngularRate()
    {
        Quaternion next = GhostAlignAnimation.Step(
            Quaternion.identity,
            Quaternion.AngleAxis(90f, Axis),
            360f,
            0.1f,
            out bool completed);

        Assert.That(completed, Is.False);
        Assert.That(Quaternion.Angle(next, Quaternion.identity), Is.EqualTo(36f).Within(0.05f));
    }

    [Test]
    public void AlignStepLandsExactlyOnTheTargetAndReportsCompletion()
    {
        Quaternion target = Quaternion.AngleAxis(48f, new Vector3(0.2f, -0.7f, 0.4f).normalized);
        Quaternion next = GhostAlignAnimation.Step(
            Quaternion.AngleAxis(-120f, Axis),
            target,
            360f,
            1f,
            out bool completed);

        Assert.That(completed, Is.True);
        Assert.That(next, Is.EqualTo(target));
    }

    [Test]
    public void AlignStepOnAnAlreadyAlignedProxyCompletesWithoutMoving()
    {
        Quaternion pose = Quaternion.AngleAxis(31f, Axis);
        Quaternion next = GhostAlignAnimation.Step(pose, pose, 360f, 0f, out bool completed);

        Assert.That(completed, Is.True);
        Assert.That(next, Is.EqualTo(pose));
    }

    [Test]
    public void AlignStepWithZeroTimeHoldsThePose()
    {
        Quaternion next = GhostAlignAnimation.Step(
            Quaternion.identity,
            Quaternion.AngleAxis(90f, Axis),
            360f,
            0f,
            out bool completed);

        Assert.That(completed, Is.False);
        Assert.That(Quaternion.Angle(next, Quaternion.identity), Is.LessThanOrEqualTo(0.05f));
    }

    [Test]
    public void AlignStepRejectsInvalidSpeedAndTime()
    {
        Quaternion target = Quaternion.AngleAxis(90f, Axis);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostAlignAnimation.Step(Quaternion.identity, target, 0f, 0.1f, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostAlignAnimation.Step(Quaternion.identity, target, float.NaN, 0.1f, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostAlignAnimation.Step(Quaternion.identity, target, 360f, -0.1f, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GhostAlignAnimation.Step(Quaternion.identity, target, 360f, float.PositiveInfinity, out _));
    }

    /// <summary>
    /// The runtime controller wiring the policies drive: the gain and align-speed tunables, the
    /// align candidate kind, and the per-proxy align state have to keep existing under these
    /// names for the inspector, the ray targeting, and the recorder rows to stay wired.
    /// </summary>
    [Test]
    public void ControllerExposesTheAmplifiedRotationAndAlignSurface()
    {
        Type controller = FindLoadedType("GhostHoldController");
        const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        Assert.That(controller.GetField("rotationGain", NonPublicInstance), Is.Not.Null);
        Assert.That(controller.GetField("alignSpeedDegreesPerSecond", NonPublicInstance), Is.Not.Null);

        Type candidateKind = controller.GetNestedType("CandidateKind", BindingFlags.NonPublic);
        Assert.That(candidateKind, Is.Not.Null);
        Assert.That(Enum.GetNames(candidateKind), Does.Contain("AlignGhost"));

        Type ghostInstance = controller.GetNestedType("GhostInstance", BindingFlags.NonPublic);
        Assert.That(ghostInstance, Is.Not.Null);
        Assert.That(ghostInstance.GetField("AlignAffordance", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null);
        Assert.That(ghostInstance.GetField("AlignAnimating", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null);
        Assert.That(
            ghostInstance.GetField("AlignSpeedDegreesPerSecond", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null,
            "The align rate is snapshotted per animation so a live inspector edit cannot fault a running return.");

        Type handPointer = controller.GetNestedType("HandPointer", BindingFlags.NonPublic);
        Assert.That(handPointer, Is.Not.Null);
        Assert.That(
            handPointer.GetField("GrabSurplusFactor", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null,
            "The gain is snapshotted per grab so a live inspector edit cannot jump a held proxy.");
    }

    private static Type FindLoadedType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name))
            .Single(type => type != null);
    }
}
