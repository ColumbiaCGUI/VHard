#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path


SOURCE_REPOSITORY = "https://github.com/e-sr/moonboard"
SOURCE_REVISION = "ccd78f587ab189acea6dd7ce8a6d4f086f65db69"
ROUTE_SOURCE_IDS = ("19215", "21329", "170190")
HOLDSET_PREFIXES = {
    "Original School Holds": "Y",
    "Hold Set A": "W",
    "Hold Set B": "B",
}
SURFACE_OFFSETS_METERS = {
    "W81": 0.0683269660,  # G2
    "W58": 0.0318372860,  # J2
    "W80": 0.0357312913,  # B3
    "Y23": 0.0424390640,  # D3
    "Y29": 0.0284666247,  # B4
    "B141": 0.0333707550,  # G4
    "W83": 0.0238698620,  # I4
    "W85": 0.0212542085,  # A5
    "B125": 0.0195552900,  # C5
    "W97": 0.0247888105,  # D5
    "W89": 0.0314177430,  # F5
    "B111": 0.0377797639,  # H5
    "Y35": 0.0375823198,  # I5
    "B130": 0.0289235847,  # J5
    "W86": 0.0466854910,  # K5
    "B147": 0.0291888775,  # B6
    "W95": 0.0387814050,  # C6
    "Y22": 0.0237552550,  # D6
    "B129": 0.0332738204,  # E6
    "Y6": 0.0390635400,  # F6
    "B134": 0.0294158967,  # G6
    "W60": 0.0184336100,  # I6
    "W92": 0.0363062216,  # J6
    "Y7": 0.0317103980,  # K6
    "Y13": 0.0303340187,  # B7
    "Y38": 0.0391796013,  # C7
    "B146": 0.0337971603,  # D7
    "W74": 0.0478821030,  # E7
    "W53": 0.0383106773,  # F7
    "Y19": 0.0280111624,  # G7
    "Y1": 0.0364153019,  # H7
    "B107": 0.0430942975,  # I7
    "B113": 0.0387089034,  # J7
    "Y3": 0.0347501604,  # K7
    "W67": 0.0296905675,  # B8
    "B135": 0.0277674962,  # C8
    "Y4": 0.0350176078,  # D8
    "B122": 0.0350859520,  # E8
    "Y17": 0.0361809824,  # F8
    "W88": 0.0277150684,  # G8
    "B131": 0.0259179925,  # H8
    "Y10": 0.0257112340,  # I8
    "W76": 0.0320088622,  # J8
    "Y30": 0.0288737920,  # K8
    "B133": 0.0431260486,  # A9
    "W78": 0.0212277430,  # B9
    "Y8": 0.0387837869,  # C9
    "W52": 0.0405496495,  # D9
    "B116": 0.0227781660,  # E9
    "Y40": 0.0495982000,  # F9
    "B128": 0.0406838310,  # G9
    "Y32": 0.0467938050,  # H9
    "B109": 0.0434763490,  # I9
    "W61": 0.0213715290,  # J9
    "B121": 0.0304231290,  # K9
    "Y9": 0.0235014360,  # A10
    "B148": 0.0299015550,  # B10
    "W79": 0.0264890280,  # C10
    "Y33": 0.0944445300,  # D10
    "W64": 0.0286201730,  # E10
    "W65": 0.0202353450,  # F10
    "B145": 0.0332069288,  # G10
    "B140": 0.0319003650,  # H10
    "B120": 0.0220872430,  # I10
    "W57": 0.0228353710,  # J10
    "Y15": 0.0371347112,  # K10
    "Y12": 0.0264958760,  # A11
    "W56": 0.0246806360,  # B11
    "W94": 0.0239938200,  # C11
    "B119": 0.0227189510,  # D11
    "W72": 0.0251544900,  # E11
    "B118": 0.0263243640,  # F11
    "Y27": 0.0335212369,  # G11
    "W73": 0.0219419220,  # H11
    "W70": 0.0460881490,  # I11
    "Y25": 0.0283209200,  # J11
    "W84": 0.0227295790,  # K11
    "W68": 0.0403908610,  # A12
    "B142": 0.0401813660,  # B12
    "Y21": 0.0443189710,  # C12
    "W75": 0.0509412850,  # D12
    "B110": 0.0241601120,  # E12
    "W54": 0.0348133152,  # F12
    "W55": 0.0315258057,  # G12
    "B114": 0.0242997380,  # H12
    "Y36": 0.0316103963,  # I12
    "B102": 0.0232923595,  # J12
    "Y11": 0.0297004660,  # K12
    "Y26": 0.0417964900,  # A13
    "W77": 0.0290132533,  # B13
    "B138": 0.0260345300,  # C13
    "Y24": 0.0605782670,  # D13
    "W59": 0.0327541787,  # E13
    "B105": 0.0285049730,  # F13
    "W90": 0.0268047390,  # G13
    "B149": 0.0240564120,  # H13
    "W63": 0.0275733240,  # I13
    "B103": 0.0308309400,  # J13
    "Y37": 0.0394819410,  # K13
    "B123": 0.0195050930,  # A14
    "W50": 0.0368168173,  # C14
    "W93": 0.0370212985,  # D14
    "B127": 0.0199557380,  # E14
    "W62": 0.0455224260,  # F14
    "B137": 0.0408411850,  # G14
    "Y34": 0.0351769743,  # H14
    "B124": 0.0324625974,  # I14
    "Y2": 0.0244668390,  # J14
    "B144": 0.0311225244,  # K14
    "W98": 0.0581315385,  # A15 - normalised half-depth like every other hold; this
                              # entry previously held TRUE half-depth in metres, so the
                              # multiplier scaled it a second time (-33.7% offset error).
    "B101": 0.0296722090,  # B15
    "Y31": 0.0240316290,  # C15
    "B126": 0.0250975900,  # D15
    "W66": 0.0322004366,  # E15
    "Y18": 0.0295406706,  # F15
    "B112": 0.0335075888,  # G15
    "Y20": 0.0358350944,  # H15
    "B108": 0.0207032150,  # I15
    "Y5": 0.0242708660,  # A16
    "W71": 0.0174126500,  # B16
    "B143": 0.0424864660,  # C16
    "Y14": 0.0350290756,  # D16
    "B106": 0.0533988280,  # E16
    "W96": 0.0421936210,  # F16
    "Y16": 0.0331499982,  # G16
    "B100": 0.0347684804,  # H16
    "W69": 0.0295039905,  # I16
    "B117": 0.0327791456,  # J16
    "B104": 0.0494955290,  # K16
    "W51": 0.0356905233,  # D17
    "B115": 0.0297150813,  # G17
    "B139": 0.0294090543,  # A18
    "W99": 0.0243279620,  # B18
    "Y39": 0.0325464416,  # C18
    "B136": 0.0409974415,  # D18
    "W91": 0.0470000840,  # E18
    "W82": 0.0440680600,  # G18
    "Y28": 0.0482480930,  # H18
    "B132": 0.0429839534,  # I18
    "W87": 0.0483429580,  # K18
}
# The aggregate FBX normalises every scan to roughly a 200 mm grid cell, so each child
# renders at NORMALIZED_MESH_SCALE regardless of the hold's true size. Verified in the live
# editor: every child sits at localScale 100 and its rendered bounds equal the normalised
# dimensions in tools' axis metrics (G2 -> 200.00 x 172.42 x 136.65 mm).
# meshScaleMultiplier restores physical size: renderedSize = normalisedSize * multiplier.
#
# "metric-scan" (104 holds): multiplier = sourceExtentMm / currentExtentMm, averaged over the
#   three axes. The source and normalised meshes differ by a pure uniform scale -- the three
#   per-axis ratios agree to 0.012% median -- so the multiplier is exact and unambiguous.
#   The legacy aggregate child localScale of 0.7 is deliberately NOT part of this formula:
#   including it would enlarge every hold by 1.4286x, which an independent photogrammetric
#   rectification of the physical board (1 px = 1 mm) contradicts. That rectification agrees
#   with the source dimensions at a median ratio of 1.0000 over 90 holds, and with the 0.7
#   the largest hold would measure 229 mm on a 200 mm grid pitch, which is impossible.
# "metric-scan" also now covers 35 holds whose geometry was absent locally but whose raw
#   laser scans were recovered from the team Drive on 2026-07-27 and measured directly. For
#   those the multiplier is scanMaxRadiusMm / (normalisedMaxRadius * 1000) -- a rotation-
#   invariant descriptor, required because a raw scanner-frame STL is NOT axis-aligned with
#   the normalised mesh. Validated end to end on 5 holds with trusted dimensions (B100, B112,
#   B140, B123, B136): median error 0.73%, worst 2.18% -- roughly 9x better than the photo
#   estimator it replaced. Flagged for a human eyeball because they exceed the known-subset
#   size envelope (legitimate: the recovered holds are different SKUs): B138 (187.6 mm) and
#   B141 (169.9 mm).
# "movement-harlem-photo" (1 hold: B127 / E14): the ONLY remaining estimate. Its Creality
#   source file was deleted, so no metric scan exists to measure -- it is also the hold that
#   renders pale on the board contact sheet. The multiplier comes
#   from the same rectified photograph of the physical board. Cross-validated against the 104
#   knowns, this estimator's error by hold set is: Set B 6.5% median / 21.6% worst (n=18),
#   Original School 4.2% / 19.9% (n=29). 35 of these 36 belong to those two sets. The single
#   exception is W98, a Set A (white) hold; white-on-grey segmentation is the estimator's weak
#   case (5.0% median but 82% worst, n=43), so W98 is the least certain record here and is
#   also flagged for rescanning in the team's scan record. Every photo-derived multiplier
#   falls inside the exact-scan band and implies a physically plausible hold, but these 36
#   remains an estimate, not a measurement -- replace it once B127 is rescanned.
NORMALIZED_MESH_SCALE = 100.0
MIN_SCALE_MULTIPLIER = 0.15
MAX_SCALE_MULTIPLIER = 1.50
SCALE_CALIBRATION_SOURCES = ("metric-scan", "metric-scan-unaligned", "movement-harlem-photo")
HOLD_SCALE_CALIBRATION = {
    "W81": (0.402781267, "metric-scan"),  # G2
    "W58": (0.534399224, "metric-scan"),  # J2
    "W80": (0.711280066, "metric-scan"),  # B3
    "Y23": (0.320772347, "metric-scan"),  # D3
    "Y29": (0.482812656, "metric-scan"),  # B4
    "B141": (0.845071513, "metric-scan"),  # G4
    "W83": (0.974671944, "metric-scan"),  # I4
    "W85": (0.730033846, "metric-scan"),  # A5
    "B125": (0.620536027, "metric-scan"),  # C5
    "W97": (0.859481370, "metric-scan"),  # D5
    "W89": (0.713183173, "metric-scan"),  # F5
    "B111": (0.424710929, "metric-scan"),  # H5
    "Y35": (0.554730843, "metric-scan"),  # I5
    "B130": (0.881061245, "metric-scan"),  # J5
    "W86": (0.556571144, "metric-scan"),  # K5
    "B147": (0.722229623, "metric-scan"),  # B6
    "W95": (0.608542761, "metric-scan"),  # C6
    "Y22": (0.445441102, "metric-scan"),  # D6
    "B129": (0.599985034, "metric-scan"),  # E6
    "Y6": (0.291977324, "metric-scan"),  # F6
    "B134": (0.775152755, "metric-scan"),  # G6
    "W60": (0.657467003, "metric-scan"),  # I6
    "W92": (0.461290417, "metric-scan"),  # J6
    "Y7": (0.295436029, "metric-scan"),  # K6
    "Y13": (0.338531460, "metric-scan"),  # B7
    "Y38": (0.458190246, "metric-scan"),  # C7
    "B146": (0.621559713, "metric-scan"),  # D7
    "W74": (0.602323031, "metric-scan"),  # E7
    "W53": (0.507582467, "metric-scan"),  # F7
    "Y19": (0.390264004, "metric-scan"),  # G7
    "Y1": (0.368080045, "metric-scan"),  # H7
    "B107": (0.523779278, "metric-scan"),  # I7
    "B113": (0.461038203, "metric-scan"),  # J7
    "Y3": (0.341255013, "metric-scan"),  # K7
    "W67": (0.512344480, "metric-scan"),  # B8
    "B135": (0.872497675, "metric-scan"),  # C8
    "Y4": (0.340103297, "metric-scan"),  # D8
    "B122": (0.551241283, "metric-scan"),  # E8
    "Y17": (0.348786294, "metric-scan"),  # F8
    "W88": (0.719157927, "metric-scan"),  # G8
    "B131": (0.841662765, "metric-scan"),  # H8
    "Y10": (0.366017043, "metric-scan"),  # I8
    "W76": (0.523982846, "metric-scan"),  # J8
    "Y30": (0.373698743, "metric-scan"),  # K8
    "B133": (0.576241670, "metric-scan"),  # A9
    "W78": (0.861472751, "metric-scan"),  # B9
    "Y8": (0.306372760, "metric-scan"),  # C9
    "W52": (0.505410647, "metric-scan"),  # D9
    "B116": (0.551617867, "metric-scan"),  # E9
    "Y40": (0.334811301, "metric-scan"),  # F9
    "B128": (0.529249528, "metric-scan"),  # G9
    "Y32": (0.371068994, "metric-scan"),  # H9
    "B109": (0.412177330, "metric-scan"),  # I9
    "W61": (0.721413135, "metric-scan"),  # J9
    "B121": (0.438418234, "metric-scan"),  # K9
    "Y9": (0.422103253, "metric-scan"),  # A10
    "B148": (0.801630551, "metric-scan"),  # B10
    "W79": (0.762676482, "metric-scan"),  # C10
    "Y33": (0.418324422, "metric-scan"),  # D10
    "W64": (0.658398272, "metric-scan"),  # E10
    "W65": (0.682601099, "metric-scan"),  # F10
    "B145": (0.542206100, "metric-scan"),  # G10
    "B140": (0.873043270, "metric-scan"),  # H10
    "B120": (0.590557419, "metric-scan"),  # I10
    "W57": (0.545719045, "metric-scan"),  # J10
    "Y15": (0.323185774, "metric-scan"),  # K10
    "Y12": (0.459882042, "metric-scan"),  # A11
    "W56": (0.683585585, "metric-scan"),  # B11
    "W94": (0.563333118, "metric-scan"),  # C11
    "B119": (0.660384068, "metric-scan"),  # D11
    "W72": (0.550294812, "metric-scan"),  # E11
    "B118": (0.620535332, "metric-scan"),  # F11
    "Y27": (0.360342191, "metric-scan"),  # G11
    "W73": (0.776349917, "metric-scan"),  # H11
    "W70": (0.426350604, "metric-scan"),  # I11
    "Y25": (0.391628537, "metric-scan"),  # J11
    "W84": (0.731873496, "metric-scan"),  # K11
    "W68": (0.503697375, "metric-scan"),  # A12
    "B142": (0.577176873, "metric-scan"),  # B12
    "Y21": (0.302337676, "metric-scan"),  # C12
    "W75": (0.475408243, "metric-scan"),  # D12
    "B110": (0.650492188, "metric-scan"),  # E12
    "W54": (0.514876273, "metric-scan"),  # F12
    "W55": (0.585639232, "metric-scan"),  # G12
    "B114": (0.514212199, "metric-scan"),  # H12
    "Y36": (0.554864071, "metric-scan"),  # I12
    "B102": (0.557911010, "metric-scan"),  # J12
    "Y11": (0.392487152, "metric-scan"),  # K12
    "Y26": (0.319417867, "metric-scan"),  # A13
    "W77": (0.751917608, "metric-scan"),  # B13
    "B138": (0.926801680, "metric-scan"),  # C13
    "Y24": (0.277058888, "metric-scan"),  # D13
    "W59": (0.481681311, "metric-scan"),  # E13
    "B105": (0.462240919, "metric-scan"),  # F13
    "W90": (0.757434180, "metric-scan"),  # G13
    "B149": (1.008292451, "metric-scan"),  # H13
    "W63": (0.572654816, "metric-scan"),  # I13
    "B103": (0.473641268, "metric-scan"),  # J13
    "Y37": (0.373124497, "metric-scan"),  # K13
    "B123": (0.811547658, "metric-scan"),  # A14
    "W50": (0.363169954, "metric-scan"),  # C14
    "W93": (0.552061801, "metric-scan"),  # D14
    "B127": (0.640229410, "metric-scan"),  # E14
    "W62": (0.504534941, "metric-scan"),  # F14
    "B137": (0.719880919, "metric-scan"),  # G14
    "Y34": (0.419348726, "metric-scan"),  # H14
    "B124": (0.734676362, "metric-scan"),  # I14
    "Y2": (0.425105564, "metric-scan"),  # J14
    "B144": (0.683303700, "metric-scan"),  # K14
    "W98": (0.665173000, "metric-scan"),  # A15
    "B101": (0.416924191, "metric-scan"),  # B15
    "Y31": (0.563930004, "metric-scan"),  # C15
    "B126": (0.803702031, "metric-scan"),  # D15
    "W66": (0.578755945, "metric-scan"),  # E15
    "Y18": (0.371858101, "metric-scan"),  # F15
    "B112": (0.561564416, "metric-scan"),  # G15
    "Y20": (0.393831080, "metric-scan"),  # H15
    "B108": (0.661132872, "metric-scan"),  # I15
    "Y5": (0.446405053, "metric-scan"),  # A16
    "W71": (0.931728202, "metric-scan"),  # B16
    "B143": (0.489693149, "metric-scan"),  # C16
    "Y14": (0.302686645, "metric-scan"),  # D16
    "B106": (0.308984870, "metric-scan"),  # E16
    "W96": (0.553566968, "metric-scan"),  # F16
    "Y16": (0.287543702, "metric-scan"),  # G16
    "B100": (0.407341205, "metric-scan"),  # H16
    "W69": (0.733720030, "metric-scan"),  # I16
    "B117": (0.448225217, "metric-scan"),  # J16
    "B104": (0.321602711, "metric-scan"),  # K16
    "W51": (0.495693809, "metric-scan"),  # D17
    "B115": (0.530847662, "metric-scan"),  # G17
    "B139": (0.787681988, "metric-scan"),  # A18
    "W99": (1.063472638, "metric-scan"),  # B18
    "Y39": (0.530175139, "metric-scan"),  # C18
    "B136": (0.663077420, "metric-scan"),  # D18
    "W91": (0.635982140, "metric-scan"),  # E18
    "W82": (0.574533696, "metric-scan"),  # G18
    "Y28": (0.331822687, "metric-scan"),  # H18
    "B132": (0.610225287, "metric-scan"),  # I18
    "W87": (0.562586813, "metric-scan"),  # K18
}
MESH_FRAME_CORRECTIONS = {
    # Approved by-eye W98 orientation from 1b9df47, expressed after the FBX -90-degree X basis.
    "W98": {"x": 0.0, "y": -0.92387953, "z": 0.0, "w": 0.38268343},
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def record_sha256(record: dict) -> str:
    encoded = json.dumps(record, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def coordinate_key(coordinate: str) -> tuple[int, int]:
    return int(coordinate[1:]), ord(coordinate[0]) - ord("A")


def build_catalog(source_root: Path, project_root: Path) -> dict:
    problems_path = source_root / "problems/fetch/moonboard_problems_setup_2016.json"
    holds_path = source_root / "problems/holds_tmp/Moonboard2016.tmp"
    dimensions_path = source_root / "doc/Moonboard.xlsx"
    mesh_path = project_root / "Assets/Resources/New_Decimated_Holds.fbx"

    problems = json.loads(problems_path.read_text(encoding="utf-8"))
    holdsets = json.loads(holds_path.read_text(encoding="utf-8"))["Data"]

    holds = []
    coordinates = set()
    for holdset in holdsets:
        holdset_name = holdset["Description"]
        prefix = HOLDSET_PREFIXES[holdset_name]
        for source_hold in holdset["Holds"]:
            location = source_hold["Location"]
            coordinate = location["Description"].upper()
            if coordinate in coordinates:
                raise ValueError(f"Duplicate MoonBoard coordinate: {coordinate}")
            coordinates.add(coordinate)
            scan_id = prefix + source_hold["Number"]
            # The seating offset was measured as half the NORMALISED mesh depth, so it is
            # expressed in normalised units and must shrink with the mesh by the same factor.
            scale_multiplier, scale_source = HOLD_SCALE_CALIBRATION[scan_id]
            hold = {
                "coordinate": coordinate,
                "scanId": scan_id,
                "holdset": holdset_name,
                "holdNumber": source_hold["Number"],
                "rotationDegrees": int(location["Rotation"]),
                "surfaceOffsetMeters": SURFACE_OFFSETS_METERS[scan_id] * scale_multiplier,
                "meshScaleMultiplier": scale_multiplier,
                "scaleCalibrationSource": scale_source,
                "sourceHoldId": str(source_hold["Id"]),
            }
            if scan_id in MESH_FRAME_CORRECTIONS:
                hold["hasMeshFrameCorrection"] = True
                hold["meshFrameCorrection"] = MESH_FRAME_CORRECTIONS[scan_id]
            holds.append(hold)
    holds.sort(key=lambda hold: coordinate_key(hold["coordinate"]))
    if len(holds) != 140:
        raise ValueError(f"Expected 140 active 2016 holds, found {len(holds)}")
    scan_ids = {hold["scanId"] for hold in holds}
    calibrated_scan_ids = set(SURFACE_OFFSETS_METERS)
    if scan_ids != calibrated_scan_ids:
        missing = sorted(scan_ids - calibrated_scan_ids)
        unknown = sorted(calibrated_scan_ids - scan_ids)
        raise ValueError(f"Hold calibration mismatch; missing={missing}, unknown={unknown}")
    scaled_scan_ids = set(HOLD_SCALE_CALIBRATION)
    if scan_ids != scaled_scan_ids:
        missing = sorted(scan_ids - scaled_scan_ids)
        unknown = sorted(scaled_scan_ids - scan_ids)
        raise ValueError(f"Hold scale mismatch; missing={missing}, unknown={unknown}")
    for hold in holds:
        multiplier = hold["meshScaleMultiplier"]
        if not MIN_SCALE_MULTIPLIER <= multiplier <= MAX_SCALE_MULTIPLIER:
            raise ValueError(
                f"Hold {hold['coordinate']} scale multiplier {multiplier} leaves "
                f"[{MIN_SCALE_MULTIPLIER}, {MAX_SCALE_MULTIPLIER}]"
            )
        if hold["scaleCalibrationSource"] not in SCALE_CALIBRATION_SOURCES:
            raise ValueError(
                f"Hold {hold['coordinate']} has unknown scale source "
                f"{hold['scaleCalibrationSource']!r}"
            )
    if not MESH_FRAME_CORRECTIONS.keys() <= scan_ids:
        raise ValueError("Mesh-frame correction references an unknown physical scan")

    routes = []
    for source_id in ROUTE_SOURCE_IDS:
        source_route = problems[source_id]
        if source_route["Holdsetup"]["Description"] != "MoonBoard 2016":
            raise ValueError(f"Problem {source_id} is not a MoonBoard 2016 problem")
        if source_route["Grade"] != "6B+" or not source_route["IsBenchmark"]:
            raise ValueError(f"Problem {source_id} is not a 6B+ benchmark")
        if len(source_route["Moves"]) != 7:
            raise ValueError(f"Problem {source_id} does not contain seven holds")
        starts = [move for move in source_route["Moves"] if move["IsStart"]]
        finishes = [move for move in source_route["Moves"] if move["IsEnd"]]
        if len(starts) != 2 or len(finishes) != 1:
            raise ValueError(f"Problem {source_id} does not have two starts and one finish")

        moves = []
        for index, move in enumerate(source_route["Moves"]):
            coordinate = move["Description"].upper()
            if coordinate not in coordinates:
                raise ValueError(f"Problem {source_id} references vacant coordinate {coordinate}")
            role = "start" if move["IsStart"] else "finish" if move["IsEnd"] else "move"
            moves.append(
                {
                    "sequence": index,
                    "coordinate": coordinate,
                    "role": role,
                    "sourceMoveId": str(move["Id"]),
                }
            )

        setter = source_route["Setter"]
        routes.append(
            {
                "id": f"MB2016-{source_id}",
                "sourceProblemId": source_id,
                "name": source_route["Name"],
                "grade": source_route["Grade"],
                "isBenchmark": True,
                "method": source_route["Method"],
                "repeatsAtArchive": int(source_route["Repeats"]),
                "setter": setter["Nickname"],
                "lockedForStudy": True,
                "selectionMatch": "6B+ benchmark; 7 holds; 2 distinct start holds",
                "sourceRecordSha256": record_sha256(source_route),
                "moves": moves,
            }
        )

    for index, route in enumerate(routes):
        route_coordinates = {move["coordinate"] for move in route["moves"]}
        for other in routes[index + 1 :]:
            shared = route_coordinates & {move["coordinate"] for move in other["moves"]}
            if len(shared) > 1:
                raise ValueError(
                    f"Study routes {route['id']} and {other['id']} share {len(shared)} holds"
                )

    main_surface_length = 3.6401785670486002
    return {
        "schemaVersion": 3,
        "setupId": "moonboard-2016",
        "setupName": "MoonBoard 2016",
        "overhangAngleDegrees": 40,
        "archiveDate": "2026-07-27",
        "geometry": {
            "boardWidthMeters": 2.44,
            "totalHeightMeters": 3.15,
            "horizontalOverhangMeters": 2.35,
            "mainSurfaceLengthMeters": main_surface_length,
            "kickerHeightMeters": 0.37,
            "gridSpacingMeters": 0.20,
            "mainFirstRowOffsetMeters": (main_surface_length - 15 * 0.20) / 2,
            "row1KickerHeightMeters": 0.10,
            "row2KickerHeightMeters": 0.30,
            "columns": 11,
            "rows": 18,
        },
        "provenance": {
            "sourceRepository": SOURCE_REPOSITORY,
            "sourceRevision": SOURCE_REVISION,
            "problemsSha256": sha256(problems_path),
            "holdsSha256": sha256(holds_path),
            "dimensionsSha256": sha256(dimensions_path),
            "meshAsset": "Assets/Resources/New_Decimated_Holds.fbx",
            "meshAssetSha256": sha256(mesh_path),
        },
        "holds": holds,
        "routes": routes,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Archive authoritative MoonBoard 2016 study content.")
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--project-root", type=Path, default=Path("."))
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("Assets/StreamingAssets/moonboard_2016_40.json"),
    )
    args = parser.parse_args()

    catalog = build_catalog(args.source_root.resolve(), args.project_root.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        output.write(json.dumps(catalog, indent=2) + "\n")


if __name__ == "__main__":
    main()
