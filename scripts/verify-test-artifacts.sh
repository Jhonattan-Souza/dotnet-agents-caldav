#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <artifact-directory> <main|complete>" >&2
  exit 64
fi
command -v python3 >/dev/null 2>&1 || { echo "python3 is required to validate TRX evidence." >&2; exit 69; }
artifact_directory=$1
phase=$2
[[ -d "$artifact_directory" ]] || { echo "Artifact directory does not exist: $artifact_directory" >&2; exit 65; }
[[ "$phase" == main || "$phase" == complete ]] || { echo "Unknown artifact phase: $phase" >&2; exit 64; }
artifact_directory=$(realpath -- "$artifact_directory")
[[ "$artifact_directory" != *';'* && "$artifact_directory" != *$'\n'* ]] || {
  echo "Artifact directory cannot contain semicolons or newlines." >&2; exit 65;
}
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
manifest="$script_directory/test-suite-manifest.json"
python3 "$script_directory/validate-test-suite-manifest.py" "$manifest"

mapfile -t coverage_prefixes < <(python3 - "$manifest" <<'PY'
import json, sys
for artifact in json.load(open(sys.argv[1], encoding="utf-8"))["artifacts"]:
    if artifact.get("coveragePrefix"):
        print(artifact["coveragePrefix"])
PY
)
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
    [[ "$format" != cobertura ]] || cobertura_reports+=("${matches[0]}")
  done
done
root_xml_reports=("$artifact_directory"/*.xml)
[[ ${#root_xml_reports[@]} -eq $expected_coverage_count ]] || {
  echo "Expected exactly $expected_coverage_count root coverage XML files, found ${#root_xml_reports[@]}." >&2; exit 66;
}

python3 - "$manifest" "$artifact_directory" "$phase" <<'PY'
import json
import pathlib
import sys
import xml.etree.ElementTree as ET

manifest_path, artifact_text, phase = sys.argv[1:]
artifact_directory = pathlib.Path(artifact_text)
manifest = json.load(open(manifest_path, encoding="utf-8"))
items = manifest.get("artifacts", [])
if manifest.get("schemaVersion") != 1 or len(items) != 5:
    raise SystemExit("Test-suite manifest must contain exactly five schema-v1 artifacts.")
names = [item.get("name") for item in items]
trx_names = [item.get("trx") for item in items]
if len(set(names)) != 5 or len(set(trx_names)) != 5:
    raise SystemExit("Test-suite manifest names and TRX paths must be unique.")
if any(pathlib.PurePath(name).name != name or not name.endswith(".trx") for name in trx_names):
    raise SystemExit("Test-suite manifest TRX paths must be safe basenames.")
if sum(item.get("phase") == "main" for item in items) != 3 or sum(item.get("phase") == "complete" for item in items) != 2:
    raise SystemExit("Test-suite manifest must contain three main and two complete artifacts.")
if any(not isinstance(item.get("exactTests"), int) or item["exactTests"] < 1 for item in items):
    raise SystemExit("Test-suite manifest exact counts must be positive integers.")
selected = [item for item in items if item["phase"] == "main" or phase == "complete"]
expected_names = {item["trx"] for item in selected}
actual_names = {path.name for path in artifact_directory.glob("*.trx")}
if actual_names != expected_names:
    raise SystemExit(f"TRX manifest mismatch: expected {sorted(expected_names)}, found {sorted(actual_names)}")

unsuccessful = ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending")
def local(element):
    return element.tag.rsplit("}", 1)[-1]

for item in selected:
    path = artifact_directory / item["trx"]
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise SystemExit(f"Incomplete or malformed TRX evidence in {path}: {error}") from error
    if local(root) != "TestRun":
        raise SystemExit(f"TRX evidence has an unexpected root element: {path}")
    summaries = [element for element in root.iter() if local(element) == "ResultSummary"]
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed":
        raise SystemExit(f"TRX evidence has no completed ResultSummary: {path}")
    counters = [element for element in summaries[0] if local(element) == "Counters"]
    if len(counters) != 1:
        raise SystemExit(f"TRX evidence has no final Counters element: {path}")
    values = counters[0].attrib
    if any(int(values.get(name, "0")) != 0 for name in unsuccessful):
        raise SystemExit(f"TRX evidence contains unsuccessful counters: {path}")
    expected = item["exactTests"]
    if any(int(values.get(name, "-1")) != expected for name in ("total", "executed", "passed")):
        raise SystemExit(f"TRX evidence does not contain exactly {expected} completed passes: {path}")
    results = [element for element in root.iter() if local(element) == "UnitTestResult"]
    if len(results) != expected:
        raise SystemExit(f"TRX evidence contains {len(results)} result records, expected {expected}: {path}")
    if any(result.get("outcome") != "Passed" for result in results):
        raise SystemExit(f"TRX evidence contains a non-passing result record: {path}")
    execution_ids = [result.get("executionId") for result in results]
    if None in execution_ids or len(set(execution_ids)) != expected:
        raise SystemExit(f"TRX evidence contains missing or duplicate execution IDs: {path}")
    result_identities = [(result.get("testId"), result.get("testName")) for result in results]
    if any(None in identity for identity in result_identities) or len(set(result_identities)) != expected:
        raise SystemExit(f"TRX evidence contains missing or duplicate result identities: {path}")
    entries = [element for element in root.iter() if local(element) == "TestEntry"]
    entry_execution_ids = [entry.get("executionId") for entry in entries]
    if len(entries) != expected or None in entry_execution_ids or set(entry_execution_ids) != set(execution_ids):
        raise SystemExit(f"TRX evidence TestEntry rows do not match executed results: {path}")
    required = item.get("requiredResult")
    class_by_test = {}
    definitions = [element for element in root.iter() if local(element) == "UnitTest"]
    definition_ids = [definition.get("id") for definition in definitions]
    if None in definition_ids or len(set(definition_ids)) != len(definition_ids):
        raise SystemExit(f"TRX evidence contains missing or duplicate test definition IDs: {path}")
    for definition in definitions:
        methods = [child for child in definition.iter() if local(child) == "TestMethod"]
        if len(methods) != 1:
            raise SystemExit(f"TRX evidence contains a malformed test definition: {path}")
        class_by_test[definition.get("id")] = methods[0].get("className")
    result_test_ids = [result.get("testId") for result in results]
    if None in result_test_ids or any(test_id not in class_by_test for test_id in result_test_ids):
        raise SystemExit(f"TRX evidence contains an unknown result test ID: {path}")
    if set(result_test_ids) != set(definition_ids):
        raise SystemExit(f"TRX evidence test definitions and result IDs do not match: {path}")
    if required:
        matching = [result for result in results if class_by_test.get(result.get("testId")) == required["className"]]
        if len(matching) != required["exactPassed"]:
            raise SystemExit(f"TRX evidence contains {len(matching)} passing {required['className']} results, expected {required['exactPassed']}: {path}")
PY

if [[ "$phase" == complete ]]; then
  python3 "$script_directory/verify-test-source-policy.py" "$script_directory/../tests"
fi
printf '%s;%s;%s\n' "${cobertura_reports[@]}"
