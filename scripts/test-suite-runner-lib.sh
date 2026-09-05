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
  local manifest=$1 phase=$2
  if [[ $# -ne 4 || ! $3 =~ ^[1-9][0-9]*$ ]]; then
    echo "Usage: run_test_suite_manifest_phase <manifest> <phase> <worker-limit> <callback>" >&2
    return 64
  fi
  local worker_limit=$3 callback=$4
  local row project trx prefix filter environment
  local next=0 failure_observed=0 finished_pid child_status index failure_index=-1
  local -a rows pids trx_by_index
  local -A index_by_pid status_by_index
  mapfile -t rows < <(emit_test_suite_manifest_rows "$manifest" "$phase")
  pids=()
  while (( next < ${#rows[@]} || ${#pids[@]} > 0 )); do
    while (( failure_observed == 0 && next < ${#rows[@]} && ${#pids[@]} < worker_limit )); do
      row=${rows[$next]}
      IFS=$'\x1f' read -r project trx prefix filter environment <<< "$row"
      (
        echo "START test manifest entry [$trx]" >&2
        if "$callback" "$project" "$trx" "$prefix" "$filter" "$environment" </dev/null; then
          echo "PASS test manifest entry [$trx]" >&2
        else
          child_status=$?
          echo "FAIL test manifest entry [$trx] (exit $child_status)" >&2
          exit "$child_status"
        fi
      ) &
      pids+=("$!")
      index_by_pid["$!"]=$next
      trx_by_index[$next]=$trx
      ((next += 1))
    done

    if (( ${#pids[@]} == 0 )); then
      break
    fi
    if wait -n -p finished_pid "${pids[@]}"; then
      child_status=0
    else
      child_status=$?
      failure_observed=1
    fi
    index=${index_by_pid[$finished_pid]}
    status_by_index[$index]=$child_status
    for index in "${!pids[@]}"; do
      if [[ "${pids[$index]}" == "$finished_pid" ]]; then
        unset 'pids[index]'
        break
      fi
    done
    pids=("${pids[@]}")
  done
  for ((index = 0; index < next; index += 1)); do
    if (( ${status_by_index[$index]:-0} != 0 )); then
      failure_index=$index
      break
    fi
  done
  if (( failure_index >= 0 )); then
    child_status=${status_by_index[$failure_index]}
    echo "FAIL test manifest phase [$phase]: first manifest failure [${trx_by_index[$failure_index]}] (exit $child_status)" >&2
    return "$child_status"
  fi
  return 0
}
