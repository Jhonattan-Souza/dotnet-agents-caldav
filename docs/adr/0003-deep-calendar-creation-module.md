# ADR 0003: Deep Calendar creation module

Status: Accepted

Date: 2026-08-20

## Context

Semantic Create, Exact Create, and conditional CalDAV dispatch currently split
creation policy across separate engines and Adapters. That shape duplicates
collision and reconciliation decisions, exposes a query-capable client to
creation, and represents reviewed destination absence as a resource revision
with the non-revision Entity Tag sentinel `"*"`.

## Decision

Concentrate Calendar Object Resource creation in an internal sealed
`CalendarCreationModule`. Its Interface has two entry points:
`ReviewExactAsync(ExactCreateIntent, CancellationToken)` and
`CreateAsync(CalendarCreationCommand, CancellationToken)`. The closed command
union represents semantic Event creation, semantic To-do creation, or a
currently reviewed Exact Create. One internal outcome model carries collision,
Mutation State, Execution Budget, dispatch, reconciliation, and fidelity truth;
`CalendarService` remains a thin Adapter to the existing caller models.

The Module depends on a narrow `ICalendarCreateTransport` port containing only
Calendar discovery, direct resource GET, and conditional resource PUT. The
production `CalDavClient` and a scripted test Adapter satisfy that Seam. The
port deliberately cannot enumerate Calendar Object Resources. Semantic and
Exact variations are closed private strategies inside the Implementation; no
extensible creation-policy Interface is exposed.

MCP remains responsible for MRTR state, credential binding, and confirmation.
Exact review returns a dedicated `ExactCreateReviewBinding` and an internally
constructed reviewed command rather than a Calendar Object Resource Revision
Reference with Entity Tag `"*"`. The reviewed command owns an immutable copy of
the authoritative bytes; Core verifies its digest and Calendar Entity identity
again immediately before PUT. A confirmed continuation performs a fresh review,
compares its binding with the protected state, and passes that reviewed command
directly to conditional PUT. The PUT protects the race after review, so no
second pre-dispatch destination GET is performed.

## Consequences

The Interface structurally guarantees constant work with respect to Calendar
size and gives Semantic Event, Semantic To-do, and Exact Create one shared path
for conditional dispatch and truthful readback. MCP concerns stay outside the
Core Module, callers cannot reorder preparation, dispatch, and reconciliation,
and future creation changes remain local. Adding a genuinely different create
policy requires revisiting the closed command union rather than preemptively
exposing a shallow extension Seam.
