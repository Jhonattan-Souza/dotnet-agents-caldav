# Release Notes

## 0.2.0 — 2026-08-17

Version 0.2.0 is a deliberate breaking replacement for the 0.1.x task-specific contract. The NuGet package and MCP server identities are unchanged, but the old tools, environment names, public task types, and response shapes are removed without aliases or a legacy mode. Follow the [0.1.x to 0.2.0 migration and rollback guide](docs/migrating-0.1.x-to-0.2.0.md) before upgrading.

### Added

- Unified Calendar Service and deterministic 16-tool semantic MCP catalog for Calendars, Events, To-dos, and recurring Occurrences.
- Four independently gated exact Calendar Object Resource tools.
- Lossless resource snapshots, strict input and output schemas, structured outcomes, bounded pagination, explicit Calendar Scope, strong-ETag revision references, and post-write verification.
- MCP Multi Round-Trip Request confirmation for delete, exact writes, destructive collection replacement, recurrence-definition changes, this-and-future, and entire-set mutations.
- Migration, deployment verification, and rollback instructions. Upgrade and rollback require no CalDAV data migration.

### Breaking changes

- Removed `list_task_lists`, `show_tasks`, `find_tasks`, `list_tasks`, `get_task`, `add_task`, `create_task`, `update_task`, `complete_task`, `complete_task_by_summary`, `delete_task`, and `delete_task_by_summary`.
- Removed `CALDAV_TASK_LISTS`, `CALDAV_DEFAULT_TASK_LIST`, and `CALDAV_EXPOSE_ADVANCED_TOOLS`. Use `CALDAV_CALENDAR_HREFS`, `CALDAV_DEFAULT_TODO_CALENDAR_NAME`, `CALDAV_DEFAULT_EVENT_CALENDAR_NAME`, and `CALDAV_EXPOSE_EXACT_TOOLS`.
- Summary and Calendar Name are no longer mutation identities. Existing-resource mutations require the reviewed href, Entity UID, Entity Kind, and exact strong Entity Tag.

### Interoperability and limitations

- The verified profile is Radicale 3.7.8 from the official Kozea image pinned by OCI index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, with CPython 3.14.7 and vobject 0.9.9. Other CalDAV servers are unverified profiles.
- CalDAV scheduling transport is unsupported. Organizer, attendee, and participant values are stored without sending invitations or propagating changes. Alarms and URI-bearing values are inert.
- Multiple-RRULE resources are preserved but return `recurrence_unevaluable`; RDATE PERIOD semantic writes return `unsupported_capability`; unresolved or conflicting time zones return `temporal_unresolved`; invalidly projectable resources return `opaque_resource`.
- Missing or weak Entity Tags return `concurrency_unavailable`. Post-dispatch uncertainty is reported as `fidelity_failure`, `committed_but_unverified`, `committed_but_concurrency_unavailable`, or `indeterminate` rather than ordinary success.

### Rollback

Pin `dotnet-agents-caldav@0.1.4`, restore the old 0.1.x environment names, restart, and rediscover the old catalog. No CalDAV data migration is required or performed.

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
