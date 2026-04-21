# Release Notes

## 0.1.4 — 2026-04-21

### Fixed
- Chat tools now return structured JSON errors for task list resolution failures instead of generic MCP exceptions
- `show_tasks` and `add_task` catch `TaskListResolutionException` and return `list_resolution_error` payload with `availableLists`
- `find_tasks`, `complete_task_by_summary`, and `delete_task_by_summary` handle explicit list name resolution failures with structured errors
- Normalize `taskListName` and `summary` consistently across all chat tool response payloads

### Changed
- Coverage pipeline now filters out test assemblies from coverage reports to ensure accurate production code metrics
- Updated `reportgenerator` invocation to use proper assembly filters

## 0.1.3 — 2026-04-21

### Fixed
- Remove console logging entirely from MCP stdio server to prevent log pollution that breaks MCP clients
- Strengthen `StdioLoggingIntegrationTests` to verify both invalid config (stderr contains error) and valid config (both stdout and stderr are clean) scenarios

### Changed
- Keep `NuGet/login@v1` because that is the version currently documented by NuGet/Microsoft for trusted publishing
- Opt the release workflow into the GitHub Actions Node 24 runtime early via `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true`
- Update `softprops/action-gh-release` from `v2` to `v3` for Node 24 runner compatibility

## 0.1.2 — 2026-04-21

### Fixed
- Include `README.md` in NuGet package to resolve NuGet.org warning
- Include `.mcp/server.json` in NuGet package and update to MCP Registry schema so NuGet.org can generate VS Code MCP configuration
- Set `VersionPrefix` to `0.0.0-local` and use `0.0.0` placeholders in `server.json` to avoid misleading version numbers in source

### Changed
- Release workflow now automatically syncs `server.json` version with the git tag before publishing

## 0.1.1 — 2026-04-20

### Fixed
- Redirect MCP server console logs to stderr to prevent JSON-RPC stream corruption on stdout

## 0.1.0 — 2026-04-19


### Added
- CalDAV Tasks (VTODO) MCP server for AI agents
- 7 MCP tools for task list and task management
- Custom HttpClient-based CalDAV client
- Ical.Net v5 integration for iCalendar parsing/serialization
- ETag-based optimistic concurrency
- Full test suite with 90%+ coverage gates
- Testcontainers integration tests with Radicale
- Slopwatch anti-slop enforcement
