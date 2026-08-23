# Compact To-do Query Result Snapshot observation

Date: 2026-08-23
Baseline revision: `e63ea4d`
Implementation revision: `c62422604c5dc62f5d7768a2217886575cfe7e9c`
Configuration: .NET 10 Release; deterministic typed query transport for work counts; real built MCP stdio and Radicale 3.7.8 pinned as `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`; loopback OTLP receiver for export privacy

## Before

The baseline windowed `CalendarTodoQueryEngine` first ran a complete Calendar Entity query for VTODO resources and then ran a separate Calendar Occurrence query over the same Calendar Scope. Each engine owned acquisition and authoritative resource materialization independently. The To-do layer then parsed the same snapshot again for timing, completion, effective-due detection, and occurrence projection. A continuation cursor encoded the final sort position and called the entire To-do query again before skipping earlier rows, so smaller pages did not bound upstream or semantic work.

These are code-derived work-shape observations at the baseline revision. No latency threshold or extrapolated server duration is attached to them.

## After

The deterministic three-resource Start corpus contains a dated non-recurring VTODO, an undated VTODO, and a recurring VTODO with a changed override. One Start records exactly one discovery acquisition, one VTODO candidate query, one multiget corpus containing the three authoritative hrefs, three snapshot materializations/parses, three semantic evaluations, and four final row serializations. It retrieves no Event candidate or body. The non-recurring resources enter the Entity lane, including the undated item, while only the recurring resource enters the Occurrence lane and does not add an extra master row.

The query-scoped materializer supplies one parsed document and one typed Calendar authority to projection, temporal resolution, completion classification, and recurrence evaluation. The parsed document and typed Calendar never enter the retained Query Result Snapshot. The internal operation Activity records `caldav.query.parse_count` for the deterministic cardinality oracle; the OTLP allowlist strips that private work counter, as required by CAL-OBS-008.

Focused override cases prove that completion and due filtering happen after effective occurrence resolution. Contradictory cancelled/completed evidence remains available for `indeterminate` classification; a moved complete override that intentionally omits DUE remains due-less; a DUE-only detached override projects and orders as due without inventing start; and a completion-only override retains nominal master-role timing. No individual or range override inherits an omitted master span.

Continue authenticates the tool-bound cursor and reads only the immutable snapshot. The deterministic Start-to-Continue tracer records one snapshot lookup and one page admission on Continue, with zero discovery, candidate, multiget, GET, parse, evaluation, projection, or item serialization work. Replaying the same cursor returns byte-identical structured content. Representative page sizes 1, 50, and 200 traverse the same frozen global order, and the To-do-specific actual-envelope accountant admits exactly 4 MiB and rejects one byte more.

## Real stdio, Radicale, and privacy boundary

`CalendarMcpStdioIntegrationTests.TodoQuery_ReturnsNormalizedCompactResultsBeforePaginationOverNativeStdioAndRadicale` passed against the built Release MCP and the pinned Radicale fixture (1 test, 12.612 seconds). Start returned `query_result_snapshot` pagination and configured UTC Temporal Evaluation Context; each following request used the closed cursor-only Continue shape; the four open items were traversed without duplication; the explicit completion-state query returned completed, cancelled, and indeterminate states; and stderr remained empty.

`OpenTelemetryStdioIntegrationTests.TodoStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` passed against the same built MCP/Radicale boundary with the loopback collector (1 test, 11.620 seconds). Start used multiget with zero direct GET attempts. Continue emitted no CalDAV HTTP span and only the `snapshot_lookup` and `page_admission` query phases. Exported telemetry omitted the internal parse counter and contained none of the cursor, evaluation zone, private UID, summary, or href sentinels; all spans contained zero exception events and stderr remained empty.

## Atomicity and cleanup

Missing temporal context and missing or weak Entity Tags fail before semantic exclusion and retain no snapshot. Unevaluable recurrence fails atomically. The snapshot writer reserves storage only when the first page returns a cursor; shared reservation, cancellation, expiry, and disposal tests prove rollback to zero retained snapshots and bytes. Test service providers, Activities, MCP processes, OTLP receiver, HTTP clients, seeded Calendar resources, and the Radicale fixture are disposed by their owners. No broad 1,200-resource rerun, benchmark framework, allocation threshold, or latency SLA was introduced.
