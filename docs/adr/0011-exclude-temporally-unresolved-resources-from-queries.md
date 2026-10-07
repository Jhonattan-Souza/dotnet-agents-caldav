# ADR 0011: Exclude Temporally Unresolved Resources from queries

Status: Accepted

Date: 2026-10-06

Supersedes the query part of ADR 0005's rule that unknown or conflicting resource-local zones fail the complete query atomically as `temporal_unresolved`.

A single Calendar Object Resource written by another client must not blind every temporal query over its Calendar. In production, one To-do rewritten by tasks.org through DAVx5 (ical4j 4.3.0) failed every To-do and Occurrence query on four Calendars. Its resource-local VTIMEZONE carried only the latest STANDARD observance.

Calendar Entity, Occurrence, and To-do queries therefore evaluate first every predicate that needs no instants, such as the Text Filter and the To-do Completion State of a non-recurring To-do. Per-Occurrence completion of a recurring To-do still needs instants. A Temporally Unresolved Resource that survives those predicates is excluded rather than guessed into or out of the window. The exclusion is frozen into the Query Result Snapshot and repeated on every page as a complete count plus a bounded sample of hrefs, so the caller can inspect the excluded resources through the exact surface or narrow the Calendar Scope. The field is absent when nothing was excluded.

Mutations keep atomic `temporal_unresolved`: when the target itself cannot be placed in time, refusing is the only truthful outcome. The authoritative content is never rewritten to repair it.

Considered options: failing atomically was rejected because one foreign resource made whole Calendars unqueryable. Including unresolved resources in the result with a marker was rejected because it presents unplaceable data as window members. Repairing the VTIMEZONE from tzdb on write was rejected because a resource-local VTIMEZONE belongs to its author.
