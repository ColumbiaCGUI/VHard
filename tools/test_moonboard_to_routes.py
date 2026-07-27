#!/usr/bin/env python3
import json
import subprocess
import sys
import unittest
from pathlib import Path

TOOL = Path(__file__).with_name("moonboard_to_routes.py")


def run_tool(tmp_dir, payload, *extra_args):
    export = tmp_dir / "export.json"
    output = tmp_dir / "routes.json"
    export.write_text(json.dumps(payload), encoding="utf-8")
    result = subprocess.run(
        [sys.executable, str(TOOL), str(export), "-o", str(output), *extra_args],
        capture_output=True, text=True)
    return result, output


class MoonboardToRoutesTests(unittest.TestCase):
    def setUp(self):
        import tempfile
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp_dir = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_converts_pascal_case_export_and_dedupes_repeated_positions(self):
        payload = [{
            "Name": "Example Problem",
            "Grade": "7A",
            "IsBenchmark": True,
            "Moves": [
                {"Description": "A5", "IsStart": False},
                {"Description": "a5", "IsStart": True},
                {"Description": "D15"},
                {"Description": "K18", "IsEnd": True},
            ],
        }]
        result, output = run_tool(self.tmp_dir, payload)
        self.assertEqual(result.returncode, 0, result.stderr)
        document = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual(document["schemaVersion"], 2)
        routes = document["routes"]
        self.assertEqual(routes, [{"name": "EXAMPLE PROBLEM", "grade": "7A",
                                   "holds": ["A5", "D15", "K18"],
                                   "start": ["A5"], "finish": ["K18"]}])

    def test_converts_camel_case_wrapped_export(self):
        payload = {"data": [{
            "name": "wrapped",
            "grade": "6C+",
            "moves": [
                {"description": "B6", "isStart": True},
                {"description": "C8", "isEnd": True},
            ],
        }]}
        result, output = run_tool(self.tmp_dir, payload)
        self.assertEqual(result.returncode, 0, result.stderr)
        routes = json.loads(output.read_text(encoding="utf-8"))["routes"]
        self.assertEqual(routes[0]["holds"], ["B6", "C8"])

    def test_benchmarks_only_filters_non_benchmarks(self):
        payload = [
            {"Name": "Bench", "Grade": "7B", "IsBenchmark": True,
             "Moves": [{"Description": "A5", "IsStart": True},
                       {"Description": "A6", "IsEnd": True}]},
            {"Name": "NotBench", "Grade": "6A", "IsBenchmark": False,
             "Moves": [{"Description": "B6", "IsStart": True},
                       {"Description": "B7", "IsEnd": True}]},
        ]
        result, output = run_tool(self.tmp_dir, payload, "--benchmarks-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        routes = json.loads(output.read_text(encoding="utf-8"))["routes"]
        self.assertEqual([route["name"] for route in routes], ["BENCH"])

    def test_rejects_invalid_grid_position(self):
        payload = [{"Name": "Bad", "Moves": [
            {"Description": "L5", "IsStart": True},
            {"Description": "A6", "IsEnd": True},
        ]}]
        result, _ = run_tool(self.tmp_dir, payload)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("L5", result.stderr)

    def test_skips_problems_shadowing_built_in_study_routes(self):
        payload = [
            {"Name": "Death Star", "Moves": [
                {"Description": "A5", "IsStart": True},
                {"Description": "A6", "IsEnd": True},
            ]},
            {"Name": "Keeper", "Moves": [
                {"Description": "B6", "IsStart": True},
                {"Description": "B7", "IsEnd": True},
            ]},
        ]
        result, output = run_tool(self.tmp_dir, payload)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("shadowing built-in", result.stderr)
        routes = json.loads(output.read_text(encoding="utf-8"))["routes"]
        self.assertEqual([route["name"] for route in routes], ["KEEPER"])

    def test_errors_when_no_routes_match(self):
        payload = [{"Name": "Only", "IsBenchmark": False, "Moves": [
            {"Description": "A5", "IsStart": True},
            {"Description": "A6", "IsEnd": True},
        ]}]
        result, _ = run_tool(self.tmp_dir, payload, "--benchmarks-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("No routes matched", result.stderr)

    def test_rejects_position_with_both_roles_across_repeated_moves(self):
        payload = [{"Name": "Conflict", "Moves": [
            {"Description": "A5", "IsStart": True},
            {"Description": "a5", "IsEnd": True},
        ]}]
        result, _ = run_tool(self.tmp_dir, payload)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("both start and finish", result.stderr)

    def test_rejects_missing_start_or_finish_flags(self):
        missing_start = [{"Name": "No Start", "Moves": [
            {"Description": "A5"},
            {"Description": "A6", "IsEnd": True},
        ]}]
        missing_finish = [{"Name": "No Finish", "Moves": [
            {"Description": "B5", "IsStart": True},
            {"Description": "B6"},
        ]}]

        start_result, _ = run_tool(self.tmp_dir, missing_start)
        finish_result, _ = run_tool(self.tmp_dir, missing_finish)

        self.assertNotEqual(start_result.returncode, 0)
        self.assertIn("no start moves", start_result.stderr)
        self.assertNotEqual(finish_result.returncode, 0)
        self.assertIn("no finish moves", finish_result.stderr)


if __name__ == "__main__":
    unittest.main()
