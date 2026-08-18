using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

[Serializable]
public sealed class MoonBoardEstimationCatalog
{
    public const string ApprovedCatalogSha256 =
        "76d5c5e6e0e36a8fe6ce1843c3a0a6abfcdbb14b5d365f706273f8a7cf71bd64";
    public const string ApprovedSourceArchive = "problems_2023_01_30.zip";
    public const string ApprovedSourceArchiveUrl =
        "https://drive.google.com/file/d/1Zoqsmc15IHtGekY99xazemxjGGx07Kep/view";
    public const string ApprovedSourceArchiveSha256 =
        "f7f8becff8d1bcb3bd93feaee67cea4b5cecf27d84e079115ef9590b0efe5c05";
    public const string ApprovedSourceFile = "problems MoonBoard 2016 .json";
    public const string ApprovedSourceFileSha256 =
        "355792de881324a51accc32e7478b7cd4535a63a4b2bf8cedf56d4280044723d";
    public const int PracticeProblemApiId = 19216;

    public static readonly int[] ExpectedProblemApiIds =
    {
        386882, 386902, 389008,
        404248, 389660, 397202,
        452771, 424830, 388011,
        395431, 441349, 387486,
    };

    private static readonly string[] ExpectedGrades = { "6B+", "6C", "7A", "7A+" };
    private static readonly int[][] ExpectedSets =
    {
        new[] { 386882, 404248, 452771, 395431 },
        new[] { 386902, 389660, 424830, 441349 },
        new[] { 389008, 397202, 388011, 387486 },
    };

    public int schemaVersion;
    public string setupId;
    public string setupName;
    public int overhangAngleDegrees;
    public string archiveDate;
    public MoonBoardEstimationProvenance provenance;
    public MoonBoardEstimationProblemDefinition[] problems =
        Array.Empty<MoonBoardEstimationProblemDefinition>();
    public MoonBoardEstimationSetDefinition[] estimationSets =
        Array.Empty<MoonBoardEstimationSetDefinition>();
    public MoonBoardEstimationProblemDefinition practiceProblem;

    public static bool TryParseApproved(
        string json,
        MoonBoardStudyCatalog mainCatalog,
        out MoonBoardEstimationCatalog catalog,
        out string error)
    {
        catalog = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "MoonBoard estimation catalog is empty.";
            return false;
        }
        if (MoonBoardStudyCatalog.ComputeSha256(json) != ApprovedCatalogSha256)
        {
            error = "MoonBoard estimation catalog does not match the approved study content.";
            return false;
        }
        return TryParse(json, mainCatalog, out catalog, out error);
    }

    public static bool TryParse(
        string json,
        MoonBoardStudyCatalog mainCatalog,
        out MoonBoardEstimationCatalog catalog,
        out string error)
    {
        catalog = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "MoonBoard estimation catalog is empty.";
            return false;
        }

        try
        {
            catalog = JsonUtility.FromJson<MoonBoardEstimationCatalog>(json);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            error = "MoonBoard estimation catalog JSON is invalid: " + exception.Message;
            return false;
        }
        if (catalog == null)
        {
            error = "MoonBoard estimation catalog JSON did not contain a catalog object.";
            return false;
        }
        return catalog.TryValidate(mainCatalog, out error);
    }

    public bool TryValidate(MoonBoardStudyCatalog mainCatalog, out string error)
    {
        if (mainCatalog == null)
        {
            error = "The main MoonBoard catalog is unavailable.";
            return false;
        }
        if (!mainCatalog.TryValidate(out error))
        {
            return false;
        }
        if (schemaVersion != 1 || setupId != "moonboard-2016" ||
            setupName != "MoonBoard 2016" || overhangAngleDegrees != 40)
        {
            error = "MoonBoard estimation catalog must use schema 1 for MoonBoard 2016 at 40 degrees.";
            return false;
        }
        if (provenance == null)
        {
            error = "MoonBoard estimation catalog provenance is missing.";
            return false;
        }
        if (!provenance.TryValidate(out error))
        {
            return false;
        }
        if (problems == null || problems.Length != 12)
        {
            error = "MoonBoard estimation catalog must contain exactly 12 problems.";
            return false;
        }

        HashSet<int> expectedIds = new(ExpectedProblemApiIds);
        HashSet<int> problemIds = new();
        HashSet<string> mountedCoordinates = GetMountedCoordinates(mainCatalog);
        if (!TryGetClimbedProblemIds(mainCatalog, out HashSet<int> climbedProblemIds, out error))
        {
            return false;
        }
        Dictionary<string, int> gradeCounts = new(StringComparer.Ordinal)
        {
            { "6B+", 0 }, { "6C", 0 }, { "7A", 0 }, { "7A+", 0 },
        };

        foreach (MoonBoardEstimationProblemDefinition problem in problems)
        {
            if (!TryValidateProblem(problem, mountedCoordinates, true, out error))
            {
                return false;
            }
            if (!expectedIds.Contains(problem.apiId) || !problemIds.Add(problem.apiId))
            {
                error = "MoonBoard estimation catalog contains an unexpected or duplicate problem id.";
                return false;
            }
            if (climbedProblemIds.Contains(problem.apiId))
            {
                error = "MoonBoard estimation problem overlaps a climb route: " + problem.apiId + ".";
                return false;
            }
            gradeCounts[problem.grade]++;
        }
        foreach (string grade in ExpectedGrades)
        {
            if (gradeCounts[grade] != 3)
            {
                error = "MoonBoard estimation catalog must contain exactly three problems at grade " +
                        grade + ".";
                return false;
            }
        }

        if (estimationSets == null || estimationSets.Length != 3)
        {
            error = "MoonBoard estimation catalog must contain exactly three sets.";
            return false;
        }
        HashSet<int> setIndices = new();
        HashSet<int> partition = new();
        foreach (MoonBoardEstimationSetDefinition set in estimationSets)
        {
            if (set == null || set.setIndex < 1 || set.setIndex > 3 ||
                !setIndices.Add(set.setIndex) || set.problemIds == null || set.problemIds.Length != 4)
            {
                error = "MoonBoard estimation catalog contains an invalid set.";
                return false;
            }
            if (!string.Equals(
                    set.climbRouteId,
                    mainCatalog.routes[set.setIndex - 1].id,
                    StringComparison.Ordinal))
            {
                error = "Estimation set yoking does not match the main-catalog route order.";
                return false;
            }

            HashSet<string> setGrades = new(StringComparer.Ordinal);
            HashSet<int> setIds = new();
            foreach (int problemId in set.problemIds)
            {
                if (!setIds.Add(problemId) || !partition.Add(problemId) ||
                    !TryGetProblem(problemId, out MoonBoardEstimationProblemDefinition problem))
                {
                    error = "Estimation sets must partition the 12 problems without duplicates.";
                    return false;
                }
                setGrades.Add(problem.grade);
            }
            if (setGrades.Count != ExpectedGrades.Length)
            {
                error = "Each estimation set must contain one problem at each approved grade.";
                return false;
            }
            if (!setIds.SetEquals(ExpectedSets[set.setIndex - 1]))
            {
                error = "Estimation set assignment does not match the approved rank rotation.";
                return false;
            }
        }
        if (!partition.SetEquals(problemIds))
        {
            error = "Estimation sets do not partition all 12 approved problems.";
            return false;
        }

        if (!TryValidateProblem(practiceProblem, mountedCoordinates, false, out error) ||
            practiceProblem.apiId != PracticeProblemApiId ||
            practiceProblem.id != "MB2016-19216" ||
            practiceProblem.name != "WUTHERING HEIGHTS" ||
            practiceProblem.grade != "6B+" || practiceProblem.vGrade != "V4" ||
            practiceProblem.repeatsAtArchive != 9138)
        {
            error = string.IsNullOrEmpty(error)
                ? "MoonBoard practice problem does not match the approved content."
                : error;
            return false;
        }
        if (problemIds.Contains(practiceProblem.apiId) || climbedProblemIds.Contains(practiceProblem.apiId))
        {
            error = "MoonBoard practice problem must be disjoint from climb and estimation problems.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TryGetProblem(int apiId, out MoonBoardEstimationProblemDefinition problem)
    {
        problem = null;
        if (problems == null)
        {
            return false;
        }
        foreach (MoonBoardEstimationProblemDefinition candidate in problems)
        {
            if (candidate != null && candidate.apiId == apiId)
            {
                problem = candidate;
                return true;
            }
        }
        return false;
    }

    public bool TryGetSetForRoute(string routeId, out MoonBoardEstimationSetDefinition set)
    {
        set = null;
        if (estimationSets == null || string.IsNullOrWhiteSpace(routeId))
        {
            return false;
        }
        foreach (MoonBoardEstimationSetDefinition candidate in estimationSets)
        {
            if (candidate != null && string.Equals(candidate.climbRouteId, routeId, StringComparison.Ordinal))
            {
                set = candidate;
                return true;
            }
        }
        return false;
    }

    public bool TryGetRotatedProblems(
        MoonBoardEstimationSetDefinition set,
        int participantIndex,
        out MoonBoardEstimationProblemDefinition[] rotated,
        out string error)
    {
        rotated = Array.Empty<MoonBoardEstimationProblemDefinition>();
        if (set == null || set.problemIds == null || set.problemIds.Length != 4)
        {
            error = "Estimation set is unavailable.";
            return false;
        }

        int offset = ((participantIndex % set.problemIds.Length) + set.problemIds.Length) %
                     set.problemIds.Length;
        rotated = new MoonBoardEstimationProblemDefinition[set.problemIds.Length];
        for (int index = 0; index < rotated.Length; index++)
        {
            int problemId = set.problemIds[(index + offset) % set.problemIds.Length];
            if (!TryGetProblem(problemId, out rotated[index]))
            {
                rotated = Array.Empty<MoonBoardEstimationProblemDefinition>();
                error = "Estimation set references an unavailable problem: " + problemId + ".";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }

    public MoonBoardRouteDefinition[] GetSupplementalRoutes()
    {
        MoonBoardRouteDefinition[] routes = new MoonBoardRouteDefinition[problems.Length + 1];
        for (int index = 0; index < problems.Length; index++)
        {
            routes[index] = problems[index].ToRouteDefinition();
        }
        routes[routes.Length - 1] = practiceProblem.ToRouteDefinition();
        return routes;
    }

    private static bool TryValidateProblem(
        MoonBoardEstimationProblemDefinition problem,
        HashSet<string> mountedCoordinates,
        bool estimationOnly,
        out string error)
    {
        error = string.Empty;
        if (problem == null || problem.apiId <= 0 ||
            problem.id != "MB2016-" + problem.apiId.ToString(CultureInfo.InvariantCulture) ||
            string.IsNullOrWhiteSpace(problem.name) || string.IsNullOrWhiteSpace(problem.setter) ||
            string.IsNullOrWhiteSpace(problem.method) || !IsSha256(problem.sourceRecordSha256) ||
            problem.moves == null || problem.moves.Length < 2 ||
            problem.purpose != (estimationOnly ? "estimation-only" : "practice-only"))
        {
            error = "MoonBoard supplemental catalog contains an invalid problem record.";
            return false;
        }
        if (!TryGetVGrade(problem.grade, out string expectedVGrade) ||
            problem.vGrade != expectedVGrade || problem.userGrade != problem.grade ||
            problem.upgraded || problem.downgraded || problem.repeatsAtArchive < 100)
        {
            error = "MoonBoard supplemental problem has invalid grade consensus: " + problem.apiId + ".";
            return false;
        }
        if (estimationOnly &&
            (!DateTimeOffset.TryParse(
                 problem.dateInserted,
                 CultureInfo.InvariantCulture,
                 DateTimeStyles.AssumeUniversal,
                 out DateTimeOffset inserted) ||
             inserted <= new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)))
        {
            error = "MoonBoard estimation problem is not post-training-window: " + problem.apiId + ".";
            return false;
        }

        int starts = 0;
        int finishes = 0;
        HashSet<int> sequences = new();
        HashSet<string> coordinates = new(StringComparer.Ordinal);
        foreach (MoonBoardRouteMove move in problem.moves)
        {
            if (move == null || move.sequence < 0 || move.sequence >= problem.moves.Length ||
                !sequences.Add(move.sequence) || !mountedCoordinates.Contains(move.coordinate) ||
                !coordinates.Add(move.coordinate) || string.IsNullOrWhiteSpace(move.sourceMoveId) ||
                (move.role != "start" && move.role != "move" && move.role != "finish"))
            {
                error = "MoonBoard supplemental problem contains an invalid move: " + problem.apiId + ".";
                return false;
            }
            starts += move.role == "start" ? 1 : 0;
            finishes += move.role == "finish" ? 1 : 0;
        }
        if (starts < 1 || starts > 2 || finishes != 1)
        {
            error = "MoonBoard supplemental problem has invalid start or finish roles: " + problem.apiId + ".";
            return false;
        }
        return true;
    }

    private static HashSet<string> GetMountedCoordinates(MoonBoardStudyCatalog mainCatalog)
    {
        HashSet<string> coordinates = new(StringComparer.Ordinal);
        foreach (MoonBoardHoldDefinition hold in mainCatalog.holds)
        {
            coordinates.Add(hold.coordinate);
        }
        return coordinates;
    }

    private static bool TryGetClimbedProblemIds(
        MoonBoardStudyCatalog mainCatalog,
        out HashSet<int> ids,
        out string error)
    {
        ids = new HashSet<int>();
        foreach (MoonBoardRouteDefinition route in mainCatalog.routes)
        {
            if (!int.TryParse(
                    route.sourceProblemId,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int id) ||
                id <= 0)
            {
                error = "Main MoonBoard route has a non-numeric source problem id: " + route.id + ".";
                return false;
            }
            ids.Add(id);
        }
        error = string.Empty;
        return true;
    }

    private static bool TryGetVGrade(string grade, out string vGrade)
    {
        vGrade = grade switch
        {
            "6B+" => "V4",
            "6C" => "V5",
            "7A" => "V6",
            "7A+" => "V7",
            _ => null,
        };
        return vGrade != null;
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }
}

[Serializable]
public sealed class MoonBoardEstimationProvenance
{
    public string sourceArchive;
    public string sourceArchiveUrl;
    public string sourceArchiveSha256;
    public string sourceFile;
    public string sourceFileSha256;
    public string practiceSourceRepository;
    public string practiceSourceRevision;
    public string practiceProblemsSha256;

    public bool TryValidate(out string error)
    {
        bool valid = sourceArchive == MoonBoardEstimationCatalog.ApprovedSourceArchive &&
                     sourceArchiveUrl == MoonBoardEstimationCatalog.ApprovedSourceArchiveUrl &&
                     sourceArchiveSha256 == MoonBoardEstimationCatalog.ApprovedSourceArchiveSha256 &&
                     sourceFile == MoonBoardEstimationCatalog.ApprovedSourceFile &&
                     sourceFileSha256 == MoonBoardEstimationCatalog.ApprovedSourceFileSha256 &&
                     practiceSourceRepository == "https://github.com/e-sr/moonboard" &&
                     practiceSourceRevision == MoonBoardStudyCatalog.ApprovedSourceRevision &&
                     practiceProblemsSha256 == MoonBoardStudyCatalog.ApprovedProblemsSha256;
        error = valid
            ? string.Empty
            : "MoonBoard estimation catalog provenance is incomplete or unexpected.";
        return valid;
    }
}

[Serializable]
public sealed class MoonBoardEstimationSetDefinition
{
    public int setIndex;
    public string climbRouteId;
    public int[] problemIds = Array.Empty<int>();
}

[Serializable]
public sealed class MoonBoardEstimationProblemDefinition
{
    public string id;
    public int apiId;
    public string name;
    public string grade;
    public string vGrade;
    public string userGrade;
    public bool isBenchmark;
    public bool upgraded;
    public bool downgraded;
    public string method;
    public int repeatsAtArchive;
    public string setter;
    public string dateInserted;
    public string purpose;
    public string sourceRecordSha256;
    public MoonBoardRouteMove[] moves = Array.Empty<MoonBoardRouteMove>();

    public MoonBoardRouteDefinition ToRouteDefinition()
    {
        return new MoonBoardRouteDefinition
        {
            id = id,
            sourceProblemId = apiId.ToString(CultureInfo.InvariantCulture),
            name = name,
            grade = grade,
            isBenchmark = isBenchmark,
            method = method,
            repeatsAtArchive = repeatsAtArchive,
            setter = setter,
            lockedForStudy = false,
            selectionMatch = purpose,
            sourceRecordSha256 = sourceRecordSha256,
            moves = moves != null ? (MoonBoardRouteMove[])moves.Clone() : Array.Empty<MoonBoardRouteMove>(),
        };
    }
}
