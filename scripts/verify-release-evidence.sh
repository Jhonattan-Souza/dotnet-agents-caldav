#!/usr/bin/env bash
set -euo pipefail

catalog=${1:-contracts/0.2.0/requirement-evidence-catalog.md}
evidence_map=${2:-contracts/0.2.0/release-evidence-map.json}
results_directory=${3:-TestResults}
row_count=$(grep -cE '^## CAL-' "$catalog" || true)
if [[ $row_count -ne 96 ]]; then
  echo "Expected 96 normative evidence rows, found $row_count." >&2
  exit 65
fi

status_count=$(grep -cE '^- (Implementation|Evidence) status:' "$catalog" || true)
if [[ $status_count -ne 192 ]]; then
  echo "Expected implementation and evidence status for all 96 rows, found $status_count fields." >&2
  exit 66
fi

terminal_count=$(grep -cE '^- (Implementation status: implemented|Evidence status: passing)([^[:alnum:]_]|$)' "$catalog" || true)
if [[ $terminal_count -ne 192 ]]; then
  grep -En '^- (Implementation|Evidence) status:' "$catalog" |
    grep -Ev 'Implementation status: implemented([^[:alnum:]_]|$)|Evidence status: passing([^[:alnum:]_]|$)' >&2 || true
  echo "Every normative requirement must declare terminal implemented/passing status before release." >&2
  exit 67
fi

if grep -Eni \
  '^- (Implementation|Evidence) status:.*(planned|broken|assigned to issue #[0-9]+|owning-ticket status|keeps its existing status|not claimed)' \
  "$catalog"; then
  echo "A normative row still contains a nonterminal or deferred status marker." >&2
  exit 74
fi

map_count=$(jq '.requirements | length' "$evidence_map")
map_unique_count=$(jq '[.requirements[].id] | unique | length' "$evidence_map")
if [[ $map_count -ne 96 || $map_unique_count -ne 96 ]]; then
  echo "Expected exactly 96 unique evidence mappings, found $map_count rows and $map_unique_count unique IDs." >&2
  exit 68
fi

catalog_ids=$(sed -n 's/^## \(CAL-[A-Z]*-[0-9][0-9][0-9]\)$/\1/p' "$catalog" | sort)
mapped_ids=$(jq -r '.requirements[].id' "$evidence_map" | sort)
if [[ $catalog_ids != "$mapped_ids" ]]; then
  echo "The release evidence map does not match the normative catalog IDs." >&2
  diff <(printf '%s\n' "$catalog_ids") <(printf '%s\n' "$mapped_ids") >&2 || true
  exit 69
fi

if [[ ! -d $results_directory ]]; then
  echo "Test result directory does not exist: $results_directory" >&2
  exit 70
fi

mapfile -d '' all_results < <(find "$results_directory" -type f -name '*.trx' -print0)
if [[ ${#all_results[@]} -eq 0 ]]; then
  echo "No TRX evidence exists under $results_directory." >&2
  exit 70
fi

for profile_prefix in strict-preconditions alternate-time-zone; do
  mapfile -t profile_results < <(find "$results_directory" -type f -name "${profile_prefix}*.trx" -print)
  if [[ ${#profile_results[@]} -eq 0 ]] ||
      ! grep -Eq '<Counters [^>]*passed="[1-9][0-9]*"' "${profile_results[@]}"; then
    echo "The required $profile_prefix profile has no nonempty passing TRX evidence." >&2
    exit 73
  fi
  while IFS= read -r profile_test_name; do
    profile_matches=$(grep -F "$profile_test_name" "${profile_results[@]}" | grep '<UnitTestResult ' || true)
    if [[ -z $profile_matches ]] || grep -qv 'outcome="Passed"' <<< "$profile_matches"; then
      echo "The required $profile_prefix profile did not pass: $profile_test_name" >&2
      exit 73
    fi
  done < <(jq -r '.requirements[] | select(.id == "CAL-EVIDENCE-005") | .testNameContains[] | select(startswith("RadicaleConformanceHarnessTests."))' "$evidence_map")
done

while IFS=$'\t' read -r requirement_id test_name; do
  if [[ -z $test_name ]]; then
    echo "$requirement_id has no executable evidence mapping." >&2
    exit 71
  fi
  matching_results=$(grep -F "$test_name" "${all_results[@]}" | grep '<UnitTestResult ' || true)
  if [[ -z $matching_results ]] || grep -qv 'outcome="Passed"' <<< "$matching_results"; then
    echo "$requirement_id did not execute passing evidence matching: $test_name" >&2
    exit 72
  fi
done < <(jq -r '.requirements[] | .id as $id | .testNameContains[]? | [$id, .] | @tsv' "$evidence_map")

echo "Verified 96 terminal normative rows and every mapped test passed in release TRX evidence."
