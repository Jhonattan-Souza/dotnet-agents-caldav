# ADR 0002: Authoritative conditional Calendar Entity creation

Status: Accepted

Date: 2026-08-20

## Context

Collection-wide UID preflight makes creation proportional to every existing
Calendar Object Resource, can exhaust the pre-dispatch budget, and still cannot
replace the atomic collision decision made by the CalDAV server. A targeted
UID `text-match` REPORT is substring-based, may require further reads, and is
also subject to a race before dispatch.

## Decision

Semantic Create and Exact Create delegate authoritative href and Entity UID
collision detection to one conditional CalDAV PUT. `If-None-Match: *` protects
the destination href, while `CALDAV:no-uid-conflict` protects Entity UID
uniqueness within the destination Calendar across Entity Kinds. Semantic
Create performs no collection-wide collision preflight. Exact Create retains
only its constant-work direct destination read during MRTR review and
revalidation; it does not enumerate other resources.

Destination href collisions return `destination_conflict`; Entity UID and
unclassified conflicts return `conflict`. Rejected dispatches are
`not_committed`. Generated identities may retry with a fresh identity within
the existing three-attempt bound, while caller-supplied identities remain
unchanged. Conflict responses never disclose a server-supplied conflicting
href.

## Consequences

Create request count no longer grows with Calendar size, and unrelated opaque,
oversized, or weakly tagged resources do not block a valid create. Writable
compatibility relies on the CalDAV server enforcing its required UID collision
precondition; the client does not compensate for a non-conforming server with
a non-atomic exhaustive scan.
