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

    def test_rejects_app_closed_block(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "app_closed")

            with self.assertRaisesRegex(SystemExit, "incomplete or crashed"):
                validate_session(block)

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
            "appVersion": "0.1.0",
            "gitRevision": "abc123-dirty.123456789abc",
            "startUtc": start.isoformat(),
            "endUtc": end.isoformat(),
            "endedEarly": end_reason == "completed_early",
            "endReason": end_reason,
            "routesJsonSha256": None,
            "gripFeedback": "ok",
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
