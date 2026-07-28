"""Tests for the hold-seating gate, including proof that it bites on a real regression."""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import fbx_mesh
import verify_hold_seating as gate

_MESHES = None


def meshes():
    """Parsed once: the aggregate FBX is 274 MB and takes ~5 s to walk."""
    global _MESHES
    if _MESHES is None:
        _MESHES = fbx_mesh.geometry_vertices(gate.MESH)
    return _MESHES


def catalog():
    return json.loads(gate.CATALOG.read_text(encoding="utf-8"))


class HoldSeatingGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not gate.has_materialized_mesh():
            raise AssertionError(
                "aggregate FBX absent or an unmaterialised Git LFS pointer; run `git lfs pull`"
            )

    def test_materialized_mesh_check_rejects_missing_and_lfs_pointer(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "holds.fbx"
            self.assertFalse(gate.has_materialized_mesh(path))
            path.write_text(
                "version https://git-lfs.github.com/spec/v1\n"
                "oid sha256:deadbeef\nsize 274147276\n",
                encoding="ascii",
            )
            self.assertFalse(gate.has_materialized_mesh(path))
            path.write_bytes(b"Kaydara FBX Binary  \x00\x1a\x00")
            self.assertTrue(gate.has_materialized_mesh(path))

    def test_command_fails_when_mesh_is_not_materialized(self):
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "holds.fbx"
            with mock.patch.object(gate, "MESH", missing), mock.patch.object(
                sys, "argv", ["verify_hold_seating.py"]
            ), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(gate.main(), 2)

    def test_every_shipped_offset_follows_from_its_own_multiplier(self):
        self.assertEqual(gate.check(catalog=catalog(), meshes=meshes()), [])

    def test_every_catalog_hold_has_a_mesh_in_the_aggregate_fbx(self):
        available = meshes()
        missing = [
            hold["scanId"]
            for hold in catalog()["holds"]
            if gate.mesh_name_for(hold["scanId"], available) is None
        ]
        self.assertEqual(missing, [])
        self.assertEqual(len(available), 140)

    def test_gate_catches_the_historical_W98_regression(self):
        # W98's stored base offset once held TRUE half-depth in mm while all 139 others
        # held NORMALISED half-depth, so the multiplier scaled it twice and W98 shipped
        # 33.7% under-seated on a live grid position. The value was finite and inside the
        # validation band, so nothing else caught it. If this gate cannot see it, it is
        # worthless -- so assert it does.
        broken = catalog()
        for hold in broken["holds"]:
            if hold["scanId"] == "W98":
                hold["surfaceOffsetMeters"] = 0.0239247223
        failures = gate.check(catalog=broken, meshes=meshes())
        self.assertEqual(len(failures), 1, failures)
        self.assertIn("A15", failures[0])
        self.assertIn("W98", failures[0])

    def test_gate_catches_a_hold_missing_from_the_mesh(self):
        trimmed = dict(meshes())
        trimmed.pop("B100", None)
        failures = gate.check(catalog=catalog(), meshes=trimmed)
        self.assertTrue(any("B100" in failure for failure in failures), failures)

    def test_gate_reports_zero_depth_instead_of_dividing_by_zero(self):
        one_hold_catalog = {
            "holds": [
                {
                    "coordinate": "A1",
                    "scanId": "B100",
                    "meshScaleMultiplier": 0.5,
                    "surfaceOffsetMeters": 0.01,
                }
            ]
        }
        flat_mesh = {"B100": (0.0, 0.0, 0.0, 1.0, 1.0, 0.0)}

        failures = gate.check(catalog=one_hold_catalog, meshes=flat_mesh)

        self.assertEqual(len(failures), 1, failures)
        self.assertIn("invalid depth", failures[0])

    def test_the_36_recovered_holds_use_the_reoriented_meshes(self):
        # Provenance inside the mesh asset itself: the 36 recovered holds were built from
        # the re-oriented scans, the original 104 were not. That split is why a descriptor
        # validated against the 104 could still be wrong on the 36.
        names = meshes()
        zplane = [name for name in names if name.lower().startswith("zplane")]
        self.assertEqual(len(zplane), 36)
        self.assertEqual(len(names) - len(zplane), 104)


if __name__ == "__main__":
    unittest.main()
