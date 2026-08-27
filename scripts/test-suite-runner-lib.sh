#!/usr/bin/env bash

emit_test_suite_manifest_rows() {
  local manifest=$1 phase=$2
  python3 - "$manifest" "$phase" <<'PY'
import json
import sys
for artifact in json.load(open(sys.argv[1], encoding="utf-8"))["artifacts"]:
    if artifact["phase"] != sys.argv[2]:
        continue
    environment = ";".join(f"{key}={value}" for key, value in artifact["environment"].items())
    print("\x1f".join((artifact["project"], artifact["trx"], artifact.get("coveragePrefix", ""),
                       artifact.get("filterClass", ""), environment)))
PY
}

run_test_suite_manifest_phase() {
  local manifest=$1 phase=$2 callback=$3
  local row project trx prefix filter environment
  local -a rows
  mapfile -t rows < <(emit_test_suite_manifest_rows "$manifest" "$phase")
  for row in "${rows[@]}"; do
    IFS=$'\x1f' read -r project trx prefix filter environment <<< "$row"
    "$callback" "$project" "$trx" "$prefix" "$filter" "$environment" </dev/null
  done
}
