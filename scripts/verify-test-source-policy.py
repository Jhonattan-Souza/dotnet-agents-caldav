#!/usr/bin/env python3
"""Reject disabled or non-normative C# tests without trusting line-oriented grep."""

from __future__ import annotations

import argparse
import pathlib
import re
import sys


ATTRIBUTE = re.compile(
    r"\[\s*(?:global::)?(?:[A-Za-z_][\w]*\.)*"
    r"(?:Fact|Theory|SkippableFact|SkippableTheory)(?:Attribute)?\b"
    r"(?P<body>.*?)\]",
    re.DOTALL,
)
DISABLING_OPTION = re.compile(
    r"\b(?:Skip|SkipWhen|SkipUnless|SkipExceptions|SkipTestWithoutData|Explicit)\s*=",
    re.DOTALL,
)
RUNTIME_SKIP = re.compile(r"\bAssert\s*\.\s*Skip\s*\(", re.DOTALL)
NON_NORMATIVE = re.compile(r"\b(?:Quarantin(?:e|ed)|Flaky)", re.IGNORECASE)
EXPLICIT_ATTRIBUTE = re.compile(
    r"\[\s*(?:global::)?(?:[A-Za-z_][\w]*\.)*Explicit(?:Attribute)?\s*(?:\([^]]*\))?\s*\]",
    re.DOTALL,
)
TRAIT_ATTRIBUTE = re.compile(
    r"(?:^\[\s*|,\s*)(?:global::)?(?:[A-Za-z_][\w]*\.)*Trait(?:Attribute)?\s*\(",
    re.DOTALL,
)
TRAIT_NON_NORMATIVE = re.compile(
    r"[\"'](?:Flaky|Quarantin(?:e|ed))[\"']",
    re.IGNORECASE,
)


def attribute_ranges(code: str) -> list[tuple[int, int]]:
    """Return balanced top-level attribute ranges from comment/literal-masked C#."""
    ranges: list[tuple[int, int]] = []
    start: int | None = None
    depth = 0
    for index, character in enumerate(code):
        if character == "[":
            if depth == 0:
                start = index
            depth += 1
        elif character == "]" and depth:
            depth -= 1
            if depth == 0 and start is not None:
                ranges.append((start, index + 1))
                start = None
    return ranges


def mask_comments_and_literals(source: str, *, mask_literals: bool) -> str:
    chars = list(source)
    index = 0
    length = len(chars)
    while index < length:
        if source.startswith("//", index):
            end = source.find("\n", index + 2)
            end = length if end < 0 else end
            for offset in range(index, end):
                chars[offset] = " "
            index = end
            continue
        if source.startswith("/*", index):
            end = source.find("*/", index + 2)
            end = length if end < 0 else end + 2
            for offset in range(index, end):
                if chars[offset] != "\n":
                    chars[offset] = " "
            index = end
            continue
        raw_prefix = re.match(r"\$*\"{3,}", source[index:])
        if raw_prefix:
            start = index
            token = raw_prefix.group(0)
            quote_count = len(token) - token.count("$")
            index += len(token)
            closing = '"' * quote_count
            end = source.find(closing, index)
            index = length if end < 0 else end + quote_count
            if mask_literals:
                for offset in range(start, min(index, length)):
                    if chars[offset] != "\n":
                        chars[offset] = " "
            continue
        prefix_length = 0
        quote = ""
        verbatim = False
        if source.startswith('$@"', index) or source.startswith('@$"', index):
            prefix_length, quote, verbatim = 2, '"', True
        elif source.startswith('@"', index):
            prefix_length, quote, verbatim = 1, '"', True
        elif source.startswith('$"', index):
            prefix_length, quote = 1, '"'
        elif source[index] in ('"', "'"):
            quote = source[index]
        if not quote:
            index += 1
            continue
        start = index
        index += prefix_length + 1
        while index < length:
            if verbatim and quote == '"' and source.startswith('""', index):
                index += 2
                continue
            if source[index] == quote:
                index += 1
                break
            if not verbatim and source[index] == "\\":
                index += 2
            else:
                index += 1
        if mask_literals:
            for offset in range(start, min(index, length)):
                if chars[offset] != "\n":
                    chars[offset] = " "
    return "".join(chars)


def violations(path: pathlib.Path) -> list[str]:
    source = path.read_text(encoding="utf-8")
    code = mask_comments_and_literals(source, mask_literals=True)
    spans = [(code[start:end], source[start:end]) for start, end in attribute_ranges(code)]
    found: list[str] = []
    if any(DISABLING_OPTION.search(masked) for masked, _ in spans):
        found.append("disabled test attribute option")
    trait_is_non_normative = False
    for masked, original in spans:
        for match in TRAIT_ATTRIBUTE.finditer(masked):
            closing = masked.find(")", match.end())
            closing = len(masked) if closing < 0 else closing + 1
            if TRAIT_NON_NORMATIVE.search(original[match.start():closing]):
                trait_is_non_normative = True
    if trait_is_non_normative:
        found.append("quarantined or flaky Trait")
    for match in ATTRIBUTE.finditer(code):
        if "Skippable" in match.group(0):
            found.append("Skippable Fact/Theory attribute")
    if re.search(r"\bAssert\s*\.\s*Skip(?:When|Unless)?\s*\(", code, re.DOTALL):
        found.append("runtime Assert.Skip")
    if re.search(r"\bSkippable(?:Fact|Theory)(?:Attribute)?\b", code):
        found.append("Skippable Fact/Theory token")
    assert_aliases = re.findall(
        r"\busing\s+([A-Za-z_]\w*)\s*=\s*(?:global::)?(?:Xunit\.)?Assert\s*;",
        code,
    )
    if any(re.search(rf"\b{re.escape(alias)}\s*\.\s*Skip(?:When|Unless)?\s*\(", code)
           for alias in assert_aliases):
        found.append("aliased runtime Assert.Skip")
    if re.search(r"\busing\s+static\s+(?:global::)?Xunit\.Assert\s*;", code) and re.search(
        r"\bSkip(?:When|Unless)?\s*\(", code
    ):
        found.append("static-imported runtime Assert.Skip")
    if re.search(r"\bSkipException\b", code):
        found.append("runtime SkipException")
    derived_fact = re.search(
        r"\bclass\s+[A-Za-z_]\w*(?:Attribute)?\s*:\s*"
        r"(?:global::)?(?:[A-Za-z_]\w*\.)*(?:Fact|Theory)Attribute\b",
        code,
    )
    fact_aliases = re.findall(
        r"\busing\s+([A-Za-z_]\w*)\s*=\s*(?:global::)?(?:[A-Za-z_]\w*\.)*"
        r"(?:Fact|Theory)Attribute\s*;",
        code,
    )
    alias_derived_fact = any(
        re.search(rf"\bclass\s+[A-Za-z_]\w*(?:Attribute)?\s*:\s*{re.escape(alias)}\b", code)
        for alias in fact_aliases
    )
    if derived_fact or alias_derived_fact:
        found.append("custom Fact/Theory-derived attribute")
    if EXPLICIT_ATTRIBUTE.search(code):
        found.append("Explicit attribute")
    if NON_NORMATIVE.search(code):
        found.append("quarantined or flaky marker")
    return found


def source_files(root: pathlib.Path) -> list[pathlib.Path]:
    if root.is_file():
        return [root]
    return sorted(
        path for path in root.rglob("*.cs")
        if not {"bin", "obj"}.intersection(path.relative_to(root).parts)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path)
    args = parser.parse_args()
    rejected = False
    for path in source_files(args.root):
        for reason in violations(path):
            print(f"{path}: {reason}", file=sys.stderr)
            rejected = True
    return 68 if rejected else 0


if __name__ == "__main__":
    raise SystemExit(main())
