# Compact To-do Query Result Snapshot observation

Date: 2026-08-23
Baseline revision: `e63ea4d62fa4b4062566a6819127c18a30a1a38d`
Implementation revision: `ecb614cf6df3582c68b0c7966ffcd0333aafb65c`
Final #112 stack revision and changed observation anchor: `608ea44d2b730aca3153cf89d1a89a2fc868c455`
Runtime: .NET SDK `10.0.100` (`b0f34d51fc`), .NET runtime `10.0.0`, Linux `7.2.0-1-cachyos` `x86_64`, Omarchy `4.0.0`, Release
Server identity: Radicale `3.7.8`, index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, resolved platform digest `sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71`; deterministic query seam digest N/A

## Before

The baseline windowed `CalendarTodoQueryEngine` first ran a complete Calendar Entity query for VTODO resources and then ran a separate Calendar Occurrence query over the same Calendar Scope. Each engine owned acquisition and authoritative resource materialization independently. The To-do layer then parsed the same snapshot again for timing, completion, effective-due detection, and occurrence projection. A continuation cursor encoded the final sort position and called the entire To-do query again before skipping earlier rows, so smaller pages did not bound upstream or semantic work.

The prior load observation supplies the concrete baseline: the first pass retrieved 601 To-dos, the occurrence pass retrieved the complete 1,201-resource mixed Event/To-do corpus, and the request issued 43 REPORTs in 4,224 ms. Its exact Start used all-Calendar Scope; completion states open/completed/cancelled; UTC window `2026-07-01T00:00:00Z` through `2026-12-31T00:00:00Z`; evaluation zone `America/Sao_Paulo`; summary/status/due/priority/categories/recurrence projection; and `pageSize: 5`. The duration is a single supporting sample, not a threshold.

## After

The deterministic three-resource Start corpus contains a dated non-recurring VTODO, an undated VTODO, and a recurring VTODO with a changed override. One Start records exactly one discovery acquisition, one VTODO candidate query, one multiget corpus containing the three authoritative hrefs, three snapshot materializations/parses, three semantic evaluations, and four final row serializations. It retrieves no Event candidate or body. The non-recurring resources enter the Entity lane, including the undated item, while only the recurring resource enters the Occurrence lane and does not add an extra master row.

The retained #114 semantic observation recorded 9 ms for `TodoStartUsesOneVtodoCorpusAndSplitsEntityAndOccurrenceLanes` and 8 ms for the complete page-size 1/50/200 global-order matrix at revision `a0d9cc217516d135a94aed3e72d47464b3ec657b`; these are supporting observations only. The changed fixture also includes an irrelevant Event sentinel and proves that its body is never retrieved.

### Page-assembly allocation observation

The source-controlled [page-assembly runner](../scripts/observations/query-page-assembly/run.sh) executed the actual historical private `CalendarTodoTools.CreatePage` at `8a9d887a0b5e44ffbca3025a41ae7c8f6705dd77` and the actual current `CalendarTodoQueryPageCodec` at `61f2607383807f96464f33350e608180c1abee49`. It exported 201 historical compact To-do item JSON blobs (93,264 encoded bytes; corpus SHA-256 `9fdfa5e29113765dbfb896af6486d69c8c95b4d8f58b12ef30baf701911b09e3`) and supplied those exact bytes to the current codec. Each synchronous current-thread observation used 12 warmups and the median of 9 samples under .NET `10.0.0`, `linux-x64`, X64, workstation GC; no collection, timing threshold, allocation threshold, or ratio gate was used.

The historical window includes the private `CreatePage` body and its `CallToolResult` construction; the current window measures the Core codec `Admit` body and excludes the thin MCP success wrapper. The columns are supporting observations of the removed repeated-prefix assembler versus the current codec, not an end-to-end equal-boundary ratio.

| Page size | Historical/current admitted items | Historical median allocation | Current median allocation | Historical median duration | Current median duration |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 / 1 | 54,040 B | 17,952 B | 274,690 ticks (0.275 ms) | 36,379 ticks (0.036 ms) |
| 50 | 50 / 50 | 3,924,992 B | 351,224 B | 33,379,580 ticks (33.380 ms) | 767,292 ticks (0.767 ms) |
| 200 | 138 / 200 | 20,171,064 B | 1,466,944 B | 49,315,537 ticks (49.316 ms) | 3,215,684 ticks (3.216 ms) |

At page sizes 1 and 50, historical and current order and bytes matched exactly: SHA-256 `3cb24eab3d152aa23470d54ce8f72e9f426c0853fab7c33b60ee1776d45b2ca3` and `743ed9ed343e98ed9e339d4999dd01365c84fb6727af35f5d525d0f7d95f970c`. At requested page size 200, the historical 64 KiB response admission stopped at 138 items and 64,032 item bytes (SHA-256 `4eb81ca5dea3aa7e7f2bf495461e01ee525ce35233691371b4c1392270fae10c`), while the current codec admitted 200 items and 92,800 item bytes (SHA-256 `c278dc9737950a561049154f9817847b23e6d43fb88a595df21864eae3073129`); the first 138 current items matched the historical bytes and order exactly. Because the actual admitted work differs, no equal-work allocation or duration ratio is claimed for that row. The historical source blob was `dfebb262493f7538d6a3bb5165fe856bdae2bedd`; the current codec source blob was `82aa8bea2f0ea7f327f8818f68030bac8171279f`.

Reproduction command: `bash scripts/observations/query-page-assembly/run.sh /tmp/issue116-page-assembly`. The runner SHA-256 was `24cbdb520f803e9a1427007d44e90fd695a244c7f5589c1249f7044a62066703`; `runner-metadata.json` records the complete 18-cell matrix, all fixture/source hashes, runtime, GC mode, and method.

The query-scoped materializer supplies one parsed document and one typed Calendar authority to projection, temporal resolution, completion classification, and recurrence evaluation. The parsed document and typed Calendar never enter the retained Query Result Snapshot. The internal operation Activity records `caldav.query.parse_count` for the deterministic cardinality oracle; the OTLP allowlist strips that private work counter, as required by CAL-OBS-008.

Focused override cases prove that completion and due filtering happen after effective occurrence resolution. Contradictory cancelled/completed evidence remains available for `indeterminate` classification; a moved complete override that intentionally omits DUE remains due-less; a DUE-only detached override projects and orders as due without inventing start; and a completion-only override retains nominal master-role timing. No individual or range override inherits an omitted master span.

Continue authenticates the tool-bound cursor and reads only the immutable snapshot. The deterministic Start-to-Continue tracer records one snapshot lookup and one page admission on Continue, with zero discovery, candidate, multiget, GET, parse, evaluation, projection, or item serialization work. Replaying the same cursor returns byte-identical structured content. Representative page sizes 1, 50, and 200 traverse the same frozen global order, and the To-do-specific actual-envelope accountant admits exactly 4 MiB and rejects one byte more.

## Real stdio, Radicale, and privacy boundary

`CalendarMcpStdioIntegrationTests.TodoQuery_ReturnsNormalizedCompactResultsBeforePaginationOverNativeStdioAndRadicale` passed against the built Release MCP and the pinned Radicale fixture (1 test, 12.612 seconds). Start returned `query_result_snapshot` pagination and configured UTC Temporal Evaluation Context; each following request used the closed cursor-only Continue shape; the four open items were traversed without duplication; the explicit completion-state query returned completed, cancelled, and indeterminate states; and stderr remained empty.

`OpenTelemetryStdioIntegrationTests.TodoStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` passed against the same built MCP/Radicale boundary with the loopback collector (1 test, 11.620 seconds). Start used multiget with zero direct GET attempts. Continue emitted no CalDAV HTTP span and only the `snapshot_lookup` and `page_admission` query phases. Exported telemetry omitted the internal parse counter and contained none of the cursor, evaluation zone, private UID, summary, or href sentinels; all spans contained zero exception events and stderr remained empty.

## Atomicity and cleanup

Missing temporal context and missing or weak Entity Tags fail before semantic exclusion and retain no snapshot. Unevaluable recurrence fails atomically. The snapshot writer reserves storage only when the first page returns a cursor; shared reservation, cancellation, expiry, and disposal tests prove rollback to zero retained snapshots and bytes. Test service providers, Activities, MCP processes, OTLP receiver, HTTP clients, seeded and temporary Calendar resources, credentials, report artifacts, and the Radicale fixture are disposed or removed by their owners. The page-assembly runner removes its isolated shared clone and temporary worktrees on every exit; its final caller-owned observation root was removed only after the matrix and hashes above were transcribed. No broad 1,200-resource rerun, benchmark framework, allocation threshold, or latency SLA was introduced.

The authoritative combined gate passed 2,119 Core tests, 919 MCP tests, 100 Integration tests, and both ten-test pinned-Radicale conformance variants. Coverage was 19,864 of 21,230 lines (93.6%) and 11,259 of 13,224 branches (85.1%), eighteen covered branches above the exact 85% integer minimum. Slopwatch reported zero issues.
