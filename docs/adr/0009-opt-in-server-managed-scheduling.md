# ADR 0009: Opt-in server-managed scheduling

Status: Accepted (first slice of plan item 2.9)

Date: 2026-09-24

Numbering: 0008 is reserved by the pending local text-search ADR. If another
ADR merges first as 0009, this record is renumbered without changing its
decision.

## Context

RFC 6638 servers that advertise the `calendar-auto-schedule` compliance class
act on ORGANIZER and ATTENDEE data themselves. Storing, changing or deleting a
scheduling object resource can send iTIP invitations, updates, replies or
cancellations, and deleting a collection can cancel every meeting in it. The
client does not submit those messages and cannot observe their delivery.

The server has always been storage-only. Any write whose prior or proposed
bytes contain ORGANIZER or ATTENDEE (a byte-level scan that also finds them in
an email VALARM) and every `calendars.delete` require fresh positive OPTIONS
evidence that automatic scheduling is absent. A missing DAV header, a
non-2xx or redirected OPTIONS, a malformed compliance list or a transport
failure blocks with `unsupported_capability` before the write. Many hosted
servers advertise `calendar-auto-schedule`, so on them no meeting could be
created, edited or deleted, and no Calendar could be deleted.

The [RFC coverage plan](../rfc-coverage-plan-2026-09-05.md) keeps full
scheduling (inbox/outbox processing, SCHEDULE-STATUS, scheduling free/busy)
out of scope. This ADR decides the scheduling model and delivers only its first
slice: an explicit opt-in that lets the server schedule, with honest
disclosure.

## Decision

### Scheduling Mode

`CALDAV_SCHEDULING_MODE` (`CalDavOptions.SchedulingMode`) selects one closed
value:

- `storage_only` is the default when the variable is unset or empty. Behavior,
  results and telemetry are byte-identical to the previous release.
- `server_managed` is an explicit opt-in. The operator accepts that the CalDAV
  server, not this MCP server, may contact participants.

Any other value, including case or whitespace variants, fails startup
validation. The error names the variable and both values without echoing the
rejected input.

### Safety-lock decision

The lock still runs only for participation-bearing writes and for collection
deletion, and it still obtains fresh OPTIONS evidence at the target Calendar
immediately before dispatch:

| Evidence at the target Calendar | `storage_only` | `server_managed` |
| --- | --- | --- |
| Well-formed DAV list without `calendar-auto-schedule` | allowed | allowed, `none` |
| Well-formed DAV list with `calendar-auto-schedule` | blocked | allowed, `possible` |
| Unknown (no or malformed header, non-2xx, href mismatch, transport failure) | blocked | blocked |

Unknown evidence blocks in both modes. Positive evidence is still required,
because only that evidence lets the outcome disclose accurately whether the
server was expected to schedule. Writes without participation data skip the
lock in both modes, because RFC 6638 implicit scheduling applies only to
scheduling object resources.

The lock governs `events.create`, `events.patch`, `todos.create`,
`todos.patch`, `todos.complete`, the five `calendar_occurrences.*` mutations,
`calendar_resources.delete`, `calendars.delete`,
`calendar_resources.exact_create` and `calendar_resources.exact_replace`.
`calendars.create` and `calendars.patch` do not touch scheduling object
resources.

Native MOVE (`calendar_resources.move`, `calendar_resources.exact_move`) is not
governed. The RFC coverage plan records MOVE as scheduling-neutral, but this
decision does not rest on that citation alone. It rests on facts that can be
checked:

- Native MOVE runs only under a verified Interoperability Profile.
  `CalendarMoveAuthorization` rejects every other configuration with
  `unsupported_capability` before any request.
- The only profile verified on this branch, Radicale 3.7.8, does not
  advertise `calendar-auto-schedule`, so MOVE there cannot trigger server
  scheduling.
- A separate change, PR #189, which may merge before or after this one, adds
  the verified `nextcloud-34.0.3` profile. Nextcloud does advertise
  `calendar-auto-schedule`. Its dated record
  (`docs/move-interoperability-profiles-2026-09-24.md` on that branch)
  observed that MOVE of an organizer Event with a local attendee changed no
  attendee resource and made no iMIP attempt.

Each newly verified profile that advertises automatic scheduling needs the
same evidence before promotion, as listed in the roadmap.

### What remains blocked or absent

- Every write and collection deletion that needs the lock when scheduling
  evidence is unknown, in both modes.
- Every participation write and collection deletion on an advertising server
  in `storage_only`.
- This server sends no iTIP message and does not use the scheduling outbox.
  It does not read the inbox or submit free/busy POSTs.
- It does not interpret SCHEDULE-AGENT, SCHEDULE-STATUS or Schedule-Tag. It
  never adds, removes or rewrites participation data or scheduling
  parameters.

### Typed disclosure

Scheduling-governed mutation outcomes gain an optional closed property
`schedulingSideEffects` with values `none` and `possible`. It is added to
`snapshotMutationSuccess`, `deleteMutationSuccess`,
`calendarCollectionDeleteSuccess`, `mutationErrorOutcome` and
`exactMutationErrorOutcome`. It is emitted only when all of these hold:

1. The mode is `server_managed`.
2. The tool is scheduling-governed.
3. The result's `mutationState` is `committed` or `unknown`. A write that
   committed or may have committed could have reached the scheduling engine.
   `not_attempted`, `not_committed`, `no_change` and `confirmation_declined`
   carry no disclosure because nothing reached the server.

The value is `possible` when this call's lock admitted a write because the
server advertised `calendar-auto-schedule`. Otherwise it is `none`, which
guarantees exactly one thing: this call admitted no write on a server that
advertised `calendar-auto-schedule`. Typical causes are a write without
participation data, a server that proved automatic scheduling absent, or a
governed tool that reached its deadline (`unknown`) before the lock ran. The
field stays absent in `storage_only`, where it could only ever be
`none`. This keeps the default output byte-identical. Payload-limit replacement
errors keep the disclosure, and compatibility text keeps matching
`structuredContent` (ADR 0007).

`possible` does not claim that a message was sent. RFC 6638 servers report
delivery per attendee in SCHEDULE-STATUS, which this slice does not model.

The disclosure is carried by per-operation state. Core records an admitted
server-managed write on the operation progress state. The MCP execution policy
activates disclosure only for governed tools in `server_managed` mode, and
result finalization adds the property before measuring budgets.

### Confirmation wording

In `server_managed`, every MRTR or form-elicitation review of a governed tool
appends this sentence:

> Scheduling notice: server-managed scheduling is enabled, so the CalDAV
> server may automatically send invitations, updates, replies, or cancellations
> to organizers and attendees as a result of this change.

The confirming tools are `calendars.delete`, `calendar_resources.delete`, the
high-impact or `replaceAll` forms of `events.patch` and `todos.patch`,
`calendar_resources.exact_create` and `calendar_resources.exact_replace`. The
notice is appended before OPTIONS evidence exists, because collection deletion
reviews do not probe OPTIONS and adding a probe to every review would change
request counts. It therefore says "may". The confirmation text is not part of
the request-state binding, so an existing review stays valid. Storage-only
confirmation text is unchanged.

Non-confirming governed tools (`events.create`, `todos.create`,
`todos.complete`, ordinary patches and Occurrence mutations) rely on the
bundled skill and the outcome disclosure. The skill requires the agent to warn
the user before any participation-affecting write and to report `possible`
afterwards.

### Telemetry

The operation span gains `caldav.scheduling.side_effects`. It is allowlisted
and closed to `none` and `possible`, and it is set only when the disclosure is
emitted. No participant, address, href or payload data enters telemetry.

### SCHEDULE-AGENT=CLIENT evaluation

One alternative was to keep `storage_only` usable on advertising servers by
rewriting ORGANIZER and ATTENDEE properties with `SCHEDULE-AGENT=CLIENT`, so
the server would not deliver. It is rejected and not implemented:

- It silently changes caller-authored data. That breaks the Storage-only
  Scheduling Data contract, semantic lossless preservation and exact-tool byte
  fidelity.
- Changing an existing value is a scheduling change in its own right. Moving
  an attendee from server to client scheduling can make the server send
  cancellations, as the coverage plan already records. The rewrite would then
  cause the side effect it was meant to prevent.
- A server may ignore or reject values it does not support. No OPTIONS
  evidence proves that the server honors the parameter, so the lock would lose
  its positive-evidence basis.
- Deleting a resource or collection cannot be made delivery-free by rewriting
  data first without an additional write that has the same problems.

A later slice may accept an explicit, caller-authored scheduling-agent choice
on new resources as ordinary data. The lock would still evaluate that data
like any other participation write.

### Interaction with collection-delete member scanning

A separate change lets `calendars.delete` proceed on an advertising server
when a complete, stable member scan finds no participation data. The two
changes are orthogonal. After both merge, collection deletion is allowed with
`none` when OPTIONS proves absence or the scan is clean. With advertised
scheduling and participation found, it is blocked in `storage_only` and
allowed with `possible` in `server_managed`. Unknown evidence still blocks.

## Roadmap

These later slices of item 2.9 are intentionally not implemented here. The
first entry is the highest-priority next slice.

1. Highest priority: in `server_managed` mode, require MRTR confirmation, or
   at least a disclosure warning before the write, for governed tools that
   do not confirm today when participants are involved. That means
   `events.create` and `todos.create` with ATTENDEE, ordinary patches and
   Occurrence mutations.
2. Re-evaluate MOVE governance for every newly verified Interoperability
   Profile that advertises automatic scheduling. Require observed evidence
   that MOVE has no scheduling side effects before promoting the profile.
3. Discover the principal's `schedule-inbox-URL`, `schedule-outbox-URL` and
   `calendar-user-address-set`, and expose them in `calendars.inspect`
   scheduling evidence.
4. Report `schedule-default-calendar-URL` and consider it for the default
   Event destination.
5. Model SCHEDULE-AGENT, SCHEDULE-FORCE-SEND and SCHEDULE-STATUS on organizer
   and attendee values, and expose server-reported SCHEDULE-STATUS after
   writes. This would refine `possible` into per-attendee evidence.
6. Support Schedule-Tag and `If-Schedule-Tag-Match` for attendee-side updates.
7. Add a free/busy POST through the scheduling outbox for other calendar users.
8. Add an `events.respond` tool that changes only the current user's
   PARTSTAT, with MRTR confirmation.
9. Read and act on scheduling inbox messages.

## Consequences

Operators of advertising servers can manage meetings and delete Calendars
after an explicit opt-in. Every affected result then says whether the server
may have contacted participants. The default deployment is unchanged. The
server still sends nothing itself, and delivery truth stays with the CalDAV
server until SCHEDULE-STATUS is modeled.
