using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class MoonBoard2016Layout : MonoBehaviour
{
    [SerializeField] private Transform holdsRoot;
    [SerializeField] private Transform mainSurface;
    [SerializeField] private Transform kickerSurface;

    public bool ApplyCatalog(MoonBoardStudyCatalog catalog, out string error)
    {
        if (catalog == null)
        {
            error = "MoonBoard catalog is unavailable.";
            return false;
        }
        if (!catalog.TryValidate(out error))
        {
            return false;
        }
        ResolveReferences();
        if (holdsRoot == null || mainSurface == null || kickerSurface == null)
        {
            error = "MoonBoard layout requires Holds, Main Surface, and Kicker references.";
            return false;
        }

        transform.localScale = Vector3.one;
        holdsRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        holdsRoot.localScale = Vector3.one;

        MoonBoardGeometryDefinition geometry = catalog.geometry;
        float tilt = catalog.SurfaceTiltDegrees;
        float tiltRadians = tilt * Mathf.Deg2Rad;
        mainSurface.localPosition = new Vector3(
            0f,
            geometry.kickerHeightMeters + Mathf.Sin(tiltRadians) * geometry.mainSurfaceLengthMeters * 0.5f,
            -Mathf.Cos(tiltRadians) * geometry.mainSurfaceLengthMeters * 0.5f);
        mainSurface.localRotation = Quaternion.Euler(tilt, 0f, 180f);
        mainSurface.localScale = new Vector3(
            geometry.boardWidthMeters / 10f,
            1f,
            geometry.mainSurfaceLengthMeters / 10f);

        kickerSurface.localPosition = new Vector3(0f, geometry.kickerHeightMeters * 0.5f, 0f);
        kickerSurface.localRotation = Quaternion.Euler(90f, 0f, 180f);
        kickerSurface.localScale = new Vector3(
            geometry.boardWidthMeters / 10f,
            1f,
            geometry.kickerHeightMeters / 10f);

        Dictionary<string, Transform> sceneHolds = new(StringComparer.Ordinal);
        foreach (Transform child in holdsRoot)
        {
            string coordinate = child.name.Split('.')[0].ToUpperInvariant();
            if (!MoonBoardStudyCatalog.TryParseCoordinate(coordinate, out _, out _) ||
                !sceneHolds.TryAdd(coordinate, child))
            {
                error = "MoonBoard hold hierarchy contains an invalid or duplicate child: " + child.name + ".";
                return false;
            }
        }
        if (sceneHolds.Count != catalog.holds.Length)
        {
            error = $"MoonBoard scene contains {sceneHolds.Count} holds; catalog requires {catalog.holds.Length}.";
            return false;
        }

        foreach (MoonBoardHoldDefinition definition in catalog.holds)
        {
            if (!sceneHolds.TryGetValue(definition.coordinate, out Transform hold))
            {
                error = "MoonBoard scene is missing hold " + definition.coordinate + ".";
                return false;
            }
            hold.localPosition = catalog.GetSeatedBoardLocalPosition(definition);
            hold.localRotation = catalog.GetBoardLocalRotation(definition);
            hold.localScale = catalog.GetHoldLocalScale(definition);
        }

        return TryValidateAppliedLayout(catalog, out error);
    }

    public bool TryValidateAppliedLayout(MoonBoardStudyCatalog catalog, out string error)
    {
        ResolveReferences();
        if (catalog == null || holdsRoot == null || mainSurface == null || kickerSurface == null)
        {
            error = "MoonBoard metric layout is not initialized.";
            return false;
        }
        if (!ApproximatelyOne(transform.lossyScale) || !ApproximatelyOne(holdsRoot.lossyScale))
        {
            error = "MoonBoard metric ancestors must remain at unit scale.";
            return false;
        }

        float mainWidth = mainSurface.lossyScale.x * 10f;
        float mainLength = mainSurface.lossyScale.z * 10f;
        float kickerWidth = kickerSurface.lossyScale.x * 10f;
        float kickerHeight = kickerSurface.lossyScale.z * 10f;
        if (Mathf.Abs(mainWidth - catalog.geometry.boardWidthMeters) > 0.001f ||
            Mathf.Abs(mainLength - catalog.geometry.mainSurfaceLengthMeters) > 0.001f ||
            Mathf.Abs(kickerWidth - catalog.geometry.boardWidthMeters) > 0.001f ||
            Mathf.Abs(kickerHeight - catalog.geometry.kickerHeightMeters) > 0.001f)
        {
            error = "MoonBoard panel dimensions do not match the metric catalog.";
            return false;
        }

        foreach (MoonBoardHoldDefinition definition in catalog.holds)
        {
            Transform hold = holdsRoot.Find(definition.coordinate);
            if (hold == null)
            {
                error = "MoonBoard layout is missing hold " + definition.coordinate + ".";
                return false;
            }
            if (Vector3.Distance(hold.localPosition, catalog.GetSeatedBoardLocalPosition(definition)) > 0.001f ||
                Quaternion.Angle(hold.localRotation, catalog.GetBoardLocalRotation(definition)) > 0.1f)
            {
                error = "MoonBoard hold transform does not match the catalog: " + definition.coordinate + ".";
                return false;
            }
            if (Vector3.Distance(hold.localScale, catalog.GetHoldLocalScale(definition)) > 0.001f)
            {
                error = "MoonBoard hold is not at its calibrated physical scale: " + definition.coordinate + ".";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private void ResolveReferences()
    {
        holdsRoot ??= transform.Find("New_Decimated_Holds");
        mainSurface ??= transform.Find("Main Surface") ?? transform.Find("Plane");
        kickerSurface ??= transform.Find("Kicker");
    }

    private static bool ApproximatelyOne(Vector3 scale)
    {
        return Mathf.Abs(scale.x - 1f) < 0.001f &&
               Mathf.Abs(scale.y - 1f) < 0.001f &&
               Mathf.Abs(scale.z - 1f) < 0.001f;
    }
}
