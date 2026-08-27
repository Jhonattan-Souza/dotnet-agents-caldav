---
name: caldav-calendars
description: Use dotnet-agents-caldav MCP tools to inspect or manage CalDAV Calendars, Events, To-dos, recurring Occurrences, and exact Calendar Object Resources.
---

# CalDAV Calendars

Translate the user's Calendar intent into the fewest safe `dotnet-agents-caldav` MCP calls. The live tool catalog, descriptions, input schemas, and structured outcomes are authoritative; use this skill for routing and sequencing, not as a cached schema. If a referenced tool is absent, report the catalog limitation.

## Resolve intent once

Before calling a tool, determine the Entity Kind, Calendar Scope, time window, and whether the request is a read, a proposal, or a mutation. Preserve only facts the user supplied or the configured defaults establish.

- Resolve relative dates from the current date and the user's known time zone. For bounded Entity queries, Occurrence queries, or To-do queries with temporal filters, pass the known user, harness, or validated configured IANA zone explicitly. Omit `evaluationTimeZone` from an unbounded Entity query, where it is unused. Ask for a zone only when it is genuinely unknown or the server returns `temporal_unresolved`.
- Never send a one-sided time filter. For example, “due this week” maps to `dueFrom` at the requested week's start and `dueTo` at its exclusive end. “Due through Friday” also needs a lower bound; use the current evaluation instant when that matches the intent, or clarify whether older overdue items belong in the result.
- Use the live schema's `default` scope or destination directly when the user has not selected another Calendar. A simple request such as an Event tomorrow at 18:00 can be one `events.create` call; omit an unspecified end or duration rather than inventing one.
- Ask one concise clarification when different answers would materially change stored data: which of several matching resources, one recurring Occurrence versus the series, or what “organize next week” should change. Gather related missing choices together. For an open-ended organization request, read the relevant week once, propose a concrete plan, then mutate only the plan the user accepts.
- Before `calendars.create`, resolve a user-authorized display name and component set: Events, To-dos, or both. Generic nouns such as “agenda”, “calendário”, “lista”, or “lista de tarefas” express purpose, not an authorized display name; ask for the name before any tool call and never invent one. Infer the component set only from explicit intent, and ask once for any missing field before the first write. When both are known, create the Calendar and use its returned canonical href for the requested resource without intervening discovery.
- Before a broad or destructive request, resolve Calendar scope, Entity Kinds, temporal cutoff, and selection criteria. Query one bounded set, present its finite targets, and proceed only through their individual confirmation flows. Do not turn an open-ended result or pagination stream into an automatic deletion loop.
- Before any exact write, require the user's message to supply the complete caller-authored Calendar Object Resource body. If it does not, ask for that body before every tool call. Do not read the existing resource and turn it into supposedly caller-authored replacement content.

Intent is resolved when one call plan covers the whole request and every remaining uncertainty can safely be left absent or to a configured default.

## Discover only when discovery changes the call

Call `calendars.list` once for a stable selection intent, and only when the Calendar's name, canonical href, Event/To-do capability, or default identity is genuinely needed and unknown. Reuse that result while the intent and scope remain unchanged; list again only after the user changes them or a result proves the selection stale.

Skip discovery when:

- the request uses `all` scope;
- the live schema offers a configured default scope or destination;
- a selected Calendar name or absolute href is already known;
- an absolute resource href can go directly to `calendar_resources.get`; or
- a preceding result already returned the Calendar identity.

Use a known Calendar name directly where the live schema accepts selection by name. If selection returns `ambiguous` with authorized candidates, ask the user to choose from those candidates rather than rediscovering them. Treat Calendar href as identity and display name as presentation.

Use `calendars.create` only for a new Calendar collection. Use `calendars.delete` only when the user intends to remove the whole collection and every resource in it; resolve its exact href first and continue the tool's own finite confirmation review. Unless the user asks for a separate preview, do not enumerate every contained resource before that review or list again after an uncommitted attempt. Event or To-do deletion is `calendar_resources.delete`.

Discovery is complete when the next operation has a schema-valid scope or destination without a speculative preflight.

## Choose the narrow read

- `calendar_occurrences.query`: an agenda or schedule in a bounded half-open time window, including expanded recurring instances.
- `todos.query`: compact To-do lists, completion or due filters, and a projection limited to fields needed for the answer. Keep a To-do-list request on this path even when a result is recurring; switch to Occurrences only when the user asks for instances on a schedule. Its completion target can bind a later completion.
- `calendar_entities.query`: persisted Event or To-do resource snapshots, especially for inspection, search, or revision-bound edits of the containing resource.
- `calendar_resources.get`: one authoritative snapshot at an already confirmed absolute resource href.

Start one query with its complete scope, filters, requested window, and optional page size. Temporal windows are complete pairs: supply both `from` and `to`, and both `dueFrom` and `dueTo`; never send only one endpoint. A bounded window must be non-empty and at most 366 days. Do not invent distant lower or upper bounds for an unbounded search; omit bounds the user did not request when the live schema permits it. For “next N”, use one reasonable horizon of at most 366 days and widen only if the result proves it insufficient. If more requested results are needed, Continue with only `cursor` and optional `pageSize`; copy the opaque cursor byte-for-byte, because changing even one character invalidates it. Repeating Start arguments creates an invalid continuation. Consume one snapshot traversal instead of rerunning the query. On `cursor_expired`, start a fresh query and disclose that the results were reevaluated.

Use only fields the answer needs. Do not add discovery, another query family, or an exact-resource read merely to enrich an already sufficient result. If a call fails, inspect its arguments and typed outcome before doing anything else. Make at most one materially corrected retry; never fan the same failing operation out across Calendars, repeat it unchanged, or describe a local argument failure as server unavailability.

For a title-only search across Calendars, make one unbounded `calendar_entities.query` with the requested Entity Kind and filter the returned snapshots locally. Do not guess a day, expand Occurrences, or query each Calendar separately.

A read is complete when the requested range is covered, the returned Temporal Evaluation Context is honored, and no extra projection or page was fetched.

## Bind every existing-resource write

Create new resources directly with `events.create` or `todos.create`; creation needs no preliminary read. To modify, complete, move, or delete an existing resource, first obtain its fresh strong revision through the narrowest query or `calendar_resources.get`, then pass the returned reference exactly as the live schema names it:

- `events.patch` or `todos.patch` for field changes and rescheduling;
- `todos.complete` for completion;
- `calendar_resources.move` to move the whole resource to another Calendar;
- `calendar_resources.delete` to delete the whole resource.

Entity and Occurrence snapshots expose `entityRevision`; compact `todos.query` results expose `completionTarget.entityRevision` when the target is writable. Use that returned reference as `snapshot` for patches, completion, and Occurrence mutations, and as `revision` for semantic move and delete. Summary, position, Calendar name, and time are not mutation identity. After a conflict, use an authorized complete `currentSnapshot` from the outcome or query again, then obtain renewed intent before another write.

For recurrence, preserve two identities:

- the resource revision identifies the containing Calendar Object Resource;
- the exact returned `recurrenceIdentity` identifies one Occurrence.

Never synthesize recurrence identity from a displayed time. Read Occurrences to distinguish one instance from `this-and-future` or `entire-set`. When a compact To-do completion target is `occurrence_required`, query the Occurrence; an `unavailable` target is not writable through `todos.complete`. Use the live patch target for ordinary changes and the `calendar_occurrences.*` tools for explicit add, exclude, cancellation, or restoration semantics.

Each call writes at most one Calendar Object Resource. An existing-resource write is ready only when the user-selected target, fresh revision, recurrence scope when applicable, and destination when applicable all refer to the same reviewed intent.

Accept a committed semantic mutation's structured outcome as completion. Do not add a verification read unless the outcome is indeterminate or committed-but-unverified and the user needs reconciliation.

## Continue confirmations faithfully

A protected mutation may return MCP `input_required`. Present the returned review, obtain the requested input, and continue the same tool with its opaque `requestState` and `inputResponses`. Keep `requestState` unchanged, single-use, and paired with the original arguments and revision.

Expiry, mismatch, decline, a changed revision, or a continuation failure ends that exchange without a new write. If the active harness cannot continue MCP Multi Round-Trip Requests, report that limitation and leave the mutation uncommitted. Do not call the protected mutation or a verification query again to work around a missing continuation. A client hint such as `allow_input_required` is not a CalDAV tool argument unless the live input schema explicitly includes it.

Treat `structuredContent` as authoritative. Report `outcome`, `mutationState`, `no_change`, `confirmation_declined`, and typed failures accurately. A transport-level success does not prove a mutation committed, and an indeterminate or committed-but-unverified result needs explicit disclosure rather than an automatic retry.

## Reserve exact tools for exact resources

Semantic tools are the normal path. Use `calendar_resources.exact_get`, `calendar_resources.exact_create`, `calendar_resources.exact_replace`, or `calendar_resources.exact_move` only when the exact catalog is exposed and the user explicitly needs byte-preserving access. For an exact read, follow its returned MCP resource link once; a saved local path is the completed read, not another MCP resource to fetch. Caller-authored complete Calendar Object Resource content is a precondition for every exact create or replace: obtain it before the first exact write call, and never fabricate it from a partial change request or retry with invented content. Exact writes also require explicit absolute hrefs, applicable strong revisions, and MRTR confirmation. Stored URI values remain inert; these tools do not fetch attachments or send invitations.

## Completion criteria

Finish only when:

- every requested read range or mutation is accounted for;
- discovery and query calls were not repeated without new information;
- each existing-resource write used the latest returned revision and exact recurrence identity when applicable;
- every MRTR exchange reached a confirmed terminal outcome or a clearly reported harness limitation; and
- the response distinguishes committed, unchanged, declined, failed, and indeterminate outcomes.
