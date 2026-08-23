# ADR 0005: Configured Temporal Evaluation Context

Status: Accepted

Date: 2026-08-23

Bounded Calendar Entity queries resolve one explicit IANA Temporal Evaluation Context before any Calendar discovery or CalDAV request. A valid caller `evaluationTimeZone` wins over the startup-validated `CALDAV_EVALUATION_TIME_ZONE`; absence of both or an invalid caller value is `invalid_input`. An unbounded Calendar Entity query does not evaluate temporal filters and therefore rejects a caller override rather than accepting an input with no observable effect.

The context is query-level state, not Calendar Object Resource content. It is frozen into each applicable Query Result Snapshot, repeated on every page as `timeZone` plus caller/configuration source, and authenticated by the Continuation Cursor configuration binding. Changing relevant configuration makes a cursor generically invalid without revealing the changed value. Telemetry never records the zone, query arguments, cursor, or resource temporal content.

UTC and named-zone values retain their source authority. Floating and date-only values evaluate in the explicit context while their projected form remains unchanged. Positive spans use half-open overlap, date-only Events without an explicit end occupy one civil day including 23-hour and 25-hour transition days, and date-only To-do boundaries retain point/span semantics. Unknown or conflicting resource-local zones fail the complete query atomically as `temporal_unresolved`.

This forbids Calendar, CalDAV server, operating-system, process, host, locale, and location inference. It accepts the operational cost of explicit configuration because deterministic interpretation and pre-I/O failure are more valuable than environment-dependent convenience. The same typed policy is the required seam for later Occurrence and To-do query-module cutovers.
