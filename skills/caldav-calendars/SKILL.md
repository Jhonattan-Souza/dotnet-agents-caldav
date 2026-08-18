---
name: caldav-calendars
description: >
  Manage CalDAV Calendars, Events, and To-dos through the unified 0.2 MCP tools. Use for
  calendar discovery, event or task queries, creation, revision-bound changes, recurrence,
  completion, moves, deletion, and exact Calendar Object Resource operations.
---

# CalDAV Calendars

Use the semantic catalog for normal work. It preserves Calendar Object Resources while exposing typed Event and To-do fields. Reach for exact tools only when the user explicitly asks to read, create, replace, or move a complete resource and the exact catalog is enabled.

## 1. Establish Calendar Scope

Call `calendars.list` before the first Calendar operation. Retain canonical Calendar hrefs, display names, Event/To-do support, and the independent defaults for the rest of the conversation.

- A canonical Calendar href is identity. A Calendar Name is display metadata.
- Omitted selection uses the default for the requested Entity Kind only.
- Use selected scope for one named or href-addressed Calendar. Use all scope only when the user explicitly asks across Calendars.
- Present authorized candidates and ask when a name is ambiguous. An explicit failed selection never falls back.

This step is complete when the intended scope resolves to the requested Entity Kind without guessing.

## 2. Choose the Read Model

- Use `calendar_entities.query` for persisted Event or To-do snapshots. Follow `nextCursor` until the requested bounded result is complete.
- Use `calendar_occurrences.query` for derived recurrence instances in a non-empty half-open UTC window. Supply an IANA evaluation time zone when floating or date-only values require one.
- Use `calendar_resources.get` for the authoritative semantic-or-opaque snapshot of one confirmed absolute resource href.

An Occurrence is read-only. Any mutation targets its containing resource revision and, when applicable, its original Recurrence Identity.

This step is complete when the chosen tool returns the requested bounded snapshots or Occurrences, or a typed failure that the user can act on.

## 3. Bind Every Existing-Resource Mutation

Carry the exact `snapshot.entityRevision` returned by the latest query or direct read:

```json
{
  "href": "https://cal.example/calendars/user/work/item.ics",
  "entityUid": "uid-123",
  "entityKind": "todo",
  "entityTag": "\"revision-7\""
}
```

Use that whole reference with `events.patch`, `todos.patch`, `todos.complete`, Occurrence mutation tools, `calendar_resources.move`, or `calendar_resources.delete`. Summary, Calendar Name, current start time, and result position are never mutation identities. On `conflict`, show the authorized current snapshot and ask the user to review it; do not merge or retry the write automatically.

This step is complete when every proposed existing-resource mutation is bound to one freshly reviewed href, UID, Entity Kind, and strong Entity Tag.

## 4. Apply One Explicit Intent

- Create one Event with `events.create` or one To-do with `todos.create`. Choose the default or one selected Calendar.
- Patch scalar fields with explicit `set` or `clear`. Patch repeated fields with `addRemove`; use `replaceAll` only when replacing the entire collection is the user's stated intent.
- Complete a non-recurring To-do, or one original recurring identity, with `todos.complete`. The server records the completion instant.
- Add or change one recurrence identity with `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, or `calendar_occurrences.restore_cancellation`. Never infer one-occurrence, this-and-future, or entire-set scope from vague wording.
- Move or delete only the reviewed revision. One call mutates at most one Calendar Object Resource.

Preserve structured outcomes in the response. `no_change` and declined confirmation are successful non-writes. Expected failures use `isError: true` with a typed `code`, `phase`, and, for mutations, `mutationState`.

This step is complete when the result matches one explicit user intent and its structured outcome is reported without implying a write that was not verified.

## 5. Complete Multi Round-Trip Requests (MRTR)

Delete, exact writes, `replaceAll`, recurrence-definition changes, this-and-future, and entire-set mutations may return MCP `input_required`. Present the server's preview without weakening it, collect the requested confirmation, and continue the same call with its opaque `requestState` and `inputResponses`.

Treat expiry, mismatch, changed arguments, changed revision, or decline as a completed non-write. Never manufacture, edit, log, or reuse `requestState`.

This step is complete when the same MRTR exchange either verifies the requested mutation or ends with an explicit, accurately reported non-write.

## Exact Operations

When `CALDAV_EXPOSE_EXACT_TOOLS=true`, the separate exact catalog contains `calendar_resources.exact_get`, `calendar_resources.exact_create`, `calendar_resources.exact_replace`, and `calendar_resources.exact_move`. Exact reads return a protected MCP resource link. Exact writes require complete caller-authored content, explicit absolute hrefs, revision checks where applicable, and MRTR confirmation. URI values and scheduling data remain inert storage; the server does not fetch content or send invitations.

For an upgrade from the removed 0.1.x contract, read [Migrating from 0.1.x to 0.2.0](../../docs/migrating-0.1.x-to-0.2.0.md) before using the new catalog.
