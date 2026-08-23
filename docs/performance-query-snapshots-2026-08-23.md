# Calendar Entity Query Result Snapshot observation

Date: 2026-08-23
Baseline revision: `ed316f9beb46a81d69e785006c0fe65d58c2298b`
Implementation revision: `47aca6e`
Configuration: .NET 10 Release; deterministic scripted CalDAV port for work counts; real MCP stdio and digest-pinned Radicale 3.7.8 for transport evidence

## Before

The 2026-08-21 fixed-state observation used 1,201 Calendar Object Resources. Both `pageSize: 1` and `pageSize: 200` repeated the complete query retrieval and issued 28 REPORTs. Page assembly serialized every growing prefix: the filter/page phase measured 34 ms for one item and 1,966 ms for 200 items. A five-item Calendar Entity page over 1,200 resources took 2,113 ms with 5 PROPFINDs, 26 REPORTs, and 0 GETs. These durations are supporting observations, not thresholds.

## After

The deterministic Start-to-Continue tracer freezes two results with `pageSize: 1`. Start performs one discovery acquisition, one candidate REPORT operation, one multiget operation, two semantic evaluations, and two item serializations. Continue performs exactly one snapshot lookup and one page admission, with zero discovery, REPORT, multiget, GET, parsing, filtering, projection, or evaluation. Repeating Continue returns byte-identical structured content and the same next cursor.

The page work oracle records one fixed-envelope serialization and one final materialization at page sizes 1, 50, and 200. The actual MCP SDK `CallToolResult` measurement is exact at 4 MiB - 1 byte, exactly 4 MiB, and 4 MiB + 1 byte with an item separator and non-null cursor present; only the last shape is rejected. Snapshot slot and byte-pool tests independently prove below, at, and above 16 snapshots and 128 MiB, and all cancellation, publication-failure, expiry, and disposal cases end with zero reservations and retained bytes.

The real stdio/Radicale/OTLP witness passed on 2026-08-23 at revision `71d7b9c` (1 test, 6.17 seconds). It started a two-page Calendar Entity query and continued it in the same built MCP process, observed `pagination.mode: query_result_snapshot`, and observed no CalDAV wire request during Continue. Both Continue phases were direct children of the existing `caldav.operation`; the Continue operation exported only `snapshot_lookup_count` and `page_admission_count`; and the exported payload contained none of the private cursor, UID, summary, or href sentinels. The MCP process also kept stderr clean.

## Boundary and cleanup

No 1,200-resource rerun was performed: the accepted regression is structural, so the smallest sufficient deterministic corpus proves zero continuation work and one-pass assembly, while the pinned Radicale case proves the server-facing transport boundary. Test stores, timers, MCP processes, loopback collectors, and Radicale fixtures are disposed by their owning fixtures; no snapshot is persisted or spilled.
