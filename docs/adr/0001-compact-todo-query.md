# ADR 0001: Compact semantic To-do query

Status: Accepted

Superseded in part by: ADR 0004 and ADR 0005 (2026-08-23)

Date: 2026-08-19

## Context

Routine agent task-list reads need a bounded semantic result without requiring
clients to parse `calendarProperties`. Existing `calendar_entities.query`
remains the compatibility surface for complete Calendar Object Resource
snapshots. To-do completion is not represented reliably by one iCalendar
property: `STATUS`, `COMPLETED`, and `PERCENT-COMPLETE` can be absent,
contradictory, or accompanied by `CANCELLED`.

## Decision

Add the read-only `todos.query` semantic surface. It requires an explicit
selected or all-Calendar Scope, defaults to the open completion state, filters
completion before pagination and compact-result admission, and returns typed
fields plus strong revision references. Entity queries return undated tasks;
occurrence-aware queries require a half-open UTC window and also return an
undated lane. Results are ordered by due, start, Calendar href, UID, resource
href, and recurrence identity. Cursors bind the complete query shape.

Completion normalization is conservative:

- `COMPLETED`, a `COMPLETED` timestamp, or `PERCENT-COMPLETE:100` means
  `completed` when no contradictory evidence exists.
- `STATUS:CANCELLED` means `cancelled` only without completion evidence.
- absent status and `NEEDS-ACTION`/`IN-PROCESS` mean `open`.
- invalid, duplicate, or contradictory evidence means `indeterminate`.

Indeterminate rows are excluded from the default open filter and counted in
the success envelope. `todos.complete` uses the same classifier and refuses
indeterminate state rather than guessing.

The compact projection is an allowlist of ten fields. Routine descriptions
direct agents to the compact surface. A 64 KiB structured-result budget is
enforced before a page is admitted. The compact MCP result is the wire-size
guarantee. The current implementation uses the existing bounded full-resource
read path; a future data-bearing CalDAV REPORT may be added as a capability
optimization, but pinned Radicale 3.7.8 ignores nested calendar-data property
projection and cannot provide selective upstream bytes.

ADR 0004 supersedes the cursor, admission, and acquisition mechanics above.
`todos.query` now owns a strict Start/Continue union through
`ICalendarQueryModule`, uses exact 4 MiB page admission and immutable ten-minute
Query Result Snapshots, and never re-executes CalDAV or semantic work on
Continue. One VTODO-only authoritative corpus supplies both non-recurring
Entity rows and recurring effective Occurrence rows. ADR 0005 further requires
every To-do Start to resolve one explicit IANA Temporal Evaluation Context
before I/O. The completion model, compact projection, lane semantics, and
strong revision targets in this ADR remain authoritative.

## Consequences

`calendar_entities.query` remains the full snapshot contract.
The semantic catalog grows from 16 to 17 default tools (21 including exact
tools), and the additive contract is documented as 0.2.1 while 0.2.0 artifacts
remain available for existing clients. Native stdio tests prove the compact
result and normalization against the pinned Radicale profile; upstream
projection reduction is not claimed for that profile.
