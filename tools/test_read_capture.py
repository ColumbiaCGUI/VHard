#!/usr/bin/env python3
import gzip
import tempfile
import unittest
from pathlib import Path

from read_capture import EXPECTED_COLUMNS, read_recovered_bytes, recover_gzip_members, validate_capture_rows


class CaptureRecoveryTests(unittest.TestCase):
    def test_reads_concatenated_gzip_members(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.csv.gz"
            path.write_bytes(gzip.compress(b"header\nrow1\n") + gzip.compress(b"row2\n"))

            self.assertEqual(recover_gzip_members(path), b"header\nrow1\nrow2\n")

    def test_ignores_truncated_final_member(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.csv.gz"
            complete = gzip.compress(b"header\nrow1\n")
            truncated = gzip.compress(b"row2\n")[:-5]
            path.write_bytes(complete + truncated)

            self.assertEqual(read_recovered_bytes(path), b"header\nrow1\n")

    def test_rejects_file_without_complete_member(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.csv.gz"
            path.write_bytes(gzip.compress(b"header\n")[:-5])

            with self.assertRaisesRegex(RuntimeError, "No complete gzip member"):
                read_recovered_bytes(path)

    def test_validates_exact_schema_width_precision_and_time_order(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.csv.gz"
            row = {column: "0.00000" for column in EXPECTED_COLUMNS}
            row.update({
                "utc": "2026-07-16T00:00:00.0000000Z",
                "frame": "1",
                "mode": "Grip",
                "route": "MB2016-19215",
                "hold": "D15",
                "LConf": "1",
                "RConf": "1",
                "touchedHold": "D15",
                "gripFlag": "0",
                "perFingerContactMask": "0",
            })
            header = ",".join(EXPECTED_COLUMNS)
            values = ",".join(row[column] for column in EXPECTED_COLUMNS)
            path.write_bytes(gzip.compress(f"{header}\n{values}\n".encode()))

            rows = validate_capture_rows(path)

            self.assertEqual(len(rows), 1)
            self.assertEqual(len(rows[0]), 384)

    def test_rejects_wrong_float_precision(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.csv.gz"
            row = ["0.00000"] * len(EXPECTED_COLUMNS)
            row[EXPECTED_COLUMNS.index("utc")] = "2026-07-16T00:00:00Z"
            row[EXPECTED_COLUMNS.index("sessionTime")] = "0.000"
            path.write_bytes(gzip.compress(
                (",".join(EXPECTED_COLUMNS) + "\n" + ",".join(row) + "\n").encode()
            ))

            with self.assertRaisesRegex(RuntimeError, "5 decimals"):
                validate_capture_rows(path)


if __name__ == "__main__":
    unittest.main()
