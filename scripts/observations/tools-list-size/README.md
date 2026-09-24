# Tool-list size observation

`measure.py` is a non-gating observation. It starts a built server over stdio
with dummy configuration that never contacts a CalDAV server, sends
`server/discover` and then `tools/list` with 2026-07-28 request metadata, and
reports the newline-delimited response bytes for the default catalog and for
the catalog with `CALDAV_EXPOSE_EXACT_TOOLS=true`. It also reports compact
input and output schema sums, per-tool output schema bytes, the caching hints on
both results, and stderr bytes.

```bash
dotnet build -c Release --no-restore
python3 scripts/observations/tools-list-size/measure.py \
  src/DotnetAgents.CalDav.Mcp/bin/Release/net10.0/DotnetAgents.CalDav.Mcp.dll
```

Observed on 2026-09-24 before and after contract 0.3.0 removed the Calendar
Snapshot from failure shapes that never carry one and added the `tools/list`
caching hint:

| Catalog | Tools | `tools/list` bytes before | after | Output schemas before | after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Default | 23 | 641,101 | 516,152 | 506,148 | 381,443 |
| With exact tools | 27 | 728,268 | 585,758 | 590,025 | 447,794 |

`server/discover` stayed at 289 and 304 bytes. Most of the remaining output
schema bytes come from the tools whose success or conflict results carry a
complete Calendar Snapshot: 15 default tools account for 325,096 bytes, and 18
tools for 387,673 bytes with exact tools. Each embeds about 17 KB of snapshot
and projection definitions.
