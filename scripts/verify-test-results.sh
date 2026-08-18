#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <TestResults directory>" >&2
  exit 64
fi

results_directory=$1
mapfile -d '' result_files < <(find "$results_directory" -type f -name '*.trx' -print0)
if [[ ${#result_files[@]} -eq 0 ]]; then
  echo "No TRX test results were found under $results_directory." >&2
  exit 65
fi

for result_file in "${result_files[@]}"; do
  if grep -Eq '<ResultSummary[^>]*outcome="(Failed|Error|Aborted|Timeout)"' "$result_file" ||
     grep -Eq '\b(failed|error|timeout|aborted|notRunnable|notExecuted|disconnected|warning|pending)="[1-9][0-9]*"' "$result_file"; then
    echo "Incomplete or unsuccessful test evidence in $result_file." >&2
    exit 66
  fi
done

mapfile -d '' test_sources < <(find tests -type f -name '*.cs' -print0)
if grep -En \
  '(^|\[)(Fact|Theory)\([^)]*Skip[[:space:]]*=|(^|\[)(Fact|Theory)\([^)]*Explicit[[:space:]]*=[[:space:]]*true|Quarantined|Flaky' \
  "${test_sources[@]}"; then
  echo "Skipped, explicit, quarantined, or flaky test evidence is forbidden for release." >&2
  exit 67
fi

echo "Verified ${#result_files[@]} complete TRX result file(s) with no disabled evidence."
