# ADR 0010: Configurable confirmation policy

Status: Accepted

Date: 2026-10-03

## Context

`calendars.delete`, `calendar_resources.delete`, the three exact writes, and
the recurrence-definition, `this-and-future`, `entire-set` and `replaceAll`
patches confirm through MCP elicitation before they write. The tool returns an
`input_required` result with a form of one required boolean, `confirm`. On
initialize-handshake revisions the SDK sends the same form as a classic
`elicitation/create` request.

Two problems showed up in production with the Hermes Agent client.

First, Hermes treats elicitation as yes/no consent. When the user approves, it
answers `action: accept` with `content: {}` and never fills in the form. The
form declared `confirm` with `default: false`. The C# SDK 2.2.0 fills a missing
field from its schema default: `McpServer.ElicitAsync` calls
`ElicitResult.WithDefaults` on the classic path, and `McpClientImpl` does the
same when a C# client resolves an MRTR input request. The server therefore read
`confirm: false` and returned `confirmation_declined` with `mutationState`
`not_attempted`, although the user had approved. The agent saw a refusal the
user never gave.

Second, some installations run agents for a single trusted user whose client
cannot complete a form confirmation, or where a confirmation for every
single-resource deletion adds friction without adding safety. Today the only
answer for them is that the mutation cannot run.

## Decision

### An incomplete accept fails explicitly

The `confirm` field no longer has a schema default in any of the four
confirmation forms (patch, resource delete, collection delete, exact writes).
The SDK then has nothing to fill in, and the answer reaches the tool as sent.

The tools classify the answer in one place:

| Answer | Result |
| --- | --- |
| `accept` with `confirm: true` and no other field | the write proceeds |
| `accept` with `confirm: false`, `decline`, `cancel` | `confirmation_declined`, `not_attempted` (unchanged) |
| `accept` whose content is missing, lacks `confirm`, or holds a non-boolean `confirm` | `confirmation_mismatch`, category `confirmation`, phase `mrtr`, `not_attempted` |
| any other shape | `confirmation_mismatch` (unchanged) |

The incomplete accept keeps the existing `confirmation_mismatch` code, so no
error enum changes. Its message says what happened: the client accepted the
confirmation without sending `confirm=true`, so nothing was changed. The state
checks still run first, so an expired or tampered `requestState` keeps its
existing result.

Treating an incomplete accept as consent was rejected. The form exists so a
human sees the reviewed target and answers explicitly; a client that does not
render the form gives no evidence that anyone saw it.

### `CALDAV_CONFIRMATION_POLICY`

`CALDAV_CONFIRMATION_POLICY` (`CalDavOptions.ConfirmationPolicy`) selects one
closed value, following the `CALDAV_SCHEDULING_MODE` precedent of ADR 0009 for
a declared, opt-in loosening of a safety gate:

- `always` is the default when the variable is unset or empty. Behavior and
  results are unchanged.
- `destructive-scope` keeps confirmation where one call reaches beyond one
  resource or one recurrence instance, or replaces complete caller-authored
  content.
- `never` skips every confirmation.

Any other value, including case or whitespace variants, fails startup
validation without echoing the rejected input.

| Mutation | `always` | `destructive-scope` | `never` |
| --- | --- | --- | --- |
| `calendars.delete` | confirms | confirms | skipped |
| `calendar_resources.exact_create`, `exact_replace`, `exact_move` | confirms | confirms | skipped |
| `events.patch` / `todos.patch` with `recurrenceSet`, `this-and-future` or `entire-set` | confirms | confirms | skipped |
| `events.patch` / `todos.patch` `replaceAll` at `master` or `one-occurrence` scope | confirms | skipped | skipped |
| `calendar_resources.delete` | confirms | skipped | skipped |

Patches that never confirmed are unaffected by every policy.

`destructive-scope` uses the same line the patch review message already draws
between a "high-impact change" and a `replaceAll` on one component. A
single-resource deletion is bounded by its strong revision. A collection
deletion removes an unknown number of resources, and an exact write replaces
the complete resource with caller-authored bytes, so both keep their
confirmation.

### What a skip removes and what it keeps

A skip removes only the confirmation round. Everything else stays:

- the fresh review read before the write, including the revision, Entity UID,
  Entity Kind and opaque-resource checks of `calendar_resources.delete`, the
  collection review of `calendars.delete`, and the authoritative reviews of
  the exact writes;
- the strong Entity Tag sent as `If-Match`, and the conflict result for an
  outdated revision;
- the scheduling safety lock and its disclosure (ADR 0009);
- payload limits and execution budgets.

A skipped patch takes the same path an ordinary patch always took: the patch
engine reads the current revision and writes with `If-Match`.

The capability guard runs only for a call that will open a confirmation. A
mutation the policy skips needs no elicitation capability on any revision, so
it neither returns `-32021` on `2026-07-28` nor `unsupported_capability` on
the initialize revisions. Such a call also ignores `requestState` and
`inputResponses`, as ordinary patches already did. The protected state only
exists for confirmations this process opened, and the policy is fixed for the
life of the process.

### Typed disclosure

Mutation outcomes gain an optional closed property `confirmation` with the
single value `skipped_by_policy`, defined as `#/$defs/confirmationSkip` and
added to the same six outcome schemas that carry `schedulingSideEffects`. It is
emitted only when this call skipped a confirmation that `always` would have
asked for, and only when `mutationState` is `committed` or `unknown`. An outcome
that attempted nothing gains nothing from the disclosure. The property is
absent under `always`, so default output is unchanged. Payload-limit
replacement errors keep it, as they keep the scheduling disclosure.

The value is snake case like the rest of the result vocabulary
(`confirmation_declined`, `not_attempted`). The environment values keep the
hyphenated spelling of the recurrence scopes they describe.

### Plumbing

The MCP execution policy attaches the validated value for each tool call, the
same way it attaches scheduling disclosure. A tool that runs without an
attached value, such as a direct raw call in tests, uses `always`, so a missing
attachment can only add a confirmation.

### Scheduling notice

Under `server_managed` scheduling, a skipped confirmation also skips the
scheduling notice that the review would have carried. The outcome still reports
`schedulingSideEffects`. An operator who enables both opt-ins accepts that the
agent, not the review, must warn the user before a participation-affecting
write.

## Consequences

Clients that accept without filling in the form get an explicit failure instead
of a false refusal, and agents can tell the user why nothing changed. Operators
can declare that single-resource deletions and single-scope `replaceAll`
patches, or every confirmed mutation, run without a confirmation round, and
every affected result says so. The default deployment keeps every confirmation
and every result unchanged, except that the confirmation form no longer
advertises `default: false`.

Telemetry does not yet record a skipped confirmation; the result disclosure is
the only signal.
