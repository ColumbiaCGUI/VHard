using System;
using System.Collections.Generic;

/// <summary>
/// Session state shared by the study modules: the loaded schedule, the experimenter's
/// selection, and the currently running block, practice or estimation sequence.
/// </summary>
public sealed class StudySessionState
{
    public static readonly string[] AdhocConditions = { "A", "B", "C" };

    public readonly List<StudyScheduleRow> schedule = new();
    public readonly List<string> participants = new();
    public readonly HashSet<string> participantsWithBlockRuns = new(StringComparer.Ordinal);
    public readonly HashSet<string> participantsWithPracticeRuns = new(StringComparer.Ordinal);

    public int participantIndex;
    public int selectedBlock = 1;
    public string statusMessage = "Select a participant and block.";

    public MoonBoardStudyCatalog routeCatalog;
    public string routeCatalogSha256 = string.Empty;
    public MoonBoardEstimationCatalog estimationCatalog;
    public string supplementalContentStatus = string.Empty;

    public bool blockRunning;
    public bool blockTimerStarted;
    public bool panelPinned;
    public StudyScheduleRow activeRow;
    public string activeDirectory;

    public bool practiceActive;
    public string practicePhase = string.Empty;

    public bool estimationActive;
    public MoonBoardEstimationSetDefinition activeEstimationSet;
    public MoonBoardEstimationProblemDefinition[] activeEstimationProblems =
        Array.Empty<MoonBoardEstimationProblemDefinition>();
    public int activeEstimationOrdinal;

    public StudyScheduleRow lastEndedRow;
    public string lastEndedDirectory;
    public int lastEndedParticipantIndex;

    public int adhocConditionIndex;
    public int adhocRouteIndex;

    public bool IsAuxiliaryActive => practiceActive || estimationActive;
}
