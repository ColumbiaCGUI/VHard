"""Audit moonboard_2016_40.json against the official MoonBoard 2016 hold-setup document.

Ground truth: "MoonBoard Hold Setups 2016" (holdsmarket.com/moonboard-holdsetups_2016.pdf),
page 2 - the authoritative table of hold number -> grid position -> arrow cardinal for
Original School Holds (1-40), Hold Set A (50-99), Hold Set B (100-149).

Checks, per catalog hold:
  1. the coordinate exists in the official table;
  2. holdset/holdNumber (and scanId) identify the same official hold;
  3. rotationDegrees agrees with the official cardinal under a single global
     cardinal->degrees convention (the script solves for the best-fitting convention
     among the 16 candidates: 8 rotations x 2 chiralities, then reports outliers).

Exit code 0 = catalog fully consistent with the official document; 1 = any mismatch.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

CATALOG_PATH = Path(__file__).resolve().parent.parent / "Assets" / "StreamingAssets" / "moonboard_2016_40.json"

CARDINAL_DEGREES = {"N": 0, "NE": 45, "E": 90, "SE": 135, "S": 180, "SW": 225, "W": 270, "NW": 315}
CATALOG_HOLDSETS = {
    "school": ("Original School Holds", "Y"),
    "setA": ("Hold Set A", "W"),
    "setB": ("Hold Set B", "B"),
}

# (hold number, coordinate, cardinal) transcribed from the official PDF page 2.
OFFICIAL = {
    # Original School Holds (1-40)
    "school": [
        (1, "H7", "SE"), (2, "J14", "NW"), (3, "K7", "N"), (4, "D8", "N"), (5, "A16", "NW"),
        (6, "F6", "E"), (7, "K6", "N"), (8, "C9", "W"), (9, "A10", "SW"), (10, "I8", "N"),
        (11, "K12", "N"), (12, "A11", "SE"), (13, "B7", "S"), (14, "D16", "N"), (15, "K10", "S"),
        (16, "G16", "N"), (17, "F8", "N"), (18, "F15", "N"), (19, "G7", "SW"), (20, "H15", "NE"),
        (21, "C12", "N"), (22, "D6", "N"), (23, "D3", "S"), (24, "D13", "N"), (25, "J11", "N"),
        (26, "A13", "N"), (27, "G11", "E"), (28, "H18", "N"), (29, "B4", "SW"), (30, "K8", "N"),
        (31, "C15", "NW"), (32, "H9", "E"), (33, "D10", "NW"), (34, "H14", "W"), (35, "I5", "N"),
        (36, "I12", "SW"), (37, "K13", "N"), (38, "C7", "N"), (39, "C18", "N"), (40, "F9", "N"),
    ],
    # Hold Set A (50-99)
    "setA": [
        (50, "C14", "N"), (51, "D17", "N"), (52, "D9", "NE"), (53, "F7", "NW"), (54, "F12", "E"),
        (55, "G12", "NE"), (56, "B11", "NW"), (57, "J10", "NE"), (58, "J2", "SE"), (59, "E13", "N"),
        (60, "I6", "NE"), (61, "J9", "SE"), (62, "F14", "NW"), (63, "I13", "E"), (64, "E10", "NW"),
        (65, "F10", "NE"), (66, "E15", "NW"), (67, "B8", "N"), (68, "A12", "E"), (69, "I16", "NE"),
        (70, "I11", "N"), (71, "B16", "NW"), (72, "E11", "N"), (73, "H11", "W"), (74, "E7", "S"),
        (75, "D12", "N"), (76, "J8", "N"), (77, "B13", "NW"), (78, "B9", "NE"), (79, "C10", "NE"),
        (80, "B3", "SW"), (81, "G2", "N"), (82, "G18", "W"), (83, "I4", "NE"), (84, "K11", "NW"),
        (85, "A5", "N"), (86, "K5", "N"), (87, "K18", "W"), (88, "G8", "N"), (89, "F5", "N"),
        (90, "G13", "N"), (91, "E18", "N"), (92, "J6", "S"), (93, "D14", "N"), (94, "C11", "W"),
        (95, "C6", "S"), (96, "F16", "S"), (97, "D5", "NW"), (98, "A15", "N"), (99, "B18", "SE"),
    ],
    # Hold Set B (100-149)
    "setB": [
        (100, "H16", "N"), (101, "B15", "N"), (102, "J12", "NE"), (103, "J13", "N"), (104, "K16", "N"),
        (105, "F13", "NW"), (106, "E16", "NW"), (107, "I7", "NE"), (108, "I15", "NW"), (109, "I9", "SE"),
        (110, "E12", "NE"), (111, "H5", "NW"), (112, "G15", "NW"), (113, "J7", "N"), (114, "H12", "NW"),
        (115, "G17", "N"), (116, "E9", "NE"), (117, "J16", "E"), (118, "F11", "NE"), (119, "D11", "SW"),
        (120, "I10", "N"), (121, "K9", "N"), (122, "E8", "N"), (123, "A14", "NW"), (124, "I14", "NW"),
        (125, "C5", "N"), (126, "D15", "NW"), (127, "E14", "E"), (128, "G9", "NE"), (129, "E6", "NW"),
        (130, "J5", "NW"), (131, "H8", "NE"), (132, "I18", "NE"), (133, "A9", "NW"), (134, "G6", "SW"),
        (135, "C8", "NW"), (136, "D18", "N"), (137, "G14", "E"), (138, "C13", "NW"), (139, "A18", "N"),
        (140, "H10", "NE"), (141, "G4", "N"), (142, "B12", "SE"), (143, "C16", "N"), (144, "K14", "NE"),
        (145, "G10", "NE"), (146, "D7", "S"), (147, "B6", "NW"), (148, "B10", "SE"), (149, "H13", "SW"),
    ],
}


def official_by_coordinate():
    table = {}
    for set_name, rows in OFFICIAL.items():
        for number, coordinate, cardinal in rows:
            if coordinate in table:
                raise SystemExit(f"official table duplicates coordinate {coordinate}")
            table[coordinate] = (set_name, number, cardinal)
    if len(table) != 140:
        raise SystemExit(f"official table has {len(table)} coordinates, expected 140")
    return table


def candidate_conventions():
    """Yield (label, fn) mapping cardinal degrees -> catalog degrees."""
    for offset in range(0, 360, 45):
        yield (f"rot = (cardinal + {offset}) % 360", lambda d, o=offset: (d + o) % 360)
        yield (f"rot = ({offset} - cardinal) % 360", lambda d, o=offset: (o - d) % 360)


def main():
    catalog = json.loads(CATALOG_PATH.read_text(encoding="utf-8"))
    holds = catalog["holds"]
    if len(holds) != 140:
        print(f"FAIL: catalog has {len(holds)} holds, expected 140")
        return 1
    table = official_by_coordinate()

    identity_errors = []
    for hold in holds:
        coordinate = hold["coordinate"]
        if coordinate not in table:
            identity_errors.append(f"{coordinate}: not an official 2016 position")
            continue
        set_name, number, _ = table[coordinate]
        expected_holdset, expected_scan_prefix = CATALOG_HOLDSETS[set_name]
        if hold.get("holdset") != expected_holdset:
            identity_errors.append(
                f"{coordinate}: catalog holdset {hold.get('holdset')!r} != official {expected_holdset!r}")
        if int(hold["holdNumber"]) != number:
            identity_errors.append(
                f"{coordinate}: catalog holdNumber {hold['holdNumber']} != official {number}")
        scan_id = hold.get("scanId", "")
        if not scan_id.startswith(expected_scan_prefix):
            identity_errors.append(
                f"{coordinate}: scanId {scan_id!r} does not match official set prefix "
                f"{expected_scan_prefix!r}")
        scan_digits = "".join(ch for ch in scan_id if ch.isdigit())
        if scan_digits and int(scan_digits) != number:
            identity_errors.append(
                f"{coordinate}: scanId {scan_id} != official hold number {number}")

    best_label, best_fn, best_hits = None, None, -1
    for label, fn in candidate_conventions():
        hits = sum(
            1 for hold in holds
            if hold["coordinate"] in table
            and hold["rotationDegrees"] == fn(CARDINAL_DEGREES[table[hold["coordinate"]][2]])
        )
        if hits > best_hits:
            best_label, best_fn, best_hits = label, fn, hits
    if best_fn is None:
        raise RuntimeError("No cardinal rotation convention was evaluated.")

    rotation_errors = []
    for hold in holds:
        coordinate = hold["coordinate"]
        if coordinate not in table:
            continue
        cardinal = table[coordinate][2]
        expected = best_fn(CARDINAL_DEGREES[cardinal])
        if hold["rotationDegrees"] != expected:
            rotation_errors.append(
                f"{coordinate}: catalog {hold['rotationDegrees']} deg, official {cardinal} "
                f"-> expected {expected} deg under '{best_label}'")

    print(f"convention: {best_label}  ({best_hits}/140 agree)")
    for line in identity_errors:
        print("IDENTITY:", line)
    for line in rotation_errors:
        print("ROTATION:", line)
    if not identity_errors and not rotation_errors:
        print("OK: all 140 holds match the official 2016 setup document (identity + rotation).")
        return 0
    print(f"FAIL: {len(identity_errors)} identity + {len(rotation_errors)} rotation mismatches")
    return 1


if __name__ == "__main__":
    sys.exit(main())
