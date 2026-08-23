#!/usr/bin/env python3
"""Capture and compare all nonignored Git worktree and index state."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import sys


def git(root: pathlib.Path, *arguments: str) -> bytes:
    return subprocess.run(
        ["git", "-C", str(root), *arguments], check=True, stdout=subprocess.PIPE
    ).stdout


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def capture(root: pathlib.Path) -> dict[str, object]:
    tracked: dict[str, dict[str, object]] = {}
    for encoded in git(root, "ls-files", "-z").split(b"\0"):
        if not encoded:
            continue
        relative = encoded.decode("utf-8", "surrogateescape")
        path = root / relative
        if not path.exists() and not path.is_symlink():
            tracked[relative] = {"kind": "missing", "mode": 0, "sha256": ""}
            continue
        metadata = os.lstat(path)
        if stat.S_ISLNK(metadata.st_mode):
            content = os.readlink(path).encode("utf-8", "surrogateescape")
            kind = "symlink"
        elif stat.S_ISREG(metadata.st_mode):
            content = path.read_bytes()
            kind = "file"
        else:
            content = b""
            kind = "other"
        tracked[relative] = {
            "kind": kind,
            "mode": stat.S_IMODE(metadata.st_mode),
            "sha256": digest(content),
        }
    untracked: dict[str, dict[str, object]] = {}
    paths = git(root, "ls-files", "--others", "--exclude-standard", "-z").split(b"\0")
    for encoded in paths:
        if not encoded:
            continue
        relative = encoded.decode("utf-8", "surrogateescape")
        path = root / relative
        metadata = os.lstat(path)
        if stat.S_ISLNK(metadata.st_mode):
            content = os.readlink(path).encode("utf-8", "surrogateescape")
            kind = "symlink"
        elif stat.S_ISREG(metadata.st_mode):
            content = path.read_bytes()
            kind = "file"
        else:
            content = b""
            kind = "other"
        untracked[relative] = {
            "kind": kind,
            "mode": stat.S_IMODE(metadata.st_mode),
            "sha256": digest(content),
        }
    return {
        "head": git(root, "rev-parse", "HEAD").decode().strip(),
        "worktreePatchSha256": digest(git(root, "diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--")),
        "indexPatchSha256": digest(git(root, "diff", "--no-ext-diff", "--no-textconv", "--binary", "--cached", "HEAD", "--")),
        "tracked": tracked,
        "untracked": untracked,
    }


def write_json(path: pathlib.Path, value: dict[str, object]) -> None:
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    capture_parser = subparsers.add_parser("capture")
    capture_parser.add_argument("repository", type=pathlib.Path)
    capture_parser.add_argument("output", type=pathlib.Path)
    compare_parser = subparsers.add_parser("compare")
    compare_parser.add_argument("repository", type=pathlib.Path)
    compare_parser.add_argument("before", type=pathlib.Path)
    compare_parser.add_argument("after", type=pathlib.Path)
    args = parser.parse_args()
    current = capture(args.repository.resolve())
    if args.command == "capture":
        write_json(args.output, current)
        return 0
    before = json.loads(args.before.read_text(encoding="utf-8"))
    write_json(args.after, current)
    if before != current:
        print("Test execution changed the Git worktree, index, or nonignored untracked state.", file=sys.stderr)
        return 70
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
