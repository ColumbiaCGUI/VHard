"""Tests for the hold-seating gate, including proof that it bites on a real regression."""
import json
import unittest

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


@unittest.skipUnless(
    gate.MESH.exists() and gate.MESH.read_bytes()[:18] == b"Kaydara FBX Binary",
    "aggregate FBX absent or an unmaterialised Git LFS pointer; run `git lfs pull`",
)
class HoldSeatingGateTests(unittest.TestCase):
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
