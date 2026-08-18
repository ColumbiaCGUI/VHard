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
            "P07,1,B,MB2016-412117\n" +
            "P07,2,C,\"ROUTE, WITH COMMA\"\n" +
            "P07,3,A,MB2016-412973\n";

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
            "P01,1,A,MB2016-412117\n" +
            "P01,1,B,MB2016-410602\n" +
            "P01,3,C,MB2016-412973\n";

        bool parsed = StudySchedule.TryParse(csv, out _, out string error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain("Duplicate"));
    }

    [Test]
    public void ScheduleRejectsRepeatedConditionOrRoute()
    {
        const string repeatedCondition =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-412117\n" +
            "P01,2,A,MB2016-410602\n" +
            "P01,3,C,MB2016-412973\n";
        const string repeatedRoute =
            "participant,block,condition,route\n" +
            "P01,1,A,MB2016-412117\n" +
            "P01,2,B,MB2016-412117\n" +
            "P01,3,C,MB2016-412973\n";

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
            "P100,1,A,MB2016-412117\n" +
            "P100,2,B,MB2016-410602\n" +
            "P100,3,C,MB2016-412973\n" +
            "P99,1,A,MB2016-412117\n" +
            "P99,2,B,MB2016-410602\n" +
            "P99,3,C,MB2016-412973\n";

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
        Assert.That(catalog.routes[0].id, Is.EqualTo("MB2016-412117"));
        Assert.That(catalog.routes[1].id, Is.EqualTo("MB2016-410602"));
        Assert.That(catalog.routes[2].id, Is.EqualTo("MB2016-412973"));
        Assert.That(catalog.TryGetHold("F18", out _), Is.False);
        Assert.That(catalog.TryGetHold("D1", out _), Is.False);
        foreach (MoonBoardRouteDefinition route in catalog.routes)
        {
            // 2026-08-12 model-blind climb rule: uncontested community 6B+ created after the
            // grading model's training window, at most eight holds; benchmark status is no
            // longer required (2021+ problems are not classic benchmarks).
            Assert.That(route.grade, Is.EqualTo("6B+"));
            Assert.That(route.lockedForStudy, Is.True);
            Assert.That(route.moves.Length, Is.InRange(3, 8));
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
            "Y12", "B123", "B135", "W97", "B126", "B110", "W66", "W74", "W53", "W65",
            "B105", "B149", "W83", "Y36", "B124", "B108", "B132", "B130", "Y2",
            // 2026-08-12 official-image landing of the remaining audit predictions.
            "Y29", "B125", "B147", "B129", "B134", "Y13",
            "B146", "Y19", "Y1", "B107", "B113", "B122", "B131", "B133",
            "W78", "Y8", "W52", "B128", "Y32", "W61", "B121", "Y9",
            "B148", "W79", "W64", "B145", "B140", "B120", "W57", "Y15",
            "W56", "W94", "B119", "B118", "Y27", "W73", "W84", "W68",
            // (W81 and Y35 were briefly corrected 08-12 and removed 08-12d.)
            "W54", "W55", "B114", "B102", "Y26", "W77", "Y24", "W63",
            "B103", "Y37", "W62", "B137", "Y34", "B144", "B101", "Y31",
            "B112", "Y20", "Y5", "W96", "W69", "B117", "W51", "B139",
            "W82",
            // 2026-08-12c true-scale re-instrument: first reliable spins for 7 cleared
            // flags + the two never-measured holds.
            // 08-12d adjudication removed W81 (G2) and Y35 (I5) again — raw frames were right.
            "W58", "W80", "W95", "W60", "W92", "Y40", "Y18", "W71", "W87",
            // 2026-08-13 bolt-hole audit: W99's bore breaks the 2-fold symmetry that kept
            // it unmeasurable (bore/window axis vs the official image + Ben's live call);
            // W91 is Ben's direct slight-clockwise call from the same headset session.
            "W99", "W91",
            // 2026-08-14: Ben hand-oriented D10's (wrong-geometry, rescan-pending) mesh on
            // the twin; landed as a full-quaternion correction like W98's.
            "Y33",
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
        Assert.That(correctedMeshes, Is.EqualTo(102));
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
    public void CatalogSeatsBoltBoreOffsetHoldsOnTheirBore()
    {
        MoonBoardStudyCatalog catalog = LoadCatalog();

        // B18/W99 is the only calibrated bore offset: the hold mounts on its bolt bore
        // (measured 30.6 mm, -32.8 mm in the mesh frame), so its seated position moves by
        // the baked-rotation image of that vector — magnitude preserved, and on the board
        // the body must move toward A (negative x), per the official setup image and Ben's
        // 2026-08-13 call.
        Assert.That(catalog.TryGetHold("B18", out MoonBoardHoldDefinition b18), Is.True);
        Assert.That(b18.meshBoltOffsetXMeters, Is.EqualTo(0.0306f).Within(1e-6f));
        Assert.That(b18.meshBoltOffsetYMeters, Is.EqualTo(-0.0328f).Within(1e-6f));

        MoonBoardHoldDefinition b18WithoutBore = new()
        {
            coordinate = b18.coordinate,
            scanId = b18.scanId,
            rotationDegrees = b18.rotationDegrees,
            surfaceOffsetMeters = b18.surfaceOffsetMeters,
            meshScaleMultiplier = b18.meshScaleMultiplier,
            scaleCalibrationSource = b18.scaleCalibrationSource,
            hasMeshFrameCorrection = b18.hasMeshFrameCorrection,
            meshFrameCorrection = b18.meshFrameCorrection,
        };
        Vector3 pinned = catalog.GetSeatedBoardLocalPosition(b18WithoutBore);
        Vector3 shift = catalog.GetSeatedBoardLocalPosition(b18) - pinned;
        float boreMagnitude = Mathf.Sqrt(0.0306f * 0.0306f + 0.0328f * 0.0328f);
        Assert.That(shift.magnitude, Is.EqualTo(boreMagnitude).Within(1e-5f));
        Assert.That(shift.x, Is.LessThan(-0.02f), "body must move toward column A");

        // D5 and H13 landed 2026-08-14: clean M10 through-bores with co-located
        // countersinks; the poster shows each body on the predicted side of its bolt.
        Assert.That(catalog.TryGetHold("D5", out MoonBoardHoldDefinition d5), Is.True);
        Assert.That(d5.meshBoltOffsetXMeters, Is.EqualTo(-0.0267f).Within(1e-6f));
        Assert.That(d5.meshBoltOffsetYMeters, Is.EqualTo(-0.0231f).Within(1e-6f));
        Assert.That(catalog.TryGetHold("H13", out MoonBoardHoldDefinition h13), Is.True);
        Assert.That(h13.meshBoltOffsetXMeters, Is.EqualTo(-0.0253f).Within(1e-6f));
        Assert.That(h13.meshBoltOffsetYMeters, Is.EqualTo(0.025f).Within(1e-6f));

        // D10/Y33 landed 2026-08-14 evening after Ben saw the bore miss the wall's t-nut
        // hole on the twin: bore re-measured in the FITTED mounting frame (the raw Zplane
        // frame is wrong for this scan) and mapped to the raw frame along the fitted
        // normal; removes a 23.1 mm in-plane displacement (measure_y33_bore.py).
        Assert.That(catalog.TryGetHold("D10", out MoonBoardHoldDefinition d10), Is.True);
        Assert.That(d10.meshBoltOffsetXMeters, Is.EqualTo(-0.0009f).Within(1e-6f));
        Assert.That(d10.meshBoltOffsetYMeters, Is.EqualTo(0.0262f).Within(1e-6f));

        // Y33's raw z = 0 plane is tilted against the wall, so the raw bore image has a
        // wall-normal component; seating must project it out. The bore shift is purely
        // in-plane (23.1 mm), and seating depth stays with surfaceOffsetMeters alone.
        MoonBoardHoldDefinition d10WithoutBore = new()
        {
            coordinate = d10.coordinate,
            scanId = d10.scanId,
            rotationDegrees = d10.rotationDegrees,
            surfaceOffsetMeters = d10.surfaceOffsetMeters,
            meshScaleMultiplier = d10.meshScaleMultiplier,
            scaleCalibrationSource = d10.scaleCalibrationSource,
            hasMeshFrameCorrection = d10.hasMeshFrameCorrection,
            meshFrameCorrection = d10.meshFrameCorrection,
        };
        Vector3 d10Shift = catalog.GetSeatedBoardLocalPosition(d10) -
                           catalog.GetSeatedBoardLocalPosition(d10WithoutBore);
        MoonBoardHoldDefinition d10Raised = new()
        {
            coordinate = d10.coordinate,
            scanId = d10.scanId,
            rotationDegrees = d10.rotationDegrees,
            surfaceOffsetMeters = d10.surfaceOffsetMeters + 0.01f,
            meshScaleMultiplier = d10.meshScaleMultiplier,
            scaleCalibrationSource = d10.scaleCalibrationSource,
            hasMeshFrameCorrection = d10.hasMeshFrameCorrection,
            meshFrameCorrection = d10.meshFrameCorrection,
        };
        Vector3 outwardNormal = (catalog.GetSeatedBoardLocalPosition(d10Raised) -
                                 catalog.GetSeatedBoardLocalPosition(d10WithoutBore)) / 0.01f;
        Assert.That(outwardNormal.magnitude, Is.EqualTo(1f).Within(1e-3f));
        Assert.That(Vector3.Dot(outwardNormal, d10Shift), Is.EqualTo(0f).Within(1e-5f));
        Assert.That(d10Shift.magnitude, Is.EqualTo(0.0231f).Within(2e-4f));

        // Every other hold carries a zero offset and is untouched by the new term.
        int boreOffsetHolds = 0;
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            if (hold.meshBoltOffsetXMeters != 0f || hold.meshBoltOffsetYMeters != 0f)
            {
                boreOffsetHolds++;
            }
        }
        Assert.That(boreOffsetHolds, Is.EqualTo(4));

        // The seating path is fail-closed on nonsense offsets.
        b18WithoutBore.meshBoltOffsetXMeters = MoonBoardStudyCatalog.MaxBoltOffsetMeters * 2f;
        Assert.Throws<System.ArgumentException>(
            () => catalog.GetSeatedBoardLocalPosition(b18WithoutBore));
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
            { "G4", ("B141", 0, -125f) },
            { "H18", ("Y28", 0, 20f) },
            { "F6", ("Y6", 90, 70f) },
            { "C13", ("B138", 315, -120f) },
            { "I4", ("W83", 45, 40f) },
            { "D5", ("W97", 315, -50f) },
            { "J5", ("B130", 315, -35f) },
            { "E7", ("W74", 180, -180f) },
            { "F7", ("W53", 315, -50f) },
            { "C8", ("B135", 315, -40f) },
            { "F10", ("W65", 45, 45f) },
            { "A11", ("Y12", 135, 125f) },
            { "E12", ("B110", 45, 40f) },
            { "I12", ("Y36", 225, -145f) },
            { "F13", ("B105", 315, -75f) },
            { "H13", ("B149", 225, -140f) },
            { "A14", ("B123", 315, -35f) },
            { "I14", ("B124", 315, -40f) },
            { "J14", ("Y2", 315, -55f) },
            { "D15", ("B126", 315, -30f) },
            { "E15", ("W66", 315, -55f) },
            { "I15", ("B108", 315, -45f) },
            { "I18", ("B132", 45, -145f) },
            // 2026-08-12 official-image landing of the remaining audit predictions.
            { "B4", ("Y29", 225, -140f) },
            { "C5", ("B125", 0, -15f) },
            { "B6", ("B147", 315, -80f) },
            { "E6", ("B129", 315, -45f) },
            { "G6", ("B134", 225, -140f) },
            { "B7", ("Y13", 180, 170f) },
            { "D7", ("B146", 180, 75f) },
            { "G7", ("Y19", 225, -140f) },
            { "H7", ("Y1", 135, 140f) },
            { "I7", ("B107", 45, 40f) },
            { "J7", ("B113", 0, -40f) },
            { "E8", ("B122", 0, -180f) },
            { "H8", ("B131", 45, 145f) },
            { "A9", ("B133", 315, -60f) },
            { "B9", ("W78", 45, 40f) },
            { "C9", ("Y8", 270, -85f) },
            { "D9", ("W52", 45, 50f) },
            { "G9", ("B128", 45, 40f) },
            { "H9", ("Y32", 90, 90f) },
            { "J9", ("W61", 135, 140f) },
            { "K9", ("B121", 0, -100f) },
            { "A10", ("Y9", 225, -140f) },
            { "B10", ("B148", 135, 50f) },
            { "C10", ("W79", 45, 50f) },
            { "E10", ("W64", 315, -40f) },
            { "G10", ("B145", 45, 165f) },
            { "H10", ("B140", 45, 35f) },
            { "I10", ("B120", 0, -95f) },
            { "J10", ("W57", 45, 45f) },
            { "K10", ("Y15", 180, 180f) },
            { "B11", ("W56", 315, -35f) },
            { "C11", ("W94", 270, -90f) },
            { "D11", ("B119", 225, -150f) },
            { "F11", ("B118", 45, -95f) },
            { "G11", ("Y27", 90, 80f) },
            { "H11", ("W73", 270, -90f) },
            { "K11", ("W84", 315, -40f) },
            { "A12", ("W68", 90, 85f) },
            { "F12", ("W54", 90, 85f) },
            { "G12", ("W55", 45, 40f) },
            { "H12", ("B114", 315, 170f) },
            { "J12", ("B102", 45, 35f) },
            { "A13", ("Y26", 0, -130f) },
            { "B13", ("W77", 315, -40f) },
            { "D13", ("Y24", 0, 155f) },
            { "I13", ("W63", 90, 80f) },
            { "J13", ("B103", 0, 40f) },
            { "K13", ("Y37", 0, -10f) },
            { "F14", ("W62", 315, -40f) },
            { "G14", ("B137", 90, 90f) },
            { "H14", ("Y34", 270, -90f) },
            { "K14", ("B144", 45, 45f) },
            { "B15", ("B101", 0, 40f) },
            { "C15", ("Y31", 315, -45f) },
            { "G15", ("B112", 315, -30f) },
            { "H15", ("Y20", 45, 50f) },
            { "A16", ("Y5", 315, -40f) },
            { "F16", ("W96", 180, 175f) },
            { "I16", ("W69", 45, 35f) },
            { "J16", ("B117", 90, 90f) },
            { "D17", ("W51", 0, -10f) },
            { "A18", ("B139", 0, -140f) },
            { "G18", ("W82", 270, -85f) },
            // 2026-08-12c true-scale re-instrument.
            { "J2", ("W58", 135, 50f) },
            { "B3", ("W80", 225, -130f) },
            { "C6", ("W95", 180, 175f) },
            { "I6", ("W60", 45, 45f) },
            { "J6", ("W92", 180, -170f) },
            { "F9", ("Y40", 0, 10f) },
            { "F15", ("Y18", 0, -155f) },
            { "B16", ("W71", 315, -40f) },
            { "K18", ("W87", 270, -90f) },
            // 2026-08-13 bolt-hole audit; B18's spin re-set by Ben's hand placement on the
            // twin against the real wall 2026-08-14 (the poster-derived +55 was 28 deg off).
            { "B18", ("W99", 135, 27f) },
            { "E18", ("W91", 0, 15f) },
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
            // 0.05 deg bound: Quaternion.Angle uses acos near dot=1, where one float32 ulp
            // reads as ~0.03 deg; the corrections themselves sit on a 5-degree grid.
            Assert.That(Quaternion.Angle(correction, expectedCorrection), Is.LessThan(0.05f), entry.Key);

            Quaternion uncorrected = boardMount *
                                     Quaternion.Euler(270f, 360f - hold.rotationDegrees, 0f);
            Quaternion corrected = catalog.GetBoardLocalRotation(hold);
            Quaternion expectedBoardDelta = Quaternion.AngleAxis(
                entry.Value.correctionDegrees,
                climbingSideNormal);
            Quaternion actualBoardDelta = corrected * Quaternion.Inverse(uncorrected);
            Assert.That(Quaternion.Angle(actualBoardDelta, expectedBoardDelta),
                Is.LessThan(0.05f), entry.Key);
            Assert.That(Vector3.Angle(corrected * Vector3.forward, climbingSideNormal),
                Is.LessThan(0.05f), entry.Key);
        }

        Assert.That(catalog.TryGetHold("A15", out MoonBoardHoldDefinition a15), Is.True);
        Assert.That(a15.scanId, Is.EqualTo("W98"));
        Assert.That(a15.rotationDegrees, Is.Zero);
        Assert.That(a15.hasMeshFrameCorrection, Is.True);

        Assert.That(catalog.TryGetHold("K8", out MoonBoardHoldDefinition k8), Is.True);
        Assert.That(k8.scanId, Is.EqualTo("Y30"));
        Assert.That(k8.hasMeshFrameCorrection, Is.False);
        // D10's correction is Ben's 2026-08-14 hand orientation of the wrong-geometry mesh
        // (rescan pending) — a full quaternion about a tilted axis, not a board-normal yaw,
        // because Y33's mesh base frame does not match the physical hold.
        Assert.That(catalog.TryGetHold("D10", out MoonBoardHoldDefinition d10), Is.True);
        Assert.That(d10.scanId, Is.EqualTo("Y33"));
        Assert.That(d10.hasMeshFrameCorrection, Is.True);
        Assert.That(d10.meshFrameCorrection.TryGetQuaternion(out Quaternion d10Correction), Is.True);
        Assert.That(
            Vector3.Angle(d10Correction * Vector3.forward, Vector3.forward),
            Is.GreaterThan(10f),
            "D10's correction must stay a genuine tilt; a pure yaw here means it regressed");
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
        // K8/Y30 is deliberately correction-free (G2/W81 gained a correction 2026-08-12).
        Assert.That(catalog.TryGetHold("K8", out MoonBoardHoldDefinition k8), Is.True);
        k8.meshFrameCorrection.w = 1f;
        Assert.That(catalog.TryValidate(out string unflaggedError), Is.False);
        Assert.That(unflaggedError, Does.Contain("physical calibration").And.Contain("K8"));
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
            "P01,1,A,MB2016-412117\n" +
            "P01,2,B,MB2016-410602\n" +
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
