#!/usr/bin/env python3
"""Validate the one closed five-artifact repository suite manifest."""

from __future__ import annotations

import json
import pathlib
import sys


CORE = "tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj"
MCP = "tests/DotnetAgents.CalDav.Mcp.Tests.Unit/DotnetAgents.CalDav.Mcp.Tests.Unit.csproj"
INTEGRATION = "tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj"
HARNESS = "*RadicaleConformanceHarnessTests"
HARNESS_CLASS = "DotnetAgents.CalDav.IntegrationTests.RadicaleConformanceHarnessTests"


def expected_shape() -> dict[str, dict[str, object]]:
    return {
        "main-core": {"project": CORE, "trx": "main-core.trx", "coveragePrefix": "main-core",
                      "phase": "main", "environment": {}},
        "main-mcp": {"project": MCP, "trx": "main-mcp.trx", "coveragePrefix": "main-mcp",
                     "phase": "main", "environment": {}},
        "main-integration": {"project": INTEGRATION, "trx": "main-integration.trx",
                             "coveragePrefix": "main-integration", "phase": "main",
                             "environment": {"RADICALE_CONFORMANCE_VARIANT": "baseline"},
                             "requiredResult": {"className": HARNESS_CLASS}},
        "strict-preconditions": {"project": INTEGRATION, "trx": "strict-preconditions.trx",
                                 "phase": "complete", "filterClass": HARNESS,
                                 "environment": {"RADICALE_CONFORMANCE_VARIANT": "strict-preconditions"}},
        "alternate-time-zone": {"project": INTEGRATION, "trx": "alternate-time-zone.trx",
                                "phase": "complete", "filterClass": HARNESS,
                                "environment": {"RADICALE_CONFORMANCE_VARIANT": "alternate-time-zone"}},
    }


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate-test-suite-manifest.py <manifest>")
    path = pathlib.Path(sys.argv[1])
    document = json.loads(path.read_text(encoding="utf-8"))
    if set(document) != {"schemaVersion", "artifacts"} or document["schemaVersion"] != 1:
        raise SystemExit("The test-suite manifest must be the closed schema-v1 document.")
    items = document["artifacts"]
    if not isinstance(items, list) or len(items) != 5:
        raise SystemExit("The test-suite manifest must contain exactly five artifacts.")
    by_name = {item.get("name"): item for item in items}
    expected = expected_shape()
    if set(by_name) != set(expected) or len(by_name) != len(items):
        raise SystemExit("The test-suite manifest artifact names must match the closed five-artifact suite.")
    for name, fixed in expected.items():
        item = by_name[name]
        exact_keys = {"name", *fixed.keys()}
        if set(item) != exact_keys:
            raise SystemExit(f"{name} must contain exactly the closed artifact fields.")
        for key, value in fixed.items():
            if item.get(key) != value:
                raise SystemExit(f"{name}.{key} does not match the closed suite contract.")
        if pathlib.PurePath(item["trx"]).name != item["trx"]:
            raise SystemExit(f"{name}.trx must be a safe basename.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
