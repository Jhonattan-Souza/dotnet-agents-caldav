---
name: caldav-calendars
description: Use the installed dotnet-agents-caldav MCP tools to inspect or manage CalDAV Calendars, Events, To-dos, recurring Occurrences, and exact Calendar Object Resources.
---

# CalDAV Calendars

Use the installed `dotnet-agents-caldav` MCP tools directly. Their current names, availability, descriptions, and input schemas are authoritative; this skill supplies intent routing, identity rules, and safe call sequencing. If a named tool is absent, report that catalog or client limitation instead of approximating the operation through unrelated tools.

## Resolve Calendar Scope

Call `calendars.list` when the intended Calendar, its canonical href, its Event or To-do capability, or its default status is unknown. Retain the returned canonical hrefs and independent Event and To-do defaults while completing the request.

- Treat a Calendar href as identity and its name as display metadata.
- Use `selected` for one reviewed Calendar and `all` only for an explicitly cross-Calendar request.
- Use `default` only when the live tool schema offers it and the user has not selected a Calendar.
- When a name matches multiple Calendars, present the authorized candidates and ask the user to choose. A failed explicit selection remains a failure.

Scope is resolved when every call has one schema-valid scope that supports the requested Entity Kind without guessing.

## Route Reads

- `calendar_entities.query`: persisted Event or To-do resource snapshots. A Start uses `selected` or `all` scope plus `entityKinds`; an optional UTC `from`/`to` pair makes it bounded.
- `calendar_occurrences.query`: derived Event and To-do Occurrences in a non-empty half-open UTC `from`/`to` window. Occurrences are read-only views of a containing resource revision.
- `todos.query`: compact normalized To-do results, completion-state and due-time filters, and an allowlisted projection. A Start uses explicit `selected` or `all` scope.
- `calendar_resources.get`: one authoritative semantic-or-opaque snapshot at a confirmed absolute resource href.

The three query tools use immutable Query Result Snapshots:

1. Start with the complete scope, filters, window, Temporal Evaluation Context, and optional `pageSize` accepted by the live schema.
2. While `pagination.nextCursor` is non-null and more requested results are needed, Continue with only that `cursor` and an optional `pageSize`. Do not repeat or change Start arguments.
3. A Continue performs no CalDAV retrieval or semantic reevaluation. Its cursor is process-local and expires after ten minutes; on `cursor_expired`, run a new Start and disclose that the result is a fresh snapshot.

For a bounded `calendar_entities.query` Start and every `calendar_occurrences.query` or `todos.query` Start, use the user's known IANA zone as `evaluationTimeZone` or rely on the server's validated configured zone. If neither is available, ask for an IANA zone after the typed failure. An unbounded Entity Start does not accept an unused caller override. Preserve the returned `temporalEvaluationContext` when explaining time-sensitive results.

Read routing is complete when the requested bounded results have been obtained from one snapshot traversal, or a typed failure has been reported with the missing decision identified.

## Bind Writes to Fresh Revisions

Immediately before changing an existing resource, query it or call `calendar_resources.get` and let the user disambiguate candidates. Entity and Occurrence reads expose the complete strong reference as `snapshot.entityRevision`; compact `todos.query` results expose it as `completionTarget.entityRevision` when that target is available:

```json
{
  "href": "https://cal.example/calendars/user/work/item.ics",
  "entityUid": "uid-123",
  "entityKind": "todo",
  "entityTag": "\"revision-7\""
}
```

Pass that exact object under the argument name required by the live schema: `snapshot` for patches, completion, and Occurrence mutations; `revision` for semantic move and delete. Summary, Calendar name, times, and result position are presentation data, not mutation identity.

- Create one typed resource with `events.create` or `todos.create` in a default or selected destination accepted by the schema.
- Change fields with `events.patch` or `todos.patch`. Choose `master`, `one-occurrence`, `this-and-future`, or `entire-set` explicitly; use `replaceAll` only for an intentional whole-collection replacement.
- Complete a To-do with `todos.complete`, adding the exact original `recurrenceIdentity` from the read or direct completion target when completion targets one recurring Occurrence. Resolve `occurrence_required` with an Occurrence query; an `unavailable` target is not writable through this shortcut.
- Change recurrence membership or cancellation with `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, or `calendar_occurrences.restore_cancellation`, using the exact recurrence identity returned by a read.
- Move one reviewed semantic resource with `calendar_resources.move`; the server uses a conditional, server-authoritative MOVE and bounded reconciliation under its verified interoperability profile.
- Delete one reviewed resource with `calendar_resources.delete`.

One call changes at most one Calendar Object Resource. On `conflict`, present the authorized current snapshot or typed conflict and ask the user to review it; a new write requires a fresh revision and intent. Never merge or retry an ambiguous write automatically.

A write is complete only when its structured outcome establishes the result. Report `no_change`, declined confirmation, `mutationState`, and typed failures without implying a committed mutation.

## Continue MCP Multi Round-Trips

High-impact operations can return MCP `input_required`, including delete, complete-resource writes, whole-collection replacements, and broad recurrence changes. Present the server's review as returned, obtain the requested confirmation, and continue the same tool with its opaque `requestState` and `inputResponses`.

Keep `requestState` opaque and single-use. Expiry, mismatch, changed arguments, changed revision, or decline completes the exchange without a write. If the MCP client cannot continue Multi Round-Trip Requests, report that client limitation and leave the operation uncommitted.

## Use Exact Resources Deliberately

Use the semantic tools for normal Calendar work. When the user explicitly needs byte-preserving access or supplies a complete Calendar Object Resource, use the opt-in exact catalog if it is exposed:

- `calendar_resources.exact_get` returns a protected MCP resource link for one confirmed absolute href.
- `calendar_resources.exact_create`, `calendar_resources.exact_replace`, and `calendar_resources.exact_move` require complete caller-authored content, explicit absolute hrefs, applicable strong revisions, and MCP Multi Round-Trip confirmation.

Exact content remains inert storage: URI values are not fetched and scheduling data does not send invitations.
