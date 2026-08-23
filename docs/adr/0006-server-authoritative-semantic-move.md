# ADR 0006: Server-authoritative semantic Move module

Status: Accepted

Date: 2026-08-23

## Context

Semantic Move previously inspected every destination Calendar Object Resource
to infer UID conflicts before issuing MOVE. That scan made work proportional to
Calendar size, parsed unrelated private resources, could disclose scan-derived
failure details, and still could not close the race between inspection and the
mutation. Move policy was also spread across a broad query-capable client and
post-conflict inspection paths.

CalDAV already owns the atomic decision. A conforming MOVE can bind the source
with a strong `If-Match`, prohibit destination replacement with `Overwrite: F`,
and report `CALDAV:no-uid-conflict`. Dispatch uncertainty still requires
bilateral observation because a lost response cannot prove whether the server
committed the mutation.

## Decision

Concentrate semantic Move in the internal sealed `CalendarMoveModule`. Its
`ICalendarMoveTransport` port exposes only operation-scoped discovery with
precomputed default-selection truth, one authoritative source read, one
content-insensitive destination presence probe, one conditional MOVE dispatch,
and direct destination/source observations for reconciliation. The port cannot
enumerate or query Calendar members. If the underlying transport lacks the
headers-only presence capability, Move fails closed with
`unsupported_capability`; it never falls back to a content GET.

The module performs lexical validation, origin and Calendar Scope
authorization, destination selection and capability checks, source projection
and revision validation, one exact destination-href absence probe, one MOVE,
and bounded concurrent bilateral reconciliation. MOVE is never retried.
`DestinationConflict` is reserved for authoritative occupancy of the exact
destination href. `CALDAV:no-uid-conflict` and generic 409/412 rejection map to
the non-disclosing `conflict` outcome.

`Dispatched` and `PossiblyDispatched` remain distinct. A definite dispatch may
claim committed success or fidelity failure only from complete destination plus
absent source evidence; unavailable observations become
`committed_but_unverified`, and contradictory complete observations remain
`indeterminate`. A possible dispatch claims committed success only for a
faithful destination plus absent source. Absent destination plus an unchanged
strong source proves `not_committed`; every other cell remains
`indeterminate`/`unknown`. Reconciliation uses its own bounded token and is not
stopped by caller cancellation after dispatch may have occurred.

The capability contract is explicit and fail closed. The only enabled profile
is `radicale-3.7.8`, which denotes the repository's digest-pinned Radicale 3.7.8
runtime evidence. Omission or any other value leaves Semantic Move disabled;
generic DAV discovery is not treated as proof of atomic UID enforcement.

## Consequences

Semantic Move work is constant with respect to destination size and unrelated
resources are neither retrieved nor parsed. One operation-scoped discovery
result preserves Calendar Scope and configured-default selection truth without
reapplying policy in the module. Conditional Create remains unchanged, and the
Exact Move MRTR plan redesign and Exact-only scan cleanup remain owned by issue
`#115`.

Operation telemetry exports only closed dispatch, collision, reconciliation,
logical outcome, and Mutation State classifications. The export allowlist still
removes hrefs, UIDs, Entity Tags, headers, payloads, exception details, and
events. Supporting another server requires new pinned interoperability evidence
and an explicit profile addition rather than a compatibility scan.
