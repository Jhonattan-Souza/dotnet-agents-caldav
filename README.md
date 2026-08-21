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
      "args": ["--yes", "dotnet-agents-caldav@0.2.2"],
      "env": {
        "CALDAV_URL": "https://caldav.example.com",
        "CALDAV_USERNAME": "user",
        "CALDAV_PASSWORD": "password",
        "CALDAV_DEFAULT_EVENT_CALENDAR_NAME": "Events",
        "CALDAV_DEFAULT_TODO_CALENDAR_NAME": "To-dos"
      }
    }
  }
}
```

## Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `CALDAV_URL` | Yes | Absolute CalDAV server endpoint or Calendar Home URL |
| `CALDAV_USERNAME` | Yes | Username for Basic auth |
| `CALDAV_PASSWORD` | Yes | Password for Basic auth |
| `CALDAV_CALENDAR_HREFS` | No | Comma-separated exact canonical Calendar href allowlist; omit to discover every Calendar |
| `CALDAV_DEFAULT_TODO_CALENDAR_NAME` | No | Display name of the default Calendar for To-do operations |
| `CALDAV_DEFAULT_EVENT_CALENDAR_NAME` | No | Display name of the default Calendar for Event operations |
| `CALDAV_EXPOSE_EXACT_TOOLS` | No | Set to `true` to expose protected exact Calendar Object Resource tools |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Non-empty OTLP endpoint that opts into telemetry export; no exporter is registered when omitted |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | Standard OTLP protocol such as `http/protobuf` or `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | No | Secret OTLP authentication or routing headers; never included in exported telemetry |
| `OTEL_SERVICE_NAME` | No | Service name override; defaults to `dotnet-agents-caldav` |
| `OTEL_SDK_DISABLED` | No | Set to `true` to disable the SDK even when an endpoint is configured |

## Available tools

### Semantic Calendar tools

- `calendars.list` — Discover the configured Calendar Scope.
- `calendar_entities.query` — Query bounded persisted Event and To-do snapshots across default, selected, or explicit-all Calendar Scope.
- `calendar_occurrences.query` — Expand Event and To-do occurrences locally within a required half-open UTC window, using an explicit IANA evaluation time zone only when floating or date-only values require it.
- `todos.query` — Read compact normalized To-do results in an explicit Calendar Scope; routine open-task reads use this surface instead of parsing full snapshots.
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
- `calendar_resources.move` — Atomically move one reviewed Calendar Object Resource to a selected Calendar.
- `calendar_resources.delete` — Delete an entire resource from an explicitly supplied revision reference (href, UID, kind, and exact strong ETag) after MCP MRTR review and confirmation; success requires verified absence.

The 0.2.2 default catalog contains exactly these 17 tools in the order shown. It has no legacy task aliases or compatibility mode.

### Exact Calendar resource tools

- `calendar_resources.exact_get` — Opt-in byte-preserving exact read through a protected MCP blob resource link.
- `calendar_resources.exact_create` — Create a complete caller-authored Calendar Object Resource from Unicode text or canonical base64 bytes at an explicit href after MRTR confirmation.
- `calendar_resources.exact_replace` — Replace a strong-tagged resource with complete caller-authored Unicode text or canonical base64 bytes after MRTR confirmation.
- `calendar_resources.exact_move` — Atomically move a strong-tagged resource to an explicit href after MRTR confirmation.

The four exact tools are enabled with `CALDAV_EXPOSE_EXACT_TOOLS=true`; this flag controls the deterministic stdio catalog without contacting the server. The configured CalDAV credentials are the stdio authorization context, 401/403 responses become typed call failures, and exact writes require client support for MCP Multi Round-Trip Requests.

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

The verified interoperability profile is the official Radicale 3.7.8 image pinned in the [Radicale 3.7.8 profile](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/v0.2.2/contracts/0.2.2/radicale-3.7.8-profile.json). Other CalDAV servers are unverified profiles even when capability negotiation allows them to operate.

## Migrating from 0.2.1

Version 0.2.2 changes Create collision handling while keeping tool names and
input shapes stable. Read [Migrating from 0.2.1 to 0.2.2](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/v0.2.2/docs/migrating-0.2.1-to-0.2.2.md)
before upgrading.

## Migrating from 0.1.x

Version 0.2.0 deliberately replaces the task-specific 0.1.x contract. Read [Migrating from 0.1.x to 0.2.0](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/migrating-0.1.x-to-0.2.0.md) for the complete tool and environment mapping, revision-bound write recipes, MRTR deployment checks, and rollback to pinned version 0.1.4. Upgrade and rollback do not migrate or rewrite CalDAV data.

## Architecture

Layered design:

`MCP tools` → `ICalendarService` → thin service facade → `CalDavClient` → `HttpClient`; lossless iCalendar projection and bounded recurrence evaluation stay in Core's iCalendar modules.

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

The behavior gates, final-package smoke test, and package-content policy are documented in [Release validation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/release-process.md).

The 0.2.0 contract is deliberately incompatible with the removed 0.1.x task-specific tools and environment names.
