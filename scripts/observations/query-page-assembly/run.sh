#!/usr/bin/env bash
set -euo pipefail

readonly entity_revision=4df75347477ca6dae463d60b938c7d28ab9b6ea6
readonly occurrence_revision=e63ea4d62fa4b4062566a6819127c18a30a1a38d
readonly todo_revision=8a9d887a0b5e44ffbca3025a41ae7c8f6705dd77
readonly current_revision=61f2607383807f96464f33350e608180c1abee49

repository_root=$(git rev-parse --show-toplevel)
fixtures_directory="$repository_root/scripts/observations/query-page-assembly"
results_directory=${1:-}
if [[ -z "$results_directory" || "$results_directory" != /* || ! -d "$results_directory" ]]; then
  echo "usage: $0 /absolute/results-directory" >&2
  exit 64
fi
results_directory=$(realpath -e -- "$results_directory")
case "$results_directory/" in
  "$repository_root/"*)
    echo "results directory must be outside the repository: $results_directory" >&2
    exit 66
    ;;
esac
if [[ -n "$(find "$results_directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "results directory must be empty: $results_directory" >&2
  exit 65
fi

temporary_root=$(mktemp -d "/tmp/caldav-page-assembly.XXXXXX")
cleanup() {
  rm -rf -- "$temporary_root"
}
trap cleanup EXIT HUP INT TERM

git clone --shared --no-checkout "$repository_root" "$temporary_root/repository" >/dev/null

run_historical() {
  local family=$1
  local revision=$2
  local fixture=$3
  local checkout="$temporary_root/$family-historical"
  git -C "$temporary_root/repository" worktree add --detach "$checkout" "$revision" >/dev/null
  cp -- "$fixtures_directory/historical-support.cs" "$checkout/src/DotnetAgents.CalDav.Mcp/QueryPageAssemblyObservationSupport.cs"
  mv -- "$checkout/src/DotnetAgents.CalDav.Mcp/Program.cs" "$checkout/src/DotnetAgents.CalDav.Mcp/Program.original.txt"
  cp -- "$fixtures_directory/$fixture" "$checkout/src/DotnetAgents.CalDav.Mcp/Program.cs"
  dotnet restore "$checkout/src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj" --locked-mode -p:NuGetAudit=false >/dev/null
  local log="$results_directory/$family-historical.log"
  dotnet run --project "$checkout/src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj" -c Release --framework net10.0 --no-restore \
    > "$log" 2>&1
  sed -n 's/^PAGE_ASSEMBLY_OBSERVATION_JSON=//p' "$log" > "$results_directory/$family-historical.json"
  [[ $(wc -l < "$results_directory/$family-historical.json") -eq 1 ]]
  git -C "$temporary_root/repository" worktree remove --force "$checkout" >/dev/null
}

run_current() {
  local checkout="$temporary_root/current"
  git -C "$temporary_root/repository" worktree add --detach "$checkout" "$current_revision" >/dev/null
  cp -- "$fixtures_directory/current-fixture.cs" "$checkout/src/DotnetAgents.CalDav.Core/QueryPageAssemblyObservation.cs"
  dotnet restore "$checkout/src/DotnetAgents.CalDav.Core/DotnetAgents.CalDav.Core.csproj" --locked-mode -p:NuGetAudit=false >/dev/null
  local log="$results_directory/current.log"
  dotnet run --project "$checkout/src/DotnetAgents.CalDav.Core/DotnetAgents.CalDav.Core.csproj" -c Release --framework net10.0 --no-restore \
    -p:OutputType=Exe -- "$results_directory" > "$log" 2>&1
  sed -n 's/^PAGE_ASSEMBLY_OBSERVATION_JSON=//p' "$log" > "$results_directory/current.json"
  [[ $(wc -l < "$results_directory/current.json") -eq 1 ]]
  git -C "$temporary_root/repository" worktree remove --force "$checkout" >/dev/null
}

run_historical entity "$entity_revision" historical-entity-fixture.cs
run_historical occurrence "$occurrence_revision" historical-occurrence-fixture.cs
run_historical todo "$todo_revision" historical-todo-fixture.cs
run_current

python3 - "$results_directory" "$repository_root" "$fixtures_directory" "$entity_revision" "$occurrence_revision" "$todo_revision" "$current_revision" <<'PY'
import hashlib
import json
import pathlib
import platform
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
repository = sys.argv[2]
fixtures = pathlib.Path(sys.argv[3])
observations = []
for path in sorted(root.glob("*.json")):
    observations.extend(json.loads(path.read_text()))
if len(observations) != 18:
    raise SystemExit(f"expected 18 observations, found {len(observations)}")
if {entry["PageSize"] for entry in observations} != {1, 50, 200}:
    raise SystemExit("observation page sizes are incomplete")
expected = {(family, implementation, page_size)
            for family in ("entity", "occurrence", "todo")
            for implementation in ("historical-private-create-page", "current-page-codec")
            for page_size in (1, 50, 200)}
actual = {(entry["Family"], entry["Implementation"], entry["PageSize"]) for entry in observations}
if actual != expected:
    raise SystemExit(f"observation matrix mismatch: expected {sorted(expected)}, found {sorted(actual)}")
if any(entry["CorpusCount"] != 201 or entry["Warmups"] != 12 or entry["Samples"] != 9 for entry in observations):
    raise SystemExit("observation corpus or sampling metadata is incomplete")
revisions = {
    ("entity", "historical-private-create-page"): sys.argv[4],
    ("occurrence", "historical-private-create-page"): sys.argv[5],
    ("todo", "historical-private-create-page"): sys.argv[6],
    ("entity", "current-page-codec"): sys.argv[7],
    ("occurrence", "current-page-codec"): sys.argv[7],
    ("todo", "current-page-codec"): sys.argv[7],
}
for entry in observations:
    if entry["Revision"] != revisions[(entry["Family"], entry["Implementation"])]:
        raise SystemExit(f"revision pin mismatch for {entry['Family']}/{entry['Implementation']}")
runtime_keys = ("Runtime", "RuntimeIdentifier", "OperatingSystem", "OperatingSystemArchitecture", "ProcessArchitecture", "IsServerGc")
if len({tuple(entry[key] for key in runtime_keys) for entry in observations}) != 1:
    raise SystemExit("runtime metadata differs between observations")
historical = [entry for entry in observations if entry["Implementation"] == "historical-private-create-page"]
for family in ("entity", "occurrence", "todo"):
    family_rows = [entry for entry in historical if entry["Family"] == family]
    if len(family_rows) != 3 or len({entry["CorpusItemBytesSha256"] for entry in family_rows}) != 1 \
            or len({entry["CorpusEncodedByteCount"] for entry in family_rows}) != 1 \
            or len({json.dumps(entry["CorpusItems"], separators=(",", ":")) for entry in family_rows}) != 1:
        raise SystemExit(f"historical {family} corpus is not identical across page sizes")
by_key = {(entry["Family"], entry["Implementation"], entry["PageSize"]): entry for entry in observations}
for family in ("entity", "occurrence", "todo"):
    for page_size in (1, 50, 200):
        old = by_key[(family, "historical-private-create-page", page_size)]
        new = by_key[(family, "current-page-codec", page_size)]
        if new["SourceCorpusItemBytesSha256"] != old["CorpusItemBytesSha256"] \
                or new["SourceCorpusEncodedByteCount"] != old["CorpusEncodedByteCount"] \
                or new["HistoricalComparablePrefixSha256"] != old["AdmittedItemBytesSha256"] \
                or new["HistoricalComparablePrefixEncodedByteCount"] != old["AdmittedItemEncodedByteCount"]:
            raise SystemExit(f"current {family} page {page_size} did not report the historical corpus/prefix")
        expected_counts = (old["AdmittedItemCount"], 200) if family == "todo" and page_size == 200 else (old["AdmittedItemCount"], old["AdmittedItemCount"])
        if (old["AdmittedItemCount"], new["AdmittedItemCount"]) != expected_counts:
            raise SystemExit(f"unexpected admitted counts for {family} page {page_size}")
metadata = {
    "historicalRevisions": {"entity": sys.argv[4], "occurrence": sys.argv[5], "todo": sys.argv[6]},
    "currentRevision": sys.argv[7],
    "observationCount": len(observations),
    "pythonRuntime": platform.python_version(),
    "hostPlatform": platform.platform(),
    "dotnetSdk": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
    "gcSettings": {"serverGc": observations[0].get("IsServerGc")},
    "fixtureSha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(fixtures.glob("*.cs"))},
    "sourceBlobs": {
        "entityHistoricalCreatePage": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[4]}:src/DotnetAgents.CalDav.Mcp/Tools/CalendarEntityTools.cs"], text=True).strip(),
        "occurrenceHistoricalCreatePage": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[5]}:src/DotnetAgents.CalDav.Mcp/Tools/CalendarOccurrenceTools.cs"], text=True).strip(),
        "todoHistoricalCreatePage": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[6]}:src/DotnetAgents.CalDav.Mcp/Tools/CalendarTodoTools.cs"], text=True).strip(),
        "currentEntityCodec": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[7]}:src/DotnetAgents.CalDav.Core/Internal/CalendarEntityQueryPageCodec.cs"], text=True).strip(),
        "currentOccurrenceCodec": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[7]}:src/DotnetAgents.CalDav.Core/Internal/CalendarOccurrenceQueryPageCodec.cs"], text=True).strip(),
        "currentTodoCodec": subprocess.check_output(["git", "-C", repository, "rev-parse", f"{sys.argv[7]}:src/DotnetAgents.CalDav.Core/Internal/CalendarTodoQueryPageCodec.cs"], text=True).strip(),
    },
    "method": "actual historical private CreatePage through MethodInfo.CreateDelegate; actual current page codecs",
    "measurement": "synchronous current-thread allocations plus Stopwatch; 12 warmups; 9 samples; no forced GC; no threshold",
}
(root / "runner-metadata.json").write_text(json.dumps(metadata, indent=2) + "\n")
PY

printf 'query page-assembly observations: %s\n' "$results_directory"
