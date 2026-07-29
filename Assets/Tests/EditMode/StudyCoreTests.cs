using System.Collections.Generic;
using System.IO;
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
            "P07,1,B,MB2016-19215\n" +
            "P07,2,C,\"ROUTE, WITH COMMA\"\n" +
            "P07,3,A,MB2016-170190\n";

        bool parsed = StudySchedule.TryParse(csv, out List<StudyScheduleRow> rows, out string error);

        Assert.That(parsed, Is.True, error);
        Assert.That(rows, Has.Count.EqualTo(3));
        Assert.That(rows[1].route, Is.EqualTo("ROUTE, WITH COMMA"));
    }

    [Test]
    public void GripReadbackHealthWaitsForThresholdAndElapsedSecond()
    {
        GripReadbackHealth health = new(false, 0f);
        for (int epoch = 0; epoch < GripReadbackHealth.FailureThreshold; epoch++)
        {
            Assert.That(health.RecordEpoch(false, 0.5f), Is.EqualTo(GripReadbackAction.None));
        }

        Assert.That(health.Evaluate(0.99f), Is.EqualTo(GripReadbackAction.None));
        Assert.That(health.Evaluate(1f), Is.EqualTo(GripReadbackAction.Recover));
    }

    [Test]
    public void GripReadbackEpochRequiresBothSuccessfulReadbacks()
    {
        GripReadbackEpochState epoch = new();
        epoch.Reset();
        epoch.RecordStatistics(true);
        Assert.That(epoch.IsComplete, Is.False);

        epoch.RecordBones(false);
        Assert.That(epoch.IsComplete, Is.True);
        Assert.That(epoch.Succeeded, Is.False);

        epoch.Reset();
        epoch.RecordBones(true);
        epoch.RecordStatistics(true);
        Assert.That(epoch.Succeeded, Is.True);
    }

    [Test]
    public void GripReadbackHealthSuccessResetsConsecutiveFailures()
    {
        GripReadbackHealth health = new(false, 0f);
        for (int epoch = 0; epoch < GripReadbackHealth.FailureThreshold - 1; epoch++)
        {
            health.RecordEpoch(false, 1.1f);
        }

        Assert.That(health.RecordEpoch(true, 1.2f), Is.EqualTo(GripReadbackAction.None));
        for (int epoch = 0; epoch < GripReadbackHealth.FailureThreshold - 1; epoch++)
        {
            Assert.That(health.RecordEpoch(false, 2.3f), Is.EqualTo(GripReadbackAction.None));
        }
        Assert.That(health.ConsecutiveFailures,
            Is.EqualTo(GripReadbackHealth.FailureThreshold - 1));
    }

    [Test]
    public void GripReadbackHealthDegradesAfterRecoveryWasAttempted()
    {
        GripReadbackHealth health = new(true, 0f);
        GripReadbackAction action = GripReadbackAction.None;
        for (int epoch = 0; epoch < GripReadbackHealth.FailureThreshold; epoch++)
        {
            action = health.RecordEpoch(false, 0.1f);
        }

        Assert.That(action, Is.EqualTo(GripReadbackAction.Degrade));
    }

    [Test]
    public void ScheduleRejectsDuplicateBlock()
    {
        const string csv =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-19215\n" +
            "P01,1,B,MB2016-21329\n" +
            "P01,3,C,MB2016-170190\n";

        bool parsed = StudySchedule.TryParse(csv, out _, out string error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain("Duplicate"));
    }

    [Test]
    public void ScheduleRejectsRepeatedConditionOrRoute()
    {
        const string repeatedCondition =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-19215\n" +
            "P01,2,A,MB2016-21329\n" +
            "P01,3,C,MB2016-170190\n";
        const string repeatedRoute =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-19215\n" +
            "P01,2,B,MB2016-19215\n" +
            "P01,3,C,MB2016-170190\n";

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
            "P100,1,A,MB2016-19215\n" +
            "P100,2,B,MB2016-21329\n" +
            "P100,3,C,MB2016-170190\n" +
            "P99,1,A,MB2016-19215\n" +
            "P99,2,B,MB2016-21329\n" +
            "P99,3,C,MB2016-170190\n";

        bool parsed = StudySchedule.TryParse(csv, out List<StudyScheduleRow> rows, out string error);

        Assert.That(parsed, Is.True, error);
        Assert.That(rows[0].participant, Is.EqualTo("P99"));
        Assert.That(rows[3].participant, Is.EqualTo("P100"));
    }

    [Test]
    public void CatalogArchivesExact2016StudyContent()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();

        Assert.That(catalog.holds, Has.Length.EqualTo(140));
        Assert.That(catalog.routes, Has.Length.EqualTo(3));
        Assert.That(catalog.routes[0].id, Is.EqualTo("MB2016-19215"));
        Assert.That(catalog.routes[1].id, Is.EqualTo("MB2016-21329"));
        Assert.That(catalog.routes[2].id, Is.EqualTo("MB2016-170190"));
        Assert.That(catalog.TryGetHold("F18", out _), Is.False);
        Assert.That(catalog.TryGetHold("D1", out _), Is.False);
        foreach (MoonBoardRouteDefinition route in catalog.routes)
        {
            Assert.That(route.grade, Is.EqualTo("6B+"));
            Assert.That(route.isBenchmark, Is.True);
            Assert.That(route.moves, Has.Length.EqualTo(7));
            Assert.That(route.moves, Has.Exactly(2).Matches<MoonBoardRouteMove>(move => move.role == "start"));
            Assert.That(route.moves, Has.Exactly(1).Matches<MoonBoardRouteMove>(move => move.role == "finish"));
        }
    }

    [Test]
    public void CatalogGeometryUsesMetricPitchAndFortyDegreeOverhang()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Vector3 a3 = catalog.GetBoardLocalPosition("A3");
        Vector3 k3 = catalog.GetBoardLocalPosition("K3");
        Vector3 a18 = catalog.GetBoardLocalPosition("A18");
        Vector3 g2 = catalog.GetBoardLocalPosition("G2");

        Assert.That(Vector3.Distance(a3, k3), Is.EqualTo(2f).Within(0.0001f));
        Assert.That(Vector3.Distance(a3, a18), Is.EqualTo(3f).Within(0.0001f));
        // Row 2 lives on the 40-degree main surface, one grid pitch below row 3, above the
        // kicker seam (official panel layout + Movement Harlem July 2026 photos).
        Vector3 g2ToA3 = a3 - g2;
        Assert.That(new Vector2(g2ToA3.y, g2ToA3.z).magnitude, Is.EqualTo(0.2f).Within(0.0001f));
        float lowRowOverhang = Mathf.Atan2(Mathf.Abs(g2ToA3.z), g2ToA3.y) * Mathf.Rad2Deg;
        Assert.That(lowRowOverhang, Is.EqualTo(40f).Within(0.001f));
        Assert.That(g2.y, Is.GreaterThan(0.37f));
        Vector3 rise = a18 - a3;
        float overhang = Mathf.Atan2(Mathf.Abs(rise.z), rise.y) * Mathf.Rad2Deg;
        Assert.That(overhang, Is.EqualTo(40f).Within(0.001f));
    }

    [Test]
    public void CatalogPreservesApprovedPhysicalCalibrations()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Assert.That(catalog.schemaVersion, Is.EqualTo(3));

        HashSet<string> expectedCorrectedMeshes = new()
        {
            "W98", "B127", "B109", "B115", "B141", "Y28", "Y6", "B138",
        };
        int correctedMeshes = 0;
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            // Seating offsets are half the hold's physical depth, so they shrank with the
            // schema-3 scale calibration (they were normalised-mesh sized before).
            Assert.That(hold.surfaceOffsetMeters, Is.InRange(0.009f, 0.040f), hold.coordinate);
            if (hold.hasMeshFrameCorrection)
            {
                correctedMeshes++;
                Assert.That(expectedCorrectedMeshes.Remove(hold.scanId), Is.True, hold.coordinate);
            }
        }
        Assert.That(correctedMeshes, Is.EqualTo(8));
        Assert.That(expectedCorrectedMeshes, Is.Empty);

        Assert.That(catalog.TryGetHold("A15", out MoonBoardHoldDefinition a15), Is.True);
        Assert.That(a15.surfaceOffsetMeters, Is.EqualTo(0.0386675299f).Within(0.0000001f));
        Vector3 expectedPosition = new(-1f, 2.4288543f, -1.7780607f);
        Quaternion expectedRotation = new(0.3159854f, -0.3596048f, 0.13088544f, -0.8681628f);
        Assert.That(Vector3.Distance(catalog.GetSeatedBoardLocalPosition(a15), expectedPosition),
            Is.LessThan(0.00001f));
        Assert.That(Quaternion.Angle(catalog.GetBoardLocalRotation(a15), expectedRotation),
            Is.LessThan(0.001f));
    }

    [Test]
    public void CatalogPreservesApprovedMeshFrameYawCorrections()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Dictionary<string, (string scanId, int setupDegrees, float correctionDegrees)> expected = new()
        {
            { "E14", ("B127", 90, 105f) },
            { "I9", ("B109", 135, -140f) },
            { "G17", ("B115", 0, -55f) },
            { "G4", ("B141", 0, -140f) },
            { "H18", ("Y28", 0, 20f) },
            { "F6", ("Y6", 90, 70f) },
            { "C13", ("B138", 315, -120f) },
        };
        Quaternion boardMount = Quaternion.Euler(catalog.SurfaceTiltDegrees, 0f, 180f);
        Vector3 climbingSideNormal = boardMount * Vector3.up;
        Assert.That(Vector3.Distance(
            climbingSideNormal,
            new Vector3(0f, -Mathf.Sin(40f * Mathf.Deg2Rad), -Mathf.Cos(40f * Mathf.Deg2Rad))),
            Is.LessThan(0.000001f));

        foreach (KeyValuePair<string,
                     (string scanId, int setupDegrees, float correctionDegrees)> entry in expected)
        {
            Assert.That(catalog.TryGetHold(entry.Key, out MoonBoardHoldDefinition hold), Is.True);
            Assert.That(hold.scanId, Is.EqualTo(entry.Value.scanId));
            Assert.That(hold.rotationDegrees, Is.EqualTo(entry.Value.setupDegrees), entry.Key);
            Assert.That(hold.hasMeshFrameCorrection, Is.True);
            Assert.That(hold.meshFrameCorrection.TryGetQuaternion(out Quaternion correction), Is.True);
            Quaternion expectedCorrection = Quaternion.AngleAxis(
                entry.Value.correctionDegrees,
                Vector3.forward);
            Assert.That(Quaternion.Angle(correction, expectedCorrection), Is.LessThan(0.001f), entry.Key);

            Quaternion uncorrected = boardMount *
                                     Quaternion.Euler(270f, 360f - hold.rotationDegrees, 0f);
            Quaternion corrected = catalog.GetBoardLocalRotation(hold);
            Quaternion expectedBoardDelta = Quaternion.AngleAxis(
                entry.Value.correctionDegrees,
                climbingSideNormal);
            Quaternion actualBoardDelta = corrected * Quaternion.Inverse(uncorrected);
            Assert.That(Quaternion.Angle(actualBoardDelta, expectedBoardDelta),
                Is.LessThan(0.001f), entry.Key);
            Assert.That(Vector3.Angle(corrected * Vector3.forward, climbingSideNormal),
                Is.LessThan(0.001f), entry.Key);
        }

        Assert.That(catalog.TryGetHold("A15", out MoonBoardHoldDefinition a15), Is.True);
        Assert.That(a15.scanId, Is.EqualTo("W98"));
        Assert.That(a15.rotationDegrees, Is.Zero);
        Assert.That(a15.hasMeshFrameCorrection, Is.True);

        Assert.That(catalog.TryGetHold("K8", out MoonBoardHoldDefinition k8), Is.True);
        Assert.That(k8.scanId, Is.EqualTo("Y30"));
        Assert.That(k8.hasMeshFrameCorrection, Is.False);
        Assert.That(catalog.TryGetHold("D10", out MoonBoardHoldDefinition d10), Is.True);
        Assert.That(d10.scanId, Is.EqualTo("Y33"));
        Assert.That(d10.hasMeshFrameCorrection, Is.False);
    }

    [Test]
    public void CatalogPinsPhysicalHoldScale()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        int metricScans = 0;
        int unalignedScans = 0;
        int photoEstimates = 0;
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            Assert.That(hold.meshScaleMultiplier,
                Is.InRange(MoonBoardStudyCatalog.MinScaleMultiplier,
                           MoonBoardStudyCatalog.MaxScaleMultiplier), hold.coordinate);
            Assert.That(MoonBoardStudyCatalog.ScaleCalibrationSources,
                Does.Contain(hold.scaleCalibrationSource), hold.coordinate);
            if (hold.scaleCalibrationSource == "metric-scan")
            {
                metricScans++;
            }
            else if (hold.scaleCalibrationSource == "metric-scan-unaligned")
            {
                unalignedScans++;
            }
            else
            {
                photoEstimates++;
            }

            // A hold must render at its calibrated physical size, never at the
            // normalised 200 mm grid-cell size the aggregate FBX ships with.
            Vector3 scale = catalog.GetHoldLocalScale(hold);
            Assert.That(scale.x, Is.EqualTo(scale.y).Within(0.000001f), hold.coordinate);
            Assert.That(scale.x, Is.EqualTo(scale.z).Within(0.000001f), hold.coordinate);
            Assert.That(scale.x,
                Is.EqualTo(MoonBoardStudyCatalog.NormalizedMeshScale * hold.meshScaleMultiplier)
                    .Within(0.000001f), hold.coordinate);
            Assert.That(scale.x, Is.LessThan(MoonBoardStudyCatalog.NormalizedMeshScale * 1.5f),
                hold.coordinate);
        }

        // All 140 are exact: depth read off the base-plane-aligned scan, an axis that reproduces
        // the trusted dimension to the last float32 bit on 12 known holds. Nothing is estimated
        // from a photograph, and nothing falls back to the weaker frame-independent descriptor -
        // W98 was the last such hold and is now resolved by a volume-ratio cross-check against the
        // normalised FBX child, which is invariant to frame and centre and agrees to 0.007%.
        Assert.That(metricScans, Is.EqualTo(140));
        Assert.That(unalignedScans, Is.Zero);
        Assert.That(photoEstimates, Is.Zero);

        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition g2), Is.True);
        Assert.That(g2.scanId, Is.EqualTo("W81"));
        Assert.That(g2.scaleCalibrationSource, Is.EqualTo("metric-scan"));
        Assert.That(g2.meshScaleMultiplier, Is.EqualTo(0.402781267f).Within(0.0000001f));
        Assert.That(catalog.GetHoldLocalScale(g2).x, Is.EqualTo(40.2781267f).Within(0.0001f));

        Assert.That(catalog.TryGetHold("E14", out MoonBoardHoldDefinition e14), Is.True);
        Assert.That(e14.scanId, Is.EqualTo("B127"));
        // B127's plain Creality file was deleted; its base-plane-aligned Zplane scan supplies
        // the exact depth used by the shipped calibration.
        Assert.That(e14.scaleCalibrationSource, Is.EqualTo("metric-scan"));
    }

    [Test]
    public void KickerFootJibsFormTheObservedLattice()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Vector3[] jibs = catalog.GetKickerJibLocalPositions();

        // 5 columns x 2 rows, matching the 10 jibs detected in physical_4_iphone13.jpg.
        Assert.That(jibs.Length, Is.EqualTo(10));

        float[] rows = { catalog.geometry.row1KickerHeightMeters, catalog.geometry.row2KickerHeightMeters };
        foreach (float row in rows)
        {
            // Each row sits on the vertical kicker, never above it.
            Assert.That(row, Is.GreaterThan(0f));
            Assert.That(row, Is.LessThan(catalog.geometry.kickerHeightMeters));
        }

        List<float> columns = new();
        foreach (Vector3 jib in jibs)
        {
            Assert.That(rows, Does.Contain(jib.y));
            // Seated proud of the kicker face, on the climber's side.
            Assert.That(jib.z, Is.LessThan(0f));
            if (!columns.Contains(jib.x))
            {
                columns.Add(jib.x);
            }
        }
        columns.Sort();
        Assert.That(columns.Count, Is.EqualTo(5));

        // Column pitch is two grid cells, and the lattice is symmetric about board centre.
        for (int i = 1; i < columns.Count; i++)
        {
            Assert.That(columns[i] - columns[i - 1],
                Is.EqualTo(catalog.geometry.gridSpacingMeters * MoonBoardStudyCatalog.KickerJibColumnStride)
                    .Within(0.000001f));
        }
        Assert.That(columns[0], Is.EqualTo(-columns[columns.Count - 1]).Within(0.000001f));

        // Jibs are furniture: they must never collide with the 140 study holds.
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            Vector3 holdPosition = catalog.GetSeatedBoardLocalPosition(hold);
            foreach (Vector3 jib in jibs)
            {
                Assert.That(Vector3.Distance(holdPosition, jib), Is.GreaterThan(0.05f),
                    hold.coordinate);
            }
        }
    }

    [Test]
    public void CatalogRejectsInvalidHoldScale()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition zero), Is.True);
        zero.meshScaleMultiplier = 0f;
        Assert.That(catalog.TryValidate(out string zeroError), Is.False);
        Assert.That(zeroError, Does.Contain("physical calibration").And.Contain("G2"));

        catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition huge), Is.True);
        huge.meshScaleMultiplier = 2f;
        Assert.That(catalog.TryValidate(out string hugeError), Is.False);
        Assert.That(hugeError, Does.Contain("physical calibration").And.Contain("G2"));

        catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition unknown), Is.True);
        unknown.scaleCalibrationSource = "guessed";
        Assert.That(catalog.TryValidate(out string sourceError), Is.False);
        Assert.That(sourceError, Does.Contain("physical calibration").And.Contain("G2"));

        catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition bad), Is.True);
        bad.meshScaleMultiplier = float.NaN;
        Assert.That(() => catalog.GetHoldLocalScale(bad), Throws.ArgumentException);
    }

    [Test]
    public void CatalogRejectsInvalidPhysicalCalibration()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("A15", out MoonBoardHoldDefinition a15), Is.True);
        a15.surfaceOffsetMeters = 0f;

        Assert.That(catalog.TryValidate(out string offsetError), Is.False);
        Assert.That(offsetError, Does.Contain("physical calibration").And.Contain("A15"));

        catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("A15", out a15), Is.True);
        a15.meshFrameCorrection.w = 0f;
        Assert.That(catalog.TryValidate(out string quaternionError), Is.False);
        Assert.That(quaternionError, Does.Contain("physical calibration").And.Contain("A15"));

        catalog = LoadCatalog();
        Assert.That(catalog.TryGetHold("G2", out MoonBoardHoldDefinition g2), Is.True);
        g2.meshFrameCorrection.w = 1f;
        Assert.That(catalog.TryValidate(out string unflaggedError), Is.False);
        Assert.That(unflaggedError, Does.Contain("physical calibration").And.Contain("G2"));
    }

    [Test]
    public void CatalogRejectsTamperedMeshProvenance()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        string json = File.ReadAllText(path).Replace(
            MoonBoardStudyCatalog.ApprovedMeshSha256,
            new string('0', 64));

        Assert.That(MoonBoardStudyCatalog.TryParse(json, out _, out string error), Is.False);
        Assert.That(error, Does.Contain("provenance"));
    }

    [Test]
    public void ScheduleRejectsRoutesOutsideCatalog()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();
        const string csv =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-19215\n" +
            "P01,2,B,MB2016-21329\n" +
            "P01,3,C,DEATH STAR\n";

        Assert.That(StudySchedule.TryParse(csv, out List<StudyScheduleRow> rows, out string parseError),
            Is.True, parseError);
        Assert.That(StudySchedule.TryValidateRoutes(rows, catalog, out string catalogError), Is.False);
        Assert.That(catalogError, Does.Contain("unknown or unlocked"));
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

    private static readonly string[] ReservedRouteNames =
    {
        "DEATH STAR", "TO JUG, OR NOT TO JUG...",
    };

    [Test]
    public void RouteLibraryParsesRoutesAndNormalizesHoldTokens()
    {
        const string json =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"EXAMPLE TRAVERSE\",\"grade\":\"6B+\"," +
            "\"holds\":[\"d15\",\" g13 \",\"K9\"],\"start\":[\" d15 \",\"D15\"]," +
            "\"finish\":[\"k9\"]}]}";

        bool parsed = RouteLibrary.TryParseJson(
            json, ReservedRouteNames, out List<RouteDefinition> routes, out string error);

        Assert.That(parsed, Is.True, error);
        Assert.That(routes, Has.Count.EqualTo(1));
        Assert.That(routes[0].name, Is.EqualTo("EXAMPLE TRAVERSE"));
        Assert.That(routes[0].grade, Is.EqualTo("6B+"));
        Assert.That(routes[0].holds, Is.EqualTo(new[] { "D15", "G13", "K9" }));
        Assert.That(routes[0].start, Is.EqualTo(new[] { "D15" }));
        Assert.That(routes[0].finish, Is.EqualTo(new[] { "K9" }));
    }

    [Test]
    public void RouteLibraryRejectsInvalidHoldToken()
    {
        const string json =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"BAD\",\"holds\":[\"L5\",\"A6\"]," +
            "\"start\":[\"L5\"],\"finish\":[\"A6\"]}]}";

        bool parsed = RouteLibrary.TryParseJson(json, ReservedRouteNames, out _, out string error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain("L5"));
    }

    [Test]
    public void RouteLibraryRejectsRowNineteenAndRepeatedHold()
    {
        const string rowTooHigh =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"HIGH\",\"holds\":[\"A19\",\"A6\"]," +
            "\"start\":[\"A19\"],\"finish\":[\"A6\"]}]}";
        const string repeated =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"DUP HOLD\",\"holds\":[\"A5\",\"a5\",\"B6\"]," +
            "\"start\":[\"A5\"],\"finish\":[\"B6\"]}]}";

        Assert.That(RouteLibrary.TryParseJson(rowTooHigh, ReservedRouteNames, out _, out string highError), Is.False);
        Assert.That(highError, Does.Contain("A19"));
        Assert.That(RouteLibrary.TryParseJson(repeated, ReservedRouteNames, out _, out string dupError), Is.False);
        Assert.That(dupError, Does.Contain("repeats hold"));
    }

    [Test]
    public void RouteLibraryRejectsBuiltInShadowAndDuplicateNames()
    {
        const string shadow =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"death star\",\"holds\":[\"A5\",\"B6\"]," +
            "\"start\":[\"A5\"],\"finish\":[\"B6\"]}]}";
        const string duplicate =
            "{\"schemaVersion\":2,\"routes\":[" +
            "{\"name\":\"MINE\",\"holds\":[\"A5\",\"A6\"],\"start\":[\"A5\"],\"finish\":[\"A6\"]}," +
            "{\"name\":\"mine\",\"holds\":[\"B6\",\"B7\"],\"start\":[\"B6\"],\"finish\":[\"B7\"]}]}";

        Assert.That(RouteLibrary.TryParseJson(shadow, ReservedRouteNames, out _, out string shadowError), Is.False);
        Assert.That(shadowError, Does.Contain("shadows a built-in"));
        Assert.That(RouteLibrary.TryParseJson(duplicate, ReservedRouteNames, out _, out string dupError), Is.False);
        Assert.That(dupError, Does.Contain("duplicates"));
    }

    [Test]
    public void RouteLibraryRejectsLegacyOrMissingSchemaVersion()
    {
        Assert.That(RouteLibrary.TryParseJson("", ReservedRouteNames, out _, out string emptyError), Is.False);
        Assert.That(emptyError, Does.Contain("empty"));

        const string legacy =
            "{\"schemaVersion\":1,\"routes\":[{\"name\":\"OLD\",\"holds\":[\"A5\",\"A6\"]," +
            "\"start\":[\"A5\"],\"finish\":[\"A6\"]}]}";
        Assert.That(RouteLibrary.TryParseJson("{}", ReservedRouteNames, out _, out string missingError), Is.False);
        Assert.That(missingError, Does.Contain("schemaVersion").And.Contain("found 0"));
        Assert.That(RouteLibrary.TryParseJson(legacy, ReservedRouteNames, out _, out string legacyError), Is.False);
        Assert.That(legacyError, Does.Contain("schemaVersion").And.Contain("found 1"));
    }

    [Test]
    public void RouteLibraryRequiresRolesAndRoleMembership()
    {
        const string missingRoles =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"X\",\"holds\":[\"A5\",\"A6\"]}]}";
        const string nonMember =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"X\",\"holds\":[\"A5\",\"A6\"]," +
            "\"start\":[\"B5\"],\"finish\":[\"A6\"]}]}";

        Assert.That(
            RouteLibrary.TryParseJson(missingRoles, ReservedRouteNames, out _, out string missingError),
            Is.False);
        Assert.That(missingError, Does.Contain("requires 1-2 start"));
        Assert.That(RouteLibrary.TryParseJson(nonMember, ReservedRouteNames, out _, out string memberError), Is.False);
        Assert.That(memberError, Does.Contain("not a member of holds"));
    }

    [Test]
    public void RouteLibraryRejectsPositionWithBothRoles()
    {
        const string json =
            "{\"schemaVersion\":2,\"routes\":[{\"name\":\"CONFLICT\",\"holds\":[\"A5\",\"A6\"]," +
            "\"start\":[\"A5\"],\"finish\":[\"A5\"]}]}";

        Assert.That(RouteLibrary.TryParseJson(json, ReservedRouteNames, out _, out string error), Is.False);
        Assert.That(error, Does.Contain("both start and finish"));
    }

    private static MoonBoardStudyCatalog LoadCatalog()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        string json = File.ReadAllText(path);
        Assert.That(MoonBoardStudyCatalog.TryParse(json, out MoonBoardStudyCatalog catalog, out string error),
            Is.True, error);
        Assert.That(MoonBoardStudyCatalog.ComputeSha256(json), Is.EqualTo(MoonBoardStudyCatalog.ApprovedCatalogSha256));
        return catalog;
    }
}
