#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixture_root=$(mktemp -d)
cleanup() {
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

phase_file="$fixture_root/recorded-phases.tsv"
bash "$script_directory/record-phase-timing.sh" "$phase_file" successful-phase bash -c 'exit 0'
if bash "$script_directory/record-phase-timing.sh" "$phase_file" failed-phase bash -c 'exit 23'; then
  echo "Expected timed command failure to propagate." >&2
  exit 1
else
  status=$?
  [[ $status -eq 23 ]] || { echo "Unexpected timed command status: $status" >&2; exit 1; }
fi
python3 - "$phase_file" <<'PY'
import re
import sys

lines = open(sys.argv[1], encoding="utf-8").read().splitlines()
if len(lines) != 2:
    raise SystemExit(f"Expected two recorded phases, found {len(lines)}")
for expected, line in zip(("successful-phase", "failed-phase"), lines, strict=True):
    if re.fullmatch(rf"{expected}\t\d+\.\d{{3}}", line) is None:
        raise SystemExit(f"Unexpected phase record: {line!r}")
PY
echo "PASS phase timing preserves command status"

write_trx() {
  python3 - "$1" "$2" <<'PY'
import json
import sys
import xml.etree.ElementTree as ET

path, rows_json = sys.argv[1:]
rows = json.loads(rows_json)
namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
ET.register_namespace("", namespace)
root = ET.Element(f"{{{namespace}}}TestRun")
results = ET.SubElement(root, f"{{{namespace}}}Results")
for index, row in enumerate(rows):
    ET.SubElement(results, f"{{{namespace}}}UnitTestResult", {
        "executionId": f"execution-{index}",
        "testId": f"test-{index}",
        "testName": row[0],
        "outcome": "Passed",
        "duration": row[1],
    })
ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
PY
}

write_trx "$fixture_root/main-core.trx" '[["fast core", "00:00:00.5000000"], ["slow | core", "00:00:05.5000000"]]'
write_trx "$fixture_root/main-mcp.trx" '[["medium mcp", "00:00:01.2500000"]]'
write_trx "$fixture_root/main-integration.trx" '[["slowest integration", "00:00:07.2500000"], ["integration", "00:00:03.0000000"]]'

cat > "$fixture_root/phase-timings.tsv" <<'EOF'
restore-local-tools	8.000
restore-nuget-packages	6.000
release-build	26.000
main-core	26.300
main-mcp	9.200
main-integration	322.200
isolated-suite	426.000
EOF

cat > "$fixture_root/expected.md" <<'EOF'
## CI timing

Dependency cache: **hit**

| Phase | Duration |
| --- | ---: |
| Restore local tools | 8.000s |
| Restore NuGet packages | 6.000s |
| Release build | 26.000s |
| Main core | 26.300s |
| Main MCP | 9.200s |
| Main integration | 5m 22.200s |
| Isolated suite | 7m 6.000s |

### Test duration distribution

| Duration | Count |
| --- | ---: |
| < 1s | 1 |
| 1s to < 2s | 1 |
| 2s to < 5s | 1 |
| 5s to < 7s | 1 |
| >= 7s | 1 |

### Ten slowest tests

| Test | Evidence | Duration |
| --- | --- | ---: |
| slowest integration | main-integration.trx | 7.250s |
| slow \| core | main-core.trx | 5.500s |
| integration | main-integration.trx | 3.000s |
| medium mcp | main-mcp.trx | 1.250s |
| fast core | main-core.trx | 0.500s |

### Baseline comparison

| Measure | Baseline | Current | Change |
| --- | ---: | ---: | ---: |
| Isolated suite | 7m 6.000s | 7m 6.000s | 0.0% |
| Baseline integration | 5m 22.200s | 5m 22.200s | 0.0% |
EOF

python3 "$script_directory/trx-timing-summary.py" \
  --artifacts-dir "$fixture_root" \
  --phase-timings "$fixture_root/phase-timings.tsv" \
  --cache-status hit \
  --baseline-isolated-suite-seconds 426 \
  --baseline-integration-seconds 322.2 > "$fixture_root/actual.md"

diff -u "$fixture_root/expected.md" "$fixture_root/actual.md"
echo "PASS stable TRX timing summary"
