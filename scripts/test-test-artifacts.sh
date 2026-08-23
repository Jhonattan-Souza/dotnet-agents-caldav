#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_directory/verify-test-artifacts.sh"
cleanup="$script_directory/cleanup-test-artifacts.sh"
fixture_root=$(mktemp -d)
trap 'rm -rf -- "$fixture_root"' EXIT

write_successful_trx() {
  local path=$1
  local total=$2
  printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">' \
    '  <ResultSummary outcome="Completed">' \
    "    <Counters total=\"$total\" executed=\"$total\" passed=\"$total\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"0\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\" />" \
    '  </ResultSummary>' \
    '</TestRun>' > "$path"
}

seed_main_artifacts() {
  local directory=$1
  mkdir -p -- "$directory"

  local prefix
  for prefix in main-core main-mcp main-integration; do
    printf '<coverage />\n' > "$directory/$prefix.coverage.cobertura.260821000000000.xml"
    printf '<coverage />\n' > "$directory/$prefix.coverage.opencover.260821000000001.xml"
  done

  write_successful_trx "$directory/main-core.trx" 1883
  write_successful_trx "$directory/main-mcp.trx" 958
  write_successful_trx "$directory/main-integration.trx" 99
}

current_run="$fixture_root/current-run"
seed_main_artifacts "$current_run"
mkdir -p -- "$current_run/_JSP-old"
printf '<coverage line-rate="0.1" />\n' > "$current_run/_JSP-old/coverage.cobertura.xml"

actual=$(
  "$verifier" "$current_run" main
)
expected="$(realpath "$current_run/main-core.coverage.cobertura.260821000000000.xml");$(realpath "$current_run/main-mcp.coverage.cobertura.260821000000000.xml");$(realpath "$current_run/main-integration.coverage.cobertura.260821000000000.xml")"

if [[ "$actual" != "$expected" ]]; then
  echo "Expected the current-run Cobertura manifest, got: $actual" >&2
  exit 1
fi

echo "PASS current-run artifacts ignore nested historical reports"

truncated_run="$fixture_root/truncated-trx"
seed_main_artifacts "$truncated_run"
printf '<TestRun>\n' > "$truncated_run/main-core.trx"
if "$verifier" "$truncated_run" main >/dev/null 2>&1; then
  echo "Expected a truncated TRX result to be rejected." >&2
  exit 1
fi

echo "PASS truncated TRX evidence is rejected"

wrong_conformance_count="$fixture_root/wrong-conformance-count"
seed_main_artifacts "$wrong_conformance_count"
write_successful_trx "$wrong_conformance_count/strict-preconditions.trx" 9
write_successful_trx "$wrong_conformance_count/alternate-time-zone.trx" 10
if "$verifier" "$wrong_conformance_count" complete >/dev/null 2>&1; then
  echo "Expected a conformance TRX with the wrong test count to be rejected." >&2
  exit 1
fi

echo "PASS conformance evidence requires the exact test count"

missing_report="$fixture_root/missing-report"
seed_main_artifacts "$missing_report"
rm -- "$missing_report/main-mcp.coverage.opencover.260821000000001.xml"
if "$verifier" "$missing_report" main >/dev/null 2>&1; then
  echo "Expected a missing coverage prefix to be rejected." >&2
  exit 1
fi

echo "PASS missing coverage reports are rejected"

duplicate_report="$fixture_root/duplicate-report"
seed_main_artifacts "$duplicate_report"
printf '<coverage />\n' > "$duplicate_report/main-core.coverage.cobertura.260821000000002.xml"
if "$verifier" "$duplicate_report" main >/dev/null 2>&1; then
  echo "Expected a duplicate coverage prefix to be rejected." >&2
  exit 1
fi

echo "PASS duplicate coverage reports are rejected"

unknown_report="$fixture_root/unknown-report"
seed_main_artifacts "$unknown_report"
printf '<coverage />\n' > "$unknown_report/coverage.cobertura.xml"
if "$verifier" "$unknown_report" main >/dev/null 2>&1; then
  echo "Expected an unknown root coverage report to be rejected." >&2
  exit 1
fi

echo "PASS unknown root coverage reports are rejected"

below_minimum="$fixture_root/below-minimum"
seed_main_artifacts "$below_minimum"
write_successful_trx "$below_minimum/main-core.trx" 1882
if "$verifier" "$below_minimum" main >/dev/null 2>&1; then
  echo "Expected a main test result below its discovery baseline to be rejected." >&2
  exit 1
fi

echo "PASS main test evidence enforces discovery baselines"

complete_run="$fixture_root/complete-run"
seed_main_artifacts "$complete_run"
write_successful_trx "$complete_run/strict-preconditions.trx" 10
write_successful_trx "$complete_run/alternate-time-zone.trx" 10
"$verifier" "$complete_run" complete >/dev/null

echo "PASS complete test evidence matches the five-file manifest"

runner_temp="$fixture_root/runner-temp"
cleanup_target="$runner_temp/caldav-tests.ABC123"
mkdir -p -- "$cleanup_target"
printf 'generated\n' > "$cleanup_target/evidence.txt"
"$cleanup" "$cleanup_target" "$runner_temp"
if [[ -e "$cleanup_target" ]]; then
  echo "Expected the generated test artifact directory to be removed." >&2
  exit 1
fi

echo "PASS cleanup removes an authorized generated directory"

unauthorized_target="$runner_temp/TestResults"
mkdir -p -- "$unauthorized_target"
printf 'preserve\n' > "$unauthorized_target/evidence.txt"
if "$cleanup" "$unauthorized_target" "$runner_temp" >/dev/null 2>&1; then
  echo "Expected cleanup outside the generated namespace to be rejected." >&2
  exit 1
fi
if [[ ! -f "$unauthorized_target/evidence.txt" ]]; then
  echo "Cleanup changed an unauthorized directory." >&2
  exit 1
fi

echo "PASS cleanup preserves unauthorized directories"
