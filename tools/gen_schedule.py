#!/usr/bin/env python3
import argparse
import csv
import itertools
import json
from pathlib import Path


CONDITION_ORDERS = list(itertools.permutations(("A", "B", "C")))
DEFAULT_CATALOG = Path(__file__).resolve().parents[1] / "Assets/StreamingAssets/moonboard_2016_40.json"


def load_routes(catalog_path: Path) -> tuple[str, str, str]:
    catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
    if catalog.get("schemaVersion") != 3:
        raise ValueError("MoonBoard catalog schemaVersion must be 3")
    if catalog.get("setupId") != "moonboard-2016" or catalog.get("overhangAngleDegrees") != 40:
        raise ValueError("Study routes must use MoonBoard 2016 at 40 degrees")
    routes = tuple(route["id"] for route in catalog.get("routes", ()) if route.get("lockedForStudy"))
    if len(routes) != 3 or len(set(routes)) != 3:
        raise ValueError("MoonBoard catalog must contain exactly three distinct locked study routes")
    return routes


ROUTES = load_routes(DEFAULT_CATALOG)
LATIN_ROUTES = tuple(
    tuple(ROUTES[(column + row) % len(ROUTES)] for column in range(len(ROUTES)))
    for row in range(len(ROUTES))
)

# Order all 18 condition/route-order combinations so the 100-participant prefix
# balances condition orders, route orders, and every condition-by-route pairing.
COUNTERBALANCE_ORDERS = (
    (0, 0), (0, 1), (1, 0), (1, 1), (2, 1), (2, 2),
    (3, 0), (4, 2), (5, 0), (5, 2),
    (0, 2), (1, 2), (2, 0), (3, 1), (3, 2), (4, 0),
    (4, 1), (5, 1),
)


def generate(count: int, routes=ROUTES):
    route_orders = tuple(
        tuple(routes[(column + row) % len(routes)] for column in range(len(routes)))
        for row in range(len(routes))
    )
    for participant_index in range(count):
        participant = f"P{participant_index + 1:02d}"
        condition_index, route_index = COUNTERBALANCE_ORDERS[
            participant_index % len(COUNTERBALANCE_ORDERS)
        ]
        conditions = CONDITION_ORDERS[condition_index]
        participant_routes = route_orders[route_index]
        for block, (condition, route) in enumerate(zip(conditions, participant_routes), start=1):
            yield participant, block, condition, route


def main():
    parser = argparse.ArgumentParser(description="Generate the VHard study schedule.")
    parser.add_argument("--participants", type=int, default=100)
    parser.add_argument("--catalog", type=Path, default=DEFAULT_CATALOG)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("Assets/StreamingAssets/study_schedule.csv"),
    )
    args = parser.parse_args()
    routes = load_routes(args.catalog)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="", encoding="utf-8") as output:
        writer = csv.writer(output, lineterminator="\n")
        writer.writerow(("participant", "block", "condition", "route"))
        writer.writerows(generate(args.participants, routes))


if __name__ == "__main__":
    main()
