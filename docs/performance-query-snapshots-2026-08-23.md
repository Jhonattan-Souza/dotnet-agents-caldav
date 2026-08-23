# Calendar Entity Query Result Snapshot observation

Date: 2026-08-23
Baseline revision: `ed316f9beb46a81d69e785006c0fe65d58c2298b`
Implementation revision: `ca0fb10491bd648bc52998789b3f7f185d806145`
Changed stdio/OTLP observation revision: `95f7711963837b3df1e7e130b7172cf402aa24d9`
Final #108 stack revision: `05f4973c8d4c1423da1c55cde4dbb0ee3c89b2f8`
Runtime: .NET SDK `10.0.100` (`b0f34d51fc`), .NET runtime `10.0.0`, Linux `7.2.0-1-cachyos` `x86_64`, Omarchy `4.0.0`, Release
Server identity: Radicale `3.7.8`, index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, resolved platform digest `sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71`

## Before

The 2026-08-21 fixed-state observation used 1,201 Calendar Object Resources. Both `pageSize: 1` and `pageSize: 200` repeated the complete query retrieval and issued 28 REPORTs. Page assembly serialized every growing prefix: the filter/page phase measured 34 ms for one item and 1,966 ms for 200 items. A five-item Calendar Entity page over 1,200 resources took 2,113 ms with 5 PROPFINDs, 26 REPORTs, and 0 GETs. These durations are supporting observations, not thresholds.

## After

The deterministic Start-to-Continue tracer freezes two results with `pageSize: 1`. Start performs one discovery acquisition, one candidate REPORT operation, one multiget operation, two semantic evaluations, and two item serializations. Continue performs exactly one snapshot lookup and one page admission, with zero discovery, REPORT, multiget, GET, parsing, filtering, projection, or evaluation. Repeating Continue returns byte-identical structured content and the same next cursor.

The changed input is an all-Calendar, Event-only Start over two authoritative Events followed by a cursor-only Continue; page-size traversal is asserted at 1, 50, and 200. The deterministic page cases did not record a standalone elapsed duration at the implementation revision; the 6.17-second real stdio/OTLP test duration below is the applicable supporting observation and is not a threshold.

### Page-assembly allocation observation

The source-controlled [page-assembly runner](../scripts/observations/query-page-assembly/run.sh) executed the actual historical private `CalendarEntityTools.CreatePage` at `4df75347477ca6dae463d60b938c7d28ab9b6ea6` and the actual current `CalendarEntityQueryPageCodec` at `61f2607383807f96464f33350e608180c1abee49`. It exported 201 valid historical projected Event item JSON blobs (418,080 encoded bytes; corpus SHA-256 `a0702ee4a10ba8e2705cc7073921583c22dfb84ad08d0bf03b2e2a6b7fada71e`) and supplied those exact bytes to the current codec. Each synchronous current-thread observation used 12 warmups and the median of 9 samples under .NET `10.0.0`, `linux-x64`, X64, workstation GC; no collection, timing threshold, allocation threshold, or ratio gate was used.

The historical window includes the private `CreatePage` body and its `CallToolResult` construction; the current window measures the Core codec `Admit` body and excludes the thin MCP success wrapper. The columns are supporting observations of the removed repeated-prefix assembler versus the current codec, not an end-to-end equal-boundary ratio.

| Page size | Admitted items | Historical median allocation | Current median allocation | Historical median duration | Current median duration |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 | 56,136 B | 22,000 B | 393,685 ticks (0.394 ms) | 86,153 ticks (0.086 ms) |
| 50 | 50 | 11,291,328 B | 763,216 B | 86,633,169 ticks (86.633 ms) | 3,391,195 ticks (3.391 ms) |
| 200 | 200 | 154,777,112 B | 3,118,192 B | 414,278,680 ticks (414.279 ms) | 6,592,252 ticks (6.592 ms) |

Historical and current admitted item order and bytes matched exactly: page-size 1 SHA-256 `6f5b4a796b559fc7cb5d2bc199398d41efe10b4db3a10a6fa21f2efcfafe5f55`, page-size 50 `1ca63e1033ff2e8905dc3fd6ab074b91dd50535e6dd9af443328f01a3239ad29`, and page-size 200 `359cdb6335b270b2bf724f34f8e608e9e4f7367b9d5d674ff1d2ae257fb61dd2`. The historical source blob was `c85965cdf855b0aed5f954e9c2a19ad64bcf7df5`; the current codec source blob was `cdc0df4f4471ffd5c5ebf456f3ec44b1262a9dbf`.

Reproduction command: `bash scripts/observations/query-page-assembly/run.sh /tmp/issue116-page-assembly`. The runner SHA-256 was `43cda1e1bbe6064565510ded291390c2aaeb8daf762f6637312c43e9ea65d383`; `runner-metadata.json` records the complete 18-cell matrix, all fixture/source hashes, runtime, GC mode, and method.

The page work oracle records one fixed-envelope serialization and one final materialization at page sizes 1, 50, and 200. The actual MCP SDK `CallToolResult` measurement is exact at 4 MiB - 1 byte, exactly 4 MiB, and 4 MiB + 1 byte with an item separator and non-null cursor present; only the last shape is rejected. Snapshot slot and byte-pool tests independently prove below, at, and above 16 snapshots and 128 MiB, and all cancellation, publication-failure, expiry, and disposal cases end with zero reservations and retained bytes.

The real stdio/Radicale/OTLP witness passed on 2026-08-23 at revision `95f7711963837b3df1e7e130b7172cf402aa24d9` (1 test, 6.17 seconds). It started a two-page Calendar Entity query and continued it in the same built MCP process, observed `pagination.mode: query_result_snapshot`, and observed no CalDAV wire request during Continue. Both Continue phases were direct children of the existing `caldav.operation`; the Continue operation exported only `snapshot_lookup_count` and `page_admission_count`; and the exported payload contained none of the private cursor, UID, summary, or href sentinels. The MCP process also kept stderr clean.

## Boundary and cleanup

No 1,200-resource rerun was performed: the accepted regression is structural, so the smallest sufficient deterministic corpus proves zero continuation work and one-pass assembly, while the pinned Radicale case proves the server-facing transport boundary. Test stores, timers, MCP processes, loopback collectors, temporary Calendars, credentials, and Radicale fixtures are disposed by their owning fixtures; no snapshot is persisted or spilled and no report artifact root is retained after successful transcription.
