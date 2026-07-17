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

            routes.Add(route);
        }

        return true;
    }
}
