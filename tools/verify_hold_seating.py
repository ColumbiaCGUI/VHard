"""Gate: every hold's seating offset must follow from its own scale multiplier.

A hold is seated by pushing it off the board along the mount normal by half its depth.
Depth is measured on the NORMALISED mesh, so the offset carries the same scale factor
as the mesh:

    surfaceOffsetMeters == (normalisedDepthMm / 2000) * meshScaleMultiplier

This gate exists because that invariant was silently violated in the shipped catalog.
W98's stored base offset held the hold's TRUE half-depth in millimetres while all 139
others held NORMALISED half-depth, so the multiplier scaled it a second time and W98
shipped 33.7% under-seated -- sunk into the board on a live grid position. Nothing
caught it: the value was finite, inside the validation band, and the scene applied it
without complaint. Only comparing the offset against the mesh it seats reveals it.

Reads the normalised meshes straight out of the aggregate FBX, so it needs no Unity and
no external measurement files. Run it before freezing a study build.

Usage:
    python verify_hold_seating.py            # exit 0 = all 140 consistent
    python verify_hold_seating.py --verbose
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import fbx_mesh

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "Assets/StreamingAssets/moonboard_2016_40.json"
MESH = ROOT / "Assets/Resources/New_Decimated_Holds.fbx"

# The offset is stored to 10 decimal places and the depth comes back through a float32
# mesh, so allow a hair of slack -- but nothing near a real authoring mistake. The bug
# this gate was written for was 33.7%; decimation noise on the worst hold is ~2.6%.
TOLERANCE = 0.05

# Half-Z-depth is the DEFAULT seating model; these scans deviate from it deliberately.
# 2026-08-14: their true mounting faces were fitted as planes on the shipped meshes
# (B18/W99 rms 0.39 mm; D10/Y33 rms 0.58 mm -- Y33's flat face sits DIAGONALLY in its
# mesh frame, so half-Z-depth floated the hold 12.4 mm off the wall). Values are the
# fitted face-plane distance from the mesh origin, in normalised units, and must match
# SURFACE_OFFSETS_METERS in generate_moonboard_catalog.py. Any further deviation from
# half-depth must be declared here or this gate fails, exactly as designed.
PLANE_SEATED_OFFSETS_NORMALISED = {
    "W99": 0.0231787816,  # B18
    "Y33": 0.0647344467,  # D10
}


def has_materialized_mesh(path: Path | None = None) -> bool:
    """True only when path starts with the binary FBX header, not an LFS pointer."""
    mesh_path = MESH if path is None else path
    try:
        with mesh_path.open("rb") as stream:
            return stream.read(20).startswith(b"Kaydara FBX Binary")
    except OSError:
        return False


def mesh_name_for(scan_id: str, meshes: dict) -> str | None:
    """Catalog scanIds map to a plain mesh name, or to a re-oriented "Zplane <id>" one."""
    if scan_id in meshes:
        return scan_id
    for candidate in ("Zplane %s" % scan_id, "Zplane_%s" % scan_id, "Zplane-%s" % scan_id):
        if candidate in meshes:
            return candidate
    return None


def check(verbose: bool = False, catalog: dict | None = None, meshes: dict | None = None) -> list[str]:
    """Returns a list of human-readable failures; empty means consistent.

    `catalog` and `meshes` may be injected so a test can verify the gate actually bites
    on a known-bad input, and so the ~5 s FBX parse can be reused across tests.
    """
    loaded_catalog = (
        catalog if catalog is not None else json.loads(CATALOG.read_text(encoding="utf-8"))
    )
    loaded_meshes = meshes if meshes is not None else fbx_mesh.geometry_vertices(MESH)
    failures: list[str] = []

    if len(loaded_meshes) != len(loaded_catalog["holds"]):
        failures.append(
            "aggregate FBX has %d meshes but the catalog has %d holds"
            % (len(loaded_meshes), len(loaded_catalog["holds"]))
        )

    for hold in loaded_catalog["holds"]:
        scan_id = hold["scanId"]
        name = mesh_name_for(scan_id, loaded_meshes)
        if name is None:
            failures.append("%s (%s): no mesh in the aggregate FBX" % (hold["coordinate"], scan_id))
            continue
        depth_mm = fbx_mesh.extent_mm(loaded_meshes[name])[2]
        if not math.isfinite(depth_mm) or depth_mm <= 0.0:
            failures.append(
                "%s (%s): aggregate mesh has invalid depth %r mm"
                % (hold["coordinate"], scan_id, depth_mm)
            )
            continue
        if scan_id in PLANE_SEATED_OFFSETS_NORMALISED:
            expected = PLANE_SEATED_OFFSETS_NORMALISED[scan_id] * hold["meshScaleMultiplier"]
        else:
            expected = (depth_mm / 2000.0) * hold["meshScaleMultiplier"]
        actual = hold["surfaceOffsetMeters"]
        if not math.isfinite(expected) or expected <= 0.0 or not math.isfinite(actual):
            failures.append(
                "%s (%s): invalid expected/actual offset (%r m / %r m)"
                % (hold["coordinate"], scan_id, expected, actual)
            )
            continue
        relative_error = actual / expected - 1.0
        error = abs(relative_error)
        if error > TOLERANCE:
            failures.append(
                "%s (%s): offset %.7f m but its own multiplier implies %.7f m (%+.1f%%)"
                % (hold["coordinate"], scan_id, actual, expected, 100.0 * relative_error)
            )
        elif verbose:
            print(
                "  %-4s %-5s mesh=%-22s depth %7.3f mm  offset %.7f  (%+.2f%%)"
                % (hold["coordinate"], scan_id, name, depth_mm, actual, 100.0 * relative_error)
            )
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    if not has_materialized_mesh():
        print(
            "ERROR: %s is absent or an unmaterialised Git LFS pointer; run `git lfs pull`"
            % MESH.name,
            file=sys.stderr,
        )
        return 2

    failures = check(args.verbose)
    if failures:
        print("Hold seating gate FAILED (%d):" % len(failures))
        for line in failures:
            print("  " + line)
        return 1
    print("Hold seating gate passed: all 140 offsets follow from their own multipliers.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
