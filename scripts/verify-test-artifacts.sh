#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <artifact-directory> <main|complete>" >&2
  exit 64
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required to validate TRX evidence." >&2
  exit 69
fi

artifact_directory=$1
phase=$2
if [[ ! -d "$artifact_directory" ]]; then
  echo "Artifact directory does not exist: $artifact_directory" >&2
  exit 65
fi
if [[ "$phase" != main && "$phase" != complete ]]; then
  echo "Unknown artifact phase: $phase" >&2
  exit 64
fi

artifact_directory=$(realpath -- "$artifact_directory")
if [[ "$artifact_directory" == *';'* || "$artifact_directory" == *$'\n'* ]]; then
  echo "Artifact directory cannot contain semicolons or newlines." >&2
  exit 65
fi

coverage_prefixes=(main-core main-mcp main-integration)
coverage_formats=(cobertura opencover)
cobertura_reports=()
expected_coverage_count=0

shopt -s nullglob
for prefix in "${coverage_prefixes[@]}"; do
  for format in "${coverage_formats[@]}"; do
    matches=("$artifact_directory/$prefix.coverage.$format."*.xml)
    if [[ ${#matches[@]} -ne 1 ]]; then
      echo "Expected exactly one $format report for $prefix, found ${#matches[@]}." >&2
      exit 66
    fi
    ((expected_coverage_count += 1))
    if [[ "$format" == cobertura ]]; then
      cobertura_reports+=("${matches[0]}")
    fi
  done
done

root_xml_reports=("$artifact_directory"/*.xml)
if [[ ${#root_xml_reports[@]} -ne $expected_coverage_count ]]; then
  echo "Expected exactly $expected_coverage_count root coverage XML files, found ${#root_xml_reports[@]}." >&2
  exit 66
fi

expected_trx=(main-core.trx main-mcp.trx main-integration.trx)
if [[ "$phase" == complete ]]; then
  expected_trx+=(strict-preconditions.trx alternate-time-zone.trx)
fi
for filename in "${expected_trx[@]}"; do
  if [[ ! -f "$artifact_directory/$filename" ]]; then
    echo "Missing expected TRX result: $filename" >&2
    exit 67
  fi
done

root_trx=("$artifact_directory"/*.trx)
if [[ ${#root_trx[@]} -ne ${#expected_trx[@]} ]]; then
  echo "Expected exactly ${#expected_trx[@]} root TRX files, found ${#root_trx[@]}." >&2
  exit 67
fi

declare -A expected_test_counts=(
  [main-core.trx]=2082
  [main-mcp.trx]=922
  [main-integration.trx]=100
  [strict-preconditions.trx]=10
  [alternate-time-zone.trx]=10
)
declare -A count_policies=(
  [main-core.trx]=minimum
  [main-mcp.trx]=minimum
  [main-integration.trx]=minimum
  [strict-preconditions.trx]=exact
  [alternate-time-zone.trx]=exact
)

validation_arguments=()
for filename in "${expected_trx[@]}"; do
  validation_arguments+=(
    "$artifact_directory/$filename"
    "${count_policies[$filename]}"
    "${expected_test_counts[$filename]}"
  )
done

python3 - "${validation_arguments[@]}" <<'PY'
import sys
import xml.etree.ElementTree as ET

unsuccessful_counters = (
    "failed",
    "error",
    "timeout",
    "aborted",
    "inconclusive",
    "passedButRunAborted",
    "notRunnable",
    "notExecuted",
    "disconnected",
    "warning",
    "inProgress",
    "pending",
)


def local_name(element):
    return element.tag.rsplit("}", 1)[-1]


arguments = sys.argv[1:]
if len(arguments) % 3 != 0:
    raise SystemExit("Internal error: malformed TRX validation arguments")

for index in range(0, len(arguments), 3):
    path, count_policy, expected_count_text = arguments[index : index + 3]
    expected_count = int(expected_count_text)
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise SystemExit(f"Incomplete or malformed TRX evidence in {path}: {error}") from error

    if local_name(root) != "TestRun":
        raise SystemExit(f"TRX evidence has an unexpected root element: {path}")

    summaries = [element for element in root.iter() if local_name(element) == "ResultSummary"]
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise SystemExit(f"TRX evidence has no completed ResultSummary: {path}")

    counters = [element for element in summaries[0] if local_name(element) == "Counters"]
    if len(counters) != 1:
        raise SystemExit(f"TRX evidence has no final Counters element: {path}")

    values = counters[0].attrib
    if any(int(values.get(name, "0")) != 0 for name in unsuccessful_counters):
        raise SystemExit(f"TRX evidence contains unsuccessful tests: {path}")

    total = int(values.get("total", "-1"))
    executed = int(values.get("executed", "-1"))
    passed = int(values.get("passed", "-1"))
    if total < 1 or total != executed or total != passed:
        raise SystemExit(f"TRX evidence is incomplete: {path}")
    if count_policy == "minimum" and total < expected_count:
        raise SystemExit(
            f"TRX evidence contains {total} tests, expected at least {expected_count}: {path}"
        )
    if count_policy == "exact" and total != expected_count:
        raise SystemExit(
            f"TRX evidence contains {total} tests, expected exactly {expected_count}: {path}"
        )
PY

if [[ "$phase" == complete ]]; then
  script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
  repository_root=$(cd -- "$script_directory/.." && pwd)
  mapfile -d '' test_sources < <(find "$repository_root/tests" -type f -name '*.cs' -print0)
  if grep -En \
    '(^|\[)(Fact|Theory)\([^)]*Skip[[:space:]]*=|(^|\[)(Fact|Theory)\([^)]*Explicit[[:space:]]*=[[:space:]]*true|Quarantined|Flaky' \
    "${test_sources[@]}" >&2; then
    echo "Skipped, explicit, quarantined, or flaky test evidence is forbidden." >&2
    exit 68
  fi
fi

printf '%s;%s;%s\n' "${cobertura_reports[@]}"
