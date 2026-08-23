# ADR 0004: Deep query module with immutable result snapshots

Status: Accepted

Date: 2026-08-23

Calendar Entity query orchestration belongs to one typed `ICalendarQueryModule`, not to the MCP adapter or a compatibility facade over `ICalendarService`. A Start uses a narrow CalDAV query transport to complete discovery, retrieval, evaluation, ordering, projection, and first-page admission. A Continue has a separate dependency graph containing only cursor authentication, snapshot lookup, the clock, and page admission, making repeat CalDAV or semantic work structurally unavailable.

The module retains an immutable, process-local Query Result Snapshot for ten minutes from its first page. It stores only the already projected encoded items and bounded diagnostics, never authoritative iCalendar bytes, and protects each cursor with the tool, snapshot identity, next position, expiry, credentials, and relevant configuration context. Exact cumulative byte accounting admits a page before one final materialization; the MCP adapter consumes the module-built presentation mechanically.

Authoritative retrieval is also owned by the module rather than exposed through `ICalendarClient`. `calendar-multiget` is planned in batches of 50 and remains the normal path. Only a verified 405, 501, or applicable structured DAV precondition activates the internal Direct GET Compatibility Mode. That mode is a bounded interoperability concession: it admits at most 200 resources and 32 MiB across all attempts, preserves the 4 MiB resource and three-attempt limits, and schedules canonical waves of at most four reads per origin. A missing or inconsistent multiget response is a protocol failure, not capability evidence. Explicit resource 404 is the only disappearance that may be omitted from an otherwise complete result.

Verified multiget unavailability is retained in one bounded, authorization- and configuration-scoped Capability State. Rediscovery or context change advances its generation so an older in-flight observation cannot repopulate stale state. The query transport returns a closed `Resources | VerifiedUnavailable` result; it exposes neither a caller-selectable mode nor a fallback policy seam. Direct GET redirects must preserve the authorized Calendar and exact resource identity.

This deliberately trades bounded memory for stable replay and zero-work continuation. Atomic reservation prevents partial publication, admitted snapshots are not evicted before expiry, aggregate exhaustion returns `busy`, and restart or authentication-context changes invalidate cursors without disclosing why. Retrieval failures likewise publish no partial items; concurrent direct-read failures are selected by canonical href after the current wave finishes. The store remains a concrete internal implementation because no second real store exists; the true external seam is the typed CalDAV query transport, implemented by both production CalDAV and deterministic scripted adapters.
