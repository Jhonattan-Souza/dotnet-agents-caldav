#!/usr/bin/env bash
set -euo pipefail

catalog=${1:-contracts/0.2.0/requirement-evidence-catalog.md}
evidence_map=${2:-contracts/0.2.0/release-evidence-map.json}
results_directory=${3:-TestResults}
contract_version=$(jq -r '.contractVersion' "$evidence_map")

if [[ $contract_version == "0.2.2" ]]; then
  grep -q '^# Requirement-to-evidence catalog: authoritative Create contract 0.2.2$' "$catalog" || {
    echo "Authoritative Create 0.2.2 evidence catalog heading is missing." >&2
    exit 69
  }
  [[ $(jq -r '.issue' "$evidence_map") == "80" ]] || {
    echo "Authoritative Create evidence map must identify issue 80." >&2
    exit 69
  }
  jq -e '.collisionContract == {href:"destination_conflict", uid:"conflict", rejectedMutationState:"not_committed"}' \
    "$evidence_map" >/dev/null || {
    echo "Authoritative Create collision contract is incomplete." >&2
    exit 69
  }
  [[ $(jq -r '.generatedUidMaximumAttempts' "$evidence_map") == "3" ]] || {
    echo "Authoritative Create retry bound must be three attempts." >&2
    exit 69
  }
  jq -e '.createLimitDimensions == ["elapsed_time"]' "$evidence_map" >/dev/null || {
    echo "Authoritative Create elapsed-time limit dimension is missing." >&2
    exit 69
  }
  [[ -d $results_directory ]] || {
    echo "Test result directory does not exist: $results_directory" >&2
    exit 70
  }
  mapfile -d '' create_results < <(find "$results_directory" -type f -name '*.trx' -print0)
  [[ ${#create_results[@]} -gt 0 ]] || {
    echo "No TRX evidence exists under $results_directory." >&2
    exit 70
  }
  while IFS= read -r evidence_name; do
    matches=$(grep -F "$evidence_name" "${create_results[@]}" | grep '<UnitTestResult ' || true)
    if [[ -z $matches ]] || grep -qv 'outcome="Passed"' <<< "$matches"; then
      echo "0.2.2 Create evidence did not execute passing evidence matching: $evidence_name" >&2
      exit 72
    fi
  done < <(jq -r '.requiredEvidence[]' "$evidence_map")
  while IFS=$'\t' read -r requirement_id evidence_name; do
    grep -q "$requirement_id" "$catalog" || {
      echo "0.2.2 evidence catalog is missing requirement $requirement_id." >&2
      exit 69
    }
    grep -q "$evidence_name" "$catalog" || {
      echo "0.2.2 evidence catalog does not name mapped evidence $evidence_name." >&2
      exit 69
    }
  done < <(jq -r '.requirementEvidence[] | [.requirement, .evidence] | @tsv' "$evidence_map")
  echo "Verified authoritative Create 0.2.2 evidence map and every required test family passed."
  exit 0
fi

if ! jq -e 'has("requirements")' "$evidence_map" >/dev/null; then
  [[ $(jq -r '.contractVersion' "$evidence_map") == "0.2.1" ]] || {
    echo "Compact evidence map must declare contractVersion 0.2.1." >&2
    exit 68
  }
  grep -q '^# Requirement-to-evidence catalog: compact To-do query contract 0.2.1$' "$catalog" || {
    echo "Compact 0.2.1 evidence catalog heading is missing." >&2
    exit 69
  }
  [[ $(jq -r '.issue' "$evidence_map") == "78" ]] || {
    echo "Compact evidence map must identify issue 78." >&2
    exit 69
  }
  [[ $(jq -r '.semanticTool' "$evidence_map") == "todos.query" ]] || {
    echo "Compact evidence map must identify todos.query." >&2
    exit 69
  }
  [[ $(jq -r '.structuredResultBudgetBytes' "$evidence_map") == "65536" ]] || {
    echo "Compact evidence map must freeze the 64 KiB structured result budget." >&2
    exit 69
  }
  [[ $(jq -r '.pageSize.default' "$evidence_map") == "50" && $(jq -r '.pageSize.maximum' "$evidence_map") == "200" ]] || {
    echo "Compact evidence map has invalid page-size bounds." >&2
    exit 69
  }
  jq -e '.normalizationStates | sort == ["cancelled", "completed", "indeterminate", "open"]' "$evidence_map" >/dev/null || {
    echo "Compact evidence map has incomplete normalization states." >&2
    exit 69
  }
  jq -e '.backwardCompatibleTools | index("calendar_entities.query") != null' "$evidence_map" >/dev/null || {
    echo "Compact evidence map does not record calendar_entities.query compatibility." >&2
    exit 69
  }
  [[ -d $results_directory ]] || {
    echo "Test result directory does not exist: $results_directory" >&2
    exit 70
  }
  mapfile -d '' compact_results < <(find "$results_directory" -type f -name '*.trx' -print0)
  [[ ${#compact_results[@]} -gt 0 ]] || {
    echo "No TRX evidence exists under $results_directory." >&2
    exit 70
  }
  while IFS= read -r evidence_name; do
    matches=$(grep -F "$evidence_name" "${compact_results[@]}" | grep '<UnitTestResult ' || true)
    if [[ -z $matches ]] || grep -qv 'outcome="Passed"' <<< "$matches"; then
      echo "0.2.1 compact evidence did not execute passing evidence matching: $evidence_name" >&2
      exit 72
    fi
  done < <(jq -r '.requiredEvidence[]' "$evidence_map")
  while IFS=$'\t' read -r requirement_id evidence_name; do
    grep -q "$requirement_id" "$catalog" || {
      echo "0.2.1 evidence catalog is missing requirement $requirement_id." >&2
      exit 69
    }
    grep -q "$evidence_name" "$catalog" || {
      echo "0.2.1 evidence catalog does not name mapped evidence $evidence_name." >&2
      exit 69
    }
  done < <(jq -r '.requirementEvidence[] | [.requirement, .evidence] | @tsv' "$evidence_map")
  echo "Verified compact 0.2.1 evidence map and every required test family passed."
  exit 0
fi

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
