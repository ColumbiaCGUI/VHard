using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Loads StreamingAssets/hold_affordances.json and keeps the parsed pocket overrides.
/// Every scan ID must exist in the authoritative route catalog, so the catalog and the overrides
/// are cross-validated whichever of the two arrives second.</summary>
public sealed class HoldAffordanceLoader
{
    private readonly RouteCatalogService routes;

    public HoldAffordanceLoader(RouteCatalogService routes)
    {
        this.routes = routes;
    }

    public HoldAffordanceCatalog Catalog { get; private set; }
    public HoldAffordancesLoadState State { get; private set; } = HoldAffordancesLoadState.Loading;
    public string FailureReason { get; private set; } = string.Empty;

    public IEnumerator LoadHoldAffordances()
    {
        State = HoldAffordancesLoadState.Loading;
        FailureReason = string.Empty;
        Catalog = null;
        string path = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/hold_affordances.json";
        string json = null;
        if (path.Contains("://") || path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
        {
            using UnityWebRequest request = UnityWebRequest.Get(path);
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                json = request.downloadHandler.text;
            }
            else
            {
                SetHoldAffordancesFailed("request failed: " + request.error);
                yield break;
            }
        }
        else
        {
            Exception readException = null;
            try
            {
                if (File.Exists(path))
                {
                    json = File.ReadAllText(path);
                }
            }
            catch (Exception exception)
            {
                readException = exception;
                Debug.LogException(exception);
            }
            if (readException != null)
            {
                SetHoldAffordancesFailed("read failed: " + readException.Message);
                yield break;
            }
        }

        if (json == null)
        {
            SetHoldAffordancesFailed("file not found in StreamingAssets.");
            yield break;
        }
        if (!HoldAffordanceCatalog.TryParse(json, out HoldAffordanceCatalog parsed, out string error))
        {
            SetHoldAffordancesFailed(error);
            yield break;
        }
        Catalog = parsed;
        if (routes.Catalog != null &&
            !TryValidateHoldAffordances(routes.Catalog, Catalog, out error))
        {
            SetHoldAffordancesFailed(error);
            yield break;
        }

        State = HoldAffordancesLoadState.Ready;
        Debug.Log("[SceneConfiguror] Loaded " + Catalog.Count +
                  " pocket affordance override(s).");
    }

    private void SetHoldAffordancesFailed(string reason)
    {
        Catalog = null;
        State = HoldAffordancesLoadState.Failed;
        FailureReason = reason;
        Debug.LogError("[SceneConfiguror] hold_affordances.json failed: " + reason);
    }

    public static bool TryValidateHoldAffordances(
        MoonBoardStudyCatalog catalog,
        HoldAffordanceCatalog affordances,
        out string error)
    {
        HashSet<string> knownScans = new(catalog.holds.Select(hold => hold.scanId),
            StringComparer.OrdinalIgnoreCase);
        foreach (string scanId in affordances.ScanIds)
        {
            if (!knownScans.Contains(scanId))
            {
                error = "Hold affordance references unknown scan ID " + scanId + ".";
                return false;
            }
        }
        error = string.Empty;
        return true;
    }
}
