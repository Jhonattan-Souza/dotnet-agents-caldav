[![Release](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml/badge.svg)](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml)

# CalDAV Tasks MCP Server

Model Context Protocol (MCP) server for CalDAV task management. It exposes VTODO tools for AI agents and is built with .NET 10. The server is distributed via `dnx`.

## Quick start

Add this MCP server to VS Code, Claude Desktop, Cursor, or any MCP client:

```json
{
  "mcpServers": {
    "caldav-tasks": {
      "command": "dnx",
      "args": ["--yes", "dotnet-agents-caldav"],
      "env": {
        "CALDAV_URL": "https://caldav.example.com",
        "CALDAV_USERNAME": "user",
        "CALDAV_PASSWORD": "password",
        "CALDAV_EXPOSE_ADVANCED_TOOLS": "true"
      }
    }
  }
}
```

## Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `CALDAV_URL` | Yes | Base URL of the CalDAV server |
| `CALDAV_USERNAME` | Yes | Username for Basic auth |
| `CALDAV_PASSWORD` | Yes | Password for Basic auth |
| `CALDAV_TASK_LISTS` | No | Comma-separated task list hrefs to expose; omit to auto-discover |
| `CALDAV_EXPOSE_ADVANCED_TOOLS` | No | Set to `true` to expose href-based tools like `get_task`, `update_task`, `complete_task`, and `delete_task` |
| `CALDAV_EXPOSE_EXACT_TOOLS` | No | Set to `true` to expose protected exact Calendar Object Resource tools |

## Available tools

### Chat-safe tools

- `list_task_lists` — List available CalDAV task lists.
- `caldav_add_task` — Create a task using a user-facing list name.
- `caldav_complete_task_by_summary` — Mark a task complete by summary.
- `caldav_delete_task_by_summary` — Delete a task by summary.

### Advanced tools

- `list_tasks` — List tasks with optional filters such as task list, text search, status, due date, and completion state.
- `get_task` — Fetch a single task by ID or href.
- `create_task` — Create a new task in a task list.
- `update_task` — Update task fields while preserving server state via ETag checks.
- `complete_task` — Mark a task complete.
- `delete_task` — Delete a task.

### Calendar resource tools

- `calendars.list` — Discover the configured Calendar Scope.
- `calendar_resources.get` — Read an authoritative semantic-or-opaque snapshot by confirmed absolute href.
- `calendar_entities.query` — Query bounded persisted Event and To-do snapshots across default, selected, or explicit-all Calendar Scope.
- `calendar_occurrences.query` — Expand Event and To-do occurrences locally within a required half-open UTC window, using an explicit IANA evaluation time zone only when floating or date-only values require it.
- `events.create` — Create one Event in a selected Calendar.
- `events.patch` — Apply a revision-bound semantic patch to one Event resource.
- `todos.create` — Create one To-do in a selected Calendar.
- `todos.patch` — Apply a revision-bound semantic patch to one To-do resource.
- `calendar_occurrences.add` — Add one explicit RDATE identity.
- `calendar_occurrences.exclude` — Add one exact EXDATE while preserving any override.
- `calendar_occurrences.restore_exclusion` — Remove only one exact EXDATE.
- `calendar_occurrences.cancel` — Create or update one complete cancelled override.
- `calendar_occurrences.restore_cancellation` — Remove only cancelled status from one override.
- `calendar_resources.delete` — Delete an entire resource from an explicitly supplied revision reference (href, UID, kind, and exact strong ETag) after MCP MRTR review and confirmation; success requires verified absence.
- `calendar_resources.exact_get` — Opt-in exact read through a protected MCP resource link; enable with `CALDAV_EXPOSE_EXACT_TOOLS=true`.

The staged default catalog is `calendars.list`, `calendar_entities.query`, `calendar_occurrences.query`, `calendar_resources.get`, `events.create`, `events.patch`, `todos.create`, `todos.patch`, `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, `calendar_occurrences.restore_cancellation`, `calendar_resources.delete`, `list_task_lists`, `show_tasks`, `add_task`, `find_tasks`, `complete_task_by_summary`, and `delete_task_by_summary`. Set `CALDAV_EXPOSE_ADVANCED_TOOLS=true` to also expose the legacy href-based advanced tools.

## Supported servers

Tested with Radicale (`tomsquest/docker-radicale`). It should work with any standard CalDAV server that supports VTODO collections.

## Architecture

Layered design:

`MCP tools` → `ICalendarService`/`ITaskService` → thin service facade → `CalDavClient` → `HttpClient`; lossless iCalendar projection and bounded recurrence evaluation stay in Core's iCalendar modules.

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

## Roadmap

- VEVENT support for calendar events
- VJOURNAL support
- WebDAV-Sync support
- Nextcloud-specific compatibility fixes
