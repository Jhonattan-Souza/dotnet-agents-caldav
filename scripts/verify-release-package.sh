#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <package-version> <package-directory>" >&2
  exit 2
fi

package_version=$1
package_directory=$2
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
temporary_directory=$(mktemp -d)
trap 'rm -rf -- "$temporary_directory"' EXIT
package_id=dotnet-agents-caldav
tool_path=tools/net10.0/any

mapfile -t packages < <(find "$package_directory" -maxdepth 1 -type f -name '*.nupkg' ! -name '*.snupkg' -print)
mapfile -t symbols < <(find "$package_directory" -maxdepth 1 -type f -name '*.snupkg' -print)
if [[ ${#packages[@]} -ne 1 || ${#symbols[@]} -ne 1 ]]; then
  echo "expected exactly one .nupkg and one .snupkg in $package_directory" >&2
  exit 1
fi

package=${packages[0]}
symbol_package=${symbols[0]}

verify_identity() {
  local archive=$1
  local nuspec
  mapfile -t nuspecs < <(unzip -Z1 "$archive" | grep -E '\.nuspec$')
  if [[ ${#nuspecs[@]} -ne 1 ]]; then
    echo "expected exactly one nuspec in $archive" >&2
    return 1
  fi
  nuspec=${nuspecs[0]}
  local actual_id actual_version
  actual_id=$(unzip -p "$archive" "$nuspec" | sed -n 's:.*<id>\([^<]*\)</id>.*:\1:p' | head -n 1)
  actual_version=$(unzip -p "$archive" "$nuspec" | sed -n 's:.*<version>\([^<]*\)</version>.*:\1:p' | head -n 1)
  if [[ "$actual_id" != "$package_id" ]]; then
    echo "package ID mismatch in $archive: expected $package_id, got $actual_id" >&2
    return 1
  fi
  if [[ "$actual_version" != "$package_version" ]]; then
    echo "package version mismatch in $archive: expected $package_version, got $actual_version" >&2
    return 1
  fi
}

require_entry() {
  local archive=$1
  local entry=$2
  if ! unzip -Z1 "$archive" | grep -Fx "$entry" >/dev/null; then
    echo "missing required package entry: $entry" >&2
    return 1
  fi
}

verify_identity "$package"
verify_identity "$symbol_package"

for entry in \
  "$tool_path/DotnetToolSettings.xml" \
  "$tool_path/DotnetAgents.CalDav.Core.dll" \
  "$tool_path/DotnetAgents.CalDav.Core.pdb" \
  "$tool_path/DotnetAgents.CalDav.Mcp.deps.json" \
  "$tool_path/DotnetAgents.CalDav.Mcp.dll" \
  "$tool_path/DotnetAgents.CalDav.Mcp.pdb" \
  "$tool_path/DotnetAgents.CalDav.Mcp.runtimeconfig.json" \
  "$tool_path/.mcp/server.json" \
  'README.md' \
  '.mcp/server.json'; do
  require_entry "$package" "$entry"
done
require_entry "$symbol_package" "$tool_path/DotnetAgents.CalDav.Core.pdb"
require_entry "$symbol_package" "$tool_path/DotnetAgents.CalDav.Mcp.pdb"

while IFS= read -r entry; do
  case "$entry" in
    _rels/*|package/*|tools/*|dotnet-agents-caldav.nuspec|'[Content_Types].xml'|README.md|.mcp/server.json)
      ;;
    *)
      echo "unexpected non-runtime package entry: $entry" >&2
      exit 1
      ;;
  esac
done < <(unzip -Z1 "$package")

root_metadata="$temporary_directory/root-server.json"
tool_metadata="$temporary_directory/tool-server.json"
unzip -p "$package" '.mcp/server.json' > "$root_metadata"
unzip -p "$package" "$tool_path/.mcp/server.json" > "$tool_metadata"
cmp -s -- "$root_metadata" "$tool_metadata" || {
  echo "root and tool-path MCP metadata differ" >&2
  exit 1
}
jq -e --arg version "$package_version" --arg package_id "$package_id" '
  .version == $version and
  (.packages | length) == 1 and
  .packages[0].identifier == $package_id and
  .packages[0].version == $version and
  .packages[0].runtimeHint == "dnx" and
  .packages[0].transport.type == "stdio"
' "$root_metadata" >/dev/null || {
  echo "packed MCP metadata does not identify the requested release" >&2
  exit 1
}

tool_directory="$temporary_directory/tool"
dotnet tool install "$package_id" \
  --tool-path "$tool_directory" \
  --source "$package_directory" \
  --version "$package_version" \
  --no-http-cache

installed_executable="$tool_directory/dotnet-agents-caldav"
[[ -x "$installed_executable" ]] || {
  echo "installed MCP executable is missing or not executable: $installed_executable" >&2
  exit 1
}

DOTNET_AGENTS_CALDAV_PACKAGE_SMOKE_EXECUTABLE="$installed_executable" \
  dotnet test \
    --project "$repository_root/tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj" \
    -c Release \
    --no-build \
    --no-restore \
    --filter-trait "Category=PackageSmoke" \
    --minimum-expected-tests 1 \
    --fail-skips on \
    --zero-tests-policy strict \
    --no-ansi

echo "verified final release packages for version $package_version"
