using System.IO;
using NUnit.Framework;
using UnityEngine;

public sealed class MoonBoardEstimationCatalogTests
{
    [Test]
    public void ApprovedCatalogPassesAndContainsPinnedContent()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);

        Assert.That(estimation.TryValidate(main, out string error), Is.True, error);
        Assert.That(estimation.problems, Has.Length.EqualTo(12));
        Assert.That(estimation.estimationSets, Has.Length.EqualTo(3));
        Assert.That(estimation.practiceProblem.apiId,
            Is.EqualTo(MoonBoardEstimationCatalog.PracticeProblemApiId));
    }

    [Test]
    public void ApprovedParserRejectsTamperedHash()
    {
        MoonBoardStudyCatalog main = LoadMainCatalog();
        string json = LoadEstimationJson() + " ";

        Assert.That(
            MoonBoardEstimationCatalog.TryParseApproved(json, main, out _, out string error),
            Is.False);
        Assert.That(error, Does.Contain("approved study content"));
    }

    [Test]
    public void CatalogRejectsClimbRouteOverlap()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        main.routes[0].sourceProblemId = estimation.problems[0].apiId.ToString();

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("overlaps a climb route"));
    }

    [Test]
    public void CatalogRejectsMainRouteWithoutNumericSourceProblemId()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        main.routes[0].sourceProblemId = "not-an-api-id";

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("non-numeric source problem id"));
    }

    [Test]
    public void CatalogEnforcesThreeProblemsPerGrade()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        estimation.problems[0].grade = "6C";
        estimation.problems[0].vGrade = "V5";
        estimation.problems[0].userGrade = "6C";

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("exactly three problems at grade"));
    }

    [Test]
    public void CatalogEnforcesSetPartition()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        estimation.estimationSets[1].problemIds[0] = estimation.estimationSets[0].problemIds[0];

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("partition"));
    }

    [Test]
    public void CatalogEnforcesOneProblemPerGradeInEachSet()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        int firstV4 = estimation.estimationSets[0].problemIds[0];
        estimation.estimationSets[0].problemIds[0] = estimation.estimationSets[1].problemIds[1];
        estimation.estimationSets[1].problemIds[1] = firstV4;

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("one problem at each approved grade"));
    }

    [Test]
    public void CatalogEnforcesPracticeDisjointness()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        main.routes[0].sourceProblemId = estimation.practiceProblem.apiId.ToString();

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("practice problem must be disjoint"));
    }

    [Test]
    public void CatalogRejectsUnmountedPracticeCoordinate()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);
        estimation.practiceProblem.moves[0].coordinate = "F18";

        Assert.That(estimation.TryValidate(main, out string error), Is.False);
        Assert.That(error, Does.Contain("invalid move"));
    }

    [Test]
    public void SupplementalRoutesRemainOutsideStudyRouteArray()
    {
        LoadCatalogs(out MoonBoardStudyCatalog main, out MoonBoardEstimationCatalog estimation);

        Assert.That(
            main.TrySetSupplementalRoutes(estimation.GetSupplementalRoutes(), out string error),
            Is.True,
            error);
        Assert.That(main.routes, Has.Length.EqualTo(3));
        Assert.That(
            main.SupplementalRouteIds,
            Has.Count.EqualTo(13),
            "The route cycle appends the 12 estimation problems and the practice problem.");
        Assert.That(main.SupplementalRouteIds[0], Is.EqualTo("MB2016-386882"));
        Assert.That(main.SupplementalRouteIds[12], Is.EqualTo("MB2016-19216"));
        Assert.That(main.TryGetRoute("MB2016-386882", out MoonBoardRouteDefinition route), Is.True);
        Assert.That(route.lockedForStudy, Is.False);
        Assert.That(main.TryValidate(out error), Is.True, error);
    }

    [Test]
    public void WithinSetRotationUsesParticipantIndexModuloFour()
    {
        LoadCatalogs(out _, out MoonBoardEstimationCatalog estimation);
        MoonBoardEstimationSetDefinition set = estimation.estimationSets[0];

        Assert.That(
            estimation.TryGetRotatedProblems(set, 5, out MoonBoardEstimationProblemDefinition[] rotated,
                out string error),
            Is.True,
            error);
        Assert.That(rotated[0].apiId, Is.EqualTo(set.problemIds[1]));
        Assert.That(rotated[3].apiId, Is.EqualTo(set.problemIds[0]));
    }

    private static void LoadCatalogs(
        out MoonBoardStudyCatalog main,
        out MoonBoardEstimationCatalog estimation)
    {
        main = LoadMainCatalog();
        string json = LoadEstimationJson();
        Assert.That(
            MoonBoardEstimationCatalog.TryParseApproved(json, main, out estimation, out string error),
            Is.True,
            error);
    }

    private static MoonBoardStudyCatalog LoadMainCatalog()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        string json = File.ReadAllText(path);
        Assert.That(
            MoonBoardStudyCatalog.TryParse(json, out MoonBoardStudyCatalog catalog, out string error),
            Is.True,
            error);
        return catalog;
    }

    private static string LoadEstimationJson()
    {
        string path = Path.Combine(
            Application.streamingAssetsPath,
            "moonboard_2016_40_estimation.json");
        return File.ReadAllText(path);
    }
}
