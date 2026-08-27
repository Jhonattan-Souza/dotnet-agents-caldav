#!/usr/bin/env python3
"""Render stable CI timing evidence from repository TRX and action outputs."""

from __future__ import annotations

import argparse
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass


DURATION_PATTERN = re.compile(
    r"^(?P<hours>\d+):(?P<minutes>[0-5]\d):(?P<seconds>[0-5]\d)(?:\.(?P<fraction>\d{1,7}))?$"
)
PHASE_LABELS = {
    "restore-local-tools": "Restore local tools",
    "restore-nuget-packages": "Restore NuGet packages",
    "release-build": "Release build",
    "main-core": "Main core",
    "main-mcp": "Main MCP",
    "main-integration": "Main integration",
    "coverage-report": "Coverage report",
    "strict-preconditions": "Strict preconditions",
    "alternate-time-zone": "Alternate time zone",
    "slopwatch": "Slopwatch",
    "isolated-suite": "Isolated suite",
}


@dataclass(frozen=True)
class TestDuration:
    name: str
    evidence: str
    seconds: float


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts-dir", required=True, type=pathlib.Path)
    parser.add_argument(
        "--phase",
        action="append",
        default=[],
        metavar="NAME=SECONDS",
        help="Phase duration obtained from a GitHub Actions step output.",
    )
    parser.add_argument(
        "--cache-status",
        required=True,
        choices=("hit", "miss", "not-reported"),
    )
    parser.add_argument(
        "--baseline-phase",
        action="append",
        default=[],
        metavar="NAME=SECONDS",
        help="Comparable before-change duration for a reported phase.",
    )
    return parser.parse_args()


def parse_duration(value: str) -> float:
    match = DURATION_PATTERN.fullmatch(value)
    if match is None:
        raise ValueError(f"Unsupported TRX duration: {value!r}")
    fraction = (match.group("fraction") or "").ljust(7, "0")
    return (
        int(match.group("hours")) * 3600
        + int(match.group("minutes")) * 60
        + int(match.group("seconds"))
        + (int(fraction) / 10_000_000 if fraction else 0)
    )


def read_test_durations(artifacts_directory: pathlib.Path) -> list[TestDuration]:
    durations: list[TestDuration] = []
    valid_trx = 0
    for path in sorted(artifacts_directory.glob("*.trx")):
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as error:
            print(f"Ignoring invalid TRX evidence {path}: {error}", file=sys.stderr)
            continue
        valid_trx += 1
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1] != "UnitTestResult":
                continue
            raw_duration = element.get("duration")
            if raw_duration is None:
                continue
            durations.append(TestDuration(
                name=element.get("testName") or "(unnamed test)",
                evidence=path.name,
                seconds=parse_duration(raw_duration),
            ))
    if valid_trx == 0:
        raise SystemExit(f"No valid TRX evidence found in {artifacts_directory}.")
    return durations


def parse_phases(values: list[str]) -> list[tuple[str, float]]:
    phases: list[tuple[str, float]] = []
    seen: set[str] = set()
    for value in values:
        name, separator, raw_seconds = value.partition("=")
        if separator == "" or name == "":
            raise SystemExit(f"Invalid phase timing: {value!r}.")
        if raw_seconds == "":
            continue
        if name in seen:
            raise SystemExit(f"Duplicate phase timing: {name!r}.")
        seen.add(name)
        try:
            seconds = float(raw_seconds)
        except ValueError as error:
            raise SystemExit(f"Invalid phase duration: {value!r}.") from error
        if seconds < 0:
            raise SystemExit(f"Negative phase timing: {value!r}.")
        phases.append((name, seconds))
    return phases


def parse_baseline_phases(values: list[str]) -> list[tuple[str, float]]:
    phases = parse_phases(values)
    for name, seconds in phases:
        if seconds == 0:
            raise SystemExit(f"Baseline phase duration must be positive: {name!r}.")
    return phases


def format_duration(seconds: float) -> str:
    minutes, remaining = divmod(seconds, 60)
    if minutes >= 1:
        return f"{int(minutes)}m {remaining:.3f}s"
    return f"{seconds:.3f}s"


def escape_cell(value: str) -> str:
    return value.replace("\n", " ").replace("\r", " ").replace("|", "\\|")


def render(
    phases: list[tuple[str, float]],
    tests: list[TestDuration],
    cache_status: str,
    baseline_phases: list[tuple[str, float]],
) -> str:
    lines = ["## CI timing", "", f"Dependency cache: **{cache_status}**", ""]
    lines.extend(("| Phase | Duration |", "| --- | ---: |"))
    phase_values = dict(phases)
    for name, seconds in phases:
        label = PHASE_LABELS.get(name, name.replace("-", " ").capitalize())
        lines.append(f"| {escape_cell(label)} | {format_duration(seconds)} |")

    buckets = [0, 0, 0, 0, 0]
    for test in tests:
        if test.seconds < 1:
            buckets[0] += 1
        elif test.seconds < 2:
            buckets[1] += 1
        elif test.seconds < 5:
            buckets[2] += 1
        elif test.seconds < 7:
            buckets[3] += 1
        else:
            buckets[4] += 1
    lines.extend((
        "",
        "### Test duration distribution",
        "",
        "| Duration | Count |",
        "| --- | ---: |",
        f"| < 1s | {buckets[0]} |",
        f"| 1s to < 2s | {buckets[1]} |",
        f"| 2s to < 5s | {buckets[2]} |",
        f"| 5s to < 7s | {buckets[3]} |",
        f"| >= 7s | {buckets[4]} |",
        "",
        "### Ten slowest tests",
        "",
        "| Test | Evidence | Duration |",
        "| --- | --- | ---: |",
    ))
    for test in sorted(tests, key=lambda item: (-item.seconds, item.name, item.evidence))[:10]:
        lines.append(
            f"| {escape_cell(test.name)} | {escape_cell(test.evidence)} | {format_duration(test.seconds)} |"
        )

    comparisons = [
        (PHASE_LABELS.get(name, name.replace("-", " ").capitalize()), baseline, phase_values[name])
        for name, baseline in baseline_phases
        if name in phase_values
    ]
    if comparisons:
        lines.extend((
            "",
            "### Baseline comparison",
            "",
            "| Phase | Before | After | Change |",
            "| --- | ---: | ---: | ---: |",
        ))
        for label, baseline, current in comparisons:
            change = ((current - baseline) / baseline) * 100
            lines.append(
                f"| {label} | {format_duration(baseline)} | {format_duration(current)} | {change:.1f}% |"
            )
    return "\n".join(lines) + "\n"


def main() -> int:
    arguments = parse_arguments()
    tests = read_test_durations(arguments.artifacts_dir)
    phases = parse_phases(arguments.phase)
    baseline_phases = parse_baseline_phases(arguments.baseline_phase)
    print(render(
        phases,
        tests,
        arguments.cache_status,
        baseline_phases,
    ), end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
