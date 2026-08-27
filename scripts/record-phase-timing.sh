#!/usr/bin/env bash
set -uo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <timing-file> <phase-name> <command> [arguments...]" >&2
  exit 64
fi

timing_file=$1
phase_name=$2
shift 2
if [[ ! $phase_name =~ ^[a-z0-9]+(-[a-z0-9]+)*$ ]]; then
  echo "Phase name must contain only lowercase words separated by hyphens: $phase_name" >&2
  exit 64
fi
if [[ ! -d "$(dirname -- "$timing_file")" ]]; then
  echo "Timing-file parent does not exist: $timing_file" >&2
  exit 65
fi

started_at=$(date +%s%N)
if "$@"; then
  status=0
else
  status=$?
fi
finished_at=$(date +%s%N)
elapsed_ns=$((finished_at - started_at))
printf '%s\t%d.%03d\n' \
  "$phase_name" \
  "$((elapsed_ns / 1000000000))" \
  "$(((elapsed_ns % 1000000000) / 1000000))" >> "$timing_file"
exit "$status"
