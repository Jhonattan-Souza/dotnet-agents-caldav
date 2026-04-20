[![CI](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/ci.yml/badge.svg)](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/ci.yml)

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
        "CALDAV_PASSWORD": "password"
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

## Available tools

- `list_task_lists` — List available CalDAV task lists.
- `list_tasks` — List tasks with optional filters such as task list, text search, status, due date, and completion state.
- `get_task` — Fetch a single task by ID or href.
- `create_task` — Create a new task in a task list.
- `update_task` — Update task fields while preserving server state via ETag checks.
- `complete_task` — Mark a task complete.
- `delete_task` — Delete a task.

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
dotnet test --collect:"XPlat Code Coverage"
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
