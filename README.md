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
dotnet build -c Release
```

Test:

```bash
dotnet test
```

Coverage:

```bash
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage"
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:"coverage-report" -reporttypes:Cobertura -assemblyfilters:"+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*"
bash scripts/verify-coverage.sh coverage-report 0.90 0.85
```

Slopwatch:

```bash
slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

The behavior gates, final-package smoke test, and package-content policy are documented in [Release validation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/release-process.md).

The 0.2.0 contract is deliberately incompatible with the removed 0.1.x task-specific tools and environment names.
