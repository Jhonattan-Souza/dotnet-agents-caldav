#!/usr/bin/env bash
set -euo pipefail

python3 - "${1:-coverage-report}" "${2:-0.90}" "${3:-0.85}" <<'PY'
from decimal import Decimal, InvalidOperation
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def rate(value, label):
    try:
        number = Decimal(value)
    except (InvalidOperation, TypeError, ValueError):
        raise ValueError(f"{label} must be a number between 0 and 1.") from None
    if not number.is_finite() or not 0 <= number <= 1:
        raise ValueError(f"{label} must be a finite number between 0 and 1.")
    return number


try:
    directory = Path(sys.argv[1])
    reports = [directory / name for name in ("coverage.cobertura.xml", "Cobertura.xml")
               if (directory / name).is_file()]
    if len(reports) != 1:
        raise ValueError(f"Expected exactly one root-level Cobertura report in {directory}, found {len(reports)}.")
    report = reports[0]
    root = ET.parse(report).getroot()
    if root.tag != "coverage":
        raise ValueError(f"Expected a Cobertura coverage root in {report}.")
    line = rate(root.get("line-rate"), "Line coverage")
    branch = rate(root.get("branch-rate"), "Branch coverage")
    line_threshold = rate(sys.argv[2], "Line threshold")
    branch_threshold = rate(sys.argv[3], "Branch threshold")
except (OSError, ET.ParseError, ValueError) as error:
    print(f"::error::{error}", file=sys.stderr)
    raise SystemExit(1) from None

print(f"Using coverage file: {report}")
print(f"Line coverage: {line:.1%}, Branch coverage: {branch:.1%}")
failed = False
for label, actual, threshold in (("Line", line, line_threshold), ("Branch", branch, branch_threshold)):
    if actual < threshold:
        print(f"::error::{label} coverage {actual:.1%} is below threshold {threshold:.1%}")
        failed = True
if failed:
    raise SystemExit(1)
print("Coverage thresholds met.")
PY
