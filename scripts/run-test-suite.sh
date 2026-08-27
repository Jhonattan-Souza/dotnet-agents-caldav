#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 [--artifacts-dir <empty-directory>]" >&2
  exit 64
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
manifest="$script_directory/test-suite-manifest.json"
. "$script_directory/test-suite-runner-lib.sh"
artifacts_owned=false
temporary_parent=
python3 "$script_directory/validate-test-suite-manifest.py" "$manifest"

case $# in
  0)
    temporary_parent=$(realpath -- "${TMPDIR:-/tmp}")
    if [[ "$temporary_parent" == *';'* || "$temporary_parent" == *$'\n'* ]]; then
      echo "Temporary artifact parent cannot contain semicolons or newlines." >&2
      exit 65
    fi
    artifacts_directory=$(mktemp -d "$temporary_parent/caldav-tests.XXXXXX")
    artifacts_owned=true
    ;;
  2)
    [[ "$1" == --artifacts-dir ]] || usage
    artifacts_directory=$2
    if [[ ! -d "$artifacts_directory" ]]; then
      echo "Artifact directory does not exist: $artifacts_directory" >&2
      exit 65
    fi
    if [[ -n "$(find "$artifacts_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
      echo "Artifact directory must be empty: $artifacts_directory" >&2
      exit 65
    fi
    artifacts_directory=$(realpath -- "$artifacts_directory")
    ;;
  *)
    usage
    ;;
esac

if [[ "$artifacts_directory" == *';'* || "$artifacts_directory" == *$'\n'* ]]; then
  echo "Artifact directory cannot contain semicolons or newlines." >&2
  exit 65
fi

cd -- "$repository_root"
case "$artifacts_directory/" in
  "$repository_root/"*)
    if [[ "$artifacts_owned" == true ]]; then
      "$script_directory/cleanup-test-artifacts.sh" "$artifacts_directory" "$temporary_parent"
    fi
    echo "Artifact directory must be outside the repository so the worktree guard can remain authoritative." >&2
    exit 65
    ;;
esac
state_parent=$(realpath -- "${TMPDIR:-/tmp}")
case "$state_parent/" in
  "$repository_root/"*)
    echo "Temporary worktree-state directory must be outside the repository." >&2
    exit 65
    ;;
esac
state_directory=$(mktemp -d "$state_parent/caldav-worktree-state.XXXXXX")
before_state="$state_directory/before.json"
after_state="$state_directory/after.json"
early_finish() {
  local status=$?
  rm -rf -- "$state_directory"
  if [[ "$artifacts_owned" == true ]]; then
    echo "Test artifacts retained after worktree-state capture failure: $artifacts_directory" >&2
  fi
  exit "$status"
}
trap early_finish EXIT
echo "Test artifacts: $artifacts_directory" >&2
python3 "$script_directory/verify-worktree-state.py" capture "$repository_root" "$before_state"
phase_timings="$artifacts_directory/phase-timings.tsv"
manifest_timing_directory="$artifacts_directory/.manifest-timings"
mkdir -p -- "$manifest_timing_directory"
touch "$phase_timings"
suite_started_at=$(date +%s%N)

append_phase_timing() {
  local phase_name=$1 started_at=$2 finished_at=$3
  local elapsed_ns=$((finished_at - started_at))
  printf '%s\t%d.%03d\n' \
    "$phase_name" \
    "$((elapsed_ns / 1000000000))" \
    "$(((elapsed_ns % 1000000000) / 1000000))" >> "$phase_timings"
}

run_timed_phase() {
  local phase_name=$1
  shift
  local started_at finished_at status
  started_at=$(date +%s%N)
  if "$@"; then
    status=0
  else
    status=$?
  fi
  finished_at=$(date +%s%N)
  append_phase_timing "$phase_name" "$started_at" "$finished_at"
  return "$status"
}

collect_manifest_timings() {
  local phase=$1 row _project trx _prefix _filter _environment timing_path
  while IFS= read -r row; do
    IFS=$'\x1f' read -r _project trx _prefix _filter _environment <<< "$row"
    timing_path="$manifest_timing_directory/$trx.tsv"
    [[ -f "$timing_path" ]] || {
      echo "Missing phase timing for test manifest entry: $trx" >&2
      return 66
    }
    cat -- "$timing_path" >> "$phase_timings"
    rm -- "$timing_path"
  done < <(emit_test_suite_manifest_rows "$manifest" "$phase")
}

collect_available_manifest_timings() {
  local phase row _project trx _prefix _filter _environment timing_path
  for phase in main complete; do
    while IFS= read -r row; do
      IFS=$'\x1f' read -r _project trx _prefix _filter _environment <<< "$row"
      timing_path="$manifest_timing_directory/$trx.tsv"
      if [[ -f "$timing_path" ]]; then
        cat -- "$timing_path" >> "$phase_timings"
        rm -- "$timing_path"
      fi
    done < <(emit_test_suite_manifest_rows "$manifest" "$phase")
  done
}

finish() {
  local status=$?
  trap - EXIT
  if ! collect_available_manifest_timings; then
    status=70
  fi
  append_phase_timing isolated-suite "$suite_started_at" "$(date +%s%N)"
  if ! python3 "$script_directory/verify-worktree-state.py" compare \
    "$repository_root" "$before_state" "$after_state"; then
    status=70
  fi
  rm -rf -- "$state_directory"
  if [[ "$artifacts_owned" == true ]]; then
    if [[ $status -eq 0 ]]; then
      "$script_directory/cleanup-test-artifacts.sh" "$artifacts_directory" "$temporary_parent"
    else
      echo "Test artifacts retained after failure: $artifacts_directory" >&2
    fi
  fi
  exit "$status"
}
trap finish EXIT

"$script_directory/test-test-artifacts.sh"
bash "$script_directory/test-trx-timing-summary.sh"

run_project() {
  local project=$1 trx_filename=$2 prefix=$3 filter_class=$4 environment=$5
  local phase_name=${trx_filename%.trx} started_at finished_at elapsed_ns status
  local arguments=(dotnet test --project "$repository_root/$project" -c Release --no-build --no-restore
    --results-directory "$artifacts_directory" --report-trx --report-trx-filename "$trx_filename"
    --fail-skips on --zero-tests-policy strict --no-ansi)
  [[ -z "$prefix" ]] || arguments+=(--coverlet --coverlet-file-prefix "$prefix")
  [[ -z "$filter_class" ]] || arguments+=(--filter-class "$filter_class")
  started_at=$(date +%s%N)
  if [[ -n "$environment" ]]; then
    IFS=';' read -r -a environment_arguments <<< "$environment"
    if env "${environment_arguments[@]}" "${arguments[@]}" </dev/null; then
      status=0
    else
      status=$?
    fi
  else
    if "${arguments[@]}" </dev/null; then
      status=0
    else
      status=$?
    fi
  fi
  finished_at=$(date +%s%N)
  elapsed_ns=$((finished_at - started_at))
  printf '%s\t%d.%03d\n' \
    "$phase_name" \
    "$((elapsed_ns / 1000000000))" \
    "$(((elapsed_ns % 1000000000) / 1000000))" > "$manifest_timing_directory/$trx_filename.tsv"
  return "$status"
}

generate_and_verify_coverage() {
  local cobertura_reports coverage_report_directory
  cobertura_reports=$("$script_directory/verify-test-artifacts.sh" "$artifacts_directory" main)
  coverage_report_directory="$artifacts_directory/coverage-report"
  dotnet reportgenerator \
    "-reports:$cobertura_reports" \
    "-targetdir:$coverage_report_directory" \
    -reporttypes:Cobertura \
    '-assemblyfilters:+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*'
  "$script_directory/verify-coverage.sh" "$coverage_report_directory" 0.90 0.85
}

run_test_suite_manifest_phase "$manifest" main 2 run_project
collect_manifest_timings main
run_timed_phase coverage-report generate_and_verify_coverage

run_test_suite_manifest_phase "$manifest" complete 2 run_project
collect_manifest_timings complete
"$script_directory/verify-test-artifacts.sh" "$artifacts_directory" complete >/dev/null

echo "Verified isolated test evidence: $artifacts_directory" >&2
