using System;
using System.Globalization;

/// <summary>
/// Console labelling for the manual estimation cycle. The experimenter walks the estimation
/// battery outside a scheduled block, so there is no participant, block or yoked set to name and
/// the console has only the problem's derived code to work with: the estimation problems carry
/// names, setters and community grades that a participant looking at the panel must never read.
/// </summary>
public static class ManualEstimationPolicy
{
    public const string StartLabel = "ESTIMATE";
    public const string FinishLabel = "FINISH";
    public const string EmptyReadout = "NO PROBLEMS";

    /// <summary>Root that manual-run recovery scans for an interrupted rehearsal run.</summary>
    public const string ManualRunRootName = "MANUAL";

    /// <summary>
    /// Root for manual estimation recordings, deliberately a sibling of the manual-run root rather
    /// than a directory inside it. Recovery enumerates every directory under the run root and
    /// reports one without a session manifest as unresolved data, which blocks the console on the
    /// next launch; estimation cycles write no manifest.
    /// </summary>
    public const string RecordingRootName = "MANUAL_ESTIMATION";

    /// <summary>
    /// Label for the advance control while the problem at <paramref name="ordinal"/> (zero-based)
    /// is on the board. It names the destination the next press reaches, the way the route step
    /// buttons name theirs before they are pressed.
    /// </summary>
    public static string FormatAdvanceLabel(int ordinal, int count)
    {
        if (count <= 0)
        {
            return StartLabel;
        }

        int shown = Math.Clamp(ordinal, 0, count - 1) + 1;
        return shown >= count
            ? FinishLabel + " " + FormatProgress(shown, count)
            : "NEXT " + FormatProgress(shown + 1, count);
    }

    /// <summary>
    /// Readout for the problem on the board: its position in the cycle and its derived code, and
    /// nothing the catalog record could otherwise be recognised by.
    /// </summary>
    public static string FormatProgressReadout(string problemId, int ordinal, int count)
    {
        if (count <= 0 || string.IsNullOrWhiteSpace(problemId))
        {
            return EmptyReadout;
        }

        int shown = Math.Clamp(ordinal, 0, count - 1) + 1;
        return "ESTIMATE " + FormatProgress(shown, count) + "     " +
               StudyRouteIdentity.FormatCodeReference(problemId);
    }

    private static string FormatProgress(int position, int count)
    {
        return position.ToString(CultureInfo.InvariantCulture) + " / " +
               count.ToString(CultureInfo.InvariantCulture);
    }
}
