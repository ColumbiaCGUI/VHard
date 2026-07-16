using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class StudyCoreTests
{
    private GripScoreConfig config;

    [SetUp]
    public void SetUp()
    {
        config = ScriptableObject.CreateInstance<GripScoreConfig>();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(config);
    }

    [Test]
    public void ScheduleParsesThreeBlocksAndQuotedRoute()
    {
        const string csv =
            "participant,block,condition,route\n" +
            "P07,1,B,DEATH STAR\n" +
            "P07,2,C,\"ROUTE, WITH COMMA\"\n" +
            "P07,3,A,THE CRUSH ALT\n";

        bool parsed = StudySchedule.TryParse(csv, out List<StudyScheduleRow> rows, out string error);

        Assert.That(parsed, Is.True, error);
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows[1].route, Is.EqualTo("ROUTE, WITH COMMA"));
    }

    [Test]
    public void ScheduleRejectsDuplicateBlock()
    {
        const string csv =
            "participant,block,condition,route\n" +
            "P01,1,A,DEATH STAR\n" +
            "P01,1,B,SPEED\n" +
            "P01,3,C,THE CRUSH ALT\n";

        bool parsed = StudySchedule.TryParse(csv, out _, out string error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain("Duplicate"));
    }

    [Test]
    public void ScheduleRejectsRepeatedConditionOrRoute()
    {
        const string repeatedCondition =
            "participant,block,condition,route\n" +
            "P01,1,A,DEATH STAR\n" +
            "P01,2,A,SPEED\n" +
            "P01,3,C,THE CRUSH ALT\n";
        const string repeatedRoute =
            "participant,block,condition,route\n" +
            "P01,1,A,DEATH STAR\n" +
            "P01,2,B,DEATH STAR\n" +
            "P01,3,C,THE CRUSH ALT\n";

        Assert.That(StudySchedule.TryParse(repeatedCondition, out _, out string conditionError), Is.False);
        Assert.That(conditionError, Does.Contain("one block in each condition"));
        Assert.That(StudySchedule.TryParse(repeatedRoute, out _, out string routeError), Is.False);
        Assert.That(routeError, Does.Contain("distinct routes"));
    }

    [Test]
    public void ScheduleSortsParticipantIdsNumerically()
    {
        const string csv =
            "participant,block,condition,route\n" +
            "P100,1,A,DEATH STAR\n" +
            "P100,2,B,SPEED\n" +
            "P100,3,C,THE CRUSH ALT\n" +
            "P99,1,A,DEATH STAR\n" +
            "P99,2,B,SPEED\n" +
            "P99,3,C,THE CRUSH ALT\n";

        bool parsed = StudySchedule.TryParse(csv, out List<StudyScheduleRow> rows, out string error);

        Assert.That(parsed, Is.True, error);
        Assert.That(rows[0].participant, Is.EqualTo("P99"));
        Assert.That(rows[3].participant, Is.EqualTo("P100"));
    }

    [Test]
    public void FullJugScoresGreen()
    {
        GripContactAccumulator[] contacts = new GripContactAccumulator[10];
        float area = config.referenceContactArea / 5f;
        SetContact(contacts, 0, area, Vector3.up);
        SetContact(contacts, 1, area, Vector3.right);
        SetContact(contacts, 2, area, Vector3.left);
        SetContact(contacts, 3, area, Vector3.forward);
        SetContact(contacts, 4, area, Vector3.back);

        GripScoreResult result = GripScoreCalculator.Calculate(contacts, 0, config);

        Assert.That(result.score, Is.InRange(0.8f, 1f));
        Assert.That(result.contactMask, Is.EqualTo(0b1_1111));
    }

    [Test]
    public void TwoFingerFlatPokeScoresLow()
    {
        GripContactAccumulator[] contacts = new GripContactAccumulator[10];
        SetContact(contacts, 0, config.referenceContactArea * 0.05f, Vector3.forward);
        SetContact(contacts, 1, config.referenceContactArea * 0.05f, Vector3.forward);

        GripScoreResult result = GripScoreCalculator.Calculate(contacts, 0, config);

        Assert.That(result.score, Is.LessThanOrEqualTo(0.35f));
    }

    [Test]
    public void OpposingWrapOutranksFlatPress()
    {
        GripContactAccumulator[] wrap = new GripContactAccumulator[10];
        GripContactAccumulator[] flat = new GripContactAccumulator[10];
        float area = config.referenceContactArea / 5f;
        Vector3[] wrapNormals = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        for (int finger = 0; finger < wrapNormals.Length; finger++)
        {
            SetContact(wrap, finger, area, wrapNormals[finger]);
            SetContact(flat, finger, area, Vector3.forward);
        }

        float wrapScore = GripScoreCalculator.Calculate(wrap, 0, config).score;
        float flatScore = GripScoreCalculator.Calculate(flat, 0, config).score;

        Assert.That(wrapScore, Is.GreaterThan(flatScore));
    }

    [Test]
    public void CurvedPatchKeepsContactWhenNormalsCancel()
    {
        GripContactAccumulator[] contacts = new GripContactAccumulator[10];
        contacts[0] = new GripContactAccumulator
        {
            area = Mathf.RoundToInt(config.referenceContactArea * config.fixedPointScale),
        };

        GripScoreResult result = GripScoreCalculator.Calculate(contacts, 0, config);

        Assert.That(result.contact, Is.EqualTo(0.2f));
        Assert.That(result.area, Is.EqualTo(1f));
        Assert.That(result.opposition, Is.Zero);
        Assert.That(result.contactMask, Is.EqualTo(1));
    }

    private void SetContact(
        GripContactAccumulator[] contacts,
        int index,
        float area,
        Vector3 normal)
    {
        contacts[index] = new GripContactAccumulator
        {
            area = Mathf.RoundToInt(area * config.fixedPointScale),
            normalX = Mathf.RoundToInt(normal.x * area * config.fixedPointScale),
            normalY = Mathf.RoundToInt(normal.y * area * config.fixedPointScale),
            normalZ = Mathf.RoundToInt(normal.z * area * config.fixedPointScale),
        };
    }
}
