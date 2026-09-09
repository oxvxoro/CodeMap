#!/usr/bin/env python3
"""Compare a benchmark summary against a JSON baseline."""

from __future__ import annotations

import argparse
import json
import sys


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("baseline", type=argparse.FileType("r"))
    parser.add_argument("current", type=argparse.FileType("r"))
    args = parser.parse_args()
    baseline = json.load(args.baseline)
    current = json.load(args.current)
    failures = []
    warnings = []
    expected_cases = baseline.get("cases", {})
    actual_cases = current.get("cases", {})
    if isinstance(expected_cases, list):
        expected_cases = {case["name"]: case for case in expected_cases if "name" in case}
    if isinstance(actual_cases, list):
        actual_cases = {case["name"]: case for case in actual_cases if "name" in case}
    for name, expected in expected_cases.items():
        actual = actual_cases.get(name)
        if actual is None:
            failures.append(f"missing case: {name}")
            continue
        if actual.get("correct") is False:
            failures.append(f"correctness failure: {name}")
        expected_alloc = expected.get("allocatedBytes")
        actual_alloc = actual.get("allocatedBytes")
        if expected_alloc and actual_alloc and actual_alloc >= expected_alloc * 2:
            failures.append(f"allocation regression >=2x: {name}")
        expected_ms = expected.get("medianMs")
        actual_ms = actual.get("medianMs")
        if expected_ms and actual_ms and actual_ms > expected_ms:
            warnings.append(f"wall-clock regression: {name} ({actual_ms} > {expected_ms} ms)")
    print(json.dumps({"failures": failures, "warnings": warnings}, indent=2))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
