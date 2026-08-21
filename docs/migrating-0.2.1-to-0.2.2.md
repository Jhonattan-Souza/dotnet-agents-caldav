# Migrating from contract 0.2.1 to 0.2.2

Contract 0.2.2 changes Create collision handling for `events.create`,
`todos.create`, and opt-in `calendar_resources.exact_create`. Tool names and
input shapes are unchanged.

## Behavior changes

- Create no longer enumerates Calendar Object Resources before writing.
- A destination href collision returns `destination_conflict` with
  `mutationState: not_committed`.
- An Entity UID collision returns `conflict` with
  `mutationState: not_committed`.
- A generated UID may be replaced and retried after a definite conflict, with
  three conditional PUT attempts maximum.
- A caller-supplied UID and an Exact Create destination remain fixed and are
  attempted once.
- Create deadline failures include `limits.dimension: elapsed_time`.

Clients that grouped every collision under `conflict` should add
`destination_conflict` handling before upgrading. Both outcomes are expected
non-committed failures and are safe to present without a conflicting server
resource href.

Exact Create still requires MRTR. Its opaque `requestState` is not portable
across versions; discard pending 0.2.1 confirmations during an upgrade and
start a fresh review.

Rollback to 0.2.1 does not migrate or rewrite CalDAV data. It restores the
older exhaustive UID preflight behavior.
