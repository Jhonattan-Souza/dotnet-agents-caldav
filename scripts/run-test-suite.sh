#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 [--artifacts-dir <empty-directory>]" >&2
  exit 64
}

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
artifacts_owned=false
temporary_parent=

case $# in
  0)
    temporary_parent=$(realpath -- "${TMPDIR:-/tmp}")
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

finish() {
  local status=$?
  trap - EXIT
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

echo "Test artifacts: $artifacts_directory" >&2
"$script_directory/test-test-artifacts.sh"

run_main_project() {
  local project=$1
  local prefix=$2
  local trx_filename=$3
  local minimum_tests=$4

  dotnet test \
    --project "$repository_root/$project" \
    -c Release \
    --no-build \
    --no-restore \
    --results-directory "$artifacts_directory" \
    --report-trx \
    --report-trx-filename "$trx_filename" \
    --coverlet \
    --coverlet-file-prefix "$prefix" \
    --minimum-expected-tests "$minimum_tests" \
    --fail-skips on \
    --zero-tests-policy strict \
    --no-ansi
}

run_main_project \
  tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  main-core \
  main-core.trx \
  2081
run_main_project \
  tests/DotnetAgents.CalDav.Mcp.Tests.Unit/DotnetAgents.CalDav.Mcp.Tests.Unit.csproj \
  main-mcp \
  main-mcp.trx \
  964
run_main_project \
  tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  main-integration \
  main-integration.trx \
  100

cobertura_reports=$("$script_directory/verify-test-artifacts.sh" "$artifacts_directory" main)
coverage_report_directory="$artifacts_directory/coverage-report"
dotnet reportgenerator \
  "-reports:$cobertura_reports" \
  "-targetdir:$coverage_report_directory" \
  -reporttypes:Cobertura \
  '-assemblyfilters:+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*'
"$script_directory/verify-coverage.sh" "$coverage_report_directory" 0.90 0.85

run_conformance_variant() {
  local variant=$1
  RADICALE_CONFORMANCE_VARIANT="$variant" \
    dotnet test \
      --project "$repository_root/tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj" \
      -c Release \
      --no-build \
      --no-restore \
      --filter-class '*RadicaleConformanceHarnessTests' \
      --results-directory "$artifacts_directory" \
      --report-trx \
      --report-trx-filename "$variant.trx" \
      --minimum-expected-tests 10 \
      --fail-skips on \
      --zero-tests-policy strict \
      --no-ansi
}

run_conformance_variant strict-preconditions
run_conformance_variant alternate-time-zone
"$script_directory/verify-test-artifacts.sh" "$artifacts_directory" complete >/dev/null

echo "Verified isolated test evidence: $artifacts_directory" >&2
