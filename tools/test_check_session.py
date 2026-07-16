#!/usr/bin/env python3
import csv
import gzip
import io
import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from check_session import validate_session
from read_capture import EXPECTED_COLUMNS


class SessionValidationTests(unittest.TestCase):
    def test_accepts_complete_thirty_hertz_block(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "completed_early")

            self.assertEqual(validate_session(block), 60)

    def test_rejects_app_closed_block(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            block = self._write_block(Path(temporary_directory), "app_closed")

            with self.assertRaisesRegex(SystemExit, "incomplete or crashed"):
                validate_session(block)

    @staticmethod
    def _write_block(root: Path, end_reason: str) -> Path:
        block = root / "P01" / "block1_B_DEATH_STAR"
        block.mkdir(parents=True)
        start = datetime(2026, 7, 16, tzinfo=timezone.utc)
        end = start + timedelta(seconds=2)
        manifest = {
            "participant": "P01",
            "block": 1,
            "condition": "B",
            "route": "DEATH STAR",
            "retry": 0,
            "appVersion": "0.1.0",
            "gitRevision": "abc123-dirty.123456789abc",
            "startUtc": start.isoformat(),
            "endUtc": end.isoformat(),
            "endedEarly": True,
            "endReason": end_reason,
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
                "route": "DEATH STAR",
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
