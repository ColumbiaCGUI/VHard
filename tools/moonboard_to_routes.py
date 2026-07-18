#!/usr/bin/env python3
"""Convert a MoonBoard problems JSON export into the app's StreamingAssets/routes.json.

MoonBoard exports (e.g. problems_2023_01_30.zip from the VHard Drive) store problems as a
list of objects with a name, a grade, and a move list whose entries carry the grid position
("A5".."K18"). Field casing varies between export vintages (``Name``/``name``,
``Moves``/``moves``, ``Description``/``description``), so both are accepted; anything else
fails loudly rather than guessing.

Usage:
  python moonboard_to_routes.py problems.json -o routes.json
  python moonboard_to_routes.py problems.json --benchmarks-only --max-routes 25
  python moonboard_to_routes.py problems.json --name "TSUNAMI" --name "DEEP THROAT"

The Unity side (RouteLibrary.TryParseJson) revalidates everything on load and rejects any
route whose name collides with a built-in study route.
"""
import argparse
import json
import re
import sys
from pathlib import Path

HOLD_TOKEN = re.compile(r"^[A-K](1[0-8]|[1-9])$")
BUILT_IN_ROUTE_NAMES = {
    "DEATH STAR", "TO JUG, OR NOT TO JUG...",
}


def field(mapping, *names):
    for name in names:
        if name in mapping:
            return mapping[name]
    return None


def extract_problems(payload):
    """Return the problem list from the known export shapes: a bare list, or an object
    wrapping it under data/problems/Problems."""
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict):
        for key in ("data", "problems", "Problems"):
            if isinstance(payload.get(key), list):
                return payload[key]
    raise SystemExit("Unrecognized MoonBoard export shape: expected a list of problems "
                     "or an object with a data/problems array.")


def convert_problem(problem, index):
    name = field(problem, "Name", "name")
    if not isinstance(name, str) or not name.strip():
        raise SystemExit(f"Problem {index} has no usable name.")
    name = name.strip().upper()

    grade = field(problem, "Grade", "grade", "UserGrade", "userGrade") or ""
    moves = field(problem, "Moves", "moves")
    if not isinstance(moves, list) or not moves:
        raise SystemExit(f"Problem {index} ('{name}') has no moves.")

    holds = []
    seen = set()
    roles = {}
    for move_index, move in enumerate(moves):
        if not isinstance(move, dict):
            raise SystemExit(f"Problem {index} ('{name}') move {move_index} is not an object.")
        token = field(move, "Description", "description", "Position", "position")
        if not isinstance(token, str):
            raise SystemExit(f"Problem {index} ('{name}') move {move_index} has no position.")
        token = token.strip().upper()
        if not HOLD_TOKEN.match(token):
            raise SystemExit(
                f"Problem {index} ('{name}') move {move_index} position '{token}' "
                "is not a MoonBoard grid position A1-K18.")
        if token not in seen:
            seen.add(token)
            holds.append(token)

        role = roles.setdefault(token, {"start": False, "finish": False})
        role["start"] = role["start"] or bool(field(move, "IsStart", "isStart"))
        role["finish"] = role["finish"] or bool(field(move, "IsEnd", "isEnd"))

    conflicts = [token for token in holds if roles[token]["start"] and roles[token]["finish"]]
    if conflicts:
        raise SystemExit(
            f"Problem {index} ('{name}') position(s) {', '.join(conflicts)} "
            "are flagged as both start and finish.")

    starts = [token for token in holds if roles[token]["start"]]
    finishes = [token for token in holds if roles[token]["finish"]]
    if not starts:
        raise SystemExit(f"Problem {index} ('{name}') has no start moves.")
    if not finishes:
        raise SystemExit(f"Problem {index} ('{name}') has no finish moves.")
    if len(starts) > 2:
        raise SystemExit(f"Problem {index} ('{name}') has more than 2 start positions.")
    if len(finishes) > 2:
        raise SystemExit(f"Problem {index} ('{name}') has more than 2 finish positions.")

    is_benchmark = bool(field(problem, "IsBenchmark", "isBenchmark"))
    return {
        "name": name,
        "grade": str(grade),
        "holds": holds,
        "start": starts,
        "finish": finishes,
    }, is_benchmark


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("export", type=Path, help="MoonBoard problems JSON export")
    parser.add_argument("-o", "--output", type=Path, default=Path("routes.json"))
    parser.add_argument("--benchmarks-only", action="store_true",
                        help="Keep only problems flagged IsBenchmark")
    parser.add_argument("--name", action="append", default=[],
                        help="Keep only problems with this exact name (repeatable)")
    parser.add_argument("--max-routes", type=int, default=0,
                        help="Cap the number of routes written (0 = no cap)")
    args = parser.parse_args()

    payload = json.loads(args.export.read_text(encoding="utf-8"))
    problems = extract_problems(payload)
    wanted_names = {name.strip().upper() for name in args.name}

    routes = []
    used_names = set()
    skipped_builtin = []
    for index, problem in enumerate(problems):
        route, is_benchmark = convert_problem(problem, index)
        if args.benchmarks_only and not is_benchmark:
            continue
        if wanted_names and route["name"] not in wanted_names:
            continue
        if route["name"] in BUILT_IN_ROUTE_NAMES:
            skipped_builtin.append(route["name"])
            continue
        if route["name"] in used_names:
            route["name"] = f"{route['name']} ({index})"
        used_names.add(route["name"])
        routes.append(route)
        if args.max_routes and len(routes) >= args.max_routes:
            break

    if skipped_builtin:
        print(f"note: skipped {len(skipped_builtin)} problem(s) shadowing built-in study routes: "
              + ", ".join(sorted(set(skipped_builtin))), file=sys.stderr)
    if not routes:
        raise SystemExit("No routes matched the given filters; nothing written.")

    args.output.write_text(
        json.dumps({"schemaVersion": 2, "routes": routes}, indent=1, ensure_ascii=False) + "\n",
        encoding="utf-8")
    print(f"wrote {len(routes)} route(s) to {args.output}")


if __name__ == "__main__":
    main()
