#!/usr/bin/env bash
set -euo pipefail

readonly direct_revision=ed316f9beb46a81d69e785006c0fe65d58c2298b
readonly temporal_revision=05f4973c8d4c1423da1c55cde4dbb0ee3c89b2f8
readonly occurrence_revision=e63ea4d62fa4b4062566a6819127c18a30a1a38d
readonly changed_revision=61f2607383807f96464f33350e608180c1abee49

repository_root=$(git rev-parse --show-toplevel)
fixture_root="$repository_root/scripts/observations/query-focused-duration"
results_directory=${1:-}
if [[ -z "$results_directory" || "$results_directory" != /* || ! -d "$results_directory" ]]; then
  echo "usage: $0 /absolute/empty/results-directory" >&2
  exit 64
fi
results_directory=$(realpath -- "$results_directory")
case "$results_directory/" in
  "$repository_root/"*)
    echo "results directory must be outside the repository" >&2
    exit 65
    ;;
esac
if [[ -n "$(find "$results_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "results directory must be empty: $results_directory" >&2
  exit 65
fi

temporary_root=$(mktemp -d "/tmp/caldav-query-duration.XXXXXX")
cleanup() {
  rm -rf -- "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

git clone --shared --no-checkout "$repository_root" "$temporary_root/repository" >/dev/null
git -C "$temporary_root/repository" worktree add --detach "$temporary_root/direct" "$direct_revision" >/dev/null
git -C "$temporary_root/repository" worktree add --detach "$temporary_root/temporal" "$temporal_revision" >/dev/null
git -C "$temporary_root/repository" worktree add --detach "$temporary_root/occurrence" "$occurrence_revision" >/dev/null
git -C "$temporary_root/repository" worktree add --detach "$temporary_root/changed" "$changed_revision" >/dev/null

cp -- "$fixture_root/direct-baseline.cs" \
  "$temporary_root/direct/tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/Issue116LegacyDirectGetDurationTests.cs"
git -C "$temporary_root/temporal" apply "$fixture_root/temporal-baseline.patch"
git -C "$temporary_root/occurrence" apply "$fixture_root/occurrence-baseline.patch"
git -C "$temporary_root/changed" apply "$fixture_root/current-temporal.patch"

restore_build() {
  local checkout=$1 project=$2 log=$3
  dotnet restore "$checkout/$project" --locked-mode -p:NuGetAudit=false > "$results_directory/$log" 2>&1
  dotnet build "$checkout/$project" -c Release --no-restore >> "$results_directory/$log" 2>&1
}

run_test() {
  local checkout=$1 project=$2 filter=$3 expected=$4 trx=$5 log=$6
  dotnet test --project "$checkout/$project" -c Release --no-build --no-restore \
    --filter-method "$filter" \
    --results-directory "$results_directory" \
    --report-trx --report-trx-filename "$trx" \
    --minimum-expected-tests "$expected" --fail-skips on --zero-tests-policy strict --no-ansi \
    > "$results_directory/$log" 2>&1
}

readonly core_project=tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj
readonly mcp_project=tests/DotnetAgents.CalDav.Mcp.Tests.Unit/DotnetAgents.CalDav.Mcp.Tests.Unit.csproj

restore_build "$temporary_root/direct" "$core_project" direct-baseline-build.log
run_test "$temporary_root/direct" "$core_project" \
  '*Issue116LegacyDirectGetDurationTests.FiveUnavailableMultigetResourcesUseFiveSequentialDirectReads' \
  1 direct-baseline.trx direct-baseline.log

restore_build "$temporary_root/temporal" "$core_project" temporal-baseline-build.log
run_test "$temporary_root/temporal" "$core_project" \
  '*CalendarQueryModuleTests.BaselineBoundedDateOnlyStartWithoutContextPerformsAuthoritativeWork' \
  1 temporal-baseline.trx temporal-baseline.log

restore_build "$temporary_root/occurrence" "$mcp_project" occurrence-baseline-build.log
run_test "$temporary_root/occurrence" "$mcp_project" \
  '*CalendarOccurrenceToolsTests.BaselineContinuationReexecutesTheComplete201OccurrenceQuery' \
  3 occurrence-baseline.trx occurrence-baseline.log

restore_build "$temporary_root/changed" "$core_project" changed-core-build.log
run_test "$temporary_root/changed" "$core_project" \
  '*CalendarQueryDirectGetTests.FiveFallbackResourcesRunAsOneWaveOfFourThenOne' \
  1 direct-changed.trx direct-changed.log
run_test "$temporary_root/changed" "$core_project" \
  '*CalendarQueryModuleTests.BoundedStartWithoutTemporalContextFailsBeforeAnyCalDavWork' \
  1 temporal-changed.trx temporal-changed.log

run_test "$temporary_root/changed" "$core_project" \
  '*CalendarOccurrenceQueryModuleTests.EqualSemanticKeysTraverseByResourceHrefWithoutDuplicatesOrOmissions' \
  3 occurrence-changed.trx occurrence-changed.log

python3 - "$results_directory" "$fixture_root" \
  "$direct_revision" "$temporal_revision" "$occurrence_revision" "$changed_revision" <<'PY'
import hashlib
import json
import pathlib
import platform
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

root = pathlib.Path(sys.argv[1])
fixtures = pathlib.Path(sys.argv[2])
expected = {
    "direct-baseline.trx": 1,
    "direct-changed.trx": 1,
    "temporal-baseline.trx": 1,
    "temporal-changed.trx": 1,
    "occurrence-baseline.trx": 3,
    "occurrence-changed.trx": 3,
}
observations = []
for name, count in expected.items():
    rows = [element for element in ET.parse(root / name).getroot().iter()
            if element.tag.endswith("UnitTestResult")]
    if len(rows) != count or any(row.get("outcome") != "Passed" for row in rows):
        raise SystemExit(f"{name}: expected exactly {count} Passed result records")
    observations.extend({
        "artifact": name,
        "testName": row.get("testName"),
        "duration": row.get("duration"),
        "outcome": row.get("outcome"),
    } for row in rows)
dotnet_info = subprocess.check_output(["dotnet", "--info"], text=True)
host_version = re.search(r"Host:\s*\n\s*Version:\s*([^\s]+)", dotnet_info)
runtime_identifier = re.search(r"\bRID:\s*([^\s]+)", dotnet_info)
if host_version is None or runtime_identifier is None:
    raise SystemExit("dotnet --info did not contain the host version and RID")
metadata = {
    "revisions": {
        "directBaseline": sys.argv[3],
        "temporalBaseline": sys.argv[4],
        "occurrenceBaseline": sys.argv[5],
        "changed": sys.argv[6],
    },
    "runtime": {
        "dotnetSdk": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
        "dotnetHost": host_version.group(1),
        "runtimeIdentifier": runtime_identifier.group(1),
        "platform": platform.platform(),
        "dotnetInfoSha256": hashlib.sha256(dotnet_info.encode()).hexdigest(),
        "gcMode": "N/A (duration-only observation)",
    },
    "policy": "Release; exact minimum; fail-skips; strict zero-tests; single supporting observation; no threshold",
    "fixtureSha256": {
        path.name: hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(fixtures.iterdir()) if path.suffix in {".cs", ".patch"}
    },
    "observations": observations,
}
(root / "runner-metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
PY

printf 'focused query duration observations: %s\n' "$results_directory"
