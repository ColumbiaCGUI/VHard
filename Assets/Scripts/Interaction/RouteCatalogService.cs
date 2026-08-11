using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Resolves route definitions from the three sources the study recognises: the
/// authoritative MoonBoard catalog, the two built-in study routes, and StreamingAssets/routes.json.
/// Owns the routes.json load state; hold-level validation stays with the facade because it needs
/// the scene hold dictionary.</summary>
public sealed class RouteCatalogService
{
    // Ad-hoc routes loaded from StreamingAssets/routes.json (e.g. MoonBoard benchmarks
    // converted via tools/moonboard_to_routes.py). Built-in study routes always win;
    // RouteLibrary rejects any file that tries to shadow them.
    private static readonly string[] BuiltInRouteNames =
    {
        "DEATH STAR", "TO JUG, OR NOT TO JUG...",
    };

    private readonly Dictionary<string, RouteDefinition> jsonRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> jsonRouteNames = new();

    public MoonBoardStudyCatalog Catalog { get; private set; }
    public RoutesLoadState RoutesJsonLoadState { get; private set; } = RoutesLoadState.Loading;
    public string RoutesLoadFailureReason { get; private set; } = string.Empty;
    public string RoutesJsonSha256 { get; private set; }

    public void SetCatalog(MoonBoardStudyCatalog catalog)
    {
        Catalog = catalog;
    }

    public bool TryGetRouteDefinition(string routeId, out MoonBoardRouteDefinition route)
    {
        route = null;
        return Catalog != null && Catalog.TryGetRoute(routeId, out route);
    }

    /// <summary>Resolves a role-aware route definition: the authoritative catalog first
    /// (start/finish mapped from move roles), then built-ins, then routes.json entries.</summary>
    public bool TryGetRouteDefinition(string routeName, out RouteDefinition route)
    {
        if (Catalog != null && Catalog.TryGetRoute(routeName, out MoonBoardRouteDefinition catalogRoute))
        {
            MoonBoardRouteMove[] ordered = catalogRoute.moves.OrderBy(move => move.sequence).ToArray();
            route = new RouteDefinition
            {
                name = catalogRoute.name,
                grade = catalogRoute.grade,
                holds = ordered.Select(move => move.coordinate).ToArray(),
                start = ordered.Where(move => move.role == "start").Select(move => move.coordinate).ToArray(),
                finish = ordered.Where(move => move.role == "finish").Select(move => move.coordinate).ToArray(),
            };
            return true;
        }
        if (TryGetBuiltInRouteDefinition(routeName, out route))
        {
            return true;
        }
        if (routeName != null && jsonRoutes.TryGetValue(routeName, out RouteDefinition jsonRoute))
        {
            route = jsonRoute;
            return true;
        }
        route = null;
        return false;
    }

    public bool IsBuiltInRoute(string routeName)
    {
        return TryGetBuiltInRouteDefinition(routeName, out _);
    }

    public string GetRoutesLoadStatusLine()
    {
        return RoutesJsonLoadState switch
        {
            RoutesLoadState.Ready => "READY (" + jsonRouteNames.Count + " imported)",
            RoutesLoadState.Failed => "FAILED: " + RoutesLoadFailureReason,
            _ => "LOADING",
        };
    }

    public bool TryEnsureRouteSourceReady(string routeName, out string error)
    {
        if (IsBuiltInRoute(routeName) ||
            (Catalog != null && Catalog.TryGetRoute(routeName, out _)) ||
            RoutesJsonLoadState == RoutesLoadState.Ready)
        {
            error = string.Empty;
            return true;
        }

        error = RoutesJsonLoadState == RoutesLoadState.Loading
            ? "routes.json is still loading; imported route '" + routeName + "' cannot start yet."
            : "routes.json failed to load; imported route '" + routeName + "' is unavailable: " +
              RoutesLoadFailureReason;
        return false;
    }

    /// <summary>Catalog routes first, then built-in study routes, then routes.json entries.</summary>
    public List<string> GetAvailableRouteNames()
    {
        List<string> names = new();
        if (Catalog != null)
        {
            names.AddRange(Catalog.routes.Select(catalogRoute => catalogRoute.id));
        }
        names.AddRange(BuiltInRouteNames);
        names.AddRange(jsonRouteNames);
        return names;
    }

    public List<string> GetStudyRouteNames()
    {
        return Catalog?.routes != null
            ? Catalog.routes.Select(route => route.id).ToList()
            : new List<string>();
    }

    public IEnumerator LoadRoutesJson()
    {
        RoutesJsonLoadState = RoutesLoadState.Loading;
        RoutesLoadFailureReason = string.Empty;
        RoutesJsonSha256 = null;
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/routes.json";
        string json = null;
        byte[] jsonBytes = null;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
                jsonBytes = request.downloadHandler.data;
            }
            else if (request.responseCode != 404)
            {
                SetRoutesLoadFailed("request failed: " + request.error);
                yield break;
            }
        }
        else if (File.Exists(path))
        {
            json = File.ReadAllText(path);
            jsonBytes = File.ReadAllBytes(path);
        }

        if (json == null)
        {
            SetRoutesLoadFailed("file not found in StreamingAssets.");
            yield break;
        }

        if (!RouteLibrary.TryParseJson(json, BuiltInRouteNames, out List<RouteDefinition> parsed, out string error))
        {
            SetRoutesLoadFailed(error);
            yield break;
        }

        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        foreach (RouteDefinition route in parsed)
        {
            jsonRoutes[route.name] = route;
            jsonRouteNames.Add(route.name);
        }
        RoutesJsonSha256 = ComputeSha256(jsonBytes);
        RoutesJsonLoadState = RoutesLoadState.Ready;
        Debug.Log("[SceneConfiguror] Loaded " + jsonRouteNames.Count + " route(s) from routes.json: " +
                  string.Join(", ", jsonRouteNames));
    }

    private void SetRoutesLoadFailed(string reason)
    {
        jsonRoutes.Clear();
        jsonRouteNames.Clear();
        RoutesJsonSha256 = null;
        RoutesLoadFailureReason = reason;
        RoutesJsonLoadState = RoutesLoadState.Failed;
        Debug.LogError("[SceneConfiguror] routes.json failed: " + reason);
    }

    private static string ComputeSha256(byte[] value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(value);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool TryGetBuiltInRouteDefinition(string routeName, out RouteDefinition route)
    {
        switch (routeName)
        {
            case "DEATH STAR":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "DEATH STAR",
                    holds = new[] { "D15", "D18", "G13", "H11", "I4", "J6", "K9" },
                    start = new[] { "I4", "J6" },
                    finish = new[] { "D18" },
                };
                return true;
            case "TO JUG, OR NOT TO JUG...":
                // start/finish derived, confirm vs official app
                route = new RouteDefinition
                {
                    name = "TO JUG, OR NOT TO JUG...",
                    holds = new[] { "D9", "D15", "F5", "F12", "G13", "H10", "H18" },
                    start = new[] { "F5" },
                    finish = new[] { "H18" },
                };
                return true;
            case "[PREVIEW ALL (SHADER OFF)]":
                route = new RouteDefinition
                {
                    name = "[PREVIEW ALL (SHADER OFF)]",
                    holds = new[] { // this was the fastest way to get this working, sue me
                    "A1", "B1", "C1", "D1", "E1", "F1", "G1", "H1", "I1", "J1", "K1",
                    "A2", "B2", "C2", "D2", "E2", "F2", "G2", "H2", "I2", "J2", "K2",
                    "A3", "B3", "C3", "D3", "E3", "F3", "G3", "H3", "I3", "J3", "K3",
                    "A4", "B4", "C4", "D4", "E4", "F4", "G4", "H4", "I4", "J4", "K4",
                    "A5", "B5", "C5", "D5", "E5", "F5", "G5", "H5", "I5", "J5", "K5",
                    "A6", "B6", "C6", "D6", "E6", "F6", "G6", "H6", "I6", "J6", "K6",
                    "A7", "B7", "C7", "D7", "E7", "F7", "G7", "H7", "I7", "J7", "K7",
                    "A8", "B8", "C8", "D8", "E8", "F8", "G8", "H8", "I8", "J8", "K8",
                    "A9", "B9", "C9", "D9", "E9", "F9", "G9", "H9", "I9", "J9", "K9",
                    "A10", "B10", "C10", "D10", "E10", "F10", "G10", "H10", "I10", "J10", "K10",
                    "A11", "B11", "C11", "D11", "E11", "F11", "G11", "H11", "I11", "J11", "K11",
                    "A12", "B12", "C12", "D12", "E12", "F12", "G12", "H12", "I12", "J12", "K12",
                    "A13", "B13", "C13", "D13", "E13", "F13", "G13", "H13", "I13", "J13", "K13",
                    "A14", "B14", "C14", "D14", "E14", "F14", "G14", "H14", "I14", "J14", "K14",
                    "A15", "B15", "C15", "D15", "E15", "F15", "G15", "H15", "I15", "J15", "K15",
                    "A16", "B16", "C16", "D16", "E16", "F16", "G16", "H16", "I16", "J16", "K16",
                    "A17", "B17", "C17", "D17", "E17", "F17", "G17", "H17", "I17", "J17", "K17",
                    "A18", "B18", "C18", "D18", "E18", "F18", "G18", "H18", "I18", "J18", "K18"
                    },
                };
                return true;
            default:
                route = null;
                return false;
        }
    }
}
