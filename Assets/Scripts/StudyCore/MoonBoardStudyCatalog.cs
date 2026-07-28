using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class MoonBoardStudyCatalog
{
    public const string ApprovedCatalogSha256 = "5fe13b67b24d174beaba7b0508f40351622f00d066728e725653e41f7f643062";

    /// <summary>Local scale at which the aggregate FBX imports each normalised hold child.</summary>
    public const float NormalizedMeshScale = 100f;

    public const float MinScaleMultiplier = 0.15f;
    public const float MaxScaleMultiplier = 1.5f;

    /// <summary>
/// "metric-scan" = depth read off the base-plane-aligned scan, exact: that depth axis reproduces
/// the trusted dimension to the last float32 bit on 12 known holds, so there is no error left to
/// measure on it.
/// "metric-scan-unaligned" is currently unused: it exists so that a hold whose aligned frame is
/// unusable must declare the weaker provenance rather than pass as exact. W98 was the only such
/// hold and is now resolved - a volume-ratio cross-check against the normalised FBX child
/// (invariant to both frame and centre) agrees with the depth ratio to 0.007%.
/// "movement-harlem-photo" remains declarable so a future un-scannable hold must label itself
/// rather than pass silently.
/// </summary>
public static readonly string[] ScaleCalibrationSources =
    { "metric-scan", "metric-scan-unaligned", "movement-harlem-photo" };

    /// <summary>Kicker foot jibs occupy every second T-nut column (400 mm pitch).</summary>
    public const int KickerJibColumnStride = 2;

    /// <summary>
    /// The jib mesh is authored with its flat mounting face already on its local Y=0 plane
    /// (30.10% of surface area has its normal within 15 degrees of -Y and its centroid within
    /// 1 mm of Y=0), so it needs no half-depth push - only an epsilon to keep the back face off
    /// the coplanar kicker surface.
    /// That face is a SCANNED surface, not a machined plane: its roughness about Y=0 runs to
    /// -1.239 mm at the lowest vertex. The epsilon therefore has to clear that excursion, or the
    /// deepest point sinks into the kicker (0.24 mm at a 1 mm epsilon - invisible behind an opaque
    /// board, but there is no reason to ship a known intersection).
    /// </summary>
    public const float KickerJibSurfaceOffsetMeters = 0.0013f;
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
        if (schemaVersion != 3)
        {
            error = "MoonBoard catalog schemaVersion must be 3.";
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
            if (!IsFinite(hold.surfaceOffsetMeters) ||
                hold.surfaceOffsetMeters <= 0f || hold.surfaceOffsetMeters > 0.15f ||
                !IsFinite(hold.meshScaleMultiplier) ||
                hold.meshScaleMultiplier < MinScaleMultiplier ||
                hold.meshScaleMultiplier > MaxScaleMultiplier ||
                Array.IndexOf(ScaleCalibrationSources, hold.scaleCalibrationSource) < 0 ||
                (hold.hasMeshFrameCorrection &&
                 (hold.meshFrameCorrection == null ||
                  !hold.meshFrameCorrection.TryGetQuaternion(out _))) ||
                (!hold.hasMeshFrameCorrection &&
                 hold.meshFrameCorrection != null && !hold.meshFrameCorrection.IsZero))
            {
                error = "MoonBoard catalog contains an invalid physical calibration: " + hold.coordinate + ".";
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
        // All 18 grid rows live on the 40-degree main surface: the official 2016 panel layout
        // (3x1220 mm main panels = rows 1-18 at 200 mm pitch) and the Movement Harlem photos
        // (July 2026 Videos, 4_iphone13.JPG) both show G2/J2 - the only sub-row-3 holds -
        // mounted above the kicker seam; the vertical kicker carries only bolt-on footholds.
        // row1/row2KickerHeightMeters remain in the approved catalog schema but are unused.
        float distance = geometry.mainFirstRowOffsetMeters + (row - 3) * geometry.gridSpacingMeters;
        float tiltRadians = Mathf.Deg2Rad * SurfaceTiltDegrees;
        return new Vector3(
            x,
            geometry.kickerHeightMeters + Mathf.Sin(tiltRadians) * distance,
            -Mathf.Cos(tiltRadians) * distance);
    }

    public Quaternion GetBoardLocalRotation(MoonBoardHoldDefinition hold)
    {
        if (hold == null || !TryParseCoordinate(hold.coordinate, out _, out _))
        {
            throw new ArgumentException("Invalid MoonBoard hold definition.", nameof(hold));
        }
        Quaternion correction = Quaternion.identity;
        if (hold.hasMeshFrameCorrection &&
            (hold.meshFrameCorrection == null ||
             !hold.meshFrameCorrection.TryGetQuaternion(out correction)))
        {
            throw new ArgumentException("Invalid mesh-frame correction: " + hold.scanId, nameof(hold));
        }

        Quaternion scanOrientation = Quaternion.Euler(270f, 360f - hold.rotationDegrees, 0f);
        return GetBoardMountRotation() * scanOrientation * correction;
    }

    public Vector3 GetSeatedBoardLocalPosition(MoonBoardHoldDefinition hold)
    {
        if (hold == null || !IsFinite(hold.surfaceOffsetMeters) || hold.surfaceOffsetMeters <= 0f)
        {
            throw new ArgumentException("Invalid MoonBoard hold calibration.", nameof(hold));
        }
        return GetBoardLocalPosition(hold.coordinate) +
               GetBoardMountRotation() * Vector3.up * hold.surfaceOffsetMeters;
    }

    /// <summary>
    /// Board-local positions of the yellow screw-on foot jibs on the vertical kicker.
    /// These are gym furniture, not study content: they are derived from the already-approved
    /// geometry block rather than added to the SHA-pinned hold list, so the study-content hash
    /// is unaffected.
    /// The jib itself is a slightly TAPERED plate (57.18 x 19.17 x 59.02 mm): its mounting face
    /// and its climbing face are 3.92 degrees apart, so a seated jib presents a foot surface tilted
    /// ~4 degrees rather than parallel to the kicker. That is the real hold, not an artefact.
    /// Evidence for the pattern (Movement Harlem, physical_4_iphone13.jpg): the jibs form a
    /// column-aligned 5 x 2 lattice - NOT staggered, contrary to earlier notes - with a
    /// horizontal pitch of two grid cells (400 mm) and a vertical spacing of ~200 mm. That
    /// vertical spacing matches <see cref="MoonBoardGeometryDefinition.row1KickerHeightMeters"/>
    /// (0.10 m) and row2 (0.30 m), which the row-2 geometry fix left in the schema but unused:
    /// they describe these jib rows, not grid rows.
    /// </summary>
    public Vector3[] GetKickerJibLocalPositions()
    {
        float[] heights = { geometry.row1KickerHeightMeters, geometry.row2KickerHeightMeters };
        List<Vector3> positions = new();
        foreach (float height in heights)
        {
            // Every second T-nut column, centred on the board, so the lattice stays symmetric.
            for (int column = 1; column < geometry.columns; column += KickerJibColumnStride)
            {
                float x = (column - (geometry.columns - 1) * 0.5f) * geometry.gridSpacingMeters;
                positions.Add(new Vector3(x, height, -KickerJibSurfaceOffsetMeters));
            }
        }
        return positions.ToArray();
    }

    /// <summary>Orientation that seats a jib's flat back against the vertical kicker plane.</summary>
    public Quaternion GetKickerJibLocalRotation()
    {
        return Quaternion.Euler(90f, 0f, 180f);
    }

    /// <summary>
    /// Absolute local scale that renders a hold at its physical size. The aggregate FBX
    /// normalises every scan to roughly a 200 mm grid cell and imports each child at
    /// <see cref="NormalizedMeshScale"/>, so the catalog multiplier restores true size.
    /// </summary>
    public Vector3 GetHoldLocalScale(MoonBoardHoldDefinition hold)
    {
        if (hold == null ||
            !IsFinite(hold.meshScaleMultiplier) ||
            hold.meshScaleMultiplier < MinScaleMultiplier ||
            hold.meshScaleMultiplier > MaxScaleMultiplier)
        {
            throw new ArgumentException("Invalid MoonBoard hold scale calibration.", nameof(hold));
        }
        return Vector3.one * (NormalizedMeshScale * hold.meshScaleMultiplier);
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

    private Quaternion GetBoardMountRotation()
    {
        return Quaternion.Euler(SurfaceTiltDegrees, 0f, 180f);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
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
    public float surfaceOffsetMeters;
    public float meshScaleMultiplier;
    public string scaleCalibrationSource;
    public string sourceHoldId;
    public bool hasMeshFrameCorrection;
    public MoonBoardQuaternionDefinition meshFrameCorrection;
}

[Serializable]
public sealed class MoonBoardQuaternionDefinition
{
    public float x;
    public float y;
    public float z;
    public float w;

    public bool IsZero => x == 0f && y == 0f && z == 0f && w == 0f;

    public bool TryGetQuaternion(out Quaternion value)
    {
        value = Quaternion.identity;
        if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(w))
        {
            return false;
        }
        float sqrMagnitude = x * x + y * y + z * z + w * w;
        if (Mathf.Abs(sqrMagnitude - 1f) > 0.001f)
        {
            return false;
        }
        value = new Quaternion(x, y, z, w).normalized;
        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
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
