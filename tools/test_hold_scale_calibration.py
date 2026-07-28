"""Pins the shipped catalog artifact to the generator's scale calibration table.

The upstream e-sr/moonboard source checkout is not present on every dev machine, so
the artifact cannot always be regenerated end to end. These tests assert that
Assets/StreamingAssets/moonboard_2016_40.json is exactly what build_catalog() would
emit from HOLD_SCALE_CALIBRATION, so the table and the artifact cannot drift apart.
"""
import json
import pathlib
import unittest

import generate_moonboard_catalog as gen

CATALOG = pathlib.Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/moonboard_2016_40.json"


def load():
    return json.loads(CATALOG.read_text(encoding="utf-8"))


class HoldScaleCalibrationTests(unittest.TestCase):
    def test_artifact_is_schema_3(self):
        self.assertEqual(load()["schemaVersion"], 3)

    def test_every_hold_matches_the_generator_table(self):
        catalog = load()
        self.assertEqual(len(catalog["holds"]), 140)
        self.assertEqual(len(gen.HOLD_SCALE_CALIBRATION), 140)
        for hold in catalog["holds"]:
            scan_id = hold["scanId"]
            multiplier, source = gen.HOLD_SCALE_CALIBRATION[scan_id]
            self.assertEqual(hold["meshScaleMultiplier"], multiplier, scan_id)
            self.assertEqual(hold["scaleCalibrationSource"], source, scan_id)
            # The seating offset is half the hold's depth measured on the NORMALISED mesh,
            # so it must carry the same scale factor as the mesh it seats.
            self.assertAlmostEqual(
                hold["surfaceOffsetMeters"],
                gen.SURFACE_OFFSETS_METERS[scan_id] * multiplier,
                places=12,
                msg=scan_id,
            )

    def test_calibration_sources_are_declared_and_counted(self):
        catalog = load()
        counts = {}
        for hold in catalog["holds"]:
            source = hold["scaleCalibrationSource"]
            self.assertIn(source, gen.SCALE_CALIBRATION_SOURCES, hold["coordinate"])
            counts[source] = counts.get(source, 0) + 1
        # All 140 are exact, measured off the base-plane-aligned scan. The weaker sources stay
        # declared in the schema so any future hold that cannot be measured exactly must label
        # itself rather than pass as exact.
        self.assertEqual(counts, {"metric-scan": 140})

    def test_multipliers_stay_inside_the_declared_band(self):
        for hold in load()["holds"]:
            self.assertGreaterEqual(
                hold["meshScaleMultiplier"], gen.MIN_SCALE_MULTIPLIER, hold["coordinate"]
            )
            self.assertLessEqual(
                hold["meshScaleMultiplier"], gen.MAX_SCALE_MULTIPLIER, hold["coordinate"]
            )

    def test_holds_are_no_longer_normalised_to_the_grid_cell(self):
        # The bug this calibration fixes: every hold rendered at the same ~200 mm cell.
        multipliers = {hold["meshScaleMultiplier"] for hold in load()["holds"]}
        self.assertGreater(len(multipliers), 100)
        self.assertNotIn(1.0, multipliers)

    def test_no_hold_falls_back_to_the_weaker_descriptor(self):
        weak = sorted(
            hold["scanId"]
            for hold in load()["holds"]
            if hold["scaleCalibrationSource"] == "metric-scan-unaligned"
        )
        self.assertEqual(weak, [])

    def test_no_hold_falls_back_to_a_photo_estimate(self):
        estimated = sorted(
            hold["scanId"]
            for hold in load()["holds"]
            if hold["scaleCalibrationSource"] == "movement-harlem-photo"
        )
        self.assertEqual(estimated, [])


if __name__ == "__main__":
    unittest.main()
