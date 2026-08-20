# Migrating from contract 0.2.0 to 0.2.1

Contract 0.2.1 adds the read-only `todos.query` tool. Existing callers may
continue using `calendar_entities.query` for full Calendar Object Resource
snapshots; its input and output contract is unchanged.

`todos.query` requires an explicit `scope` with `mode` `selected` or `all`.
Without an explicit `completionStates` list it returns only normalized `open`
To-dos. The compact result carries typed completion fields and a strong
revision target. For a recurring entity, `completionTarget.kind=occurrence_required`
means the client must query or otherwise choose a concrete occurrence first;
that occurrence result carries `completionTarget.kind=direct` and its
`recurrenceIdentity` for a safe follow-up mutation.

The default projection is `summary`, `due`, `priority`, and `categories`.
Clients may request the ten-field allowlist documented in the 0.2.1 catalog.
The result is bounded to 64 KiB and uses a query-bound non-snapshot cursor.

Completion mutations also use the normalized completion classifier in 0.2.1.
`todos.complete` treats an already-completed timestamp or `PERCENT-COMPLETE:100`
as a no-op, rejects `CANCELLED`, and rejects contradictory or otherwise
indeterminate completion evidence with the typed `completion_state_conflict`
outcome. Callers that previously inspected only `STATUS:COMPLETED` should
handle these additional outcomes.
