#!/usr/bin/env python3
"""Capture source identity before a build and bind it to the copied assemblies."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def git(repository, *args):
    return subprocess.check_output(['git', '-C', str(repository), *args])


def source_identity(repository):
    untracked = {}
    for name in git(repository, 'ls-files', '--others', '--exclude-standard', '-z').split(b'\0'):
        if name:
            path = Path(name.decode())
            untracked[str(path)] = hashlib.sha256((repository / path).read_bytes()).hexdigest()
    return dict(sha=git(repository, 'rev-parse', 'HEAD').decode().strip(),
                dirty=bool(git(repository, 'status', '--porcelain=v1', '--untracked-files=all')),
                working_tree_patch_sha256=hashlib.sha256(git(repository, 'diff', 'HEAD', '--binary')).hexdigest(),
                untracked_file_sha256=untracked)


def capture(repository, manifest):
    with manifest.open('x') as stream:
        json.dump(dict(source=source_identity(repository)), stream, indent=2)


def finalize(repository, manifest, assemblies):
    record = json.loads(manifest.read_text())
    if record['source'] != source_identity(repository):
        raise RuntimeError('Source changed during the build; prepare a new comparison')
    record['assembly_sha256'] = {
        name: hashlib.sha256((assemblies / name).read_bytes()).hexdigest()
        for name in ['DotnetAgents.CalDav.Mcp.dll', 'DotnetAgents.CalDav.Core.dll']}
    manifest.write_text(json.dumps(record, indent=2))


def load_builds(root):
    builds = {label: json.loads((root / (label + '-build.json')).read_text())
              for label in ['baseline', 'candidate']}
    for label, build in builds.items():
        for name, digest in build['assembly_sha256'].items():
            if hashlib.sha256((root / label / name).read_bytes()).hexdigest() != digest:
                raise RuntimeError(f'{label} assembly differs from its prepared build manifest')
    return builds


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['capture', 'finalize'])
    parser.add_argument('repository', type=Path)
    parser.add_argument('manifest', type=Path)
    parser.add_argument('assemblies', type=Path, nargs='?')
    args = parser.parse_args()
    if args.action == 'capture':
        capture(args.repository, args.manifest)
    else:
        if args.assemblies is None:
            parser.error('finalize requires the copied assembly directory')
        finalize(args.repository, args.manifest, args.assemblies)
