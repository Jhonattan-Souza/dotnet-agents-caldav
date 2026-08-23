# CalDAV MCP performance and operation report, 2026-08-21

## Result

The test exercised all 21 tools in the live catalog against 1,200 seeded Calendar Object Resources. The server completed each operation, including revision-bound writes, recurrence mutations, atomic moves, exact replacement, and deletion. A raw MCP continuation driver completed protected writes that Hermes 0.20.4 could not continue after `InputRequiredResult`.

Three code paths need performance work:

1. Both move engines perform one GET per resource in the destination Calendar before dispatch. A semantic move into a 600-To-do Calendar issued 604 GETs and took 2,334 ms. A full exact-move MRTR workflow issued 1,808 GETs across its initial review and confirmed call and took 5,631 ms across the two server traces.
2. The paged query tools fetch the full candidate set before applying the cursor and page size. They also serialize the growing result prefix once per admitted item. On the same 1,201-resource state, `pageSize: 1` and `pageSize: 200` each issued 28 REPORTs, while the filter/page phase rose from 34 ms to 1,966 ms.
3. An occurrence-aware `todos.query` retrieves the To-do corpus, then runs a separate occurrence query that retrieves the mixed Event and To-do corpus again. The bounded five-item query issued 43 REPORTs and took 4,224 ms.

The normal query path did not show N+1 behavior. Radicale served authoritative resources through 50-resource `calendar-multiget` batches. A 1,200-resource query used 26 REPORTs and zero GETs. The direct-GET compatibility fallback remains a latent sequential N+1 path when multiget is unavailable.

## Test boundary

| Item | Value |
|---|---|
| Checkout | `main` at `ed316f9beb46a81d69e785006c0fe65d58c2298b` |
| Server | .NET SDK 10.0.100, Release build, 0 warnings and 0 errors |
| CalDAV | Radicale 3.7.8, `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80` |
| Telemetry | Aspire Dashboard 13.4.2, `mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b` |
| Agent client | Hermes Agent 0.20.4 with `openai/gpt-5.6-luna`, reasoning `medium` |
| MCP transport | Local stdio process from this checkout; OTLP/HTTP protobuf to the loopback Dashboard |
| Network | Radicale and Dashboard bound to loopback on the same host |
| Exact surface | Enabled for the disposable server process |

Hermes launched a fresh MCP process for each one-shot session. Aspire durations below measure one representative server trace and exclude model inference. The test gives reliable request shapes and code-path comparisons, not benchmark distributions or production latency percentiles. Radicale used a local Docker volume, so WAN latency and a remote server's concurrency limits remain outside this test.

## Corpus

The seed created two primary Calendars and later added an empty archive Calendar for move tests.

| Calendar | Resources | Distribution |
|---|---:|---|
| Performance Events | 600 | Dates spread across 180 days; 60 weekly recurring Events with 20 occurrences; 30 all-day Events; 12 cancelled Events; categories and location data |
| Performance To-dos | 600 | Dates spread across 180 days; 60 weekly recurring To-dos with 12 occurrences; 390 open, 180 completed, 30 cancelled; due dates, priorities, and categories |
| Performance To-dos Archive | 0 at seed | Lifecycle and empty-versus-populated move comparison |

An authoritative CalDAV REPORT counted 600 Event resources and 600 To-do resources after seeding. One disposable lifecycle To-do brought the later pagination comparison to 1,201 resources. Lifecycle checks read the resource after each write and checked strong Entity Tags, recurrence properties, source absence after MOVE, and destination content.

## Tool coverage

Aspire recorded all 21 catalog names. Hermes completed 17 operations. It reached `InputRequiredResult` for delete and exact create but could not continue them. The raw MCP driver completed those confirmations plus exact replace and exact move. Protected writes list separate review and confirmed calls because MRTR creates separate traces.

| Tool | Result | Driver | Sample server duration | Evidence trace | Note |
|---|---|---|---:|---|---|
| `calendars.list` | Success | Hermes | 341 ms | `7d91107ad0ce97e1d9647315d169c568` | Returned the authorized Calendars |
| `calendar_entities.query` | Success | Hermes | 2,113 ms | `da3dbeae38bfbc1fe252e98223ac19af` | Five-item page over the 1,200-resource corpus |
| `calendar_occurrences.query` | Success | Hermes | 2,063 ms | `4a8b65a4aa5fab80d1564fa6adbfae34` | Expanded the requested UTC window |
| `todos.query` | Success | Hermes | 985 ms entity; 4,224 ms occurrence | `948e854d1cfa8070e7bf8d4bfd2b1999`, `12dc088cc0c618f3bbbfd6cc155997ed` | Compact entity and occurrence-aware shapes |
| `calendar_resources.get` | Success | Hermes | 121 ms | `63c7971fa09024f7ca1cb07e25761bf3` | Semantic snapshot read |
| `calendar_resources.exact_get` | Success | Hermes | 142 ms | `16117544213764bfe057047b4ae023bf` | Authoritative byte read |
| `events.create` | Committed | Hermes | 597 ms | `37b03e366ae06ad18ce8273b954624f9` | Recurring Event fixture |
| `events.patch` | Committed | Hermes | 284 ms | `94598f719b0d3517722fb4e7bda2eb5a` | Summary and location changed; Entity Tag changed |
| `todos.create` | Committed | Hermes | 552 ms | `1ed396aa9f91dab3084c0ba3dc3aa49f` | Recurring To-do fixture |
| `todos.patch` | Committed | Hermes | 334 ms | `026665feda719c5623787d75eee946a1` | Summary and priority changed |
| `todos.complete` | Committed | Hermes | 420 ms | `f8e89b28e0fa8e214bccf14fd72a4535` | Completed one recurrence identity |
| `calendar_occurrences.add` | Committed | Hermes | 282 ms | `e094a28635f523dee7136f0f0cf6e22e` | Added an RDATE |
| `calendar_occurrences.exclude` | Committed | Hermes | 261 ms | `7c9c18d4a096c049cdd91388f8475aed` | Added an exclusion |
| `calendar_occurrences.restore_exclusion` | Committed | Hermes | 337 ms | `e76d09ec4307bdde0eb13cccbbbf026d` | Removed the exclusion while preserving other semantics |
| `calendar_occurrences.cancel` | Committed | Hermes | 375 ms | `a109ecd0ea18faaea94a4dcb045f0710` | Added a cancelled override |
| `calendar_occurrences.restore_cancellation` | Committed | Hermes | 346 ms | `b7fd8150a0359a92f6196f208517be28` | Restored the override content |
| `calendar_resources.move` | Committed | Hermes | 276 ms empty; 2,334 ms populated | `b38a47ce65ace5a95234ea7d0ba2940a`, `a2da1ae9cd59bb8c1b78a393494cd97f` | Same operation against empty and 600-resource destinations |
| `calendar_resources.delete` | Committed | Hermes review; raw confirmation | 112 ms review + 349 ms confirmed | `57236d26b9bd38db7502bfb61f5d8685`, `28cc1eb713df633445a0c9933bedc08e` | Hermes could not carry request state into confirmation |
| `calendar_resources.exact_create` | Committed | Hermes review; raw workflow | 368 ms review + 330 ms confirmed | `1353a7a054d61256130fe3b2f575cc5f`, `d67423845f2a3c98bf58b3a29011c6ea` | Conditional create and authoritative readback |
| `calendar_resources.exact_replace` | Committed | Raw MCP | 125 ms review + 263 ms confirmed | `bc7cafd9dbae949a1913b2c5d0ad3032`, `870111f56dc2dc6270bf5ebdf9aeaae7` | Complete replacement preserved identity and changed revision |
| `calendar_resources.exact_move` | Committed | Raw MCP | 1,982 ms review + 3,649 ms confirmed | `7433fc1b26cad6ab0575c39b11d4addc`, `9759ed6b1d62dad542af35271762bcc3` | Three destination scans across the full MRTR workflow |

No successful mutation produced a fidelity failure or an unknown Mutation State. The latest 300 Aspire log records contained 197 Information, 90 Debug, 13 framework diagnostic Warning records, and no Error or Fatal log record.

## HTTP and phase observations

| Path | Duration | HTTP shape | Main phase cost |
|---|---:|---|---|
| Entity query, 1,200 resources, page 5 | 2,113 ms | 5 PROPFIND, 26 REPORT, 0 GET | Fetch 1,628 ms |
| Occurrence query, 1,200 resources, page 5 | 2,063 ms | 5 PROPFIND, 26 REPORT, 0 GET | Fetch 1,372 ms; expand 493 ms |
| Compact To-do query, 600 To-dos, page 5 | 985 ms | 5 PROPFIND, 13 REPORT, 0 GET | Fetch 698 ms; filter 108 ms |
| Occurrence-aware To-do query, all scope, page 5 | 4,224 ms | 10 PROPFIND, 43 REPORT, 0 GET | Filter 1,652 ms; expand 1,272 ms |
| Resource GET | 121 ms | 5 PROPFIND, 1 GET | Discovery 109 ms |
| Event create | 597 ms | 5 PROPFIND, 1 GET, 1 PUT | Discovery and reconciliation dominate |
| Event patch | 284 ms | 10 PROPFIND, 2 GET, 1 PUT | Two discovery passes |
| Semantic move, empty destination | 276 ms | 10 PROPFIND, 4 GET, 2 REPORT, 1 MOVE | Constant-size fixture path |
| Semantic move, 600-resource destination | 2,334 ms | 10 PROPFIND, 604 GET, 2 REPORT, 1 MOVE | Destination UID scan |
| Exact move review, 600-resource destination | 1,982 ms | 4 PROPFIND, 602 GET, 2 REPORT | Destination UID scan |
| Exact move confirmed, dedicated repro | 3,649 ms | 8 PROPFIND, 1,206 GET, 4 REPORT, 1 MOVE | Review plus execution repeat the scan |
| Exact move, full MRTR workflow | 5,631 ms across two calls | 12 PROPFIND, 1,808 GET, 6 REPORT, 1 MOVE | Three destination scans |

The server-authoritative implementation follow-up is recorded in
[`performance-server-authoritative-move-2026-08-23.md`](performance-server-authoritative-move-2026-08-23.md).
For Exact Move it replaces the `3N + 8` involved-resource observation and
six-REPORT shape with six constant involved-resource observations, zero REPORT,
and one MOVE at destination sizes 1, 50, and 600. The historical timings above
remain the before measurement.

The query request count matches the batching policy. The first two REPORTs collect Event and To-do candidates; 24 multiget REPORTs retrieve 1,200 resources in batches of 50. After the archive Calendar and lifecycle resource existed, candidate selection plus 25 multiget batches produced 28 REPORTs.

## Findings and recommendations

### P0: Move performs an exhaustive UID scan

The semantic engine collects all destination hrefs and GETs each resource in [`CalendarResourceMoveEngine.cs`](../src/DotnetAgents.CalDav.Core/Services/CalendarResourceMoveEngine.cs#L146). The exact engine repeats the same policy in [`CalendarExactResourceEngine.cs`](../src/DotnetAgents.CalDav.Core/Services/CalendarExactResourceEngine.cs#L676). Exact confirmation runs review and execution in sequence in [`ExactCalendarResourceWriteTools.cs`](../src/DotnetAgents.CalDav.Mcp/Tools/ExactCalendarResourceWriteTools.cs#L369), and both paths prepare the destination. The confirmed call therefore performs about twice the collection work.

Radicale already returns `CALDAV:no-uid-conflict` for an atomic MOVE collision, and [`CalendarResourceMoveProtocol.cs`](../src/DotnetAgents.CalDav.Core/Internal/Xml/CalendarResourceMoveProtocol.cs#L131) maps that precondition to `DestinationConflict`. Creation removed the same non-atomic collection preflight under ADR 0002.

Recommended change:

- Make MOVE collision handling server-authoritative, subject to an explicit interoperability decision like ADR 0002.
- Keep the direct destination absence check, `If-Match`, `Overwrite: F`, dispatch classification, and two-sided reconciliation.
- Add real-HTTP regression cases with destination sizes 1, 50, and 600. The pre-dispatch request count should remain constant with Calendar size for semantic and exact moves.

This change would alter observable `opaque_resource`, `limit_exhausted`, inspection-count, concurrency, and error-precedence behavior in the frozen move contract. It also depends on each supported server enforcing `CALDAV:no-uid-conflict` for MOVE. Treat it as a contract and interoperability decision, run the project decision grill, and require explicit confirmation plus profile evidence before implementation. A compatible alternative would need to preserve those outcomes without an exhaustive authoritative scan.

### P1: Paged queries rescan the corpus and repeatedly serialize page prefixes

`CalendarEntityTools` calls the full service query before it applies a cursor or page size in [`CalendarEntityTools.cs`](../src/DotnetAgents.CalDav.Mcp/Tools/CalendarEntityTools.cs#L148). The Core engine collects all candidates and reads every 50-resource batch in [`CalendarEntityQueryEngine.cs`](../src/DotnetAgents.CalDav.Core/Services/CalendarEntityQueryEngine.cs#L82). A cursor changes the returned suffix, but it does not reduce CalDAV work.

The page builder creates and serializes a complete candidate result after each admitted item in [`CalendarEntityTools.cs`](../src/DotnetAgents.CalDav.Mcp/Tools/CalendarEntityTools.cs#L207). `CalendarOccurrenceTools` and `CalendarTodoTools` use the same prefix-measurement pattern. The fixed-state differential showed:

| Page size | Total | Discovery | Fetch | Filter/page | REPORTs | Returned payload |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 1,869 ms | 230 ms | 1,504 ms | 34 ms | 28 | One item |
| 200 | 4,335 ms | 233 ms | 1,489 ms | 1,966 ms | 28 | 200 items, about 770 KiB in Hermes spillover |

Recommended change:

- Measure a complete proposed page once, then find a fitting prefix with cumulative byte accounting or a logarithmic prefix search. Do not serialize prefixes 1 through N.
- Add page-size benchmarks at 1, 50, and 200 for all three query tools and track allocations as well as elapsed time.
- Decide whether cursor pages may reuse a bounded query snapshot or CalDAV sync-token state. Stateless cursor calls will otherwise rescan the corpus by design. Keep global ordering and semantic filtering correct; pushing `pageSize` into candidate collection would change results.

### P1: Occurrence-aware To-do queries retrieve the corpus twice

[`CalendarTodoQueryEngine`](../src/DotnetAgents.CalDav.Core/Services/CalendarTodoQueryEngine.cs#L58) first runs a To-do entity query. With `from` and `to`, it then runs `CalendarOccurrenceQueryEngine`, which performs a second entity retrieval for occurrence expansion. In all-Calendar Scope the second retrieval includes Events before the To-do filter runs.

The live bounded query returned five items but issued 43 REPORTs:

- The first pass selected To-dos and retrieved 601 To-do resources.
- The occurrence pass retrieved the 1,201-resource mixed corpus before retaining To-do occurrences.

The request took 4,224 ms, compared with 985 ms and 13 REPORTs for the entity-only compact query. Both are single samples, but the doubled discovery and retrieval follow the engine composition.

Recommended change:

- Let occurrence evaluation consume the authoritative To-do snapshots from the first pass when the query semantics permit it.
- If the occurrence engine must own retrieval, avoid the first full read for dated To-dos and perform a bounded undated lane without duplicating the dated corpus.
- Add real-HTTP assertions for entity-only and occurrence-aware shapes. The occurrence-aware path should not fetch Event resources for a To-do-only result.

### P1: Discovery work repeats inside one operation

A simple resource GET spent 109 of 121 ms in discovery and issued five PROPFIND requests. Patch and move paths issued ten PROPFINDs. A confirmed delete issued fifteen. Exact move issued up to twenty in the unrestricted workflow.

[`CalendarService.GetResourceAsync`](../src/DotnetAgents.CalDav.Core/Services/CalendarService.cs#L170) discovers Calendars for each read. Exact move discovers once in `ReadScopedAsync` and again in destination preparation. Review and execute then create new engine calls. The CalDAV client also runs home-set discovery before listing Calendar properties in [`CalDavClient.cs`](../src/DotnetAgents.CalDav.Core/Internal/CalDavClient.cs#L53).

Recommended change:

- Carry one immutable discovery result through each operation and share it across source and destination checks.
- Keep MRTR continuation revalidation across calls. Reuse discovery inside one review or execute call, not across an unbounded confirmation interval.
- Add HTTP-count assertions for GET, patch, move, and confirmed delete so later refactors cannot add another discovery pass.

### P2: Expected protocol states make successful traces appear failed

Aspire marked successful To-do completion, semantic move, exact create, exact move, and confirmed delete traces with `hasError: true`.

Two causes appear in the spans:

- The execution filter catches `InputRequiredException` and calls `CalendarTelemetry.Fail`, which sets `caldav.outcome=error` and `ActivityStatusCode.Error` in [`CalendarExecutionPolicy.cs`](../src/DotnetAgents.CalDav.Mcp/Hosting/CalendarExecutionPolicy.cs#L57) and [`CalendarTelemetry.cs`](../src/DotnetAgents.CalDav.Mcp/Hosting/CalendarTelemetry.cs#L97). MRTR review is expected control flow.
- .NET HTTP instrumentation marks expected 404 reads as failed child spans. Create checks destination absence; move and delete verify source absence after dispatch. The root operation reports `success` and `committed`, but Aspire derives `hasError` from the child.

One `todos.complete` trace also contained a recovered `response_ended` PROPFIND attempt. That child error carries useful retry evidence and should remain distinguishable from expected absence.

Recommended change:

- Record MRTR review as `caldav.outcome=input_required` without error status.
- Mark expected 404 absence probes as expected domain observations, or replace generic HTTP spans for these calls with custom safe spans that classify the status in context.
- Preserve recovered transport failures with a tag such as `caldav.recovered=true` so operators can separate retries from failed operations.

### P2: Bounded entity queries cannot evaluate date-only resources

A windowed `calendar_entities.query` over the mixed corpus returned `temporal_unresolved` after 2,361 ms and all 26 REPORTs. The unbounded form succeeded. `CalendarEntityQuery` has no evaluation-time-zone field in [`CalendarEntityQuery.cs`](../src/DotnetAgents.CalDav.Core/Models/CalendarEntityQuery.cs#L25), and the temporal matcher constructs its resolver without a zone in [`CalendarEntityTemporalMatcher.cs`](../src/DotnetAgents.CalDav.Core/Internal/Ical/CalendarEntityTemporalMatcher.cs#L35). The corpus contained 30 all-day Events. This run did not isolate one failing resource, so other unresolved floating or date values may also contribute.

Clients can use `calendar_occurrences.query` with `evaluationTimeZone` for occurrence windows, and `todos.query` exposes the same context. The full entity-query contract needs an explicit decision: add an evaluation time zone, define date-only overlap semantics, or reject the shape before fetching the corpus. A host-time-zone fallback would violate the explicit Temporal Evaluation Context invariant.

### P2: Multiget failure falls back to sequential GETs

The pinned Radicale profile used multiget for every query, so the observed fast path stayed bounded. Code inspection found a separate path: [`CalendarEntityQueryEngine.ReadBatchAsync`](../src/DotnetAgents.CalDav.Core/Services/CalendarEntityQueryEngine.cs#L146) falls back to [`ReadDirectlyAsync`](../src/DotnetAgents.CalDav.Core/Services/CalendarEntityQueryEngine.cs#L168) when multiget is unavailable or returns an unexpected count. That fallback performs one awaited GET per resource. This run did not disable multiget, so the fallback conclusion is code-derived rather than trace-observed.

Recommended change:

- Add `caldav.query.fetch_mode` and inspected-resource telemetry so operators can see `multiget` versus `direct_get_fallback`.
- Set a separate work budget for the fallback. Consider bounded concurrency or an explicit unsupported-capability result for large candidate sets.
- Keep the successful-multiget regression assertion at zero GETs.

## Hermes client boundary

Hermes discovered all 21 tools and used `openai/gpt-5.6-luna` for each model-driven run. It handled normal reads and writes, including a 604-GET semantic move.

Hermes 0.20.4 could not continue MCP elicitation. A valid exact-create review produced:

```text
MCP call failed: RuntimeError: Server returned InputRequiredResult; pass allow_input_required=True to receive it and retry call_tool(..., input_responses=..., request_state=result.request_state).
```

The adapter did not expose `requestState` to the model-facing tool. Adding `confirm` or `allow_input_required` to the strict server arguments produced `invalid_input`, as expected. Repeated delete retries then tripped Hermes's local circuit breaker even though `hermes mcp test` still connected to the server.

This is a Hermes adapter limitation, not a CalDAV MCP failure. A raw MCP 2026-07-28 stdio driver received `InputRequiredResult`, returned the confirmation response and protected request state, and completed exact create, replace, move, and delete. Trace `1cbc3614ecefa2b4404c601678f75a5b` captures the valid Hermes exact-create review; the direct continuation success appears in `d67423845f2a3c98bf58b3a29011c6ea`.

Until Hermes supports MRTR continuation, keep a raw-protocol acceptance driver for protected operations and report the client exception in Hermes-based validation.

## Reproduction anchors

The dedicated exact-move repro restricted scope to the 600-Event Calendar, created one disposable source, read its revision, reviewed and confirmed an exact move, queried Aspire, and removed the fixture. Trace `9759ed6b1d62dad542af35271762bcc3` recorded 3,649 ms, 1,206 GETs, 8 PROPFINDs, 4 REPORTs, and one MOVE.

The retained input shapes for the main differentials are:

```json
{
  "entityPageOne": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "pageSize": 1
  },
  "entityPageTwoHundred": {
    "scope": { "mode": "all" },
    "entityKinds": ["event", "todo"],
    "pageSize": 200
  },
  "occurrenceAwareTodos": {
    "scope": { "mode": "all" },
    "completionStates": ["open", "completed", "cancelled"],
    "from": { "kind": "utcDateTime", "value": "2026-07-01T00:00:00Z" },
    "to": { "kind": "utcDateTime", "value": "2026-12-31T00:00:00Z" },
    "evaluationTimeZone": "America/Sao_Paulo",
    "projection": ["summary", "status", "due", "priority", "categories", "recurrence"],
    "pageSize": 5
  }
}
```

The exact-write driver used one stdio process and MCP protocol `2026-07-28`: initialize, read the current revision, call the protected tool without continuation state, retain `requestState` and the confirmation form from `InputRequiredResult`, call the same tool with the accepted input response and protected state, then verify the destination and source. The report omits disposable credentials and Dashboard login tokens.

Aspire CLI commands used the temporary Dashboard login URL while it was alive:

```bash
aspire otel traces --dashboard-url '<temporary-login-url>' \
  --search 'resource:dotnet-agents-caldav-hermes-load-test' \
  --format Json --limit 300 --non-interactive --nologo

aspire otel logs --dashboard-url '<temporary-login-url>' \
  --search 'resource:dotnet-agents-caldav-hermes-load-test' \
  --format Json --limit 300 --non-interactive --nologo
```

The Dashboard URLs and trace-detail links stop working after removal of the temporary container. This tracked report retains the environment digests, corpus rules, input shapes, representative trace summaries, and code paths. It does not retain raw OTLP records, so a new trace-level audit requires rerunning the disposable fixture.

## Cleanup verification

The teardown removed:

- Hermes MCP entry `caldav-perf-branch` and the regenerable schema-cache file that retained the stale entry
- Containers `caldav-perf-radicale` and `caldav-perf-aspire`
- Docker volume `caldav-perf-data`
- Disposable Radicale configuration and credential files under `.tmp/caldav-load-test`
- Hermes usage JSON files, temporary resource captures, and the 770 KiB spillover result

Post-cleanup checks found no matching container or volume, no listener on ports 5232, 18888, or 4318, and no disposable credential directory. `hermes mcp list` showed only the pre-existing `caldav-local` entry. Docker retained the digest-pinned images in its shared image cache; teardown did not remove shared cache layers. Hermes retained its normal session and log audit history, which contains the temporary server name but not the disposable password.
