# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial release: CalDAV Tasks (VTODO) MCP server
- 7 MCP tools: list_task_lists, list_tasks, get_task, create_task, update_task, complete_task, delete_task
- Custom thin HttpClient-based CalDAV client with Ical.Net v5
- ETag-based optimistic concurrency control
- Full test suite: 220+ tests with 98%+ line coverage
- Testcontainers integration tests against Radicale
