using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class MoonBoardStudyCatalog
{
    public const string ApprovedCatalogSha256 = "076794dcfde57b3b8e99380a46e82ddbefc1c4702904706ff555992717e84467";
    public const string ApprovedSourceRevision = "ccd78f587ab189acea6dd7ce8a6d4f086f65db69";
    public const string ApprovedProblemsSha256 = "f0b035f53d8aef73fcadd3cbfe23ac776856b3d2d714928089c2d54d63abdcc8";
    public const string ApprovedHoldsSha256 = "c29a4215a61baa11d66bacd622447b017ece1dbdb39e954b4703626f4ecb7d9d";
    public const string ApprovedDimensionsSha256 = "850a2b7e8de0b7e74fb00309b3c0f27cb69be5f10372384e4d385e0afc587a78";
    public const string ApprovedMeshSha256 = "ec1dafd9ed8ee134395af0919f7ff96cc2f34a353baaa604102e4fd2f968b22a";

    public int schemaVersion;
    public string setupId;
    public string setupName;
    public int overhangAngleDegrees;
    public string archiveDate;
    public MoonBoardGeometryDefinition geometry;
    public MoonBoardProvenance provenance;
    public MoonBoardHoldDefinition[] holds = Array.Empty<MoonBoardHoldDefinition>();
    public MoonBoardRouteDefinition[] routes = Array.Empty<MoonBoardRouteDefinition>();

    public static bool TryParse(string json, out MoonBoardStudyCatalog catalog, out string error)
    {
        catalog = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "MoonBoard catalog is empty.";
            return false;
        }

        try
        {
            catalog = JsonUtility.FromJson<MoonBoardStudyCatalog>(json);
        }
        catch (Exception exception)
        {
            error = "MoonBoard catalog JSON is invalid: " + exception.Message;
            return false;
        }
        return catalog != null && catalog.TryValidate(out error);
    }

    public bool TryValidate(out string error)
    {
        if (schemaVersion != 1)
        {
            error = "MoonBoard catalog schemaVersion must be 1.";
            return false;
        }
        if (setupId != "moonboard-2016" || setupName != "MoonBoard 2016" || overhangAngleDegrees != 40)
        {
            error = "MoonBoard catalog must describe MoonBoard 2016 at 40 degrees.";
            return false;
        }
        if (geometry == null)
        {
            error = "MoonBoard catalog geometry is missing.";
            return false;
        }
        if (!geometry.TryValidate(overhangAngleDegrees, out error))
        {
            return false;
        }
        if (provenance == null)
        {
            error = "MoonBoard catalog provenance is missing.";
            return false;
        }
        if (!provenance.TryValidate(out error))
        {
            return false;
        }
        if (holds == null || holds.Length != 140)
        {
            error = "MoonBoard 2016 catalog must contain exactly 140 mounted holds.";
            return false;
        }

        HashSet<string> coordinates = new(StringComparer.Ordinal);
        HashSet<string> scans = new(StringComparer.Ordinal);
        foreach (MoonBoardHoldDefinition hold in holds)
        {
            if (hold == null ||
                !TryParseCoordinate(hold.coordinate, out _, out _) ||
                string.IsNullOrWhiteSpace(hold.scanId) ||
                string.IsNullOrWhiteSpace(hold.holdset) ||
                string.IsNullOrWhiteSpace(hold.holdNumber) ||
                hold.rotationDegrees < 0 || hold.rotationDegrees >= 360 || hold.rotationDegrees % 45 != 0)
            {
                error = "MoonBoard catalog contains an invalid hold record.";
                return false;
            }
            if (!coordinates.Add(hold.coordinate) || !scans.Add(hold.scanId))
            {
                error = "MoonBoard catalog contains a duplicate coordinate or physical scan.";
                return false;
            }
        }

        if (routes == null || routes.Length != 3)
        {
            error = "MoonBoard catalog must contain exactly three study routes.";
            return false;
        }
        HashSet<string> routeIds = new(StringComparer.Ordinal);
        foreach (MoonBoardRouteDefinition route in routes)
        {
            if (route == null ||
                !route.lockedForStudy ||
                string.IsNullOrWhiteSpace(route.id) ||
                string.IsNullOrWhiteSpace(route.sourceProblemId) ||
                string.IsNullOrWhiteSpace(route.name) ||
                route.grade != "6B+" ||
                !route.isBenchmark ||
                route.moves == null || route.moves.Length != 7 ||
                !IsSha256(route.sourceRecordSha256) ||
                !routeIds.Add(route.id))
            {
                error = "MoonBoard catalog contains an invalid or unlocked study route.";
                return false;
            }

            int starts = 0;
            int finishes = 0;
            HashSet<string> routeCoordinates = new(StringComparer.Ordinal);
            foreach (MoonBoardRouteMove move in route.moves)
            {
                if (move == null ||
                    !coordinates.Contains(move.coordinate) ||
                    !routeCoordinates.Add(move.coordinate) ||
                    (move.role != "start" && move.role != "move" && move.role != "finish"))
                {
                    error = "Route " + route.id + " contains an invalid or duplicate hold.";
                    return false;
                }
                starts += move.role == "start" ? 1 : 0;
                finishes += move.role == "finish" ? 1 : 0;
            }
            if (starts != 2 || finishes != 1)
            {
                error = "Route " + route.id + " must contain two starts and one finish.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public bool TryGetRoute(string routeId, out MoonBoardRouteDefinition route)
    {
        route = null;
        if (routes == null || string.IsNullOrWhiteSpace(routeId))
        {
            return false;
        }
        foreach (MoonBoardRouteDefinition candidate in routes)
        {
            if (candidate != null && string.Equals(candidate.id, routeId, StringComparison.Ordinal))
            {
                route = candidate;
                return true;
            }
        }
        return false;
    }

    public bool TryGetHold(string coordinate, out MoonBoardHoldDefinition hold)
    {
        hold = null;
        if (holds == null || string.IsNullOrWhiteSpace(coordinate))
        {
            return false;
        }
        foreach (MoonBoardHoldDefinition candidate in holds)
        {
            if (candidate != null && string.Equals(candidate.coordinate, coordinate, StringComparison.Ordinal))
            {
                hold = candidate;
                return true;
            }
        }
        return false;
    }

    public Vector3 GetBoardLocalPosition(string coordinate)
    {
        if (!TryParseCoordinate(coordinate, out int column, out int row))
        {
            throw new ArgumentException("Invalid MoonBoard coordinate: " + coordinate, nameof(coordinate));
        }

        float x = (column - (geometry.columns - 1) * 0.5f) * geometry.gridSpacingMeters;
        if (row <= 2)
        {
            float height = row == 1
                ? geometry.row1KickerHeightMeters
                : geometry.row2KickerHeightMeters;
            return new Vector3(x, height, 0f);
        }

        float distance = geometry.mainFirstRowOffsetMeters + (row - 3) * geometry.gridSpacingMeters;
        float tiltRadians = Mathf.Deg2Rad * SurfaceTiltDegrees;
        return new Vector3(
            x,
            geometry.kickerHeightMeters + Mathf.Sin(tiltRadians) * distance,
            -Mathf.Cos(tiltRadians) * distance);
    }

    public Quaternion GetBoardLocalRotation(MoonBoardHoldDefinition hold)
    {
        if (!TryParseCoordinate(hold.coordinate, out _, out int row))
        {
            throw new ArgumentException("Invalid MoonBoard coordinate: " + hold.coordinate, nameof(hold));
        }
        float surfaceTilt = row <= 2 ? 90f : SurfaceTiltDegrees;
        Quaternion mount = Quaternion.Euler(surfaceTilt, 0f, 180f);
        Quaternion scanOrientation = Quaternion.Euler(270f, 360f - hold.rotationDegrees, 0f);
        return mount * scanOrientation;
    }

    public float SurfaceTiltDegrees => 90f - overhangAngleDegrees;

    public static string ComputeSha256(string text)
    {
        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        StringBuilder output = new(digest.Length * 2);
        foreach (byte value in digest)
        {
            output.Append(value.ToString("x2"));
        }
        return output.ToString();
    }

    public static bool TryParseCoordinate(string coordinate, out int column, out int row)
    {
        column = -1;
        row = -1;
        if (string.IsNullOrWhiteSpace(coordinate) || coordinate.Length < 2 || coordinate.Length > 3)
        {
            return false;
        }
        char columnCharacter = coordinate[0];
        if (columnCharacter < 'A' || columnCharacter > 'K' ||
            !int.TryParse(coordinate.Substring(1), out row) || row < 1 || row > 18)
        {
            return false;
        }
        column = columnCharacter - 'A';
        return true;
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
public sealed class MoonBoardGeometryDefinition
{
    public float boardWidthMeters;
    public float totalHeightMeters;
    public float horizontalOverhangMeters;
    public float mainSurfaceLengthMeters;
    public float kickerHeightMeters;
    public float gridSpacingMeters;
    public float mainFirstRowOffsetMeters;
    public float row1KickerHeightMeters;
    public float row2KickerHeightMeters;
    public int columns;
    public int rows;

    public bool TryValidate(int angleDegrees, out string error)
    {
        float expectedLength = Mathf.Sqrt(
            horizontalOverhangMeters * horizontalOverhangMeters +
            (totalHeightMeters - kickerHeightMeters) * (totalHeightMeters - kickerHeightMeters));
        float expectedFirstRowOffset = (mainSurfaceLengthMeters - 15f * gridSpacingMeters) * 0.5f;
        float measuredAngle = Mathf.Atan2(horizontalOverhangMeters, totalHeightMeters - kickerHeightMeters) *
                              Mathf.Rad2Deg;
        bool valid = Mathf.Abs(boardWidthMeters - 2.44f) < 0.001f &&
                     Mathf.Abs(totalHeightMeters - 3.15f) < 0.001f &&
                     Mathf.Abs(kickerHeightMeters - 0.37f) < 0.001f &&
                     Mathf.Abs(gridSpacingMeters - 0.20f) < 0.0001f &&
                     Mathf.Abs(mainSurfaceLengthMeters - expectedLength) < 0.01f &&
                     Mathf.Abs(mainFirstRowOffsetMeters - expectedFirstRowOffset) < 0.001f &&
                     Mathf.Abs(measuredAngle - angleDegrees) < 0.5f &&
                     row1KickerHeightMeters > 0f && row1KickerHeightMeters < kickerHeightMeters &&
                     row2KickerHeightMeters > row1KickerHeightMeters &&
                     row2KickerHeightMeters < kickerHeightMeters &&
                     columns == 11 && rows == 18;
        error = valid ? string.Empty : "MoonBoard catalog geometry does not match the 2016/40-degree specification.";
        return valid;
    }
}

[Serializable]
public sealed class MoonBoardProvenance
{
    public string sourceRepository;
    public string sourceRevision;
    public string problemsSha256;
    public string holdsSha256;
    public string dimensionsSha256;
    public string meshAsset;
    public string meshAssetSha256;

    public bool TryValidate(out string error)
    {
        bool valid = sourceRepository == "https://github.com/e-sr/moonboard" &&
                     sourceRevision == MoonBoardStudyCatalog.ApprovedSourceRevision &&
                     meshAsset == "Assets/Resources/New_Decimated_Holds.fbx" &&
                     problemsSha256 == MoonBoardStudyCatalog.ApprovedProblemsSha256 &&
                     holdsSha256 == MoonBoardStudyCatalog.ApprovedHoldsSha256 &&
                     dimensionsSha256 == MoonBoardStudyCatalog.ApprovedDimensionsSha256 &&
                     meshAssetSha256 == MoonBoardStudyCatalog.ApprovedMeshSha256 &&
                     IsHash(problemsSha256) && IsHash(holdsSha256) &&
                     IsHash(dimensionsSha256) && IsHash(meshAssetSha256);
        error = valid ? string.Empty : "MoonBoard catalog provenance is incomplete or unexpected.";
        return valid;
    }

    private static bool IsHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
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
public sealed class MoonBoardHoldDefinition
{
    public string coordinate;
    public string scanId;
    public string holdset;
    public string holdNumber;
    public int rotationDegrees;
    public string sourceHoldId;
}

[Serializable]
public sealed class MoonBoardRouteDefinition
{
    public string id;
    public string sourceProblemId;
    public string name;
    public string grade;
    public bool isBenchmark;
    public string method;
    public int repeatsAtArchive;
    public string setter;
    public bool lockedForStudy;
    public string selectionMatch;
    public string sourceRecordSha256;
    public MoonBoardRouteMove[] moves = Array.Empty<MoonBoardRouteMove>();
}

[Serializable]
public sealed class MoonBoardRouteMove
{
    public int sequence;
    public string coordinate;
    public string role;
    public string sourceMoveId;
}
