#!/usr/bin/env bash
set -euo pipefail
[[ $# == 1 ]] || { echo 'Usage: package.sh <external-run-directory>' >&2; exit 64; }
results_directory=$(realpath -- "$1")
repository_root=$(git rev-parse --show-toplevel)
package_checkout="$results_directory/package-checkout"
package_version=0.0.0-perf.20260905
git diff HEAD --binary -- src > "$results_directory/package-source.patch"
git clone --shared --no-checkout -- "$repository_root" "$package_checkout"
git -C "$package_checkout" checkout --detach "$(git rev-parse HEAD)"
git -C "$package_checkout" remote set-url origin "$(git -C "$repository_root" remote get-url origin)"
if [[ -s "$results_directory/package-source.patch" ]]; then
  git -C "$package_checkout" apply "$results_directory/package-source.patch"
fi
cd -- "$package_checkout"
metadata_path="$results_directory/package-metadata/server.json"
bash scripts/prepare-release-metadata.sh "v$package_version" "$metadata_path"
dotnet tool restore
dotnet restore
dotnet build -c Release --no-restore "/p:Version=$package_version" "/p:McpServerMetadataPath=$metadata_path"
dotnet pack src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj -c Release --no-build --no-restore \
  "/p:Version=$package_version" "/p:McpServerMetadataPath=$metadata_path" \
  --include-symbols -p:SymbolPackageFormat=snupkg -o "$results_directory/packages"
bash scripts/verify-release-package.sh "$package_version" "$results_directory/packages"
