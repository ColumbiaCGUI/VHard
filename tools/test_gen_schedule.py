#!/usr/bin/env python3
import collections
import unittest

from gen_schedule import CONDITION_ORDERS, DEFAULT_CATALOG, LATIN_ROUTES, ROUTES, generate, load_routes


class ScheduleGeneratorTests(unittest.TestCase):
    def test_routes_come_from_authoritative_catalog(self):
        self.assertEqual(
            load_routes(DEFAULT_CATALOG),
            ("MB2016-19215", "MB2016-21329", "MB2016-170190"),
        )

    def test_one_hundred_participants_are_balanced(self):
        rows = list(generate(100))
        self.assertEqual(len(rows), 300)

        by_participant = collections.defaultdict(list)
        for participant, block, condition, route in rows:
            by_participant[participant].append((block, condition, route))

        order_counts = collections.Counter()
        route_order_counts = collections.Counter()
        condition_route_counts = collections.Counter()
        for participant_rows in by_participant.values():
            participant_rows.sort()
            conditions = tuple(row[1] for row in participant_rows)
            routes = tuple(row[2] for row in participant_rows)
            self.assertIn(conditions, CONDITION_ORDERS)
            self.assertIn(routes, LATIN_ROUTES)
            self.assertEqual(set(routes), set(ROUTES))
            order_counts[conditions] += 1
            route_order_counts[routes] += 1
            condition_route_counts.update(zip(conditions, routes))

        self.assertLessEqual(max(order_counts.values()) - min(order_counts.values()), 1)
        self.assertLessEqual(max(route_order_counts.values()) - min(route_order_counts.values()), 1)
        self.assertEqual(len(condition_route_counts), 9)
        self.assertLessEqual(
            max(condition_route_counts.values()) - min(condition_route_counts.values()),
            1,
        )


if __name__ == "__main__":
    unittest.main()
