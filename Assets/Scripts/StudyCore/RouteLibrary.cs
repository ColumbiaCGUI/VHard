using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

[Serializable]
public sealed class RouteDefinition
{
    public string name;
    public string grade;
    public string[] holds;
    public string[] start;
    public string[] finish;
}

/// <summary>
/// Parses StreamingAssets/routes.json so ad-hoc testing can load arbitrary MoonBoard
/// problems (e.g. benchmark climbs exported via tools/moonboard_to_routes.py) without
/// touching the built-in study route table. Fail-closed like StudySchedule: any invalid
/// entry rejects the whole file with a precise error, never a silent partial load.
/// </summary>
public static class RouteLibrary
{
    // MoonBoard grid: columns A-K, rows 1-18.
    private static readonly Regex HoldTokenPattern = new("^[A-K](1[0-8]|[1-9])$", RegexOptions.Compiled);

    [Serializable]
    private sealed class RouteFileModel
    {
        public int schemaVersion;
        public RouteDefinition[] routes;
    }

    public static bool TryParseJson(
        string json,
        IReadOnlyCollection<string> reservedNames,
        out List<RouteDefinition> routes,
        out string error)
    {
        routes = new List<RouteDefinition>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "routes.json is empty.";
            return false;
        }

        RouteFileModel model;
        try
        {
            model = JsonUtility.FromJson<RouteFileModel>(json);
        }
        catch (ArgumentException exception)
        {
            error = "routes.json is not valid JSON: " + exception.Message;
            return false;
        }

        if (model == null || model.schemaVersion != 2)
        {
            int version = model?.schemaVersion ?? 0;
            error = "routes.json schemaVersion must be 2; found " + version +
                    (version == 0 ? " (missing or legacy schema)." : ".");
            return false;
        }

        if (model?.routes == null || model.routes.Length == 0)
        {
            error = "routes.json must contain a non-empty 'routes' array.";
            return false;
        }

        HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase);
        if (reservedNames != null)
        {
            foreach (string name in reservedNames)
            {
                reserved.Add(name);
            }
        }

        HashSet<string> seenNames = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < model.routes.Length; index++)
        {
            RouteDefinition route = model.routes[index];
            string label = "routes[" + index + "]";
            if (route == null || string.IsNullOrWhiteSpace(route.name))
            {
                error = label + " is missing a name.";
                return false;
            }

            route.name = route.name.Trim();
            if (reserved.Contains(route.name))
            {
                error = label + " ('" + route.name + "') shadows a built-in study route; rename it.";
                return false;
            }
            if (!seenNames.Add(route.name))
            {
                error = label + " ('" + route.name + "') duplicates an earlier route name.";
                return false;
            }
            if (route.holds == null || route.holds.Length == 0)
            {
                error = label + " ('" + route.name + "') has no holds.";
                return false;
            }

            HashSet<string> seenHolds = new(StringComparer.OrdinalIgnoreCase);
            for (int holdIndex = 0; holdIndex < route.holds.Length; holdIndex++)
            {
                string token = route.holds[holdIndex]?.Trim().ToUpperInvariant() ?? string.Empty;
                if (!HoldTokenPattern.IsMatch(token))
                {
                    error = label + " ('" + route.name + "') hold " + holdIndex +
                            " ('" + route.holds[holdIndex] + "') is not a MoonBoard position A1-K18.";
                    return false;
                }
                if (!seenHolds.Add(token))
                {
                    error = label + " ('" + route.name + "') repeats hold " + token + ".";
                    return false;
                }
                route.holds[holdIndex] = token;
            }

            if (!TryNormalizeRole(route.start, "start", label, route.name, seenHolds,
                    out string[] normalizedStart, out error))
            {
                return false;
            }
            if (!TryNormalizeRole(route.finish, "finish", label, route.name, seenHolds,
                    out string[] normalizedFinish, out error))
            {
                return false;
            }
            route.start = normalizedStart;
            route.finish = normalizedFinish;

            HashSet<string> starts = new(route.start, StringComparer.OrdinalIgnoreCase);
            foreach (string finish in route.finish)
            {
                if (starts.Contains(finish))
                {
                    error = label + " ('" + route.name + "') position " + finish +
                            " cannot be both start and finish.";
                    return false;
                }
            }

            routes.Add(route);
        }

        return true;
    }

    private static bool TryNormalizeRole(
        string[] values,
        string role,
        string label,
        string routeName,
        HashSet<string> holds,
        out string[] normalized,
        out string error)
    {
        normalized = Array.Empty<string>();
        error = string.Empty;
        if (values == null || values.Length == 0)
        {
            error = label + " ('" + routeName + "') requires 1-2 " + role + " positions.";
            return false;
        }

        List<string> deduped = new(2);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < values.Length; index++)
        {
            string token = values[index]?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!HoldTokenPattern.IsMatch(token))
            {
                error = label + " ('" + routeName + "') " + role + " " + index +
                        " ('" + values[index] + "') is not a MoonBoard position A1-K18.";
                return false;
            }
            if (!holds.Contains(token))
            {
                error = label + " ('" + routeName + "') " + role + " position " + token +
                        " is not a member of holds.";
                return false;
            }
            if (seen.Add(token))
            {
                deduped.Add(token);
            }
        }

        if (deduped.Count < 1 || deduped.Count > 2)
        {
            error = label + " ('" + routeName + "') requires 1-2 distinct " + role +
                    " positions; found " + deduped.Count + ".";
            return false;
        }

        normalized = deduped.ToArray();
        return true;
    }
}
