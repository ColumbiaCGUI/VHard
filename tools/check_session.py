#!/usr/bin/env python3
import argparse
import json
import re
from datetime import datetime
from pathlib import Path

from read_capture import validate_capture_rows


DIRECTORY_PATTERN = re.compile(r"block[1-3]_[ABC]_[A-Z0-9_]+(?:_retry[0-9]+)?$")


def validate_session(directory: Path) -> int:
    if not DIRECTORY_PATTERN.match(directory.name):
        raise SystemExit(f"Invalid block directory name: {directory.name}")
    manifest = json.loads((directory / "session.json").read_text(encoding="utf-8"))
    required = {
        "participant", "block", "condition", "route", "retry", "appVersion", "gitRevision",
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
