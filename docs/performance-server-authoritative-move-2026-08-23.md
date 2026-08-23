# Server-authoritative Semantic Move evidence

Date: 2026-08-23

Baseline revision: `4df75347477ca6dae463d60b938c7d28ab9b6ea6`

Changed revision: `932cf9f`

## Claim

Semantic Move no longer searches a destination calendar for a matching UID. The operation uses a narrow transport that can discover the already-scoped calendars, read the authoritative source, probe only the exact destination href without consuming its content, dispatch one conditional MOVE, and observe the source and destination directly for reconciliation.

This is structural evidence, not a wall-clock service-level claim. The deterministic witness fixes the same source, absent destination href, and successful Move while varying unrelated destination resources across 1, 50, and 600 items. The server profile is the digest-pinned Radicale 3.7.8 profile used by the repository gates.

## Before and after

| Destination resources | Baseline resource observations | Changed resource observations | Baseline REPORTs | Changed REPORTs | Changed unrelated reads | Changed MOVEs |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 5 | 4 | 2 | 0 | 0 | 1 |
| 50 | 54 | 4 | 2 | 0 | 0 | 1 |
| 600 | 604 | 4 | 2 | 0 | 0 | 1 |

The baseline counts are derived directly from `CalendarResourceMoveEngine` at the baseline revision: one source GET + one exact-destination preflight GET + `N` candidate GETs after two kind-specific UID-query REPORTs + two destination/source reconciliation GETs = `N + 4` resource observations. The fixed cardinalities therefore produce exactly 5, 54, and 604 observations. The changed four resource observations are one source GET, one headers-only exact-destination presence probe, and the two independent destination/source reconciliation observations. Calendar discovery remains one acquisition for every size. No calendar-query REPORT, calendar-multiget REPORT, or unrelated resource GET occurs. The dispatch is exactly one MOVE and is never retried.

`CalendarMoveModuleTests.DestinationCardinalityDoesNotChangeMoveWork` records the exact changed-revision trace for 1, 50, and 600 resources:

```text
discover
read-source
probe-destination
dispatch
observe-destination + observe-source
```

The last two reads start concurrently under the module's bounded reconciliation token, so their completion order is not part of the contract.

## Protocol and outcome evidence

- `CalendarResourceMoveProtocolTests` proves a strong `If-Match`, `Overwrite: F`, no request body, bounded method-preserving redirects, one dispatch, and the server-authoritative `CALDAV:no-uid-conflict` classification.
- `CalendarMoveModuleTests` covers the complete `Dispatched` and `PossiblyDispatched` bilateral reconciliation matrices, pre-dispatch cancellation, post-dispatch cancellation isolation, capability fail-closed behavior, and constant work.
- `RadicaleConformanceHarnessTests` exercises the digest-pinned Radicale profile for occupied exact hrefs, same-kind and cross-kind UID conflicts, stale revisions, and the race between the destination probe and MOVE.
- `OpenTelemetryStdioIntegrationTests` runs the real stdio MCP and OTLP path for success, definite UID rejection, `PossiblyDispatched`, expected-absence 404 observations, and caller cancellation. It verifies the closed Move dimensions and rejects hrefs, UIDs, ETags, headers, content, credentials, and events from exported telemetry.

Every integration case uses unique resources in a disposable Radicale container or bounded loopback listener; cleanup is explicit and does not leave shared calendar state.

## Scope boundary

This change removes scan work only from Semantic Move. Exact Move's MRTR planning and Exact-only scan cleanup remain assigned to issue #115. The shared dispatch truth introduced here is intentionally reused rather than duplicated.

The architectural rationale is recorded in [ADR 0004](adr/0004-server-authoritative-semantic-move.md), and the canonical requirement mapping is in [requirement-to-evidence.md](requirement-to-evidence.md).
