#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <package-version> <package-directory> <generated-server-metadata>" >&2
  exit 2
fi

package_version=$1
package_directory=$2
generated_metadata=$3
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
temporary_directory=$(mktemp -d)
trap 'rm -rf -- "$temporary_directory"' EXIT

mapfile -t packages < <(find "$package_directory" -maxdepth 1 -type f -name '*.nupkg' ! -name '*.snupkg' -print)
mapfile -t symbols < <(find "$package_directory" -maxdepth 1 -type f -name '*.snupkg' -print)
if [[ ${#packages[@]} -ne 1 || ${#symbols[@]} -ne 1 ]]; then
  echo "expected exactly one .nupkg and one .snupkg in $package_directory" >&2
  exit 1
fi

package=${packages[0]}
symbol_package=${symbols[0]}

verify_version() {
  local archive=$1
  local nuspec
  mapfile -t nuspecs < <(unzip -Z1 "$archive" | grep -E '\.nuspec$')
  if [[ ${#nuspecs[@]} -ne 1 ]]; then
    echo "expected exactly one nuspec in $archive" >&2
    return 1
  fi
  nuspec=${nuspecs[0]}
  local actual_version
  actual_version=$(unzip -p "$archive" "$nuspec" | sed -n 's:.*<version>\([^<]*\)</version>.*:\1:p' | head -n 1)
  if [[ "$actual_version" != "$package_version" ]]; then
    echo "package version mismatch in $archive: expected $package_version, got $actual_version" >&2
    return 1
  fi
}

verify_entry() {
  local archive=$1
  local entry=$2
  local expected=$3
  local extracted="$temporary_directory/${entry//\//_}"
  if ! unzip -p "$archive" "$entry" > "$extracted"; then
    echo "missing required package entry: $entry" >&2
    return 1
  fi
  if ! cmp -s -- "$expected" "$extracted"; then
    echo "package content mismatch: $entry" >&2
    return 1
  fi
}

verify_version "$package"
verify_version "$symbol_package"
unzip -Z1 "$symbol_package" | grep -q 'tools/net10.0/any/DotnetAgents.CalDav.Core.pdb'
unzip -Z1 "$symbol_package" | grep -q 'tools/net10.0/any/DotnetAgents.CalDav.Mcp.pdb'

verify_entry "$package" '.mcp/server.json' "$generated_metadata"
verify_entry "$package" 'tools/net10.0/any/.mcp/server.json' "$generated_metadata"

while IFS='|' read -r entry source; do
  verify_entry "$package" "$entry" "$repository_root/$source"
done <<'EOF'
README.md|README.md
CHANGELOG.md|CHANGELOG.md
RELEASE_NOTES.md|RELEASE_NOTES.md
contracts/0.2.0/mcp-tool-catalog.json|contracts/0.2.0/mcp-tool-catalog.json
contracts/0.2.0/mcp-server.schema.json|contracts/0.2.0/mcp-server.schema.json
contracts/0.2.0/mcp-authority-manifest.json|contracts/0.2.0/mcp-authority-manifest.json
contracts/0.2.0/release-evidence-map.json|contracts/0.2.0/release-evidence-map.json
contracts/0.2.1/mcp-tool-catalog.json|contracts/0.2.1/mcp-tool-catalog.json
contracts/0.2.1/mcp-server.schema.json|contracts/0.2.1/mcp-server.schema.json
contracts/0.2.1/mcp-authority-manifest.json|contracts/0.2.1/mcp-authority-manifest.json
contracts/0.2.1/radicale-3.7.8-profile.json|contracts/0.2.1/radicale-3.7.8-profile.json
contracts/0.2.1/compatibility-matrix.md|contracts/0.2.1/compatibility-matrix.md
contracts/0.2.1/requirement-evidence-catalog.md|contracts/0.2.1/requirement-evidence-catalog.md
contracts/0.2.1/release-evidence-map.json|contracts/0.2.1/release-evidence-map.json
docs/migrating-0.1.x-to-0.2.0.md|docs/migrating-0.1.x-to-0.2.0.md
docs/migrating-0.2.0-to-0.2.1.md|docs/migrating-0.2.0-to-0.2.1.md
skills/caldav-calendars/SKILL.md|skills/caldav-calendars/SKILL.md
EOF

echo "verified final release packages for version $package_version"
