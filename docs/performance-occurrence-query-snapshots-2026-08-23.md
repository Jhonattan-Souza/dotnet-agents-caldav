# Occurrence Query Result Snapshot observation

Date: 2026-08-23
Baseline revision: `e63ea4d62fa4b4062566a6819127c18a30a1a38d`
Implementation revision: `b9d1bae75291b83d7f756785db49a9bcc21166bf`
Configuration: .NET 10 Release; deterministic scripted CalDAV port for work counts; real built MCP stdio, OTLP loopback export, and digest-pinned Radicale 3.7.8 for transport evidence

## Before

The Occurrence adapter decoded a cursor into the original query and last semantic key, called `ICalendarService.QueryOccurrencesAsync` again, filtered everything before that key, and constructed the next page and cursor locally. Every continuation could therefore repeat discovery, candidate REPORT, resource retrieval, parsing, recurrence expansion, ordering, and projection. The host filter and the adapter also owned independent 30-second deadlines. Resource href was absent from the cursor comparison, so otherwise equal effective-start, Calendar, UID, and Recurrence Identity keys did not have a complete traversal tie-breaker.

## After

The deterministic 201-resource equal-key tracer performs one Start and traverses the same immutable result at page sizes 1, 50, and 200. Every run returns all 201 unique Occurrences in ordinal Resource-href order with no omission or duplicate. Start performs one discovery, one candidate operation, five 50-resource multiget batches, 201 evaluations, and 201 projections. Every Continue performs only authenticated snapshot lookup and exact page admission; the transport call count remains unchanged. Replaying one cursor returns byte-identical structured content after the scripted remote body changes, while a new Start observes the changed body.

The focused Core module class passed 10 tests, including explicit included/excluded cancellation, neutral acquisition/context constructor structure, invalid context with zero I/O, strong-revision atomicity, frozen context, and page sizes 1/50/200. The complete Core and MCP unit projects passed 2,091 and 910 tests respectively in Release. These are deterministic structural and work-count observations, not latency thresholds.

The built stdio/Radicale Occurrence witness passed on 2026-08-23 (1 test, 11.69 seconds), preserving the authoritative recurring To-do identity, timing, snapshot revision, strict Start schema, and clean stderr. The built stdio/Radicale/OTLP witness passed separately (1 test, 11.82 seconds): Start returned a non-null snapshot cursor and Continue consumed it in the same MCP process. The Continue Operation exported only `caldav.query.mode`, `caldav.query.snapshot_lookup_count`, and `caldav.query.page_admission_count`; its trace contained only snapshot-lookup and page-admission query phases and no HTTP spans. Exported OTLP contained none of the private cursor, UID, summary, href, Entity Tag, credentials, or Calendar payload sentinels.

## Boundary and cleanup

No 1,200-resource rerun, benchmark framework, allocation threshold, or latency SLA was introduced. The 201-item deterministic corpus is the smallest one that crosses every accepted page size and proves the Resource-href tie-breaker. Radicale, MCP processes, the OTLP receiver, timers, and snapshot stores are disposed by their owning fixtures; snapshots remain process-local and are never persisted or spilled.
