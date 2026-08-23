# CalDAV MCP full-implementation load-test report, 2026-08-23

## Result

The full implementation completed the same 1,200-resource workload used by the [2026-08-21 baseline](performance-load-test-2026-08-21.md). All 21 catalog tools completed, all writes reached their intended committed state, the final authoritative corpus returned to 600 Events and 600 To-dos, and no operation exported an exception event.

The three main performance problems from the baseline changed as intended:

1. A semantic Move into a 600-resource destination fell from 617 to 10 HTTP requests, a 98.38% reduction. Server duration fell from 2,334 ms to 1,298 ms, a 44.39% reduction or 1.80x speedup.
2. The complete two-call exact-Move MRTR workflow fell from 1,827 to 17 HTTP requests, a 99.07% reduction. Its supporting combined server duration fell from 5,631 ms to 462 ms, a 91.80% reduction or 12.19x speedup in the observed process topologies.
3. A corpus-matched occurrence-aware To-do query fell from 53 to 20 HTTP requests, a 62.26% reduction. Server duration fell from 4,224 ms to 1,470 ms, a 65.20% reduction or 2.87x speedup.

One acceptance gap remains: three confirmed deletes committed successfully but inherited `hasError=true` from expected reconciliation 404 spans.

There is no defensible single percentage for the whole server. The workload contains different operations, and several ordinary reads were slower in this one local run despite unchanged request counts. The strongest claims are the request-count reductions, removal of corpus-size-dependent work, and zero-remote-work continuation. The latency values are supporting single samples, not benchmark distributions or service-level targets.

## Comparison with the August 21 baseline

| Path | Baseline | Full implementation | Change |
|---|---:|---:|---:|
| Semantic Move, populated destination | 2,334 ms; 617 requests | 1,298 ms; 10 requests | 44.39% lower duration; 98.38% fewer requests |
| Exact Move, complete MRTR workflow | 5,631 ms; 1,827 requests | 462 ms; 17 requests | 99.07% fewer requests; supporting duration 91.80% lower |
| Occurrence-aware To-do query, matched 601-To-do topology | 4,224 ms; 53 requests | 1,470 ms; 20 requests | 65.20% lower duration; 62.26% fewer requests |
| Entity query, page size 200 | 4,335 ms; 33 requests | 4,118 ms; 33 requests | 5.01% lower duration; same upstream work |
| Entity query, page size 1 | 1,869 ms; 33 requests | 3,301 ms; 33 requests | 76.62% higher duration; same upstream work |
| Entity query, page size 5 | 2,113 ms; 31 requests | 4,282 ms; 31 requests | 102.65% higher duration; same upstream work |
| Occurrence query, page size 5 | 2,063 ms; 31 requests | 4,429 ms; 31 requests | 114.69% higher duration; same upstream work |
| Compact To-do query | 985 ms; 18 requests | 1,964 ms; 18 requests | 99.39% higher duration; same upstream work |
| Resource GET | 121 ms; 6 requests | 704 ms; 6 requests | 481.82% higher duration; same upstream work |

The ordinary-read regressions prevent an overall latency-win claim. Their request shapes did not regress. Current traces still spend most operation time in discovery and fetch, but these single samples cannot attribute the cross-run latency difference. The new snapshot Start also serializes the complete immutable result once so later Continue calls can avoid CalDAV and semantic work. Local Radicale, cold-process effects, host load, and response transfer can move single-sample wall times substantially. A latency distribution would require a separate controlled benchmark with warmups and repeated samples.

## Test boundary

| Item | Value |
|---|---|
| Baseline | `main` at `ed316f9beb46a81d69e785006c0fe65d58c2298b` |
| Full implementation | `stack105/issue-116-performance-gates` at `fb61342c4006ba2b3bbfd10bbe482566795410ad` |
| MCP assembly | Release DLL SHA-256 `3158808fc207574263dbf85176be09b988906df6dbb24599f4c1d859ed9228c3` |
| Build | .NET SDK 10.0.100, Release, 0 warnings and 0 errors |
| CalDAV | Radicale 3.7.8, `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80` |
| Telemetry | Aspire Dashboard 13.4.2, `mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b` |
| Driver | Raw MCP protocol `2026-07-28` over local stdio |
| Network | Radicale and Dashboard on loopback on the same host |
| Run window | 2026-08-23 22:04:17Z through 22:05:14Z |

The driver used the server-root CalDAV URL, matching the baseline discovery boundary. A validation run with a principal URL was discarded before comparison because it skipped three baseline discovery PROPFINDs. The corrected run started from a clean Dashboard telemetry window. It created the empty archive Calendar after the initial read cases and before the mutation and pagination cases, matching the baseline lifecycle order.

The raw driver made every current call. This removes model and Hermes-adapter variability, supports MRTR continuation, and leaves the measured server traces comparable with the baseline's server-side durations, which also excluded model inference. The current run does not compare Hermes inference or agent behavior.

## Corpus and lifecycle

The seed rules and fixed data distribution match the baseline:

| Calendar | Resources when introduced | Distribution |
|---|---:|---|
| Performance Events | 600 | 180-day spread, weekly recurrences, all-day Events, cancellations, categories, and locations |
| Performance To-dos | 600 | 180-day spread, weekly recurrences, open/completed/cancelled states, due dates, priorities, and categories |
| Performance To-dos Archive | 0 | Empty-destination Move case and temporary lifecycle resource |

An authoritative REPORT counted 600 Events and 600 To-dos before measurement. Setup and seeding occurred outside the measured traces. The matrix added disposable resources, exercised reads, semantic mutations, exact MRTR workflows, pagination, and both Move destination sizes, then removed the fixtures. The final authoritative count was again 600 Events, 600 To-dos, and zero archive resources.

The empty semantic Move sent the lifecycle To-do from the 600-resource To-do Calendar to the empty archive. The populated case moved it back from the archive to the 600-resource To-do Calendar. Exact Move used an Event source and destination href in the 600-resource Event Calendar.

## Tool coverage

Aspire recorded all 21 live catalog tool names. Protected operations list review and confirmation together.

| Tool | Result | Representative server duration | HTTP observation |
|---|---|---:|---|
| `calendars.list` | Success | 1,123 ms | 5 PROPFIND |
| `calendar_entities.query` | Success | 4,282 ms | 5 PROPFIND, 26 REPORT |
| `calendar_occurrences.query` | Success | 4,429 ms | 5 PROPFIND, 26 REPORT |
| `todos.query` | Success | 1,964 ms compact; 2,203 ms occurrence | 5 PROPFIND, 13 REPORT in either mode |
| `calendar_resources.get` | Success | 704 ms | 5 PROPFIND, 1 GET |
| `calendar_resources.exact_get` | Success | 643 ms | 5 PROPFIND, 1 GET |
| `events.create` | Committed | 1,476 ms | 5 PROPFIND, 1 GET, 1 PUT |
| `events.patch` | Committed | 537 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `todos.create` | Committed | 1,286 ms | 5 PROPFIND, 1 GET, 1 PUT |
| `todos.patch` | Committed | 288 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `todos.complete` | Committed | 245 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_occurrences.add` | Committed | 248 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_occurrences.exclude` | Committed | 227 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_occurrences.restore_exclusion` | Committed | 233 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_occurrences.cancel` | Committed | 264 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_occurrences.restore_cancellation` | Committed | 234 ms | 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_resources.move` | Committed | 329 ms empty; 1,298 ms populated | 5 PROPFIND, 4 GET, 1 MOVE |
| `calendar_resources.delete` | Committed | 342 ms review + 343 ms confirmation | Confirmation: 5 PROPFIND, 3 GET, 1 DELETE |
| `calendar_resources.exact_create` | Committed | 1,119 ms review + 872 ms confirmation | Confirmation: 5 PROPFIND, 2 GET, 1 PUT |
| `calendar_resources.exact_replace` | Committed | 297 ms review + 318 ms confirmation | Confirmation: 5 PROPFIND, 3 GET, 1 PUT |
| `calendar_resources.exact_move` | Committed | 209 ms review + 253 ms confirmation | Workflow: 10 PROPFIND, 6 GET, 1 MOVE |

The latest 300 logs contained 160 Information and 140 Debug records, with no Warning, Error, or Fatal record. Across the 36 server traces, the matrix issued 175 PROPFINDs, 160 REPORTs, 54 GETs, 12 PUTs, 3 DELETEs, and 3 MOVEs.

## Query observations

### Main query lanes

| Path | Server duration | Operation duration | Candidate and snapshot work | HTTP |
|---|---:|---:|---|---|
| Entity query | 4,282 ms | 4,044 ms | 1,200 candidates, snapshots, evaluations, and serializations | 5 PROPFIND, 26 REPORT, 0 GET |
| Windowed entity query | 3,368 ms | 3,151 ms | 1,200 candidates and snapshots | 5 PROPFIND, 26 REPORT, 0 GET |
| Occurrence query | 4,429 ms | 4,197 ms | 1,200 snapshots; 2,987 serialized occurrences | 5 PROPFIND, 26 REPORT, 0 GET |
| Compact To-do query | 1,964 ms | 1,749 ms | 600 To-do snapshots and serializations | 5 PROPFIND, 13 REPORT, 0 GET |
| Occurrence-aware To-do query | 2,203 ms | 1,995 ms | One 600-To-do snapshot corpus; 1,260 serialized results | 5 PROPFIND, 13 REPORT, 0 GET |

Every Query Start reported `caldav.query.fetch_mode=multiget`, with zero direct-GET resources and zero direct-GET attempts. Continue does not have a fetch mode because it performs no fetch. The full load therefore verifies the Radicale multiget path, not the compatibility fallback. The fallback has separate focused evidence in [`performance-direct-get-compatibility-2026-08-23.md`](performance-direct-get-compatibility-2026-08-23.md).

The occurrence-aware To-do path now consumes one VTODO-only snapshot corpus. It no longer performs the baseline's second mixed Event/To-do retrieval. The main matrix ran this query before it created the archive and while the To-do corpus contained 600 resources, yielding 13 REPORTs. The baseline sample ran after a lifecycle resource and archive existed: 601 To-dos across two VTODO Calendars, followed by a second 1,201-resource mixed retrieval.

A targeted follow-up reproduced that baseline topology with 601 To-dos and the archive present. Trace `13b78cdb49dc3c88d2212d63f5e1c063` recorded 1,470 ms, 5 PROPFINDs, and 15 REPORTs. It reported 601 candidates, multiget resources, snapshots, and evaluations; 1,265 serializations; zero direct GET attempts; `fetch_mode=multiget`; and `outcome=success`. The fixture was removed and the authoritative To-do count returned to 600. Against the matched baseline, REPORTs fell from 43 to 15 and PROPFINDs from 10 to 5.

The bounded Entity query that failed with `temporal_unresolved` in the baseline now succeeded with an explicit caller Temporal Evaluation Context. It still fetched the full corpus, so this is a correctness result, not a speed result.

### Query Result Snapshot pagination

| Call | Returned items | Server duration | Operation duration | Semantic phases | HTTP |
|---|---:|---:|---:|---|---|
| Start, page size 1 | 1 | 3,301 ms | 3,096 ms | Serialization 404 ms; page admission 8 ms | 5 PROPFIND, 28 REPORT |
| Continue from that snapshot, page size 200 | 200 | 856 ms | 66 ms | Snapshot lookup 4 ms; page admission 26 ms | None |
| Independent Start, page size 200 | 200 | 4,118 ms | 3,109 ms | Serialization 387 ms; page admission 10 ms | 5 PROPFIND, 28 REPORT |

Both Start calls performed the same candidate, multiget, snapshot, evaluation, and full-snapshot serialization work over 1,201 resources. Their page-admission phase stayed flat at 8 versus 10 ms. Continue performed one snapshot lookup and one page admission, with zero discovery, remote requests, parsing, filtering, projection, recurrence expansion, or other semantic work.

The old page-size 1-to-200 server-duration increase was 2,466 ms. It is 817 ms in the current Start comparison, a 66.87% smaller increment. That comparison includes different response sizes and process transfer costs. The more direct structural result is that the page-size-dependent in-operation phase is now flat, while Start pays one complete serialization to support zero-work continuation. Focused allocation and byte-boundary evidence remains in [`performance-query-snapshots-2026-08-23.md`](performance-query-snapshots-2026-08-23.md).

The baseline `filter/page` phase and current `page_admission` phase are not like-for-like instrumentation, so their absolute values are not directly compared.

## Move observations

### Semantic Move

| Destination | Revision | Duration | Requests | PROPFIND | REPORT | GET | Unrelated GET | MOVE |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Empty | Baseline | 276 ms | 17 | 10 | 2 | 4 | 0 | 1 |
| Empty | Current | 329 ms | 10 | 5 | 0 | 4 | 0 | 1 |
| 600 resources | Baseline | 2,334 ms | 617 | 10 | 2 | 604 | 600 | 1 |
| 600 resources | Current | 1,298 ms | 10 | 5 | 0 | 4 | 0 | 1 |

The current request shape is constant across the two destination sizes. The populated case removed all 600 unrelated GETs and both destination enumeration REPORTs. The empty case was 53 ms slower in its single timing sample but still used 41.18% fewer requests. The populated case provides the meaningful load differential.

### Exact Move

| MRTR stage | Baseline duration | Current duration | Baseline HTTP | Current HTTP |
|---|---:|---:|---|---|
| Review | 1,982 ms | 209 ms | 4 PROPFIND, 2 REPORT, 602 GET | 5 PROPFIND, 2 GET |
| Confirmation | 3,649 ms | 253 ms | 8 PROPFIND, 4 REPORT, 1,206 GET, 1 MOVE | 5 PROPFIND, 4 GET, 1 MOVE |
| Complete workflow | 5,631 ms | 462 ms | 1,827 requests | 17 requests |

The review trace reported `caldav.outcome=input_required` and `caldav.mutation.state=not_attempted` without error status. The confirmed trace reported `success` and `committed`. Both stages performed fresh discovery, while neither scanned an unrelated destination resource. The server-authoritative design and 1/50/600-resource focused witnesses are documented in [`performance-server-authoritative-move-2026-08-23.md`](performance-server-authoritative-move-2026-08-23.md).

The current exact-Move calls ran in a process already used for exact create and replace, while the baseline timing came from a dedicated exact-Move reproduction. The 1,827-to-17 request reduction is the strong comparison. The 12.19x timing is a supporting observation with that warm-process difference.

## Telemetry finding: confirmed deletes still look failed

The telemetry improvements worked for MRTR review and absence checks in create and Move. Nine HTTP spans were classified as expected absence observations, and no Move or exact-create trace derived an error from those 404s.

Three confirmed delete traces still had `hasError=true`:

| Trace | Tool outcome | Mutation State | Error source |
|---|---|---|---|
| `64a2567e50223f4a6ef5e055819363f9` | Success | Committed | One reconciliation GET returned expected 404 but lacked absence-probe classification |
| `85f4f19d3f59d6278ed70d2502f7053c` | Success | Committed | One reconciliation GET returned expected 404 but lacked absence-probe classification |
| `832f5dfaa2cb47955ef791954eafa5e0` | Success | Committed | One reconciliation GET returned expected 404 but lacked absence-probe classification |

Each operation root remained successful, each delete committed, and none exported an exception event. The error comes from a native HTTP child span with status `404 Not Found` and `error.type=404`.

The remaining seam is in [`CalendarResourceDeleteEngine.cs`](../src/DotnetAgents.CalDav.Core/Services/CalendarResourceDeleteEngine.cs#L77): dispatched verification and uncertain-dispatch reconciliation call the injected generic resource reader. [`CalendarService.cs`](../src/DotnetAgents.CalDav.Core/Services/CalendarService.cs#L227) supplies `GetResourceAsync`, not `ProbeCalendarResourceAbsenceAsync`. The generic GET therefore cannot tag the expected 404 as `caldav.http.request_purpose=absence_probe`.

This is a remaining observability defect, not a failed mutation. Delete verification should use the absence-probe seam only where `NotFound` is the expected successful observation, while preflight and conflict refresh should retain normal GET semantics.

## What the rerun verifies

This load rerun directly verifies these claims:

- Semantic Move used the same ten-request shape against empty and 600-resource destinations.
- Occurrence-aware To-do queries use one VTODO-only corpus and do not retrieve Events.
- Query Start freezes one immutable result; Continue performs no remote or semantic work.
- Bounded Entity queries accept explicit Temporal Evaluation Context and no longer fail after doing all retrieval work.
- Discovery is acquired once inside the measured mutation operations that previously performed two passes.
- MRTR review is recorded as expected `input_required` control flow.
- Expected absence telemetry works for create and Move, but the rerun found the delete-specific gap above.

Combined with the linked focused evidence, the full stack also proves that Exact Move stays constant at destination sizes 1, 50, and 600, page assembly performs one bounded admission rather than repeated-prefix serialization, and the direct-GET compatibility fallback has the accepted fail-closed boundary.

This rerun does not verify a universal latency improvement, the direct-GET compatibility fallback under full load, WAN behavior, server concurrency limits, or production percentiles.

## Reproduction and retained evidence

The driver used MCP protocol `2026-07-28`: launch one built stdio process per scenario, send `tools/call` with protocol and client-capability metadata, retain protected MRTR request state when present, send the accepted form response, and verify the authoritative postcondition. The report omits disposable credentials, payload contents, Dashboard login tokens, and trace-detail URLs.

Each server process used these non-secret settings in addition to the server-root URL and disposable credentials:

```text
CALDAV_DEFAULT_EVENT_CALENDAR_NAME=Performance Events
CALDAV_DEFAULT_TODO_CALENDAR_NAME=Performance To-dos
CALDAV_EVALUATION_TIME_ZONE=America/Sao_Paulo
CALDAV_INTEROPERABILITY_PROFILE=radicale-3.7.8
CALDAV_EXPOSE_EXACT_TOOLS=true
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

The key query inputs were:

```json
{
  "entityPageFive": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "pageSize": 5
  },
  "boundedEntityPageFive": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "from": { "kind": "utcDateTime", "value": "2026-07-01T00:00:00Z" },
    "to": { "kind": "utcDateTime", "value": "2026-12-31T00:00:00Z" },
    "evaluationTimeZone": "America/Sao_Paulo",
    "pageSize": 5
  },
  "occurrenceAwareTodos": {
    "scope": { "mode": "all" },
    "completionStates": ["open", "completed", "cancelled"],
    "from": { "kind": "utcDateTime", "value": "2026-07-01T00:00:00Z" },
    "to": { "kind": "utcDateTime", "value": "2026-12-31T00:00:00Z" },
    "evaluationTimeZone": "America/Sao_Paulo",
    "projection": ["summary", "status", "due", "priority", "categories", "recurrence"],
    "pageSize": 5
  },
  "entityStartPageOne": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "pageSize": 1
  },
  "entityContinuePageTwoHundred": {
    "cursor": "<cursor returned by entityStartPageOne>",
    "pageSize": 200
  },
  "entityStartPageTwoHundred": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "pageSize": 200
  }
}
```

The semantic Move comparison used the same lifecycle To-do for both calls: To-do Calendar to empty archive, followed by archive to the populated 600-resource To-do Calendar. Exact Move created an Event at `full-load-exact-source.ics`, reviewed and confirmed a same-Calendar Move to `full-load-exact-destination.ics`, verified the destination, and deleted it.

[`performance-load-test-2026-08-23-results.json`](performance-load-test-2026-08-23-results.json) retains the environment identity, run window, corpus counts, key comparisons, representative trace IDs, HTTP shapes, query work counts, and aggregate telemetry. Raw OTLP exports and disposable server state are not retained after cleanup.

## Cleanup verification

Cleanup removed containers `caldav-full-perf-radicale` and `caldav-full-perf-aspire`, Docker volume `caldav-full-perf-data`, temporary Radicale configuration and credential files, raw trace and log exports, and generated harness scripts. Post-cleanup checks found no matching container or volume, no listener on ports 5232, 18888, or 4318, and no temporary evidence directory. The digest-pinned images remain in the shared Docker image cache.
