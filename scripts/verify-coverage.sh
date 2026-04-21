#!/usr/bin/env bash
set -euo pipefail

TEST_RESULTS_PATH="${1:-coverage-report}"
LINE_THRESHOLD="${2:-0.90}"
BRANCH_THRESHOLD="${3:-0.90}"

COBERTURA_FILE=$(find "$TEST_RESULTS_PATH" \( -name "coverage.cobertura.xml" -o -name "Cobertura.xml" \) -print -quit)

if [ -z "$COBERTURA_FILE" ]; then
  echo "::error::No Cobertura coverage file found in $TEST_RESULTS_PATH"
  exit 1
fi

echo "Using coverage file: $COBERTURA_FILE"

LINE_RATE=$(grep -oP 'line-rate="[^"]*"' "$COBERTURA_FILE" | head -1 | grep -oP '[\d.]+')
BRANCH_RATE=$(grep -oP 'branch-rate="[^"]*"' "$COBERTURA_FILE" | head -1 | grep -oP '[\d.]+')

if [ -z "$LINE_RATE" ] || [ -z "$BRANCH_RATE" ]; then
  echo "::error::Failed to parse coverage rates from $COBERTURA_FILE"
  exit 1
fi

LINE_PCT=$(awk "BEGIN {printf \"%.1f\", $LINE_RATE * 100}")
BRANCH_PCT=$(awk "BEGIN {printf \"%.1f\", $BRANCH_RATE * 100}")

echo "Line coverage: ${LINE_PCT}%, Branch coverage: ${BRANCH_PCT}%"

FAILED=false

LINE_THRESHOLD_PCT=$(awk "BEGIN {printf \"%.1f\", $LINE_THRESHOLD * 100}")
if awk "BEGIN {exit !($LINE_RATE < $LINE_THRESHOLD)}"; then
  echo "::error::Line coverage ${LINE_PCT}% is below threshold ${LINE_THRESHOLD_PCT}%"
  FAILED=true
fi

BRANCH_THRESHOLD_PCT=$(awk "BEGIN {printf \"%.1f\", $BRANCH_THRESHOLD * 100}")
if awk "BEGIN {exit !($BRANCH_RATE < $BRANCH_THRESHOLD)}"; then
  echo "::error::Branch coverage ${BRANCH_PCT}% is below threshold ${BRANCH_THRESHOLD_PCT}%"
  FAILED=true
fi

if [ "$FAILED" = true ]; then
  exit 1
fi

echo "Coverage thresholds met."
