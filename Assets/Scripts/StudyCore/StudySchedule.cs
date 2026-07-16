using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public static class StudySchedule
{
    private static readonly Regex ParticipantPattern = new("^P[0-9]{2,3}$", RegexOptions.Compiled);

    public static bool TryParse(
        string csv,
        out List<StudyScheduleRow> rows,
        out string error)
    {
        rows = new List<StudyScheduleRow>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(csv))
        {
            error = "Schedule is empty.";
            return false;
        }

        string[] lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 ||
            !string.Equals(lines[0].Trim(), "participant,block,condition,route", StringComparison.OrdinalIgnoreCase))
        {
            error = "Schedule header must be participant,block,condition,route.";
            return false;
        }

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> participantCounts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> participantConditions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> participantRoutes = new(StringComparer.OrdinalIgnoreCase);
        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            if (string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                continue;
            }

            if (!TryParseCsvLine(lines[lineIndex], out List<string> columns) || columns.Count != 4)
            {
                error = $"Malformed schedule row {lineIndex + 1}.";
                return false;
            }

            string participant = columns[0].Trim().ToUpperInvariant();
            string condition = columns[2].Trim().ToUpperInvariant();
            string route = columns[3].Trim();
            if (!ParticipantPattern.IsMatch(participant))
            {
                error = $"Invalid participant at row {lineIndex + 1}: {participant}.";
                return false;
            }
            if (!int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int block) ||
                block < 1 || block > 3)
            {
                error = $"Invalid block at row {lineIndex + 1}: {columns[1]}.";
                return false;
            }
            if (condition != "A" && condition != "B" && condition != "C")
            {
                error = $"Invalid condition at row {lineIndex + 1}: {condition}.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(route))
            {
                error = $"Missing route at row {lineIndex + 1}.";
                return false;
            }

            string key = participant + ":" + block;
            if (!keys.Add(key))
            {
                error = $"Duplicate participant/block row: {key}.";
                return false;
            }

            rows.Add(new StudyScheduleRow
            {
                participant = participant,
                block = block,
                condition = condition,
                route = route,
            });
            participantCounts.TryGetValue(participant, out int count);
            participantCounts[participant] = count + 1;
            if (!participantConditions.TryGetValue(participant, out HashSet<string> conditions))
            {
                conditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                participantConditions.Add(participant, conditions);
                participantRoutes.Add(participant, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            conditions.Add(condition);
            participantRoutes[participant].Add(route);
        }

        if (rows.Count == 0)
        {
            error = "Schedule contains no rows.";
            return false;
        }

        foreach (KeyValuePair<string, int> pair in participantCounts)
        {
            if (pair.Value != 3)
            {
                error = $"Participant {pair.Key} must have exactly three blocks.";
                return false;
            }
            if (participantConditions[pair.Key].Count != 3)
            {
                error = $"Participant {pair.Key} must have one block in each condition A, B, and C.";
                return false;
            }
            if (participantRoutes[pair.Key].Count != 3)
            {
                error = $"Participant {pair.Key} must have three distinct routes.";
                return false;
            }
        }

        rows.Sort((left, right) =>
        {
            int leftParticipant = int.Parse(left.participant.Substring(1), CultureInfo.InvariantCulture);
            int rightParticipant = int.Parse(right.participant.Substring(1), CultureInfo.InvariantCulture);
            int participantComparison = leftParticipant.CompareTo(rightParticipant);
            return participantComparison != 0 ? participantComparison : left.block.CompareTo(right.block);
        });
        return true;
    }

    private static bool TryParseCsvLine(string line, out List<string> columns)
    {
        columns = new List<string>(4);
        StringBuilder current = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                columns.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            return false;
        }
        columns.Add(current.ToString());
        return true;
    }
}
