#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
verifier="$script_directory/verify-test-artifacts.sh"
manifest="$script_directory/test-suite-manifest.json"
manifest_validator="$script_directory/validate-test-suite-manifest.py"
source_policy="$script_directory/verify-test-source-policy.py"
state_guard="$script_directory/verify-worktree-state.py"
cleanup="$script_directory/cleanup-test-artifacts.sh"
. "$script_directory/test-suite-runner-lib.sh"
fixture_root=$(mktemp -d)
inside_tmp=
caller_inside=
state_inside=
cleanup_selftest() {
  rm -rf -- "$fixture_root"
  [[ -z "$inside_tmp" ]] || rm -rf -- "$inside_tmp"
  [[ -z "$caller_inside" ]] || rm -rf -- "$caller_inside"
  [[ -z "$state_inside" ]] || rm -rf -- "$state_inside"
}
trap cleanup_selftest EXIT

write_trx() {
  python3 - "$1" "$2" "${3:-0}" <<'PY'
import sys
import xml.etree.ElementTree as ET
path, total_text, harness_text = sys.argv[1:]
total, harness = int(total_text), int(harness_text)
namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
ET.register_namespace("", namespace)
root = ET.Element(f"{{{namespace}}}TestRun")
results = ET.SubElement(root, f"{{{namespace}}}Results")
definitions = ET.SubElement(root, f"{{{namespace}}}TestDefinitions")
entries = ET.SubElement(root, f"{{{namespace}}}TestEntries")
for index in range(total):
    test_id = f"test-{index}"
    ET.SubElement(results, f"{{{namespace}}}UnitTestResult", {
        "executionId": f"execution-{index}", "testId": test_id, "testName": f"test {index}", "outcome": "Passed"})
    definition = ET.SubElement(definitions, f"{{{namespace}}}UnitTest", {"id": test_id, "name": f"test {index}"})
    ET.SubElement(definition, f"{{{namespace}}}TestMethod", {
        "className": ("DotnetAgents.CalDav.IntegrationTests.RadicaleConformanceHarnessTests"
                      if index < harness else "Synthetic.Tests"),
        "name": f"test {index}"})
    ET.SubElement(entries, f"{{{namespace}}}TestEntry", {
        "executionId": f"execution-{index}", "testId": test_id})
summary = ET.SubElement(root, f"{{{namespace}}}ResultSummary", {"outcome": "Completed"})
ET.SubElement(summary, f"{{{namespace}}}Counters", {
    "total": str(total), "executed": str(total), "passed": str(total), "failed": "0", "error": "0",
    "timeout": "0", "aborted": "0", "inconclusive": "0", "passedButRunAborted": "0",
    "notRunnable": "0", "notExecuted": "0", "disconnected": "0", "warning": "0",
    "completed": "0", "inProgress": "0", "pending": "0"})
ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
PY
}

seed_artifacts() {
  local directory=$1
  mkdir -p -- "$directory"
  local prefix
  for prefix in main-core main-mcp main-integration; do
    printf '<coverage />\n' > "$directory/$prefix.coverage.cobertura.260823000000000.xml"
    printf '<coverage />\n' > "$directory/$prefix.coverage.opencover.260823000000001.xml"
  done
  while IFS=$'\x1f' read -r trx total harness; do
    write_trx "$directory/$trx" "$total" "$harness"
  done < <(python3 - "$manifest" <<'PY'
import json, sys
for item in json.load(open(sys.argv[1], encoding="utf-8"))["artifacts"]:
    harness = item.get("requiredResult", {}).get("exactPassed", 0)
    print("\x1f".join((item["trx"], str(item["exactTests"]), str(harness))))
PY
  )
}

expect_rejected() {
  local description=$1
  shift
  if "$@" >/dev/null 2>&1; then
    echo "Expected rejection: $description" >&2
    exit 1
  fi
  echo "PASS $description"
}

current="$fixture_root/current"
seed_artifacts "$current"
actual=$("$verifier" "$current" complete)
expected="$(realpath "$current/main-core.coverage.cobertura.260823000000000.xml");$(realpath "$current/main-mcp.coverage.cobertura.260823000000000.xml");$(realpath "$current/main-integration.coverage.cobertura.260823000000000.xml")"
[[ "$actual" == "$expected" ]] || { echo "Unexpected Cobertura manifest: $actual" >&2; exit 1; }
echo "PASS exact five-artifact manifest"

truncated="$fixture_root/truncated"
seed_artifacts "$truncated"
printf '<TestRun>\n' > "$truncated/main-core.trx"
expect_rejected "truncated TRX is rejected" "$verifier" "$truncated" complete

missing_trx="$fixture_root/missing-trx"
seed_artifacts "$missing_trx"
rm -- "$missing_trx/main-integration.trx"
expect_rejected "missing TRX is rejected" "$verifier" "$missing_trx" complete

extra_trx="$fixture_root/extra-trx"
seed_artifacts "$extra_trx"
write_trx "$extra_trx/unexpected.trx" 1
expect_rejected "unexpected TRX is rejected" "$verifier" "$extra_trx" complete

missing_coverage="$fixture_root/missing-coverage"
seed_artifacts "$missing_coverage"
rm -- "$missing_coverage/main-mcp.coverage.opencover.260823000000001.xml"
expect_rejected "missing coverage report is rejected" "$verifier" "$missing_coverage" complete

duplicate_coverage="$fixture_root/duplicate-coverage"
seed_artifacts "$duplicate_coverage"
printf '<coverage />\n' > "$duplicate_coverage/main-core.coverage.cobertura.260823000000002.xml"
expect_rejected "duplicate coverage report is rejected" "$verifier" "$duplicate_coverage" complete

unknown_coverage="$fixture_root/unknown-coverage"
seed_artifacts "$unknown_coverage"
printf '<coverage />\n' > "$unknown_coverage/coverage.cobertura.xml"
expect_rejected "unknown root coverage report is rejected" "$verifier" "$unknown_coverage" complete

for delta in -1 1; do
  wrong="$fixture_root/wrong-main-$delta"
  seed_artifacts "$wrong"
  core_count=$(python3 - "$manifest" "$delta" <<'PY'
import json, sys
item = next(value for value in json.load(open(sys.argv[1], encoding="utf-8"))["artifacts"] if value["name"] == "main-core")
print(item["exactTests"] + int(sys.argv[2]))
PY
  )
  write_trx "$wrong/main-core.trx" "$core_count"
  expect_rejected "main count delta $delta is rejected" "$verifier" "$wrong" complete
done

integration_count=$(python3 - "$manifest" <<'PY'
import json, sys
print(next(item["exactTests"] for item in json.load(open(sys.argv[1], encoding="utf-8"))["artifacts"]
           if item["name"] == "main-integration"))
PY
)
for harness_count in 10 12; do
  wrong="$fixture_root/wrong-baseline-harness-$harness_count"
  seed_artifacts "$wrong"
  write_trx "$wrong/main-integration.trx" "$integration_count" "$harness_count"
  expect_rejected "baseline main TRX with $harness_count harness rows is rejected" "$verifier" "$wrong" complete
done

for delta in -1 1; do
  wrong="$fixture_root/wrong-variant-$delta"
  seed_artifacts "$wrong"
  write_trx "$wrong/strict-preconditions.trx" "$((11 + delta))"
  expect_rejected "variant count delta $delta is rejected" "$verifier" "$wrong" complete
done

forged="$fixture_root/forged-row"
seed_artifacts "$forged"
python3 - "$forged/main-core.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
result = next(element for element in tree.getroot().iter() if element.tag.endswith("UnitTestResult"))
result.set("outcome", "Failed")
tree.write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
expect_rejected "green counters cannot hide a failed result record" "$verifier" "$forged" complete

unknown="$fixture_root/unknown-test-id"
seed_artifacts "$unknown"
python3 - "$unknown/main-mcp.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
result = next(element for element in tree.getroot().iter() if element.tag.endswith("UnitTestResult"))
result.set("testId", "unknown")
tree.write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
expect_rejected "unknown result test IDs are rejected" "$verifier" "$unknown" complete

duplicate="$fixture_root/duplicate-result-identity"
seed_artifacts "$duplicate"
python3 - "$duplicate/main-mcp.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
results = [element for element in tree.getroot().iter() if element.tag.endswith("UnitTestResult")]
results[1].set("testId", results[0].get("testId"))
results[1].set("testName", results[0].get("testName"))
tree.write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
expect_rejected "duplicate passing result identities cannot inflate counters" "$verifier" "$duplicate" complete

duplicate_execution="$fixture_root/duplicate-execution"
seed_artifacts "$duplicate_execution"
python3 - "$duplicate_execution/main-mcp.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
results = [element for element in tree.getroot().iter() if element.tag.endswith("UnitTestResult")]
results[1].set("executionId", results[0].get("executionId"))
tree.write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
expect_rejected "duplicate execution IDs are rejected" "$verifier" "$duplicate_execution" complete

duplicate_definition="$fixture_root/duplicate-definition"
seed_artifacts "$duplicate_definition"
python3 - "$duplicate_definition/main-mcp.trx" <<'PY'
import sys, xml.etree.ElementTree as ET
tree = ET.parse(sys.argv[1])
definitions = [element for element in tree.getroot().iter() if element.tag.endswith("UnitTest")]
definitions[1].set("id", definitions[0].get("id"))
tree.write(sys.argv[1], encoding="utf-8", xml_declaration=True)
PY
expect_rejected "duplicate test definition IDs are rejected" "$verifier" "$duplicate_definition" complete

bad_manifest="$fixture_root/bad-manifest.json"
cp -- "$manifest" "$bad_manifest"
python3 - "$bad_manifest" <<'PY'
import json, sys
path = sys.argv[1]
document = json.load(open(path, encoding="utf-8"))
document["artifacts"][0]["project"] = "tests/Tiny/Tiny.csproj"
open(path, "w", encoding="utf-8").write(json.dumps(document))
PY
expect_rejected "manifest cannot replace the closed suite with a tiny project" python3 "$manifest_validator" "$bad_manifest"

declare -a transported_trx=()
consume_stdin_and_record_trx() {
  local _project=$1 row_trx=$2 _exact=$3 _prefix=$4 _filter=$5 _environment=$6
  cat >/dev/null
  transported_trx+=("$row_trx")
}
run_test_suite_manifest_phase "$manifest" main consume_stdin_and_record_trx
[[ "${transported_trx[*]}" == "main-core.trx main-mcp.trx main-integration.trx" ]] || {
  echo "A child process consumed a pending manifest row." >&2
  exit 1
}
echo "PASS materialized manifest rows survive child stdin consumption"

early_exit_tmp="$fixture_root/early-exit-tmp"
mkdir -p -- "$early_exit_tmp"
if TMPDIR="$early_exit_tmp" "$script_directory/run-test-suite.sh" invalid >/dev/null 2>&1; then
  echo "Expected invalid runner arguments to fail." >&2
  exit 1
fi
if find "$early_exit_tmp" -mindepth 1 -print -quit | grep -q .; then
  echo "Runner leaked temporary worktree state during early validation." >&2
  exit 1
fi
echo "PASS runner early validation leaves no worktree-state artifacts"

capture_failure_tmp="$fixture_root/capture-failure-tmp"
fake_bin="$fixture_root/fake-bin"
mkdir -p -- "$capture_failure_tmp" "$fake_bin"
printf '%s\n' '#!/usr/bin/env bash' 'exit 69' > "$fake_bin/git"
chmod +x "$fake_bin/git"
if capture_output=$(PATH="$fake_bin:$PATH" TMPDIR="$capture_failure_tmp" \
  "$script_directory/run-test-suite.sh" 2>&1); then
  echo "Expected worktree-state capture failure." >&2
  exit 1
fi
captured_artifact=$(printf '%s\n' "$capture_output" | sed -n 's/^Test artifacts: //p' | head -1)
[[ -n "$captured_artifact" && -d "$captured_artifact" ]] || {
  echo "Runner did not retain and print its accepted artifact root after capture failure." >&2
  exit 1
}
printf '%s\n' "$capture_output" | grep -F \
  "Test artifacts retained after worktree-state capture failure: $captured_artifact" >/dev/null
"$cleanup" "$captured_artifact" "$capture_failure_tmp"
echo "PASS worktree-state capture failure retains and prints accepted artifacts"

separator_tmp="$fixture_root/early;separator"
mkdir -p -- "$separator_tmp"
if TMPDIR="$separator_tmp" "$script_directory/run-test-suite.sh" >/dev/null 2>&1; then
  echo "Expected a temporary parent containing a semicolon to be rejected." >&2
  exit 1
fi
if find "$separator_tmp" -mindepth 1 -print -quit | grep -q .; then
  echo "Runner leaked an owned artifact before rejecting its temporary parent." >&2
  exit 1
fi
echo "PASS unsafe temporary parent is rejected before artifact creation"

repository_root=$(cd -- "$script_directory/.." && pwd)
inside_tmp="$repository_root/tests/DotnetAgents.CalDav.Core.Tests.Unit/bin/runner-inside-$RANDOM"
mkdir -p -- "$inside_tmp"
if TMPDIR="$inside_tmp" "$script_directory/run-test-suite.sh" >/dev/null 2>&1; then
  echo "Expected a repository-contained owned artifact directory to be rejected." >&2
  exit 1
fi
if find "$inside_tmp" -mindepth 1 -print -quit | grep -q .; then
  echo "Runner leaked an owned artifact directory inside the repository." >&2
  exit 1
fi
rmdir -- "$inside_tmp"
echo "PASS repository-contained owned artifacts are rejected and cleaned"

caller_inside="$repository_root/tests/DotnetAgents.CalDav.Core.Tests.Unit/bin/caller-inside-$RANDOM"
mkdir -p -- "$caller_inside"
expect_rejected "repository-contained caller artifact directory is rejected" \
  "$script_directory/run-test-suite.sh" --artifacts-dir "$caller_inside"
rmdir -- "$caller_inside"

state_inside="$repository_root/tests/DotnetAgents.CalDav.Core.Tests.Unit/bin/state-inside-$RANDOM"
outside_caller="$fixture_root/outside-caller"
mkdir -p -- "$state_inside" "$outside_caller"
if TMPDIR="$state_inside" "$script_directory/run-test-suite.sh" \
  --artifacts-dir "$outside_caller" >/dev/null 2>&1; then
  echo "Expected a repository-contained worktree-state parent to be rejected." >&2
  exit 1
fi
[[ -z "$(find "$outside_caller" -mindepth 1 -maxdepth 1 -print -quit)" ]] || {
  echo "Runner changed its caller-owned artifact root while rejecting the state parent." >&2
  exit 1
}
rmdir -- "$state_inside"
state_inside=
echo "PASS repository-contained worktree-state directories are rejected"

policy_ok="$fixture_root/policy-ok"
mkdir -p -- "$policy_ok/bin" "$policy_ok/obj"
printf '%s\n' '// [Fact(Skip = "comment")]' 'var text = "Flaky Assert.Skip()";' 'var raw = """[Theory(SkipWhen = true)]""";' '[Fact] public void Passes() { }' > "$policy_ok/Ok.cs"
printf '%s\n' '[Fact(Skip = "ignored output")]' > "$policy_ok/bin/Ignored.cs"
python3 "$source_policy" "$policy_ok"
echo "PASS source policy ignores comments, literals, bin, and obj"

declare -a disabled_sources=(
  $'[Fact(\n Skip = "reason")] public void A() {}'
  $'[Xunit.TheoryAttribute(\n Explicit = true)] public void A() {}'
  $'[Fact(\n SkipWhen = true)] public void A() {}'
  $'[Theory(\n SkipUnless = false)] public void A() {}'
  $'[Fact(\n SkipExceptions = [typeof(TimeoutException)])] public void A() {}'
  $'[Theory(\n SkipTestWithoutData = true)] public void A() {}'
  $'[global::Xunit.v3.FactAttribute(\n Skip = "reason")] public void A() {}'
  $'[global::Xunit.Fact(\n Skip = "reason")] public void A() {}'
  $'using Disabled = Xunit.FactAttribute; [Disabled(\n Skip = "reason")] public void A() {}'
  $'[global::Xunit.v3.ExplicitAttribute] [Fact] public void A() {}'
  $'[SkippableFact] public void A() {}'
  $'[SkippableTheory] public void A() {}'
  $'using Disabled = Xunit.SkippableFactAttribute; [Disabled] public void A() {}'
  $'[Fact] public void A() { Assert.\n Skip("reason"); }'
  $'[Fact] public void A() { Assert.SkipWhen(true, "reason"); }'
  $'[Fact] public void A() { Assert.SkipUnless(false, "reason"); }'
  $'using Check = Xunit.Assert; [Fact] public void A() { Check.SkipWhen(true, "reason"); }'
  $'using static Xunit.Assert; [Fact] public void A() { SkipWhen(true, "reason"); }'
  $'[Fact] public void A() { throw Xunit.Sdk.SkipException.ForSkip("reason"); }'
  $'[Trait("Category", "Flaky")] [Fact] public void A() {}'
  $'[global::Xunit.TraitAttribute("Category", "Quarantined")] [Fact] public void A() {}'
  $'[Fact, Trait("Category", "Flaky")] public void A() {}'
  $'sealed class ConditionalFactAttribute : Xunit.FactAttribute { public ConditionalFactAttribute() { Skip = "reason"; } } [ConditionalFact] public void A() {}'
  $'using BaseFact = Xunit.FactAttribute; sealed class ConditionalFactAttribute : BaseFact { } [ConditionalFact] public void A() {}'
  $'[Fact] public void Quarantined_case() {}'
  $'[Fact] public void FlakyCase() {}'
)
index=0
for source in "${disabled_sources[@]}"; do
  rejected="$fixture_root/policy-$index.cs"
  printf '%s\n' "$source" > "$rejected"
  expect_rejected "disabled test source shape $index is rejected" python3 "$source_policy" "$rejected"
  ((index += 1))
done

new_repository() {
  local path=$1
  git init -q "$path"
  git -C "$path" config user.email tests@example.invalid
  git -C "$path" config user.name Tests
  printf 'tracked\n' > "$path/tracked.txt"
  git -C "$path" add tracked.txt
  git -C "$path" commit -qm initial
}

unchanged_repo="$fixture_root/unchanged-repo"
new_repository "$unchanged_repo"
printf 'dirty\n' >> "$unchanged_repo/tracked.txt"
printf 'untracked\n' > "$unchanged_repo/untracked.txt"
python3 "$state_guard" capture "$unchanged_repo" "$fixture_root/unchanged-before.json"
python3 "$state_guard" compare "$unchanged_repo" "$fixture_root/unchanged-before.json" "$fixture_root/unchanged-after.json"
echo "PASS initially dirty worktree is accepted when unchanged"

guard_case() {
  local name=$1
  local repository="$fixture_root/$name"
  new_repository "$repository"
  shift
  "$@" "$repository"
  expect_rejected "$name is detected" python3 "$state_guard" compare "$repository" \
    "$fixture_root/$name-before.json" "$fixture_root/$name-after.json"
}

tracked_change() { printf 'dirty\n' >> "$1/tracked.txt"; python3 "$state_guard" capture "$1" "$fixture_root/tracked-change-before.json"; printf 'again\n' >> "$1/tracked.txt"; }
index_change() { python3 "$state_guard" capture "$1" "$fixture_root/index-change-before.json"; printf 'staged\n' >> "$1/tracked.txt"; git -C "$1" add tracked.txt; }
new_untracked() { python3 "$state_guard" capture "$1" "$fixture_root/new-untracked-before.json"; mkdir -p "$1/new"; printf 'new\n' > "$1/new/file.txt"; }
modified_untracked() { printf 'first\n' > "$1/untracked.txt"; python3 "$state_guard" capture "$1" "$fixture_root/modified-untracked-before.json"; printf 'second\n' >> "$1/untracked.txt"; }
mode_change() { git -C "$1" config core.fileMode false; python3 "$state_guard" capture "$1" "$fixture_root/mode-change-before.json"; chmod +x "$1/tracked.txt"; }
guard_case tracked-change tracked_change
guard_case index-change index_change
guard_case new-untracked new_untracked
guard_case modified-untracked modified_untracked
guard_case mode-change mode_change

runner_temp="$fixture_root/runner-temp"
cleanup_target="$runner_temp/caldav-tests.ABC123"
mkdir -p -- "$cleanup_target"
printf 'generated\n' > "$cleanup_target/evidence.txt"
"$cleanup" "$cleanup_target" "$runner_temp"
[[ ! -e "$cleanup_target" ]] || { echo "Generated artifact directory was retained." >&2; exit 1; }
echo "PASS cleanup removes only an authorized generated directory"

unauthorized_target="$runner_temp/TestResults"
mkdir -p -- "$unauthorized_target"
printf 'preserve\n' > "$unauthorized_target/evidence.txt"
expect_rejected "cleanup refuses paths outside its generated namespace" "$cleanup" "$unauthorized_target" "$runner_temp"
[[ -f "$unauthorized_target/evidence.txt" ]] || { echo "Cleanup changed an unauthorized directory." >&2; exit 1; }
echo "PASS cleanup preserves unauthorized directories"

symlink_target="$runner_temp/caldav-tests.TARGET"
mkdir -p -- "$symlink_target"
printf 'preserve\n' > "$symlink_target/evidence.txt"
symlink_path="$runner_temp/caldav-tests.SYMLNK"
ln -s -- "$symlink_target" "$symlink_path"
expect_rejected "cleanup refuses symbolic-link artifact paths" "$cleanup" "$symlink_path" "$runner_temp"
[[ -f "$symlink_target/evidence.txt" ]] || { echo "Cleanup followed a symbolic link." >&2; exit 1; }
expect_rejected "cleanup refuses symbolic-link artifact paths with a trailing slash" \
  "$cleanup" "$symlink_path/" "$runner_temp"
[[ -f "$symlink_target/evidence.txt" ]] || { echo "Cleanup followed a trailing-slash symbolic link." >&2; exit 1; }
echo "PASS cleanup preserves symbolic-link targets"
