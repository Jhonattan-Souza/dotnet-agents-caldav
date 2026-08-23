# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Added immutable ten-minute Query Result Snapshot pagination for `calendar_entities.query`, including authenticated replayable Continuation Cursors, variable page sizes, and typed `cursor_expired` and `busy` failures.
- Added a typed `ICalendarQueryModule`, a narrow CalDAV query transport, exact linear page-byte admission, bounded process-local snapshot retention, and privacy-safe query phases and aggregate work counters.
- Added an automatic bounded Direct GET Compatibility Mode for verified `calendar-multiget` unavailability, with canonical four-wide waves, shared per-origin permits, closed execution-limit evidence, and strict all-or-nothing resource truth.
- Added an explicit caller-or-configured IANA Temporal Evaluation Context for bounded Calendar Entity queries, including source-preserving DST and date-only evaluation and frozen context on every snapshot page.
- Added immutable Query Result Snapshot traversal for `calendar_occurrences.query`, preserving full recurrence identities, source and effective timing, strong revision lineage, and explicit cancellation policy across pages.
- Added immutable Query Result Snapshot Start/Continue pagination for `todos.query`, with one VTODO-only authoritative corpus, one-pass parsed semantic state, effective override-aware filtering, and frozen Temporal Evaluation Context.
- Added opt-in OTLP traces, MCP metrics, and trace-correlated safe logs for the stdio server, including CalDAV aggregate-phase and outbound HTTP attempt waterfalls.
- Added automated loopback OTLP coverage for trace parentage, metrics, log correlation, redaction, disabled defaults, collector failure isolation, and stdin-EOF shutdown.
- Added truthful Operation outcome and Mutation State telemetry, explicit expected-absence observations, and per-wire-attempt retry recovery evidence through the built MCP stdio server.

### Changed

- Replaced Calendar Entity cursor re-execution and MCP-owned page assembly with one complete Start execution and zero-CalDAV, zero-semantic-work Continue execution.
- Changed `calendar_entities.query` input to a strict Start-or-Continue union and its pagination mode from `non_snapshot` to `query_result_snapshot`.
- Changed `todos.query` to a strict Start-or-Continue union; windowed Starts combine non-recurring Entity and recurring Occurrence lanes under one global order, while Continue performs zero CalDAV or semantic work.
- Bounded `calendar_entities.query` Start requests now reject a missing or invalid Temporal Evaluation Context before discovery; unbounded Starts reject an unused caller override.
- Replaced Occurrence cursor-bound query replay, MCP-owned page assembly, and duplicate deadlines with one typed module Start and a cursor-only Continue that performs no CalDAV, parsing, projection, or recurrence expansion.
- Reused one immutable, authorization-bound CalDAV discovery result inside each MCP tool call while keeping MRTR continuations fresh and Capability State separate.
- Kept successful query retrieval on 50-resource `calendar-multiget` batches with zero GETs, and made missing, duplicate, unsafe, unrequested, incomplete, or inconsistently tagged multiget results atomic protocol failures instead of fallback triggers.
- Migrated test execution from VSTest to Microsoft.Testing.Platform v2 and xUnit 4.
- Isolated coverage and TRX evidence per run so historical or nested runner artifacts cannot affect quality gates.
- Classified MRTR input requests and cooperative cancellation as expected control flow while preserving committed failures and exhausted retries as errors.

### Removed

- Removed the three legacy service query engines, their result, cursor, page, deadline, and query-progress contracts, and the remaining Occurrence and To-do query members from `ICalendarService`; all query members are now absent and all three query tools use only `ICalendarQueryModule`.
- Removed the shallow query-resource transport pass-through; the one production query transport now composes operation discovery with `CalDavClient`'s internal resource reads.

### Security

- Continuation Cursors now authenticate the tool, snapshot position, fixed expiry, credentials, and relevant configuration without exposing those values; retained snapshots contain projected result bytes rather than authoritative iCalendar content.
- Telemetry export now applies explicit span, metric, log, and resource allowlists that exclude CalDAV identifiers, credentials, payloads, URLs, OTLP headers, trace state, client-controlled metric labels, and exception details.
- Telemetry outcome, Mutation State, error, phase, purpose, observation, and recovery dimensions now use closed vocabularies; exported HTTP attempts contain no exception events or request targets.

## [0.2.2] - 2026-08-21

### Changed

- Create operations now use one authoritative conditional PUT instead of enumerating Calendar resources for UID preflight.
- Destination href collisions return `destination_conflict`; UID collisions remain `conflict`, both with `not_committed` mutation state.
- Exact Create MRTR now binds a dedicated reviewed create intent rather than a synthetic `"*"` revision.
- Release validation now relies on behavioral tests plus installation and MCP discovery from the final local NuGet artifact.

### Removed

- Retired executable requirement-to-TRX maps, catalogs, and gates; Git history preserves the former audit snapshots.
- Removed contracts, migration guides, changelog, release notes, and the bundled skill from the NuGet payload; these remain available in the repository or GitHub Releases.

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
