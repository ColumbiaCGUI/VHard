#!/usr/bin/env python3
import argparse
import csv
import itertools
from pathlib import Path


CONDITION_ORDERS = list(itertools.permutations(("A", "B", "C")))
ROUTES = ("DEATH STAR", "SPEED", "THE CRUSH ALT")
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


def generate(count: int):
    for participant_index in range(count):
        participant = f"P{participant_index + 1:02d}"
        condition_index, route_index = COUNTERBALANCE_ORDERS[
            participant_index % len(COUNTERBALANCE_ORDERS)
        ]
        conditions = CONDITION_ORDERS[condition_index]
        routes = LATIN_ROUTES[route_index]
        for block, (condition, route) in enumerate(zip(conditions, routes), start=1):
            yield participant, block, condition, route


def main():
    parser = argparse.ArgumentParser(description="Generate the VHard study schedule.")
    parser.add_argument("--participants", type=int, default=100)
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("Assets/StreamingAssets/study_schedule.csv"),
    )
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="", encoding="utf-8") as output:
        writer = csv.writer(output)
        writer.writerow(("participant", "block", "condition", "route"))
        writer.writerows(generate(args.participants))


if __name__ == "__main__":
    main()
