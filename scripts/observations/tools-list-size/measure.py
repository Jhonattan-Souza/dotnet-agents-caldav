#!/usr/bin/env python3
"""Measure the stdio bytes of server/discover plus tools/list for both catalogs."""

from __future__ import annotations

import json
import os
import subprocess
import sys

META = {
    "io.modelcontextprotocol/protocolVersion": "2026-07-28",
    "io.modelcontextprotocol/clientInfo": {"name": "tools-list-size", "version": "1"},
    "io.modelcontextprotocol/clientCapabilities": {},
}


def compact_size(value: object) -> int:
    return len(json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode())


def exchange(dll: str, expose_exact_tools: bool) -> tuple[bytes, bytes, bytes]:
    """Send the two requests one at a time and return both response lines and stderr."""
    env = dict(os.environ)
    env.update({
        "CALDAV_URL": "http://127.0.0.1:1/",
        "CALDAV_USERNAME": "tools-list-size",
        "CALDAV_PASSWORD": "tools-list-size",
        "CALDAV_EVALUATION_TIME_ZONE": "UTC",
        "CALDAV_EXPOSE_EXACT_TOOLS": "true" if expose_exact_tools else "false",
    })
    process = subprocess.Popen(
        ["dotnet", dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    responses = []
    for request_id, method in enumerate(("server/discover", "tools/list"), start=1):
        request = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": {"_meta": META}}
        process.stdin.write((json.dumps(request) + "\n").encode())
        process.stdin.flush()
        responses.append(process.stdout.readline())
    process.stdin.close()
    process.wait(timeout=60)
    return responses[0], responses[1], process.stderr.read()


def measure(dll: str, expose_exact_tools: bool) -> dict[str, object]:
    discover, tools_list, stderr = exchange(dll, expose_exact_tools)
    discover_result = json.loads(discover)["result"]
    tools_result = json.loads(tools_list)["result"]
    tools = tools_result["tools"]
    return {
        "toolCount": len(tools),
        "discoverBytes": len(discover),
        "toolsListBytes": len(tools_list),
        "totalBytes": len(discover) + len(tools_list),
        "inputSchemaBytes": sum(compact_size(tool["inputSchema"]) for tool in tools),
        "outputSchemaBytes": sum(compact_size(tool.get("outputSchema", {})) for tool in tools),
        "toolsListCache": {key: tools_result.get(key) for key in ("ttlMs", "cacheScope")},
        "discoverCache": {key: discover_result.get(key) for key in ("ttlMs", "cacheScope")},
        "outputSchemaBytesByTool": {tool["name"]: compact_size(tool.get("outputSchema", {})) for tool in tools},
        "stderrBytes": len(stderr),
    }


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: measure.py <path to DotnetAgents.CalDav.Mcp.dll>", file=sys.stderr)
        return 2
    report = {
        "default": measure(sys.argv[1], expose_exact_tools=False),
        "exact": measure(sys.argv[1], expose_exact_tools=True),
    }
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
