# Release Notes

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
