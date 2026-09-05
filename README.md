[![Release](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml/badge.svg)](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml)

# CalDAV Calendars MCP Server

Model Context Protocol (MCP) server for CalDAV Calendars, Events, and To-dos. It is built with .NET 10 and distributed via `dnx`.

## Quick start

Add this MCP server to VS Code, Claude Desktop, Cursor, or any MCP client:

```json
{
  "mcpServers": {
    "caldav-calendars": {
      "command": "dnx",
      "args": ["--yes", "dotnet-agents-caldav@0.2.4"],
      "env": {
        "CALDAV_URL": "https://caldav.example.com",
        "CALDAV_USERNAME": "user",
        "CALDAV_PASSWORD": "password",
        "CALDAV_EVALUATION_TIME_ZONE": "America/Sao_Paulo",
        "CALDAV_DEFAULT_EVENT_CALENDAR_NAME": "Events",
        "CALDAV_DEFAULT_TODO_CALENDAR_NAME": "To-dos"
      }
    }
  }
}
```

## Bundled Agent Skill

The NuGet package includes the harness-neutral Agent Skill at
`skills/caldav-calendars/SKILL.md`. It teaches an agent to choose the semantic
or exact tool that matches the request, avoid unnecessary discovery calls,
bind updates to fresh revisions, and continue MCP confirmation exchanges.

A harness that discovers Agent Skills from installed packages can load that
path directly. For a harness with a user-managed skill directory, extract or
copy the complete `skills/caldav-calendars` directory into the harness's
documented skill location. Keep the directory name and `SKILL.md` frontmatter
unchanged. MCP server registration and credentials remain configured through
the harness's normal MCP settings; the skill contains no credentials or
client-specific commands.

## Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `CALDAV_URL` | Yes | Absolute CalDAV server endpoint or Calendar Home URL |
| `CALDAV_USERNAME` | Yes | Username for Basic auth |
| `CALDAV_PASSWORD` | Yes | Password for Basic auth |
| `CALDAV_CALENDAR_HREFS` | No | Comma-separated exact canonical Calendar href allowlist; omit to discover every Calendar |
| `CALDAV_DEFAULT_TODO_CALENDAR_NAME` | No | Display name of the default Calendar for To-do operations |
| `CALDAV_DEFAULT_EVENT_CALENDAR_NAME` | No | Display name of the default Calendar for Event operations |
| `CALDAV_EVALUATION_TIME_ZONE` | No | Exact IANA zone used as the configured Temporal Evaluation Context for bounded Calendar Entity Starts and every Occurrence or To-do Start; invalid values fail startup and a caller `evaluationTimeZone` override wins |
| `CALDAV_INTEROPERABILITY_PROFILE` | No | Set to `radicale-3.7.8` only for that verified runtime; otherwise server-authoritative Move fails closed with `unsupported_capability` |
| `CALDAV_EXPOSE_EXACT_TOOLS` | No | Set to `true` to expose protected exact Calendar Object Resource tools |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Non-empty OTLP endpoint that opts into telemetry export; no exporter is registered when omitted |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | Standard OTLP protocol such as `http/protobuf` or `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | No | Secret OTLP authentication or routing headers; never included in exported telemetry |
| `OTEL_SERVICE_NAME` | No | Service name override; defaults to `dotnet-agents-caldav` |
| `OTEL_SDK_DISABLED` | No | Set to `true` to disable the SDK even when an endpoint is configured |

## Available tools

### Semantic Calendar tools

- `calendars.list` — Discover the configured Calendar Scope.
- `calendars.inspect` — Inspect standard Calendar metadata, report and privilege advertisements, storage limits, timezone identifiers, and scheduling evidence.
- `calendars.patch` — Set or remove Calendar display name and description with one atomic, unconditional metadata update; preserve unaddressed properties.
- `calendars.free_busy` — Read native server-computed busy intervals for one Calendar and a bounded UTC window without downloading Events.
- `calendar_resources.changes` — Read an initial inventory or incremental href/ETag changes and removals from view, using session-bound synchronization checkpoints.
- `calendars.create` — Create an Event-only, To-do-only, or mixed Calendar collection with native `MKCALENDAR`.
- `calendars.delete` — Confirm and recursively delete one exact Calendar collection, including its resources.
- `calendar_entities.query` — Start a persisted Event and To-do query across Calendar Scope, or continue its immutable Query Result Snapshot without repeating CalDAV work. A bounded Start requires an explicit caller or configured IANA Temporal Evaluation Context and reports the frozen context on every page.
- `calendar_occurrences.query` — Start one bounded Event and To-do Occurrence query under an explicit caller or configured IANA Temporal Evaluation Context, or continue its immutable Query Result Snapshot with no CalDAV or recurrence work.
- `todos.query` — Start a compact normalized To-do query over one authoritative VTODO-only corpus, or continue its immutable Query Result Snapshot without remote or semantic re-execution. Every Start requires a caller or configured IANA Temporal Evaluation Context.
- `calendar_resources.get` — Read an authoritative semantic-or-opaque snapshot by confirmed absolute href.
- `events.create` — Create one Event in a selected Calendar.
- `events.patch` — Apply a revision-bound semantic patch to one Event resource.
- `todos.create` — Create one To-do in a selected Calendar.
- `todos.patch` — Apply a revision-bound semantic patch to one To-do resource.
- `todos.complete` — Complete one non-recurring To-do or one explicitly identified recurring Occurrence.
- `calendar_occurrences.add` — Add one explicit RDATE identity.
- `calendar_occurrences.exclude` — Add one exact EXDATE while preserving any override.
- `calendar_occurrences.restore_exclusion` — Remove only one exact EXDATE.
- `calendar_occurrences.cancel` — Create or update one complete cancelled override.
- `calendar_occurrences.restore_cancellation` — Remove only cancelled status from one override.
- `calendar_resources.move` — Move one reviewed resource with exact `If-Match`, `Overwrite: F`, server-authoritative UID collision truth, and bounded bilateral reconciliation; requires a verified interoperability profile.
- `calendar_resources.delete` — Delete an entire resource from an explicitly supplied revision reference (href, UID, kind, and exact strong ETag) after MCP MRTR review and confirmation; success requires verified absence.

The default semantic catalog contains these 19 tools in the order shown.

### Exact Calendar resource tools

- `calendar_resources.exact_get` — Opt-in byte-preserving exact read through a protected MCP blob resource link.
- `calendar_resources.exact_create` — Create a complete caller-authored Calendar Object Resource from Unicode text or canonical base64 bytes at an explicit href after MRTR confirmation.
- `calendar_resources.exact_replace` — Replace a strong-tagged resource with complete caller-authored Unicode text or canonical base64 bytes after MRTR confirmation.
- `calendar_resources.exact_move` — Review and atomically move a strong-tagged complete resource to an explicit href with constant-work MRTR and authoritative-byte verification; requires the verified interoperability profile.

The four exact tools are enabled with `CALDAV_EXPOSE_EXACT_TOOLS=true`; this flag controls the deterministic stdio catalog without contacting the server. The configured CalDAV credentials are the stdio authorization context, 401/403 responses become typed call failures, and exact writes require client support for MCP Multi Round-Trip Requests. Exact Move uses headers-only GET absence probes, never scans destination members, never retries MOVE, and keeps its executable one-use plan inside Core.

## Optional OpenTelemetry observability

To enable telemetry, set `OTEL_EXPORTER_OTLP_ENDPOINT` and leave `OTEL_SDK_DISABLED` unset or `false`. The server exports MCP and outbound HTTP signals, CalDAV operation and phase spans, and correlated logs through an allowlist. Each OTLP export call has a 250-millisecond limit to bound shutdown time if the collector stops responding.

For local troubleshooting, run the standalone Aspire Dashboard on loopback only:

```bash
docker run --rm --detach \
  --name caldav-otel-dashboard \
  --publish 127.0.0.1:18888:18888 \
  --publish 127.0.0.1:4318:18890 \
  mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b
```

Keep the generated browser token enabled, open `http://127.0.0.1:18888`, and launch the current-checkout MCP process through a real stdio client with:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_SERVICE_NAME=dotnet-agents-caldav
```

Exported spans show the MCP request, `caldav.operation`, the applicable `discovery`, `fetch`, `filter`, `expand`, and `reconcile` phases, and individual HTTP attempts. The allowlist excludes credentials, OTLP headers, URLs/hrefs, Calendar Names, UIDs, Entity Tags, cursors, iCalendar/XML/HTTP bodies, MCP payloads/results, and exception messages or stack traces. Collector failure cannot change tool results or write telemetry diagnostics to stdout/stderr.

## Supported servers

The verified interoperability profile is the official Radicale 3.7.8 image pinned in the [Radicale 3.7.8 profile](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/v0.2.4/contracts/0.2.3/radicale-3.7.8-profile.json). Set `CALDAV_INTEROPERABILITY_PROFILE=radicale-3.7.8` only for that runtime. Server-authoritative Semantic and Exact Move fail closed with `unsupported_capability` when the profile is omitted because atomic `If-Match`, `Overwrite: F`, and `CALDAV:no-uid-conflict` enforcement cannot be inferred from stored resources or generic DAV discovery. Other CalDAV servers remain unverified profiles even when capability negotiation allows other operations.

## Architecture

Native reports use `ICalendarReportModule`; collection metadata uses
`ICalendarMetadataModule`. Each accepts an exact canonical Calendar href.
An exact `CALDAV_CALENDAR_HREFS` entry authorizes these operations without
discovery; otherwise the initial operation discovers the Calendar first.
Configure that exact entry for a Calendar available only through free/busy
privileges when metadata discovery cannot authorize it. Report advertisements
are evidence: the client still attempts a native report when the advertisement
is absent, and reports an unsupported or forbidden operation as an error.

`calendar_resources.changes` transfers hrefs and ETags without resource bodies.
Apply the entire returned page before saving its checkpoint; continue while
`hasMore` is true. Copy its 36-character opaque checkpoint exactly. It binds
the Calendar and configuration to retained state in the current MCP process.
Unchanged state reuses its handle; an advancing report preserves earlier
handles for retry while they remain retained. The session keeps at most 1,024
states and 8 MiB of serialized state, evicting the least recently used entries
when either limit is reached. Continuation performs no discovery. On
`sync_reset_required`, restart with `calendarHref` and rebuild the inventory.
An observed ETag is not a semantic revision reference, and a removal can mean
revoked visibility. Query cursors and synchronization checkpoints are separate.

Free/busy uses server permissions and temporal interpretation. It accepts
whole-second UTC boundaries within 366 days and bounds the response to 5,000
periods before merging. An empty successful report means no reported busy time;
a failed report does not. Free/busy and established sync checkpoints use one logical REPORT per call,
with up to three HTTP attempts under the existing read retry policy, a 4 MiB
response limit and a 30-second deadline. Initial authorization can add
discovery requests. Sync accepts at most the requested page size, capped at
500 entries, and returns no new checkpoint if the server exceeds it.

Initial sync can make one additional REPORT without the optional server limit
after the standard limit-rejection response. Its checkpoint retains that mode
for later changes, which still use one REPORT and the same client limits.
In that mode the entire inventory or delta must fit the requested page size;
overflow leaves the prior checkpoint usable for a retry with a larger page
size, up to 500. The MCP does not blindly retry native REPORT 507 responses.

Calendar metadata updates set or remove only the addressed display name and
description. Description accepts an optional language tag. The server applies
the property instructions atomically, but the operation is **unconditional**:
a concurrent edit to an addressed property can be overwritten. The MCP sends
one PROPPATCH without retries and reads the target back. A committed or
uncertain error requires inspection before another write.

Participation-bearing creates, updates and deletes require fresh OPTIONS
evidence that automatic server scheduling is absent. Updates check both stored
and proposed data, including removed participation fields. Collection deletion
requires that evidence regardless of its current members. Unknown evidence or
`calendar-auto-schedule` returns `unsupported_capability` before the write.
Native Calendar-to-Calendar MOVE retains its scheduling-neutral RFC behavior.
Invitation/reply delivery remains outside the tool contract.

Discovery follows all advertised Calendar homes and nested ordinary
collections, stopping at Calendar collections. It fails without partial results
when its depth, request, byte, home or Calendar limits are exhausted. With
multiple homes, creation requires an explicit destination below the intended
home. See the [RFC coverage plan](docs/rfc-coverage-plan-2026-09-05.md) and
[live observation harness](scripts/observations/rfc-coverage/README.md).

Configure exact Calendar hrefs when unrelated collection branches reject
discovery. Scoped traversal avoids those branches. For example, unrestricted
discovery can fail on Nextcloud trash descendants that reject PROPFIND;
the MCP returns that failure rather than claiming a complete Calendar list.

Calendar Entity, Occurrence, and compact To-do reads use `MCP adapter` → `ICalendarQueryModule` → the single narrow `ICalendarQueryTransport` → `CalDavClient`; unrelated discovery and mutation operations retain the `ICalendarService` path. `ICalendarQueryModule` exposes exactly those three query operations, and `ICalendarService` exposes none. Lossless iCalendar projection and bounded recurrence evaluation stay in Core's iCalendar modules.

A query Start completes discovery, authoritative retrieval, evaluation, ordering, and projection before returning its first page. Windowed To-do Starts acquire VTODOs once, route non-recurring resources through the Entity lane and recurring resources through the Occurrence lane, then apply one global order. A Continue authenticates its opaque cursor and reads only the bounded process-local Query Result Snapshot. Snapshots expire ten minutes after the first page, are never extended by replay, and are not CalDAV caches or mutation authority.

Operations that need discovery reuse one scoped result for the duration of that operation. A later operation, including an MRTR continuation, acquires its own result. Query Result Snapshots and capability observations have separate lifetimes.

## Development

Build:

```bash
dotnet tool restore
dotnet restore
dotnet build -c Release --no-restore
```

Run one test project with Microsoft.Testing.Platform:

```bash
dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release
```

Run the complete CI-equivalent test, coverage, and Radicale conformance suite:

```bash
bash scripts/run-test-suite.sh
```

The suite uses a new temporary artifact directory for every run. Successful local runs clean it up; failed runs retain it and print its path. Coverage aggregation validates and uses only the three current root-level reports, so stale `TestResults` or nested runner staging files cannot affect a later run.

Slopwatch:

```bash
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

The behavior gates, final-package smoke test, and package-content policy are documented in [Release validation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/release-process.md). The [developer documentation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/README.md) links design decisions, behavioral tests, and historical performance reports.
Published release history and notes are maintained in [GitHub Releases](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/releases).
