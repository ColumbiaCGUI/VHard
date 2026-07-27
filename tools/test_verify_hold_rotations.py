import unittest

import verify_hold_rotations


class OfficialSetupAuditTests(unittest.TestCase):
    def test_catalog_matches_official_2016_setup_document(self):
        self.assertEqual(verify_hold_rotations.main(), 0)

    def test_official_table_is_complete_and_unique(self):
        table = verify_hold_rotations.official_by_coordinate()
        self.assertEqual(len(table), 140)
        set_name, number, cardinal = table["A15"]
        self.assertEqual((set_name, number, cardinal), ("setA", 98, "N"))
        self.assertEqual(table["E14"], ("setB", 127, "E"))
        self.assertEqual(table["D10"], ("school", 33, "NW"))


if __name__ == "__main__":
    unittest.main()
