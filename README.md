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
      "args": ["--yes", "dotnet-agents-caldav@0.2.3"],
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

The default semantic catalog contains exactly these 19 tools in the order shown. It has no legacy task aliases or compatibility mode.

### Exact Calendar resource tools

- `calendar_resources.exact_get` — Opt-in byte-preserving exact read through a protected MCP blob resource link.
- `calendar_resources.exact_create` — Create a complete caller-authored Calendar Object Resource from Unicode text or canonical base64 bytes at an explicit href after MRTR confirmation.
- `calendar_resources.exact_replace` — Replace a strong-tagged resource with complete caller-authored Unicode text or canonical base64 bytes after MRTR confirmation.
- `calendar_resources.exact_move` — Review and atomically move a strong-tagged complete resource to an explicit href with constant-work MRTR and authoritative-byte verification; requires the verified interoperability profile.

The four exact tools are enabled with `CALDAV_EXPOSE_EXACT_TOOLS=true`; this flag controls the deterministic stdio catalog without contacting the server. The configured CalDAV credentials are the stdio authorization context, 401/403 responses become typed call failures, and exact writes require client support for MCP Multi Round-Trip Requests. Exact Move uses headers-only GET absence probes, never scans destination members, never retries MOVE, and keeps its executable one-use plan inside Core.

## Optional OpenTelemetry observability

Telemetry is disabled by default: the process registers no exporter and makes no collector connection unless `OTEL_EXPORTER_OTLP_ENDPOINT` is non-empty and `OTEL_SDK_DISABLED` is not `true`. The MCP SDK and .NET runtime provide MCP and outbound HTTP signals; this server adds the in-process provider/export pipeline, CalDAV operation and aggregate-phase spans, correlated safe logs, and an export allowlist. It does not add Aspire libraries, an AppHost, health endpoints, console/file exporters, or a hosted backend. Each OTLP export call is capped at 250 milliseconds so an accepting but unresponsive collector cannot hold the stdio child process past its shutdown bound.

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

The verified interoperability profile is the official Radicale 3.7.8 image pinned in the [Radicale 3.7.8 profile](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/v0.2.3/contracts/0.2.3/radicale-3.7.8-profile.json). Set `CALDAV_INTEROPERABILITY_PROFILE=radicale-3.7.8` only for that runtime. Server-authoritative Semantic and Exact Move fail closed with `unsupported_capability` when the profile is omitted because atomic `If-Match`, `Overwrite: F`, and `CALDAV:no-uid-conflict` enforcement cannot be inferred from stored resources or generic DAV discovery. No collection-scan compatibility fallback is provided. Other CalDAV servers remain unverified profiles even when capability negotiation allows other operations.

## Architecture

Layered design:

Calendar Entity, Occurrence, and compact To-do reads use `MCP adapter` → `ICalendarQueryModule` → the single narrow `ICalendarQueryTransport` → `CalDavClient`; unrelated discovery and mutation operations retain the `ICalendarService` path. `ICalendarQueryModule` exposes exactly those three query operations, and `ICalendarService` exposes none. Lossless iCalendar projection and bounded recurrence evaluation stay in Core's iCalendar modules.

A query Start completes discovery, authoritative retrieval, evaluation, ordering, and projection before returning its first page. Windowed To-do Starts acquire VTODOs once, route non-recurring resources through the Entity lane and recurring resources through the Occurrence lane, then apply one global order. A Continue authenticates its opaque cursor and reads only the bounded process-local Query Result Snapshot. Snapshots expire ten minutes after the first page, are never extended by replay, and are not CalDAV caches or mutation authority.

Each MCP tool call owns one immutable, authorization-bound discovery coordinator. Same-key source, destination, query, and reconciliation consumers share its complete in-scope result; a new call, including an MRTR continuation, performs fresh discovery. This is an operation-local lifetime only: credentials, resource snapshots, query results, Entity Tags, and process-lifetime Capability State are never retained in the discovery result, and there is no process-wide or TTL discovery cache.

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

The behavior gates, final-package smoke test, and package-content policy are documented in [Release validation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/release-process.md). Current specification evidence is linked from the [requirement-to-evidence catalog](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/requirement-to-evidence.md).
Published release history and notes are maintained in [GitHub Releases](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/releases).
