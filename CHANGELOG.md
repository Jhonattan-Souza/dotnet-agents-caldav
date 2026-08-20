# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.1] - 2026-08-19

### Added

- Added the bounded `todos.query` semantic MCP tool with explicit Calendar Scope, typed completion normalization, projection allowlisting, query-bound cursors, and strong revision targets.
- Added the `To-do Completion State` domain term, ADR 0001, 0.2.1 contract/evidence artifacts, and native Radicale/stdio coverage.

### Changed

- Default semantic discovery now contains 17 tools; `calendar_entities.query` remains backward compatible.
- `todos.complete` now shares normalized completion evidence with `todos.query`; cancelled and contradictory evidence produce typed state failures.

## [0.2.0] - 2026-08-17

### Added

- Unified semantic MCP catalog for Calendars, Events, To-dos, and bounded recurring Occurrences.
- Opt-in exact Calendar Object Resource reads and writes, protected independently from the semantic catalog.
- Strict structured results, typed failures, strong-ETag revision references, post-write verification, and MCP MRTR confirmation for high-impact writes.
- Digest-pinned Radicale 3.7.8 interoperability profile and permanent requirement-to-evidence catalog.
- [0.1.x to 0.2.0 migration and rollback guide](docs/migrating-0.1.x-to-0.2.0.md).

### Changed

- Replaced task-list configuration with canonical Calendar href scope, independent Event and To-do defaults, and an exact-tool gate.
- Made the Calendar Object Resource the persistence and concurrency aggregate while retaining server-returned UTF-8 content as authority.

### Removed

- Removed all twelve 0.1.x task tools, task-specific public domain types, old environment names, summary-based mutation shortcuts, blind href-only writes, and legacy compatibility modes.

### Security

- Existing-resource writes now require an exact strong Entity Tag; ambiguous outcomes reconcile through reads and are never blindly retried.
- Calendar scope and origin checks constrain network access; alarms, scheduling data, and URI values remain inert.
