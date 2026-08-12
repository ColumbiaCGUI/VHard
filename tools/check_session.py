#!/usr/bin/env python3
import argparse
import hashlib
import json
import math
import re
from datetime import datetime
from pathlib import Path

from read_capture import validate_capture_rows


SCHEDULED_DIRECTORY_PATTERN = re.compile(r"block[1-3]_[ABC]_[A-Z0-9_]+(?:_retry[0-9]+)?$")
MANUAL_DIRECTORY_PATTERN = re.compile(
    r"[0-9]{8}_[0-9]{6}_[0-9]{3}_[BC]_[A-Z0-9_]+(?:_retry[0-9]+)?$"
)
CATALOG_PATH = Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/moonboard_2016_40.json"
APPROVED_CATALOG_SHA256 = "09d3d066254afb341c49d2fb10769e28fcb50764b376eefb07b98fdb0e7e7ea7"
MAX_ALIGNMENT_DRIFT_METERS = 0.02
MAX_ALIGNMENT_DRIFT_DEGREES = 2.0
COMPLETE_END_REASONS = {"completed_manual", "completed_early", "timer_expired"}


def _reject_nonfinite_json(value: str):
    raise ValueError(f"non-finite JSON number: {value}")


def _finite_number(value) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError("value is not a JSON number")
    result = float(value)
    if not math.isfinite(result):
        raise ValueError("value is not finite")
    return result


def _vector_distance(left: dict, right: dict) -> float:
    return math.sqrt(sum((_finite_number(left[axis]) - _finite_number(right[axis])) ** 2 for axis in "xyz"))


def _quaternion_angle_degrees(left: dict, right: dict) -> float:
    left_values = [_finite_number(left[axis]) for axis in "xyzw"]
    right_values = [_finite_number(right[axis]) for axis in "xyzw"]
    left_norm = math.sqrt(sum(value * value for value in left_values))
    right_norm = math.sqrt(sum(value * value for value in right_values))
    if left_norm == 0 or right_norm == 0:
        raise ValueError("zero-length alignment rotation")
    dot = abs(sum(a * b for a, b in zip(left_values, right_values))) / (left_norm * right_norm)
    return math.degrees(2 * math.acos(max(-1.0, min(1.0, dot))))


def validate_session(directory: Path) -> int:
    if not (
        SCHEDULED_DIRECTORY_PATTERN.match(directory.name)
        or MANUAL_DIRECTORY_PATTERN.match(directory.name)
    ):
        raise SystemExit(f"Invalid block directory name: {directory.name}")
    try:
        manifest = json.loads(
            (directory / "session.json").read_text(encoding="utf-8"),
            parse_constant=_reject_nonfinite_json,
        )
    except (json.JSONDecodeError, ValueError) as error:
        raise SystemExit("Manifest is not strict finite JSON") from error
    required = {
        "participant", "block", "condition", "route", "routeName", "routeSourceProblemId",
        "routeCatalogSha256", "boardSetup", "boardOverhangAngleDegrees", "routeDefinition",
        "boardAlignment", "boardAlignmentEnd", "retry", "adhoc", "appVersion", "gitRevision",
        "startUtc", "rehearsalStartUtc", "rehearsalDeadlineUtc", "resumeCount",
        "pendingStart", "pendingResumeIndex", "firstInteractionRecorded", "recordingSummaryComplete",
        "endUtc", "endedEarly", "endReason",
        "routesJsonSha256", "gripFeedback",
        "droppedCaptureFrames", "holdAggregates",
    }
    missing = required - set(manifest)
    if missing:
        raise SystemExit(f"Manifest missing fields: {sorted(missing)}")
    if not manifest["endUtc"] or manifest["endReason"] not in COMPLETE_END_REASONS:
        raise SystemExit("Manifest describes an incomplete or crashed block")
    if manifest["endReason"] == "completed_manual" and manifest["endedEarly"]:
        raise SystemExit("Manually completed block is incorrectly marked endedEarly")
    if manifest["endReason"] == "completed_early" and not manifest["endedEarly"]:
        raise SystemExit("Early completion is not marked endedEarly")
    if type(manifest["resumeCount"]) is not int or manifest["resumeCount"] < 0:
        raise SystemExit("Manifest resumeCount is not a non-negative integer")
    if not isinstance(manifest["firstInteractionRecorded"], bool):
        raise SystemExit("Manifest firstInteractionRecorded is not boolean")
    if type(manifest["pendingResumeIndex"]) is not int or manifest["pendingResumeIndex"] != 0:
        raise SystemExit("Completed manifest still has a pending resume transaction")
    if not isinstance(manifest["pendingStart"], bool):
        raise SystemExit("Manifest pendingStart is not boolean")
    if manifest["pendingStart"]:
        raise SystemExit("Completed manifest still has a pending start transaction")
    if not isinstance(manifest["recordingSummaryComplete"], bool):
        raise SystemExit("Manifest recordingSummaryComplete is not boolean")
    if not manifest["appVersion"] or not manifest["gitRevision"]:
        raise SystemExit("Manifest is missing build provenance")
    if manifest["gitRevision"] == "development" and not manifest["adhoc"]:
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
    if type(manifest["droppedCaptureFrames"]) is not int or manifest["droppedCaptureFrames"] < 0:
        raise SystemExit("Manifest droppedCaptureFrames is not a non-negative integer")
    if manifest["droppedCaptureFrames"] != 0:
        raise SystemExit(f"Capture dropped {manifest['droppedCaptureFrames']} frames")
    if manifest["adhoc"]:
        expected_marker = f"_{manifest['condition']}_"
        if (
            manifest["participant"] != "UNASSIGNED"
            or manifest["block"] != 0
            or directory.parent.name != "MANUAL"
            or not MANUAL_DIRECTORY_PATTERN.match(directory.name)
            or expected_marker not in directory.name
        ):
            raise SystemExit("Manual directory does not match its manifest")
    else:
        expected_prefix = f"block{manifest['block']}_{manifest['condition']}_"
        if not directory.name.startswith(expected_prefix):
            raise SystemExit("Directory name does not match the manifest block and condition")
        if directory.parent.name != manifest["participant"]:
            raise SystemExit("Directory participant does not match the manifest")

    capture = []
    previous_block_time = -1.0
    for segment_index in range(manifest["resumeCount"] + 1):
        suffix = "" if segment_index == 0 else f"_resume{segment_index}"
        events = directory / f"events{suffix}.csv"
        if not events.exists() or events.stat().st_size == 0:
            raise SystemExit(f"events{suffix}.csv is missing or empty")
        try:
            segment_capture = validate_capture_rows(directory / f"capture{suffix}.csv.gz")
        except RuntimeError as error:
            raise SystemExit(str(error)) from error
        first_block_time = float(segment_capture[0]["blockTime"])
        if first_block_time < previous_block_time:
            raise SystemExit(f"Capture blockTime decreases at segment {segment_index}")
        previous_block_time = float(segment_capture[-1]["blockTime"])
        segment_duration = (
            previous_block_time - first_block_time + 1.0 / 30.0
        )
        segment_rate = len(segment_capture) / segment_duration
        minimum_rate, maximum_rate = (29.0, 31.0) if len(segment_capture) >= 30 else (20.0, 45.0)
        if not minimum_rate <= segment_rate <= maximum_rate:
            raise SystemExit(
                f"Capture segment {segment_index} sample rate is {segment_rate:.2f} Hz, "
                "expected approximately 30 Hz"
            )
        capture.extend(segment_capture)

    expected_recording_files = {
        name
        for segment_index in range(manifest["resumeCount"] + 1)
        for name in (
            "events.csv" if segment_index == 0 else f"events_resume{segment_index}.csv",
            "capture.csv.gz" if segment_index == 0 else f"capture_resume{segment_index}.csv.gz",
        )
    }
    actual_recording_files = {
        path.name
        for path in directory.iterdir()
        if re.fullmatch(r"(?:events(?:_resume[0-9]+)?\.csv|capture(?:_resume[0-9]+)?\.csv\.gz)", path.name)
    }
    if actual_recording_files != expected_recording_files:
        raise SystemExit("Recording segment files do not match manifest resumeCount")
    expected_mode = {"A": "Basic", "B": "Grip", "C": "Ghost"}[manifest["condition"]]
    if any(row["mode"] != expected_mode or row["route"] != manifest["route"] for row in capture):
        raise SystemExit("Capture mode or route does not match the manifest")
    rehearsal_start = datetime.fromisoformat(manifest["rehearsalStartUtc"].replace("Z", "+00:00"))
    rehearsal_deadline = datetime.fromisoformat(manifest["rehearsalDeadlineUtc"].replace("Z", "+00:00"))
    if abs((rehearsal_deadline - rehearsal_start).total_seconds() - 300.0) > 0.001:
        raise SystemExit("Manifest rehearsal deadline is not exactly five minutes after its start")
    end_utc = datetime.fromisoformat(manifest["endUtc"].replace("Z", "+00:00"))
    if (end_utc - rehearsal_deadline).total_seconds() > 1.0:
        raise SystemExit("Run ended after the persisted rehearsal deadline")
    if manifest["endReason"] == "timer_expired":
        if manifest["endedEarly"]:
            raise SystemExit("Timer expiry is incorrectly marked endedEarly")
        if end_utc != rehearsal_deadline:
            raise SystemExit("Timer expiry does not match the persisted rehearsal deadline")
    elapsed = (
        end_utc - rehearsal_start
    ).total_seconds()
    capture_duration = float(capture[-1]["blockTime"])
    if abs(capture_duration - elapsed) > max(1.0, elapsed * 0.02):
        raise SystemExit(
            f"Capture duration {capture_duration:.2f}s does not match manifest duration {elapsed:.2f}s"
        )
    return len(capture)


def main():
    parser = argparse.ArgumentParser(description="Validate a VHard study block directory.")
    parser.add_argument("block_directory", type=Path)
    args = parser.parse_args()
    row_count = validate_session(args.block_directory)
    print(f"valid: {args.block_directory} ({row_count} capture rows)")


if __name__ == "__main__":
    main()
