# ADR 0004: Deep query module with immutable result snapshots

Status: Accepted

Date: 2026-08-23

Calendar Entity query orchestration belongs to one typed `ICalendarQueryModule`, not to the MCP adapter or a compatibility facade over `ICalendarService`. A Start uses a narrow CalDAV query transport to complete discovery, retrieval, evaluation, ordering, projection, and first-page admission. A Continue has a separate dependency graph containing only cursor authentication, snapshot lookup, the clock, and page admission, making repeat CalDAV or semantic work structurally unavailable.

The module retains an immutable, process-local Query Result Snapshot for ten minutes from its first page. It stores only the already projected encoded items and bounded diagnostics, never authoritative iCalendar bytes, and protects each cursor with the tool, snapshot identity, next position, expiry, credentials, and relevant configuration context. Exact cumulative byte accounting admits a page before one final materialization; the MCP adapter consumes the module-built presentation mechanically.

This deliberately trades bounded memory for stable replay and zero-work continuation. Atomic reservation prevents partial publication, admitted snapshots are not evicted before expiry, aggregate exhaustion returns `busy`, and restart or authentication-context changes invalidate cursors without disclosing why. The store remains a concrete internal implementation because no second real store exists; the true external seam is the typed CalDAV query transport, implemented by both production CalDAV and deterministic scripted adapters.
