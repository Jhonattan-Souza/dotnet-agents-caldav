#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixture_root=$(mktemp -d)
cleanup() {
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

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
| Coverage report | 1.800s |
| Strict preconditions | 20.400s |
| Alternate time zone | 20.600s |
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

| Phase | Before | After | Change |
| --- | ---: | ---: | ---: |
| Restore local tools | 16.000s | 8.000s | -50.0% |
| Restore NuGet packages | 12.000s | 6.000s | -50.0% |
| Release build | 52.000s | 26.000s | -50.0% |
| Main core | 52.600s | 26.300s | -50.0% |
| Main MCP | 18.400s | 9.200s | -50.0% |
| Main integration | 10m 44.400s | 5m 22.200s | -50.0% |
| Coverage report | 3.600s | 1.800s | -50.0% |
| Strict preconditions | 40.800s | 20.400s | -50.0% |
| Alternate time zone | 41.200s | 20.600s | -50.0% |
| Isolated suite | 14m 12.000s | 7m 6.000s | -50.0% |
EOF

python3 "$script_directory/trx-timing-summary.py" \
  --artifacts-dir "$fixture_root" \
  --phase restore-local-tools=8 \
  --phase restore-nuget-packages=6 \
  --phase release-build=26 \
  --phase main-core=26.3 \
  --phase main-mcp=9.2 \
  --phase main-integration=322.2 \
  --phase coverage-report=1.8 \
  --phase strict-preconditions=20.4 \
  --phase alternate-time-zone=20.6 \
  --phase isolated-suite=426 \
  --baseline-phase restore-local-tools=16 \
  --baseline-phase restore-nuget-packages=12 \
  --baseline-phase release-build=52 \
  --baseline-phase main-core=52.6 \
  --baseline-phase main-mcp=18.4 \
  --baseline-phase main-integration=644.4 \
  --baseline-phase coverage-report=3.6 \
  --baseline-phase strict-preconditions=40.8 \
  --baseline-phase alternate-time-zone=41.2 \
  --baseline-phase isolated-suite=852 \
  --cache-status hit \
  > "$fixture_root/actual.md"

diff -u "$fixture_root/expected.md" "$fixture_root/actual.md"
echo "PASS stable TRX timing summary"
