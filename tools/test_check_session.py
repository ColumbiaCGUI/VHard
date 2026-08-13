#!/usr/bin/env python3
import csv
import gzip
import io
import json
import math
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from check_session import APPROVED_CATALOG_SHA256, CATALOG_PATH, validate_session
from read_capture import EXPECTED_COLUMNS


class SessionValidationTests(unittest.TestCase):
    def test_accepts_complete_thirty_hertz_block(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")

            self.assertEqual(validate_session(block), 60)

    def test_rejects_manual_completion_marked_early(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["endedEarly"] = True
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "incorrectly marked endedEarly"):
                validate_session(block)

    def test_rejects_condition_b_block_without_role_rings(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["routeCuePresentation"] = "Hidden"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "routeCuePresentation"):
                validate_session(block)

    def test_rejects_app_closed_block(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "app_closed")

            with self.assertRaisesRegex(SystemExit, "incomplete or crashed"):
                validate_session(block)

    def test_rejects_boolean_resume_count(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["resumeCount"] = False
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "resumeCount"):
                validate_session(block)

    def test_rejects_boolean_pending_resume_index(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pendingResumeIndex"] = False
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "pending resume transaction"):
                validate_session(block)

    def test_rejects_nonfinite_alignment_number(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["boardAlignmentEnd"]["position"]["x"] = float("nan")
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "strict finite JSON"):
                validate_session(block)

    def test_rejects_completed_manifest_with_pending_start(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pendingStart"] = True
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "pending start transaction"):
                validate_session(block)

    def test_rejects_manifest_without_pending_start_field(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            del manifest["pendingStart"]
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "pendingStart"):
                validate_session(block)

    def test_rejects_timer_expiry_before_persisted_deadline(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "timer_expired")

            with self.assertRaisesRegex(SystemExit, "persisted rehearsal deadline"):
                validate_session(block)

    def test_rejects_timer_expiry_with_subsecond_deadline_drift(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "timer_expired")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            deadline = datetime.fromisoformat(manifest["rehearsalDeadlineUtc"])
            manifest["endUtc"] = (deadline + timedelta(milliseconds=1)).isoformat()
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "persisted rehearsal deadline"):
                validate_session(block)

    def test_rejects_early_completion_without_ended_early_flag(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["endedEarly"] = False
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "not marked endedEarly"):
                validate_session(block)

    def test_rejects_manual_completion_after_persisted_deadline(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            start = datetime.fromisoformat(manifest["rehearsalStartUtc"])
            manifest["endUtc"] = (start + timedelta(seconds=301.1)).isoformat()
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "after the persisted rehearsal deadline"):
                validate_session(block)

    def test_accepts_continuous_resumed_segments(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_manual")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["resumeCount"] = 1
            manifest["recordingSummaryComplete"] = False
            start = datetime.fromisoformat(manifest["rehearsalStartUtc"])
            manifest["endUtc"] = (start + timedelta(seconds=12)).isoformat()
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            capture_lines = gzip.decompress((block / "capture.csv.gz").read_bytes()).decode().splitlines()
            (block / "capture.csv.gz").write_bytes(
                gzip.compress(("\n".join(capture_lines[:31]) + "\n").encode())
            )
            resumed_output = io.StringIO()
            resumed_writer = csv.DictWriter(
                resumed_output,
                fieldnames=EXPECTED_COLUMNS,
                lineterminator="\n",
            )
            resumed_writer.writeheader()
            for row in csv.DictReader(io.StringIO("\n".join([capture_lines[0]] + capture_lines[31:]))):
                row["blockTime"] = f"{float(row['blockTime']) + 10.0:.5f}"
                resumed_writer.writerow(row)
            (block / "capture_resume1.csv.gz").write_bytes(
                gzip.compress(resumed_output.getvalue().encode())
            )
            (block / "events_resume1.csv").write_text(
                "utcTime,sessionTime,frame,playerPosition,action,hand,hold,details\n",
                encoding="utf-8",
            )

            self.assertEqual(validate_session(block), 60)

    def test_accepts_manual_directory_contract(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            scheduled = self._write_block(root, "completed_manual")
            manual = root / "MANUAL" / "20260811_120000_000_B_MB2016_19215"
            manual.parent.mkdir()
            scheduled.rename(manual)
            manifest_path = manual / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest.update({
                "participant": "UNASSIGNED",
                "block": 0,
                "adhoc": True,
                "gitRevision": "development",
            })
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            self.assertEqual(validate_session(manual), 60)

    def test_rejects_board_alignment_change(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["boardAlignmentEnd"]["recenterEpoch"] = 1
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "alignment changed"):
                validate_session(block)

    def test_accepts_small_spatial_anchor_pose_refinement(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            for snapshot in (manifest["boardAlignment"], manifest["boardAlignmentEnd"]):
                snapshot.update({
                    "isAligned": True,
                    "isSpatiallyAnchored": True,
                    "spatialAnchorUuid": "11111111-1111-1111-1111-111111111111",
                })
            manifest["boardAlignmentEnd"]["position"]["x"] = 0.01
            angle = math.radians(1.0) / 2
            manifest["boardAlignmentEnd"]["rotation"].update({"y": math.sin(angle), "w": math.cos(angle)})
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            self.assertEqual(validate_session(block), 60)

    def test_rejects_large_board_position_change(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["boardAlignmentEnd"]["position"]["x"] = 0.03
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "alignment changed"):
                validate_session(block)

    def test_rejects_large_board_rotation_change(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")
            manifest_path = block / "session.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            angle = math.radians(3.0) / 2
            manifest["boardAlignmentEnd"]["rotation"].update({"y": math.sin(angle), "w": math.cos(angle)})
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

            with self.assertRaisesRegex(SystemExit, "alignment changed"):
                validate_session(block)

    @staticmethod
    def _write_block(root: Path, end_reason: str) -> Path:
        block = root / "P01" / "block1_B_MB2016_19215"
        block.mkdir(parents=True)
        start = datetime(2026, 7, 16, tzinfo=timezone.utc)
        end = start + timedelta(seconds=2)
        catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
        route_definition = next(route for route in catalog["routes"] if route["id"] == "MB2016-19215")
        manifest = {
            "participant": "P01",
            "block": 1,
            "condition": "B",
            "route": "MB2016-19215",
            "routeName": "FAR FROM THE MADDING CROWD",
            "routeSourceProblemId": "19215",
            "routeCatalogSha256": APPROVED_CATALOG_SHA256,
            "boardSetup": "MoonBoard 2016",
            "boardOverhangAngleDegrees": 40,
            "routeDefinition": route_definition,
            "boardAlignment": {
                "isAligned": False,
                "isSpatiallyAnchored": False,
                "spatialAnchorUuid": "",
                "recenterEpoch": 0,
                "position": {"x": 0.0, "y": 0.0, "z": 0.0},
                "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
            },
            "boardAlignmentEnd": {
                "isAligned": False,
                "isSpatiallyAnchored": False,
                "spatialAnchorUuid": "",
                "recenterEpoch": 0,
                "position": {"x": 0.0, "y": 0.0, "z": 0.0},
                "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
            },
            "retry": 0,
            "adhoc": False,
            "appVersion": "0.1.0",
            "gitRevision": "abc123-dirty.123456789abc",
            "startUtc": start.isoformat(),
            "rehearsalStartUtc": start.isoformat(),
            "rehearsalDeadlineUtc": (start + timedelta(minutes=5)).isoformat(),
            "resumeCount": 0,
            "pendingStart": False,
            "pendingResumeIndex": 0,
            "firstInteractionRecorded": True,
            "recordingSummaryComplete": True,
            "endUtc": end.isoformat(),
            "endedEarly": end_reason == "completed_early",
            "endReason": end_reason,
            "routesJsonSha256": None,
            "gripFeedback": "ok",
            "routeCuePresentation": "VirtualHalos",
            "droppedCaptureFrames": 0,
            "holdAggregates": [],
        }
        (block / "session.json").write_text(json.dumps(manifest), encoding="utf-8")
        (block / "events.csv").write_text(
            "utcTime,sessionTime,frame,playerPosition,action,hand,hold,details\n",
            encoding="utf-8",
        )

        output = io.StringIO()
        writer = csv.DictWriter(output, fieldnames=EXPECTED_COLUMNS, lineterminator="\n")
        writer.writeheader()
        for index in range(1, 61):
            row = {column: "0.00000" for column in EXPECTED_COLUMNS}
            row.update({
                "utc": start.isoformat(),
                "sessionTime": f"{index / 30:.5f}",
                "frame": str(index),
                "blockTime": f"{index / 30:.5f}",
                "mode": "Grip",
                "route": "MB2016-19215",
                "LConf": "1",
                "RConf": "1",
                "gripFlag": "0",
                "perFingerContactMask": "0",
            })
            writer.writerow(row)
        (block / "capture.csv.gz").write_bytes(gzip.compress(output.getvalue().encode()))
        return block


if __name__ == "__main__":
    unittest.main()
