import json
import os
import pathlib
import unittest
from unittest import mock

import generate_moonboard_catalog as gen


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN_CATALOG = PROJECT_ROOT / "Assets/StreamingAssets/moonboard_2016_40.json"
ESTIMATION_CATALOG = PROJECT_ROOT / "Assets/StreamingAssets/moonboard_2016_40_estimation.json"
SOURCE_ROOT = os.environ.get("MOONBOARD_SOURCE_ROOT")
ESTIMATION_SOURCE = os.environ.get("MOONBOARD_ESTIMATION_SOURCE")


class MoonBoardEstimationCatalogGeneratorTests(unittest.TestCase):
    def test_selection_filters_and_uses_repeat_then_api_id_order(self):
        records = []
        expected_ids = {}
        expected_pool_sizes = {}

        def make_record(grade, api_id, repeats, **overrides):
            record = {
                "grade": grade,
                "dateDeleted": None,
                "dateInserted": "2021-02-01T00:00:00",
                "holdsetup": {"description": "MoonBoard 2016"},
                "apiId": api_id,
                "name": f"Problem {api_id}",
                "userGrade": grade,
                "downgraded": False,
                "upgraded": False,
                "repeats": repeats,
                "moves": [{"description": "A1"}],
            }
            record.update(overrides)
            return record

        for grade_index, grade in enumerate(gen.ESTIMATION_GRADES):
            base = 10000 + grade_index * 10
            records.extend(
                [
                    make_record(grade, base + 3, 200),
                    make_record(grade, base + 2, 150),
                    make_record(grade, base + 1, 150),
                    make_record(grade, base + 4, 100),
                ]
            )
            expected_ids[grade] = (base + 3, base + 1, base + 2)
            expected_pool_sizes[grade] = 4

        records.extend(
            [
                make_record("6B+", 10991, 9999, dateDeleted="2022-01-01"),
                make_record("6B+", 10992, 9999, dateInserted="2020-12-31"),
                make_record("6B+", 10993, 9999, moves=[{"description": "B2"}]),
                make_record("6B+", 10994, 9999, userGrade="6C"),
            ]
        )

        with mock.patch.object(gen, "ESTIMATION_EXPECTED_IDS", expected_ids), \
             mock.patch.object(gen, "ESTIMATION_EXPECTED_POOL_SIZES", expected_pool_sizes):
            selected = gen.select_estimation_records(records, {"A1"})

        self.assertEqual(
            {
                grade: tuple(record["apiId"] for record in selected[grade])
                for grade in gen.ESTIMATION_GRADES
            },
            expected_ids,
        )

    def test_shipped_selection_sets_and_practice_are_pinned(self):
        catalog = json.loads(ESTIMATION_CATALOG.read_text(encoding="utf-8"))
        selected = {
            grade: tuple(
                problem["apiId"]
                for problem in catalog["problems"]
                if problem["grade"] == grade
            )
            for grade in gen.ESTIMATION_GRADES
        }

        self.assertEqual(selected, gen.ESTIMATION_EXPECTED_IDS)
        self.assertEqual(
            [item["problemIds"] for item in catalog["estimationSets"]],
            [
                [386882, 404248, 452771, 395431],
                [386902, 389660, 424830, 441349],
                [389008, 397202, 388011, 387486],
            ],
        )
        self.assertEqual(catalog["practiceProblem"]["apiId"], gen.PRACTICE_EXPECTED_ID)
        self.assertEqual(catalog["practiceProblem"]["name"], "WUTHERING HEIGHTS")
        self.assertEqual(
            catalog["provenance"]["sourceArchiveSha256"],
            gen.ESTIMATION_SOURCE_SHA256,
        )
        self.assertEqual(
            catalog["provenance"]["sourceFileSha256"],
            gen.ESTIMATION_INNER_SHA256,
        )
        self.assertEqual(
            catalog["provenance"]["sourceArchiveUrl"],
            "https://drive.google.com/file/d/1Zoqsmc15IHtGekY99xazemxjGGx07Kep/view",
        )

    def test_shipped_artifact_uses_deterministic_serialization(self):
        catalog = json.loads(ESTIMATION_CATALOG.read_text(encoding="utf-8"))
        self.assertEqual(gen.catalog_bytes(catalog), ESTIMATION_CATALOG.read_bytes())

    @unittest.skipUnless(
        SOURCE_ROOT and ESTIMATION_SOURCE,
        "set MOONBOARD_SOURCE_ROOT and MOONBOARD_ESTIMATION_SOURCE for source regeneration",
    )
    def test_pinned_sources_regenerate_both_catalogs_byte_identically(self):
        source_root = pathlib.Path(SOURCE_ROOT or "")
        estimation_source = pathlib.Path(ESTIMATION_SOURCE or "")

        main = gen.build_catalog(source_root, PROJECT_ROOT)
        first = gen.build_estimation_catalog(estimation_source, source_root, main)
        second = gen.build_estimation_catalog(estimation_source, source_root, main)

        self.assertEqual(gen.catalog_bytes(main), MAIN_CATALOG.read_bytes())
        self.assertEqual(gen.catalog_bytes(first), ESTIMATION_CATALOG.read_bytes())
        self.assertEqual(gen.catalog_bytes(first), gen.catalog_bytes(second))

    def test_paired_generation_builds_both_catalogs_before_writing(self):
        with mock.patch.object(gen, "build_catalog", return_value={"catalog": "main"}), \
             mock.patch.object(
                 gen,
                 "build_estimation_catalog",
                 side_effect=ValueError("invalid estimation source"),
             ), \
             mock.patch.object(gen, "write_catalog") as write_catalog, \
             mock.patch(
                 "sys.argv",
                 [
                     "generate_moonboard_catalog.py",
                     "--source-root", "source",
                     "--output", "main.json",
                     "--estimation-source", "estimation.zip",
                     "--estimation-output", "estimation.json",
                 ],
             ):
            with self.assertRaisesRegex(ValueError, "invalid estimation source"):
                gen.main()

        write_catalog.assert_not_called()


if __name__ == "__main__":
    unittest.main()
