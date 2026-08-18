#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <vMAJOR.MINOR.PATCH tag> <output server.json>" >&2
  exit 64
fi

release_tag=$1
output_path=$2
semver_pattern='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
if [[ ! $release_tag =~ $semver_pattern ]]; then
  echo "Release tag must be a v-prefixed semantic version." >&2
  exit 65
fi

release_version=${release_tag#v}
version_without_build=${release_version%%+*}
if [[ $version_without_build == *-* ]]; then
  prerelease=${version_without_build#*-}
  IFS='.' read -r -a prerelease_identifiers <<< "$prerelease"
  for identifier in "${prerelease_identifiers[@]}"; do
    if [[ $identifier =~ ^[0-9]+$ && $identifier != 0 && $identifier == 0* ]]; then
      echo "Numeric prerelease identifiers must not contain leading zeroes." >&2
      exit 65
    fi
  done
fi
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
source_path="$repository_root/src/DotnetAgents.CalDav.Mcp/.mcp/server.json"

jq -e '.version == "0.0.0" and .packages[0].version == "0.0.0"' "$source_path" >/dev/null
mkdir -p -- "$(dirname -- "$output_path")"
temporary_path="${output_path}.tmp"
jq --arg version "$release_version" \
  '.version = $version | .packages[0].version = $version' \
  "$source_path" > "$temporary_path"
mv -- "$temporary_path" "$output_path"
