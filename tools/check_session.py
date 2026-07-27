#!/usr/bin/env python3
import argparse
import hashlib
import json
import math
import re
from datetime import datetime
from pathlib import Path

from read_capture import validate_capture_rows


DIRECTORY_PATTERN = re.compile(r"block[1-3]_[ABC]_[A-Z0-9_]+(?:_retry[0-9]+)?$")
CATALOG_PATH = Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/moonboard_2016_40.json"
APPROVED_CATALOG_SHA256 = "076794dcfde57b3b8e99380a46e82ddbefc1c4702904706ff555992717e84467"
MAX_ALIGNMENT_DRIFT_METERS = 0.02
MAX_ALIGNMENT_DRIFT_DEGREES = 2.0


def _vector_distance(left: dict, right: dict) -> float:
    return math.sqrt(sum((float(left[axis]) - float(right[axis])) ** 2 for axis in "xyz"))


def _quaternion_angle_degrees(left: dict, right: dict) -> float:
    left_values = [float(left[axis]) for axis in "xyzw"]
    right_values = [float(right[axis]) for axis in "xyzw"]
    left_norm = math.sqrt(sum(value * value for value in left_values))
    right_norm = math.sqrt(sum(value * value for value in right_values))
    if left_norm == 0 or right_norm == 0:
        raise ValueError("zero-length alignment rotation")
    dot = abs(sum(a * b for a, b in zip(left_values, right_values))) / (left_norm * right_norm)
    return math.degrees(2 * math.acos(max(-1.0, min(1.0, dot))))


def validate_session(directory: Path) -> int:
    if not DIRECTORY_PATTERN.match(directory.name):
        raise SystemExit(f"Invalid block directory name: {directory.name}")
    manifest = json.loads((directory / "session.json").read_text(encoding="utf-8"))
    required = {
        "participant", "block", "condition", "route", "routeName", "routeSourceProblemId",
        "routeCatalogSha256", "boardSetup", "boardOverhangAngleDegrees", "routeDefinition",
        "boardAlignment", "boardAlignmentEnd", "retry", "appVersion", "gitRevision",
        "startUtc", "endUtc", "endedEarly", "endReason", "routesJsonSha256", "gripFeedback",
        "droppedCaptureFrames", "holdAggregates",
    }
    missing = required - set(manifest)
    if missing:
        raise SystemExit(f"Manifest missing fields: {sorted(missing)}")
    if not manifest["endUtc"] or manifest["endReason"] not in {"timer_expired", "completed_early"}:
        raise SystemExit("Manifest describes an incomplete or crashed block")
    if not manifest["appVersion"] or not manifest["gitRevision"]:
        raise SystemExit("Manifest is missing build provenance")
    if manifest["gitRevision"] == "development":
        raise SystemExit("Manifest was produced by an unstamped development run")
    routes_hash = manifest["routesJsonSha256"]
    if routes_hash is not None and not re.fullmatch(r"[0-9a-f]{64}", routes_hash):
        raise SystemExit("Manifest routesJsonSha256 is not null or a lowercase SHA-256")
    grip_feedback = manifest["gripFeedback"]
    if grip_feedback != "ok":
        if not isinstance(grip_feedback, str) or not grip_feedback.startswith("degraded_at_"):
            raise SystemExit("Manifest gripFeedback is not 'ok' or a degraded_at_<utcISO> value")
        try:
            degraded_timestamp = grip_feedback[len("degraded_at_"):]
            datetime.fromisoformat(degraded_timestamp.replace("Z", "+00:00"))
        except ValueError as error:
            raise SystemExit("Manifest gripFeedback has an invalid degradation timestamp") from error
    if manifest["boardSetup"] != "MoonBoard 2016" or manifest["boardOverhangAngleDegrees"] != 40:
        raise SystemExit("Manifest does not describe MoonBoard 2016 at 40 degrees")
    catalog_bytes = CATALOG_PATH.read_bytes()
    if hashlib.sha256(catalog_bytes).hexdigest() != APPROVED_CATALOG_SHA256:
        raise SystemExit("Local authoritative route catalog does not match the approved hash")
    if manifest["routeCatalogSha256"] != APPROVED_CATALOG_SHA256:
        raise SystemExit("Manifest does not reference the approved route catalog")
    catalog = json.loads(catalog_bytes)
    expected_route = next(
        (candidate for candidate in catalog["routes"] if candidate["id"] == manifest["route"]),
        None,
    )
    route = manifest["routeDefinition"]
    if (
        route != expected_route
        or expected_route is None
        or route.get("id") != manifest["route"]
        or route.get("name") != manifest["routeName"]
        or route.get("sourceProblemId") != manifest["routeSourceProblemId"]
        or route.get("grade") != "6B+"
        or route.get("isBenchmark") is not True
        or route.get("lockedForStudy") is not True
        or len(route.get("moves", ())) != 7
    ):
        raise SystemExit("Manifest route snapshot is inconsistent or not study-locked")
    roles = [move.get("role") for move in route["moves"]]
    if roles.count("start") != 2 or roles.count("finish") != 1:
        raise SystemExit("Manifest route snapshot has invalid start/finish roles")
    if not isinstance(manifest["boardAlignment"], dict) or not isinstance(manifest["boardAlignmentEnd"], dict):
        raise SystemExit("Manifest is missing a board alignment snapshot")
    alignment_start = manifest["boardAlignment"]
    alignment_end = manifest["boardAlignmentEnd"]
    exact_fields = ("isAligned", "isSpatiallyAnchored", "spatialAnchorUuid", "recenterEpoch")
    try:
        identity_changed = any(alignment_start[field] != alignment_end[field] for field in exact_fields)
        pose_changed = (
            _vector_distance(alignment_start["position"], alignment_end["position"])
            > MAX_ALIGNMENT_DRIFT_METERS
            or _quaternion_angle_degrees(alignment_start["rotation"], alignment_end["rotation"])
            > MAX_ALIGNMENT_DRIFT_DEGREES
        )
    except (KeyError, TypeError, ValueError) as error:
        raise SystemExit("Manifest contains a malformed board alignment snapshot") from error
    if identity_changed or pose_changed:
        raise SystemExit("Board alignment changed during the recorded block")
    if manifest["droppedCaptureFrames"] != 0:
        raise SystemExit(f"Capture dropped {manifest['droppedCaptureFrames']} frames")
    expected_prefix = f"block{manifest['block']}_{manifest['condition']}_"
    if not directory.name.startswith(expected_prefix):
        raise SystemExit("Directory name does not match the manifest block and condition")
    if directory.parent.name != manifest["participant"]:
        raise SystemExit("Directory participant does not match the manifest")
    events = directory / "events.csv"
    if not events.exists() or events.stat().st_size == 0:
        raise SystemExit("events.csv is missing or empty")
    try:
        capture = validate_capture_rows(directory / "capture.csv.gz")
    except RuntimeError as error:
        raise SystemExit(str(error)) from error
    expected_mode = {"A": "Basic", "B": "Grip", "C": "Ghost"}[manifest["condition"]]
    if any(row["mode"] != expected_mode or row["route"] != manifest["route"] for row in capture):
        raise SystemExit("Capture mode or route does not match the manifest")
    elapsed = (
        datetime.fromisoformat(manifest["endUtc"].replace("Z", "+00:00"))
        - datetime.fromisoformat(manifest["startUtc"].replace("Z", "+00:00"))
    ).total_seconds()
    capture_duration = float(capture[-1]["blockTime"])
    if abs(capture_duration - elapsed) > max(1.0, elapsed * 0.02):
        raise SystemExit(
            f"Capture duration {capture_duration:.2f}s does not match manifest duration {elapsed:.2f}s"
        )
    if capture_duration >= 1.0:
        sample_rate = len(capture) / capture_duration
        if not 29.0 <= sample_rate <= 31.0:
            raise SystemExit(f"Capture sample rate is {sample_rate:.2f} Hz, expected approximately 30 Hz")
    return len(capture)


def main():
    parser = argparse.ArgumentParser(description="Validate a VHard study block directory.")
    parser.add_argument("block_directory", type=Path)
    args = parser.parse_args()
    row_count = validate_session(args.block_directory)
    print(f"valid: {args.block_directory} ({row_count} capture rows)")


if __name__ == "__main__":
    main()
