#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <artifact-directory> <allowed-parent-directory>" >&2
  exit 64
fi

artifact_directory=$1
allowed_parent=$2
if [[ ! -d "$allowed_parent" ]]; then
  echo "Allowed parent directory does not exist: $allowed_parent" >&2
  exit 65
fi
if [[ -L "$artifact_directory" ]]; then
  echo "Refusing to clean a symbolic link: $artifact_directory" >&2
  exit 65
fi

allowed_parent=$(realpath -- "$allowed_parent")
artifact_directory=$(realpath -m -- "$artifact_directory")
artifact_parent=$(dirname -- "$artifact_directory")
artifact_name=$(basename -- "$artifact_directory")

if [[ "$artifact_parent" != "$allowed_parent" || ! "$artifact_name" =~ ^caldav-tests\.[[:alnum:]]{6,}$ ]]; then
  echo "Refusing to clean an artifact directory outside the generated test namespace: $artifact_directory" >&2
  exit 65
fi
if [[ -e "$artifact_directory" && ! -d "$artifact_directory" ]]; then
  echo "Artifact path is not a directory: $artifact_directory" >&2
  exit 65
fi

if [[ -d "$artifact_directory" ]]; then
  rm -rf -- "$artifact_directory"
fi
