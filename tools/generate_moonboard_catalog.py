#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path


SOURCE_REPOSITORY = "https://github.com/e-sr/moonboard"
SOURCE_REVISION = "ccd78f587ab189acea6dd7ce8a6d4f086f65db69"
ROUTE_SOURCE_IDS = ("19215", "21329", "170190")
HOLDSET_PREFIXES = {
    "Original School Holds": "Y",
    "Hold Set A": "W",
    "Hold Set B": "B",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def record_sha256(record: dict) -> str:
    encoded = json.dumps(record, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def coordinate_key(coordinate: str) -> tuple[int, int]:
    return int(coordinate[1:]), ord(coordinate[0]) - ord("A")


def build_catalog(source_root: Path, project_root: Path) -> dict:
    problems_path = source_root / "problems/fetch/moonboard_problems_setup_2016.json"
    holds_path = source_root / "problems/holds_tmp/Moonboard2016.tmp"
    dimensions_path = source_root / "doc/Moonboard.xlsx"
    mesh_path = project_root / "Assets/Resources/New_Decimated_Holds.fbx"

    problems = json.loads(problems_path.read_text(encoding="utf-8"))
    holdsets = json.loads(holds_path.read_text(encoding="utf-8"))["Data"]

    holds = []
    coordinates = set()
    for holdset in holdsets:
        holdset_name = holdset["Description"]
        prefix = HOLDSET_PREFIXES[holdset_name]
        for source_hold in holdset["Holds"]:
            location = source_hold["Location"]
            coordinate = location["Description"].upper()
            if coordinate in coordinates:
                raise ValueError(f"Duplicate MoonBoard coordinate: {coordinate}")
            coordinates.add(coordinate)
            holds.append(
                {
                    "coordinate": coordinate,
                    "scanId": prefix + source_hold["Number"],
                    "holdset": holdset_name,
                    "holdNumber": source_hold["Number"],
                    "rotationDegrees": int(location["Rotation"]),
                    "sourceHoldId": str(source_hold["Id"]),
                }
            )
    holds.sort(key=lambda hold: coordinate_key(hold["coordinate"]))
    if len(holds) != 140:
        raise ValueError(f"Expected 140 active 2016 holds, found {len(holds)}")

    routes = []
    for source_id in ROUTE_SOURCE_IDS:
        source_route = problems[source_id]
        if source_route["Holdsetup"]["Description"] != "MoonBoard 2016":
            raise ValueError(f"Problem {source_id} is not a MoonBoard 2016 problem")
        if source_route["Grade"] != "6B+" or not source_route["IsBenchmark"]:
            raise ValueError(f"Problem {source_id} is not a 6B+ benchmark")
        if len(source_route["Moves"]) != 7:
            raise ValueError(f"Problem {source_id} does not contain seven holds")
        starts = [move for move in source_route["Moves"] if move["IsStart"]]
        finishes = [move for move in source_route["Moves"] if move["IsEnd"]]
        if len(starts) != 2 or len(finishes) != 1:
            raise ValueError(f"Problem {source_id} does not have two starts and one finish")

        moves = []
        for index, move in enumerate(source_route["Moves"]):
            coordinate = move["Description"].upper()
            if coordinate not in coordinates:
                raise ValueError(f"Problem {source_id} references vacant coordinate {coordinate}")
            role = "start" if move["IsStart"] else "finish" if move["IsEnd"] else "move"
            moves.append(
                {
                    "sequence": index,
                    "coordinate": coordinate,
                    "role": role,
                    "sourceMoveId": str(move["Id"]),
                }
            )

        setter = source_route["Setter"]
        routes.append(
            {
                "id": f"MB2016-{source_id}",
                "sourceProblemId": source_id,
                "name": source_route["Name"],
                "grade": source_route["Grade"],
                "isBenchmark": True,
                "method": source_route["Method"],
                "repeatsAtArchive": int(source_route["Repeats"]),
                "setter": setter["Nickname"],
                "lockedForStudy": True,
                "selectionMatch": "6B+ benchmark; 7 holds; 2 distinct start holds",
                "sourceRecordSha256": record_sha256(source_route),
                "moves": moves,
            }
        )

    for index, route in enumerate(routes):
        route_coordinates = {move["coordinate"] for move in route["moves"]}
        for other in routes[index + 1 :]:
            shared = route_coordinates & {move["coordinate"] for move in other["moves"]}
            if len(shared) > 1:
                raise ValueError(
                    f"Study routes {route['id']} and {other['id']} share {len(shared)} holds"
                )

    main_surface_length = 3.6401785670486002
    return {
        "schemaVersion": 1,
        "setupId": "moonboard-2016",
        "setupName": "MoonBoard 2016",
        "overhangAngleDegrees": 40,
        "archiveDate": "2026-07-21",
        "geometry": {
            "boardWidthMeters": 2.44,
            "totalHeightMeters": 3.15,
            "horizontalOverhangMeters": 2.35,
            "mainSurfaceLengthMeters": main_surface_length,
            "kickerHeightMeters": 0.37,
            "gridSpacingMeters": 0.20,
            "mainFirstRowOffsetMeters": (main_surface_length - 15 * 0.20) / 2,
            "row1KickerHeightMeters": 0.10,
            "row2KickerHeightMeters": 0.30,
            "columns": 11,
            "rows": 18,
        },
        "provenance": {
            "sourceRepository": SOURCE_REPOSITORY,
            "sourceRevision": SOURCE_REVISION,
            "problemsSha256": sha256(problems_path),
            "holdsSha256": sha256(holds_path),
            "dimensionsSha256": sha256(dimensions_path),
            "meshAsset": "Assets/Resources/New_Decimated_Holds.fbx",
            "meshAssetSha256": sha256(mesh_path),
        },
        "holds": holds,
        "routes": routes,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Archive authoritative MoonBoard 2016 study content.")
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--project-root", type=Path, default=Path("."))
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("Assets/StreamingAssets/moonboard_2016_40.json"),
    )
    args = parser.parse_args()

    catalog = build_catalog(args.source_root.resolve(), args.project_root.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
