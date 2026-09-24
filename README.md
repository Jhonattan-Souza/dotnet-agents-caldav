[![Release](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml/badge.svg)](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/actions/workflows/release.yml)

# CalDAV Calendars MCP Server

Model Context Protocol (MCP) server for CalDAV Calendars, Events, and To-dos. It is built with .NET 10 and distributed via `dnx`.

## Quick start

Add this MCP server to VS Code, Claude Desktop, Cursor, or any MCP client:

```json
{
  "mcpServers": {
    "caldav-calendars": {
      "command": "dnx",
      "args": ["--yes", "dotnet-agents-caldav"],
      "env": {
        "CALDAV_URL": "https://caldav.example.com",
        "CALDAV_USERNAME": "user",
        "CALDAV_PASSWORD": "password",
        "CALDAV_EVALUATION_TIME_ZONE": "America/Sao_Paulo",
        "CALDAV_DEFAULT_EVENT_CALENDAR_NAME": "Events",
        "CALDAV_DEFAULT_TODO_CALENDAR_NAME": "To-dos"
      }
    }
  }
}
```

The server negotiates the MCP revision with the client. It answers the
`initialize` handshake for `2024-11-05`, `2025-03-26`, `2025-06-18`, and
`2025-11-25`, and `server/discover` with per-request metadata for `2026-07-28`,
so current and older MCP clients connect without configuration. Reads and
unconfirmed writes behave identically on every revision; see
[Confirmed mutations](#confirmed-mutations) for how each revision confirms a
protected change.

## Bundled Agent Skill

The NuGet package includes the harness-neutral Agent Skill at
`skills/caldav-calendars/SKILL.md`. It teaches an agent to choose the semantic
or exact tool that matches the request, avoid unnecessary discovery calls,
bind updates to fresh revisions, and continue MCP confirmation exchanges.
The server also returns a condensed routing summary as MCP server
instructions for clients that do not load Agent Skills.

A harness that discovers Agent Skills from installed packages can load that
path directly. For a harness with a user-managed skill directory, extract or
copy the complete `skills/caldav-calendars` directory into the harness's
documented skill location. Keep the directory name and `SKILL.md` frontmatter
unchanged. MCP server registration and credentials remain configured through
the harness's normal MCP settings; the skill contains no credentials or
client-specific commands.

## Environment variables

| Variable | Required | Description |
| --- | --- | --- |
| `CALDAV_URL` | Yes | Absolute CalDAV server endpoint or Calendar Home URL |
| `CALDAV_AUTH_SCHEME` | No | HTTP authentication scheme: `basic` (default), `bearer`, or `oauth2`; any other value fails startup |
| `CALDAV_USERNAME` | For `basic` | Username for Basic auth; must be omitted for `bearer` and `oauth2` |
| `CALDAV_PASSWORD` | For `basic` and `bearer` | Password for Basic auth, or the static token sent as `Authorization: Bearer` for `bearer`; must be omitted for `oauth2` |
| `CALDAV_OAUTH_TOKEN_ENDPOINT` | For `oauth2` | Absolute HTTPS OAuth 2.0 token endpoint used for the refresh-token grant |
| `CALDAV_OAUTH_CLIENT_ID` | For `oauth2` | OAuth 2.0 client identifier |
| `CALDAV_OAUTH_CLIENT_SECRET` | No | Secret client credential for confidential `oauth2` clients; omit for public clients |
| `CALDAV_OAUTH_REFRESH_TOKEN` | For `oauth2` | Secret OAuth 2.0 refresh token exchanged for short-lived access tokens |
| `CALDAV_CALENDAR_HREFS` | No | Comma-separated exact canonical Calendar href allowlist; omit to discover every Calendar |
| `CALDAV_DEFAULT_TODO_CALENDAR_NAME` | No | Display name of the default Calendar for To-do operations |
| `CALDAV_DEFAULT_EVENT_CALENDAR_NAME` | No | Display name of the default Calendar for Event operations |
| `CALDAV_EVALUATION_TIME_ZONE` | Yes (installation) | Exact IANA zone used as the configured Temporal Evaluation Context for bounded Calendar Entity Starts and every Occurrence or To-do Start; invalid values fail startup and a caller `evaluationTimeZone` override wins. Manual runs may omit it when the call supplies an IANA identifier; To-do Starts require context even without a window |
| `CALDAV_INTEROPERABILITY_PROFILE` | No | Set to `radicale-3.7.8` or `nextcloud-34.0.3` only for that verified runtime; otherwise server-authoritative Move fails closed with `unsupported_capability`  Radicale’s non-RFC free/busy representation requires `radicale-3.7.8` |
| `CALDAV_SCHEDULING_MODE` | No | `storage_only` (default when unset or empty) or `server_managed`; any other value fails startup. `storage_only` blocks participation-bearing writes and Calendar collection deletion unless fresh OPTIONS evidence shows the server does not advertise `calendar-auto-schedule`. `server_managed` also allows them when the server advertises it; the server may then send invitations, updates or cancellations, and affected outcomes report `schedulingSideEffects`. See [ADR 0009](docs/adr/0009-opt-in-server-managed-scheduling.md) |
| `CALDAV_REDIRECT_HOSTS` | No | Comma-separated HTTPS host allowlist for providers that redirect or delegate to other hosts, such as `.icloud.com` for iCloud. An entry is an exact host name or a leading-dot suffix matching strict subdomains; schemes, ports, paths, wildcards and IP addresses fail startup, and `CALDAV_URL` must use HTTPS. Allowlisted hosts receive the configured credentials at the configured port; every other origin is refused before any request. Omit to keep every request on the `CALDAV_URL` origin |
| `CALDAV_EXPOSE_EXACT_TOOLS` | No | Set to `true` to expose protected exact Calendar Object Resource tools |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | Non-empty OTLP endpoint that opts into telemetry export; no exporter is registered when omitted |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | Standard OTLP protocol such as `http/protobuf` or `grpc` |
| `OTEL_EXPORTER_OTLP_HEADERS` | No | Secret OTLP authentication or routing headers; never included in exported telemetry |
| `OTEL_SERVICE_NAME` | No | Service name override; defaults to `dotnet-agents-caldav` |
| `OTEL_SDK_DISABLED` | No | Set to `true` to disable the SDK even when an endpoint is configured |

## Authentication

`CALDAV_AUTH_SCHEME` selects how each CalDAV request is authenticated:

- `basic` (default) sends `CALDAV_USERNAME` and `CALDAV_PASSWORD` as HTTP Basic credentials.
- `bearer` sends `CALDAV_PASSWORD` unchanged as `Authorization: Bearer <token>` for gateways, proxies, and other static tokens. `CALDAV_USERNAME` must be omitted, and the token is never refreshed.
- `oauth2` exchanges `CALDAV_OAUTH_REFRESH_TOKEN` at `CALDAV_OAUTH_TOKEN_ENDPOINT` (RFC 6749 refresh-token grant, client credentials in the form body) for Bearer access tokens, as servers such as Google Calendar's CalDAV API require. `CALDAV_USERNAME` and `CALDAV_PASSWORD` must be omitted.

With `oauth2`, the access token is held only in memory. The first CalDAV request obtains it; it is renewed before `expires_in` elapses (60 seconds early, or halfway through shorter lifetimes) and once when the CalDAV server answers 401, after which that request is resent exactly once. Concurrent requests share one token request, and each token request is limited to 5 seconds and 64 KiB of response. A refresh token issued in a token response replaces the configured one for the rest of the process; a restart uses the configured value again. The server never runs the interactive consent flow, so obtain the refresh token with the provider's tooling. A rejected grant (HTTP 400 or 401 from the token endpoint) becomes a typed `upstream_unauthorized` failure that is not retried and is remembered for 30 seconds, so later requests fail fast without contacting the token endpoint; an unreachable endpoint becomes `upstream_unavailable`. A write that fails this way was never sent, so its mutation state is definitive (not committed) rather than `unknown`; a CalDAV 401 whose renewal fails remains that 401 (`upstream_unauthorized`, not committed). Token endpoint responses, access tokens, refresh tokens, and client secrets never appear in results, logs, or telemetry.

Use an `https` `CALDAV_URL` with every scheme; plain `http` sends passwords and tokens in cleartext and is only appropriate for loopback testing.

Credentials are attached only to the `CALDAV_URL` origin and HTTPS hosts authorized by `CALDAV_REDIRECT_HOSTS` at the configured port. Redirects are followed manually under the same account-origin rules. Other origins receive no credentials. The token endpoint uses its own client that follows no redirects. Digest and client-certificate (mTLS) authentication are not supported.

## Available tools

### Semantic Calendar tools

- `calendars.list` — Discover the configured Calendar Scope, with an advisory Calendar Change Tag when the server reports one.
- `calendars.create` — Create an Event-only, To-do-only, or mixed Calendar collection with one atomic native `MKCALENDAR`, optionally initializing its Calendar Color, Calendar Order and Calendar Time Zone.
- `calendars.delete` — Confirm and recursively delete one exact Calendar collection, including its resources.
- `calendar_entities.query` — Start a persisted Event and To-do query across Calendar Scope, optionally selected by `text` and `categories`, or continue its immutable Query Result Snapshot without repeating CalDAV work. A bounded Start requires an explicit caller or configured IANA Temporal Evaluation Context and reports the frozen context on every page.
- `calendar_occurrences.query` — Start one bounded Event and To-do Occurrence query under an explicit caller or configured IANA Temporal Evaluation Context, optionally selected by the `text` and `categories` of each Occurrence's own component, or continue its immutable Query Result Snapshot with no CalDAV or recurrence work.
- `todos.query` — Start a compact normalized To-do query over one authoritative VTODO-only corpus, optionally selected by `text` and `categories`, or continue its immutable Query Result Snapshot without remote or semantic re-execution. Every Start requires a caller or configured IANA Temporal Evaluation Context.
- `calendar_resources.get` — Read an authoritative semantic-or-opaque snapshot by confirmed absolute href.
- `events.create` — Create one Event in a selected Calendar. Timed Events without an explicit `end` or `duration` default to `PT1H`; date-only Events remain one nominal day.
- `events.patch` — Apply a revision-bound semantic patch to one Event resource.
- `todos.create` — Create one To-do in a selected Calendar.
- `todos.patch` — Apply a revision-bound semantic patch to one To-do resource.
- `todos.complete` — Complete one non-recurring To-do or one explicitly identified recurring Occurrence.
- `calendar_occurrences.add` — Add one explicit RDATE identity.
- `calendar_occurrences.exclude` — Add one exact EXDATE while preserving any override.
- `calendar_occurrences.restore_exclusion` — Remove only one exact EXDATE.
- `calendar_occurrences.cancel` — Create or update one complete cancelled override.
- `calendar_occurrences.restore_cancellation` — Remove only cancelled status from one override.
- `calendar_resources.move` — Move one reviewed resource with one MOVE that sends a strong `If-Match` and `Overwrite: F`, relies on server-authoritative UID collision truth, and ends with bounded bilateral reconciliation; requires a verified interoperability profile. Server enforcement of `If-Match` varies by profile (see the [2026-09-24 Move interoperability record](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/move-interoperability-profiles-2026-09-24.md)).
- `calendar_resources.delete` — Delete an entire resource from an explicitly supplied revision reference (href, UID, kind, and exact strong ETag) after MCP MRTR review and confirmation; success requires verified absence.
- `calendars.inspect` — Inspect standard Calendar metadata (display name, CalDAV and WebDAV descriptions, color, order, timezone identifiers), report and privilege advertisements, storage limits, scheduling evidence, and any advisory Calendar Change Tag.
- `calendars.patch` — Set or remove Calendar display name, description, color, order and time zone with one atomic, unconditional metadata update; preserve unaddressed properties.
- `calendars.free_busy` — Read native server-computed busy intervals for one Calendar and a bounded UTC window without downloading Events.
- `calendar_resources.changes` — Read an initial inventory or incremental href/ETag changes and removals from view, using session-bound synchronization checkpoints.

The default semantic catalog contains these 23 tools in the order shown. Each tool also advertises a short human-readable `title` and a private cache hint (`ttlMs`, `cacheScope`) under the `io.github.jhonattan-souza/cache` `_meta` key. The catalog is fixed for the process configuration, so `tools/list` advertises `ttlMs: 3600000` with `cacheScope: private`.

### Time zones

A `zonedDateTime` input names an IANA tzdb zone such as `America/New_York`. A Windows zone identifier such as `Eastern Standard Time` is also accepted; the server stores its CLDR-mapped IANA identifier together with a generated IANA `VTIMEZONE`, so semantic writes never author a Windows or custom `TZID`. Other identifiers keep failing with the existing typed validation error. Semantic patches that introduce a new IANA zone also add its `VTIMEZONE`.

Reads and queries resolve each `TZID` from the resource's own `VTIMEZONE` first. A `TZID` without one, which RFC 7809 servers and many clients produce, resolves as an IANA zone or through the Windows-to-IANA mapping. The resource keeps its original `TZID` text, and a mapped Windows reference carries the informational diagnostic `timezone_reference_resolved_externally`. A `TZID` that resolves neither way leaves the resource readable and patchable, adds the warning `timezone_reference_unresolved`, and keeps queries that need its instants failing with `temporal_unresolved`. Exact tools apply the same rule: a complete resource may reference a `TZID` without a `VTIMEZONE` only when it resolves in one of these two ways. Recurrence-set patch overrides and other recurrence identities refer to stored values, so they are matched exactly and never mapped. As a result, a recurring master stored under a Windows `TZID` does not support start patches or recurrence-set patches. None of this uses the installation time zone from `CALDAV_EVALUATION_TIME_ZONE`, which applies only to floating and date-only values.

### Exact Calendar resource tools

- `calendar_resources.exact_get` — Opt-in byte-preserving exact read through a protected MCP blob resource link.
- `calendar_resources.exact_create` — Create a complete caller-authored Calendar Object Resource from Unicode text or canonical base64 bytes at an explicit href after MRTR confirmation.
- `calendar_resources.exact_replace` — Replace a strong-tagged resource with complete caller-authored Unicode text or canonical base64 bytes after MRTR confirmation.
- `calendar_resources.exact_move` — Review and atomically move a strong-tagged complete resource to an explicit href with constant-work MRTR and authoritative-byte verification; requires the verified interoperability profile.

Exact create, replace, and move accept a complete resource whose `TZID` has no `VTIMEZONE` when that `TZID` resolves as an IANA or mapped Windows zone. RFC 5545 strictly requires a `VTIMEZONE`, so the server may still reject or rewrite such a resource; a rejection comes back as a typed failure, and a rewrite as a fidelity failure.

The four exact tools are enabled with `CALDAV_EXPOSE_EXACT_TOOLS=true`; this flag controls the deterministic stdio catalog without contacting the server. The configured CalDAV credentials are the stdio authorization context, 401/403 responses become typed call failures, and exact writes require form elicitation support, confirmed as described in [Confirmed mutations](#confirmed-mutations). Exact Move uses headers-only GET absence probes, never scans destination members, never retries MOVE, and keeps its executable one-use plan inside Core. `resources/list` is always empty; `resources/read` of a protected link whose href or revision is no longer available fails with JSON-RPC `-32602` (Invalid Params), and other read failures use `-32603`.

### Confirmed mutations

`calendars.delete`, `calendar_resources.delete`, the three exact writes, and the
recurrence-definition, `this-and-future`, `entire-set`, and `replaceAll` patches
require a form confirmation before they mutate. A blank `"elicitation": {}`
counts as form support under the revision's compatibility rule; an elicitation
that names only `url` does not.

On `2026-07-28` the confirmation travels through MCP Multi Round-Trip Requests.
A call that opens a confirmation requires `_meta` to declare
`io.modelcontextprotocol/clientCapabilities.elicitation` with form support.
Without a form-capable declaration the server answers JSON-RPC error `-32021`
with `data.requiredCapabilities` before it issues any CalDAV request, so the
mutation is never attempted. That refusal is the one documented case where a
tool answers with a JSON-RPC error instead of typed `structuredContent`.

Elicitation exists from `2025-06-18`. On the `2025-06-18` and `2025-11-25`
`initialize` revisions, a session that declared form elicitation receives the
same confirmation form as a classic `elicitation/create` request inside the
original `tools/call`, and the tool completes in that call; the protected
continuation state never leaves the server. The server does not bound the wait
for that answer, but the user must answer within 10 minutes of the review or the
call ends as `confirmation_expired` with `mutationState` `not_attempted`.
`2024-11-05` and `2025-03-26` sessions, and any session without form
elicitation, receive the typed `unsupported_capability` result in phase `mrtr`
with `mutationState` `not_attempted`, and the mutation is never attempted.

Every tool result is checked against its advertised output schema before it is returned. A mutation whose result fails the check may already have committed, so it returns `isError` with the `indeterminate` code, `postWriteTruth` category and `postWriteVerificationOrReconciliation` phase, keeping the handler's reported `mutationState` (otherwise `unknown`); inspect the target before another write. A read that fails the check returns an unstructured tool error.

## Optional OpenTelemetry observability

To enable telemetry, set `OTEL_EXPORTER_OTLP_ENDPOINT` and leave `OTEL_SDK_DISABLED` unset or `false`. The server exports MCP and outbound HTTP signals, CalDAV operation and phase spans, and correlated logs through an allowlist. Each OTLP export call has a 250-millisecond limit to bound shutdown time if the collector stops responding.

For local troubleshooting, run the standalone Aspire Dashboard on loopback only:

```bash
docker run --rm --detach \
  --name caldav-otel-dashboard \
  --publish 127.0.0.1:18888:18888 \
  --publish 127.0.0.1:4318:18890 \
  mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b
```

Keep the generated browser token enabled, open `http://127.0.0.1:18888`, and launch the current-checkout MCP process through a real stdio client with:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_SERVICE_NAME=dotnet-agents-caldav
```

Exported spans show the MCP request, `caldav.operation`, the applicable `discovery`, `fetch`, and `reconcile` phases, and individual HTTP attempts. A result that fails its output schema check also exports a `caldav.output_contract` span with the closed `caldav.output_contract.violation` kind; `caldav.operation` keeps the handler's outcome and mutation state, while `caldav.output_contract` carries the client-visible replacement. The allowlist excludes credentials, OTLP headers, URLs/hrefs, Calendar Names, UIDs, Entity Tags, cursors, iCalendar/XML/HTTP bodies, MCP payloads/results, and exception messages or stack traces. Collector failure cannot change tool results or write telemetry diagnostics to stdout/stderr.

A request whose `_meta` carries a W3C `traceparent`, the trace-context key MCP 2026-07-28 reserves, continues the caller's trace: the MCP request span becomes a child of the caller's span, and `caldav.operation`, its phases, and HTTP attempts share the caller's trace ID and sampling decision. An invalid, oversized, or non-string `traceparent` is ignored, and the request starts a new trace with an unchanged result. The caller's `tracestate` is dropped when the MCP request span starts and `baggage` is not read, so neither is exported or forwarded.

## Supported servers

The verified interoperability profiles are the official Radicale 3.7.8 image pinned in the [Radicale 3.7.8 profile](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/contracts/0.3.0/radicale-3.7.8-profile.json) and the official Nextcloud 34.0.3 Apache image pinned in the [Nextcloud 34.0.3 profile](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/contracts/0.3.0/nextcloud-34.0.3-profile.json). Set `CALDAV_INTEROPERABILITY_PROFILE` to `radicale-3.7.8` or `nextcloud-34.0.3` only for that runtime. Server-authoritative Semantic and Exact Move fail closed with `unsupported_capability` when the profile is omitted because atomic `Overwrite: F` and `CALDAV:no-uid-conflict` enforcement cannot be inferred from stored resources or generic DAV discovery. Nextcloud rejects a rename within one Calendar, so under `nextcloud-34.0.3` an Exact Move whose destination is in the source Calendar fails closed with `unsupported_capability` before any write. Baïkal 0.10.1 is not a verified profile: its MOVE commits a duplicate UID into the destination Calendar. The Radicale profile also admits its non-RFC free/busy representation described under Architecture. Other CalDAV servers remain unverified profiles even when capability negotiation allows other operations. The [2026-09-24 Move interoperability record](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/move-interoperability-profiles-2026-09-24.md) holds the observations.

## Architecture

Native reports use `ICalendarReportModule`; collection metadata uses
`ICalendarMetadataModule`. Each accepts an exact canonical Calendar href.
An exact `CALDAV_CALENDAR_HREFS` entry authorizes these operations without
discovery; otherwise the initial operation discovers the Calendar first.
Configure that exact entry for a Calendar available only through free/busy
privileges when metadata discovery cannot authorize it. Report advertisements
are evidence: the client still attempts a native report when the advertisement
is absent, and reports an unsupported or forbidden operation as an error.

`calendar_resources.changes` transfers hrefs and ETags without resource bodies.
Apply the entire returned page before saving its checkpoint; continue while
`hasMore` is true. Copy its 36-character opaque checkpoint exactly. It binds
the Calendar and configuration to retained state in the current MCP process.
Unchanged state reuses its handle; an advancing report preserves earlier
handles for retry while they remain retained. The session keeps at most 1,024
states and 8 MiB of serialized state, evicting the least recently used entries
when either limit is reached. Continuation performs no discovery. On
`sync_reset_required`, restart with `calendarHref` and rebuild the inventory.
An observed ETag is not a semantic revision reference, and a removal can mean
revoked visibility. Query cursors and synchronization checkpoints are separate.

Free/busy uses server permissions and temporal interpretation. It accepts
whole-second UTC boundaries within 366 days and bounds the response to 5,000
periods before merging. Periods from every VFREEBUSY component are clipped to
the window and coalesced per busy type. An empty successful report means no
reported busy time; a failed report does not. Radicale 3.7.8 returns one
VFREEBUSY per busy period, with the period in `DTSTART`/`DTEND` (UTC, or
`TZID`-qualified and resolved through the report's own VTIMEZONE, else tzdb;
an unresolvable or inconsistent definition fails) and its type in a standalone `FBTYPE` property, and an empty
VCALENDAR for a window without busy time. Only the `radicale-3.7.8` profile
accepts that representation. Without it, the representation fails with
`upstream_protocol_error`, as does, in every profile, a stray `FBTYPE` property
(outside VFREEBUSY, repeated, beside `FREEBUSY`, or without exactly one
`DTSTART` and `DTEND`) or any other misplaced or malformed busy information.
Free/busy and established sync checkpoints use one logical REPORT per call,
with up to three HTTP attempts under the existing read retry policy, a 4 MiB
response limit and a 30-second deadline. Initial authorization can add
discovery requests. Sync accepts at most the requested page size, capped at
500 entries, and returns no new checkpoint if the server exceeds it.

Initial sync can make one additional REPORT without the optional server limit
after the standard limit-rejection response. Its checkpoint retains that mode
for later changes, which still use one REPORT and the same client limits.
In that mode the entire inventory or delta must fit the requested page size;
overflow leaves the prior checkpoint usable for a retry with a larger page
size, up to 500. The MCP does not blindly retry native REPORT 507 responses.

`calendars.list` and `calendars.inspect` include `changeTag` only when the
server reports the CalendarServer `getctag` property. It is an opaque, advisory
Calendar Change Tag: compare it only for exact equality as cheap evidence that
Calendar contents may have changed. It is not a revision, a precondition, or a
synchronization checkpoint; use `calendar_resources.changes` for authoritative
change tracking. A `calendars.list` result declares a 30-second client cache
lifetime, so a cached listing's tag can lag.

Calendar metadata updates set or remove only the addressed display name,
description, color, order and time zone. Description accepts an optional
language tag. The server applies the property instructions atomically, but the
operation is **unconditional**: PROPPATCH carries no `If-Match` precondition
because servers do not generally honor one on collection properties, so a
concurrent edit to an addressed property can be overwritten. The MCP sends one
PROPPATCH without retries and reads the target back. A committed or uncertain
error requires inspection before another write. When the server reports
per-property statuses, a rejected update is `not_committed` and `violations`
name each property it rejected (`property_rejected`) or did not apply because
another one failed (`property_not_applied`, HTTP 424); contradictory statuses
remain `unknown`.

Collection properties use these representations:

| Tool member | Stored property | Written | Read |
| --- | --- | --- | --- |
| `description` | `CALDAV:calendar-description` | Text with optional language | Text and `descriptionLanguage` |
| `davDescription` | `DAV:description` | Not written | Text, independent of `description` |
| `color` | Apple `calendar-color` (`http://apple.com/ns/ical/`) | `#RRGGBB` | `#RRGGBB`; an Apple `#RRGGBBAA` alpha channel is discarded; other values read as `null` |
| `order` | Apple `calendar-order` | Integer 0 to 2147483647 | The same range; other values read as `null` |
| `timeZone` / `timeZoneIds` | `CALDAV:calendar-timezone` | An IANA identifier, stored as a VCALENDAR with one VTIMEZONE generated from tzdb for 1970 to 2100; carriage returns are sent as `&#xD;` so CRLF survives XML parsing | The embedded `TZID` |

`calendars.create` sends requested color, order and time zone in the same
atomic `MKCALENDAR`. Readback verifies color and order through discovery; the
time zone relies on the server's definitive MKCALENDAR acknowledgement and is
visible through `calendars.inspect`. When the server's failure body names
rejected properties, the result is `not_committed` with per-property
`violations`, and its code comes from the first non-424 property status as for
PROPPATCH (for example 403 `upstream_forbidden`, 409 `conflict`). RFC 7986 collection-level
`NAME`, `IMAGE`, `REFRESH-INTERVAL` and `SOURCE` are not supported.

Participation-bearing creates, updates and deletes require fresh OPTIONS
evidence that automatic server scheduling is absent. Updates check both stored
and proposed data, including removed participation fields. Unknown evidence or
`calendar-auto-schedule` returns `unsupported_capability` before the write.
Native Calendar-to-Calendar MOVE retains its scheduling-neutral RFC behavior.
Invitation/reply delivery remains outside the tool contract.

With `CALDAV_SCHEDULING_MODE=server_managed`, the same writes and collection
deletion also proceed when fresh OPTIONS evidence advertises
`calendar-auto-schedule`. The server may then send invitations, updates, replies
or cancellations on its own. Unknown or failed evidence still blocks.
Confirmation reviews add a scheduling notice. Scheduling-governed outcomes whose
`mutationState` is `committed` or `unknown` report `schedulingSideEffects`:
`possible` when the call admitted a write on a server that advertised
automatic scheduling, otherwise `none`.
The default mode never emits the field. See
[ADR 0009](docs/adr/0009-opt-in-server-managed-scheduling.md).

Collection deletion needs that evidence only when a member could trigger
scheduling. When the scheduling mode does not admit the OPTIONS evidence, that
is, unknown evidence in either mode or `calendar-auto-schedule` in the default
mode (for example on Nextcloud or Baïkal), the MCP first scans every direct
member: one Depth 1 PROPFIND lists strong ETags, calendar-multiget
reads the data in batches of 50, and a second listing must match the first.
The DELETE proceeds only when no member contains an `ORGANIZER` or `ATTENDEE`
property, including one inside an alarm. A nested collection, a missing or weak
ETag, a redirect, more than 5,000 members or 32 MiB of data, a transport or
parse failure, or a change between listings returns `unsupported_capability`
with `not_attempted` and a message naming the scheduling boundary. If the
60-second operation budget runs out before the DELETE is sent, for example
while scanning a large Calendar, the result is `limit_exhausted` with
`not_attempted` and is not retryable. The second listing narrows, but cannot
remove, the window for a member written concurrently before the recursive
DELETE. A scan-admitted deletion reports `schedulingSideEffects: none` under
`server_managed`. On a server that advertises automatic scheduling,
`server_managed` admits the deletion without a scan and reports `possible`,
because that race keeps a scan from guaranteeing `none`.

Discovery follows all advertised Calendar homes and nested ordinary
collections, stopping at Calendar collections. It fails without partial results
when its depth, request, byte, home or Calendar limits are exhausted. With
multiple homes, creation requires an explicit destination below the intended
home. See the [RFC coverage plan](docs/rfc-coverage-plan-2026-09-05.md) and
[live observation harness](scripts/observations/rfc-coverage/README.md).

Configure exact Calendar hrefs when unrelated collection branches reject
discovery. Scoped traversal avoids those branches. For example, unrestricted
discovery can fail on Nextcloud trash descendants that reject PROPFIND;
the MCP returns that failure rather than claiming a complete Calendar list.

Discovery probes `CALDAV_URL`, then `/.well-known/caldav`, then the
`current-user-principal`; DNS SRV/TXT bootstrap is not implemented. Reads,
PROPFIND and REPORT follow 301, 302, 307 and 308 redirects with the same
method; conditional writes follow only 307 and 308. A 303 See Other is
rejected without a further request. By default every redirect, discovered
href and caller-supplied href must stay on the `CALDAV_URL` origin. Providers
that place Calendar homes on other hosts, such as iCloud moving
`caldav.icloud.com` to `pNN-caldav.icloud.com`, need
`CALDAV_REDIRECT_HOSTS=.icloud.com`. Every operation then accepts hrefs on an
allowlisted host and sends the configured credentials there. Collection
members, resources and MOVE destinations must still share the origin of their
Calendar or source resource.

Calendar Entity, Occurrence, and compact To-do reads use `MCP adapter` → `ICalendarQueryModule` → the single narrow `ICalendarQueryTransport` → `CalDavClient`; unrelated discovery and mutation operations retain the `ICalendarService` path. `ICalendarQueryModule` exposes exactly those three query operations, and `ICalendarService` exposes none. Lossless iCalendar projection and bounded recurrence evaluation stay in Core's iCalendar modules.

A query Start completes discovery, authoritative retrieval, evaluation, ordering, and projection before returning its first page. Windowed To-do Starts acquire VTODOs once, route non-recurring resources through the Entity lane and recurring resources through the Occurrence lane, then apply one global order. A Continue authenticates its opaque cursor and reads only the bounded process-local Query Result Snapshot. Snapshots expire ten minutes after the first page, are never extended by replay, and are not CalDAV caches or mutation authority.

Query `text` and `categories` form a Text Filter that is always evaluated locally on the authoritative resources. Every whitespace-separated term must be a case-insensitive substring of SUMMARY, DESCRIPTION, LOCATION, or one CATEGORIES value, and every category must equal one trimmed CATEGORIES value, all in the same master or Recurrence Override component. An Entity or compact To-do row matches through any such component; an Occurrence matches only through its own effective component, without inheriting master text. ASCII letters fold only to ASCII and other letters use the invariant Unicode lowercase mapping; Opaque Calendar Object Resources never match. To download fewer resources, a Start also sends one `calendar-query` per searched property with an `i;ascii-casemap` `text-match` for the longest printable-ASCII run of one term, intersected with the ordinary candidates. The run excludes iCalendar TEXT escapes, so a conforming server returns a superset of the local match. A `CALDAV:supported-filter` or `CALDAV:supported-collation` rejection is retained as unavailable text-match capability, and the query continues from the unreduced candidates with the same result. A text REPORT that exceeds the 4 MiB response limit, or an unexplained 400 or 403, also continues unreduced without retaining capability state. The `caldav.query.text_prefilter` telemetry attribute records `applied`, `unreduced`, `unavailable`, or `ineligible`. Only execution budgets can differ between those paths. The reduction assumes, as RFC 4791 requires, that the server evaluates each property filter against every Event or To-do component, including Recurrence Overrides; a server that indexes only the master component would silently omit resources whose only match is an override. See [ADR 0008](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/adr/0008-local-text-filter-with-server-candidate-reduction.md).

Operations that need discovery reuse one scoped result for the duration of that operation. A later operation, including an MRTR continuation, acquires its own result. Query Result Snapshots and capability observations have separate lifetimes.

## Development

Build:

```bash
dotnet tool restore
dotnet restore
dotnet build -c Release --no-restore
```

Run one test project with Microsoft.Testing.Platform:

```bash
dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release
```

Run the complete CI-equivalent test, coverage, and Radicale conformance suite:

```bash
bash scripts/run-test-suite.sh
```

The suite uses a new temporary artifact directory for every run. Successful local runs clean it up; failed runs retain it and print its path. Coverage aggregation validates and uses only the three current root-level reports, so stale `TestResults` or nested runner staging files cannot affect a later run.

Slopwatch:

```bash
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

The behavior gates, final-package smoke test, and package-content policy are documented in [Release validation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/release-process.md). The [developer documentation](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/blob/main/docs/README.md) links design decisions, behavioral tests, and historical performance reports.
Published release history and notes are maintained in [GitHub Releases](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/releases).
