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

finish() {
  local status=$?
  trap - EXIT
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

run_project() {
  local project=$1 trx_filename=$2 prefix=$3 filter_class=$4 environment=$5
  local arguments=(dotnet test --project "$repository_root/$project" -c Release --no-build --no-restore
    --results-directory "$artifacts_directory" --report-trx --report-trx-filename "$trx_filename"
    --fail-skips on --zero-tests-policy strict --no-ansi)
  [[ -z "$prefix" ]] || arguments+=(--coverlet --coverlet-file-prefix "$prefix")
  [[ -z "$filter_class" ]] || arguments+=(--filter-class "$filter_class")
  if [[ -n "$environment" ]]; then
    IFS=';' read -r -a environment_arguments <<< "$environment"
    env "${environment_arguments[@]}" "${arguments[@]}" </dev/null
  else
    "${arguments[@]}" </dev/null
  fi
}

run_test_suite_manifest_phase "$manifest" main run_project

cobertura_reports=$("$script_directory/verify-test-artifacts.sh" "$artifacts_directory" main)
coverage_report_directory="$artifacts_directory/coverage-report"
dotnet reportgenerator \
  "-reports:$cobertura_reports" \
  "-targetdir:$coverage_report_directory" \
  -reporttypes:Cobertura \
  '-assemblyfilters:+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*'
"$script_directory/verify-coverage.sh" "$coverage_report_directory" 0.90 0.85

run_test_suite_manifest_phase "$manifest" complete run_project
"$script_directory/verify-test-artifacts.sh" "$artifacts_directory" complete >/dev/null

echo "Verified isolated test evidence: $artifacts_directory" >&2
