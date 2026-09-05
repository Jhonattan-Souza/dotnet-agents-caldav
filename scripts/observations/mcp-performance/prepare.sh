#!/usr/bin/env bash
set -euo pipefail
[[ $# == 2 ]] || { echo 'Usage: prepare.sh <new-external-directory> <baseline-sha>' >&2; exit 64; }
results_directory=$(realpath -m -- "$1")
baseline_revision=$2
repository_root=$(git rev-parse --show-toplevel)
case "$results_directory/" in "$repository_root/"*) echo 'Use an external directory' >&2; exit 65;; esac
mkdir -- "$results_directory"
git status --porcelain=v1 > "$results_directory/initial-git-status.txt"
git rev-parse HEAD > "$results_directory/initial-sha.txt"
dotnet --info > "$results_directory/dotnet-info.txt"
lscpu > "$results_directory/cpu.txt"
free -b > "$results_directory/memory.txt"
hermes --version > "$results_directory/hermes-version.txt"
aspire --version > "$results_directory/aspire-version.txt" 2>&1
git clone --shared --no-checkout -- "$repository_root" "$results_directory/baseline-checkout"
git -C "$results_directory/baseline-checkout" checkout --detach "$baseline_revision"
git -C "$results_directory/baseline-checkout" remote set-url origin "$(git -C "$repository_root" remote get-url origin)"
(
  cd -- "$results_directory/baseline-checkout"
  dotnet tool restore
  dotnet restore
  dotnet build -c Release --no-restore
) > "$results_directory/baseline-build.log" 2>&1
dotnet tool restore > "$results_directory/candidate-build.log" 2>&1
dotnet restore >> "$results_directory/candidate-build.log" 2>&1
dotnet build -c Release --no-restore >> "$results_directory/candidate-build.log" 2>&1
mkdir -- "$results_directory/baseline" "$results_directory/candidate"
cp -a -- "$results_directory/baseline-checkout/src/DotnetAgents.CalDav.Mcp/bin/Release/net10.0/." "$results_directory/baseline/"
cp -a -- "$repository_root/src/DotnetAgents.CalDav.Mcp/bin/Release/net10.0/." "$results_directory/candidate/"
sha256sum "$results_directory/"{baseline,candidate}/DotnetAgents.CalDav.{Core,Mcp}.dll > "$results_directory/assembly-hashes.txt"
