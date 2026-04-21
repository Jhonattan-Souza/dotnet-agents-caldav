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

By default, the server exposes only the chat-safe tools: `list_task_lists`, `caldav_add_task`, `caldav_complete_task_by_summary`, and `caldav_delete_task_by_summary`. Set `CALDAV_EXPOSE_ADVANCED_TOOLS=true` to also expose the href-based advanced tools.

## Supported servers

Tested with Radicale (`tomsquest/docker-radicale`). It should work with any standard CalDAV server that supports VTODO collections.

## Architecture

Layered design:

`MCP tools` → `ITaskService` → `CalDavClient` → `HttpClient` + `Ical.Net`

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
