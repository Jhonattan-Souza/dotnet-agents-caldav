# RFC coverage plan, 2026-09-05

Status: implemented and locally validated. See the [validation report](rfc-coverage-validation-2026-09-05.md); GitHub records the external PR review status.

The existing MCP covers resource CRUD, conditional writes, semantic moves,
recurrence editing, and bounded snapshot queries. This change targets native
collection operations that reduce data transfer or enable workflows the current
tools cannot express. RFC requirements establish protocol behavior; live tests
establish support for a particular server version.

| Gap | Protocol source | Implemented behavior |
| --- | --- | --- |
| Free/busy reports | [RFC 4791 §7.10](https://www.rfc-editor.org/rfc/rfc4791.html#section-7.10) | Return bounded server-computed busy intervals without downloading Events. |
| Calendar property updates | [RFC 4918 §9.2](https://www.rfc-editor.org/rfc/rfc4918.html#section-9.2) | Set or remove display name and description through one atomic PROPPATCH. |
| Incremental collection changes | [RFC 6578 §3](https://www.rfc-editor.org/rfc/rfc6578.html#section-3) | Return changed hrefs and removals from view using one native sync report. |
| Capability inspection | [RFC 4791 §5.2](https://www.rfc-editor.org/rfc/rfc4791.html#section-5.2), [RFC 3744 §5.4](https://www.rfc-editor.org/rfc/rfc3744.html#section-5.4) | Expose property/report/privilege evidence and advertised limits. |
| Multiple calendar homes and nested discovery | [RFC 4791 §§6.2.1, 8.4](https://www.rfc-editor.org/rfc/rfc4791.html#section-6.2.1) | Follow all successful home properties and traverse ordinary collections within bounded work. |
| Storage-only scheduling boundary | [RFC 6638 §3.2](https://www.rfc-editor.org/rfc/rfc6638.html#section-3.2) | Prevent participation-bearing writes from initiating server scheduling without a scheduling-aware contract. |

Free/busy support is a server requirement in RFC 4791. Its absence here is
missing client functionality. Sync is an optional extension. Neither claim
establishes that an individual homelab server supports the operation.

## MCP contracts

`calendars.inspect` accepts one exact Calendar href and returns standard
metadata plus capability evidence. Advertisement, successful execution,
unavailable properties, and unknown support remain distinct. The operation
does no member enumeration beyond any necessary authorization discovery.

`calendars.patch` accepts one exact Calendar href and explicit set/remove
instructions for display name and description, including description language.
Omitted properties remain untouched. Its concurrency mode is unconditional:
a read-before-write fingerprint cannot provide atomic metadata revision checks.
One PROPPATCH carries all requested property instructions; one target read
checks the result. Mutation outcomes retain committed, not committed, unknown,
and committed-but-unverified distinctions. Automatic retries stay disabled.

`calendars.free_busy` accepts one exact Calendar href and a bounded UTC window.
It returns typed UTC periods from the native server report, retaining busy type
and server temporal authority. It never equates permission failures or
unsupported reports with an empty busy set. It performs no Event-body fallback.

`calendar_resources.changes` starts with an exact Calendar href and a requested
page size. Later calls carry a short opaque checkpoint referring to immutable
session state containing the collection and native sync token, bound to the
current authorization context.
Each invocation returns one accepted report with metadata only. Native
truncation supplies the next checkpoint and an explicit continuation indicator.
Initial inventory and subsequent changes remain distinguishable. A removal
means removal from the caller's view, which can include revoked access.
Invalid tokens require an explicit reset; process restart invalidation is
documented, along with capacity eviction from the bounded state store.
Checkpoints remain distinct from immutable query snapshot cursors.

## Correctness and performance boundaries

- Validate canonical hrefs, origin, configured scope, response identity and
  successful direct propstat children before using upstream data.
- Bound elapsed time, transferred bytes, report entries, discovery requests,
  traversal depth, and returned busy intervals. Stop on exhaustion with a
  truthful error; never present a silently truncated response as complete.
- Preserve the existing multiget batches and zero-remote-work query Continue
  behavior. Native sync transfers href/ETag deltas without calendar bodies.
- Decode response XML using [RFC 7303 §3.2](https://datatracker.ietf.org/doc/html/rfc7303#section-3.2):
  a byte-order mark takes precedence over the HTTP charset, followed by XML
  encoding detection. Reject invalid encoded bytes before accepting protocol
  evidence. Keep multiget streaming and apply response byte bounds after
  decompression, before character decoding; discovery's aggregate budget counts
  those bytes directly. Authoritative iCalendar resources retain their UTF-8 contract.
- Interpret PROPPATCH status per requested property. A 207 response alone
  cannot prove success; ambiguous transport or malformed response truth cannot
  prove that a write did not commit.
- Parse all calendar homes, deduplicate identities, detect cycles, and stop at
  Calendar collections. Require an explicit creation destination when home
  selection is ambiguous.
- Apply a storage-only scheduling policy to relevant create/update/delete
  paths, checking both prior and proposed data for replacement. Collection
  deletion requires fresh OPTIONS evidence only when a bounded scan of its
  direct members finds `ORGANIZER` or `ATTENDEE` data or cannot complete: one
  Depth 1 PROPFIND of strong ETags, calendar-multiget batches, and a matching
  second listing before the recursive DELETE, within the 5,000-resource and
  32 MiB query budgets. Preserve native calendar-to-calendar
  MOVE, which RFC 6638 §3.2.3.4 defines as scheduling-neutral. Changing existing
  SCHEDULE-AGENT values can itself send cancellation, so existing resources
  must never be silently rewritten to suppress delivery.

The implemented bounds are 30 seconds per operation, 4 MiB per
report response, a 366-day free/busy window, 5,000 busy periods, and at most 500
change entries per returned report. These are implementation limits, not RFC
requirements. Discovery permits at most 16 homes, 64 logical requests, eight
levels, 256 Calendars, 4 MiB per response and 16 MiB of aggregate response data.

The rubber-duck review required these refinements, which the implementation
adopts:

- A sync page that exceeds its requested size or contains malformed,
  contradictory, or missing evidence fails without issuing a new checkpoint.
  A property-level ETag 404 is not a removal. Distinguish top-level 507 failure
  from a collection-self 507 in a successful multistatus report.
- A collection-self 507 in a successful sync multistatus is the required
  pagination signal under RFC 6578 §3.6. Its recommended error element may be
  absent. Reject malformed or recognized contradictory error conditions;
  ignore unknown XML extensions as required by RFC 4918 §§14.5 and 17.
- A truncated sync page needs an advancing token. Checkpoints retain the
  authorized Calendar and configuration binding, avoiding discovery on later
  calls. ETags in a change report are observations, not semantic revisions.
- Free/busy limits apply before period merging. Constant client request shape
  does not imply constant server computation; measurements include discovery,
  response bytes and all HTTP retry attempts.
- A single logical REPORT can take up to three HTTP attempts under the
  existing read resilience policy, with native 507 treated as definitive.
  Native 507 is excluded from both retries and circuit-breaker failure counts,
  so repeated initial limit negotiation cannot block unrelated operations.
  PROPPATCH has one write attempt.
- Contradictory PROPPATCH status truth remains uncertain. A mismatching readback
  after acknowledged commit remains committed-but-unverified.
  Verification compares the complete text/language value: omitting description
  language requires undefined effective language, including inherited XML scope.
- Successful, well-formed OPTIONS evidence is required for the scheduling
  guard. A server/product name cannot establish absence of scheduling.
- Exact configured Calendar hrefs authorize read-free/busy-only access without
  requiring metadata discovery. Other initial calls retain discovery-based
  Calendar authorization. Document this configuration requirement.

The reviewer gave explicit agreement with no remaining blocking design findings.

Live validation prompted one further agreed refinement. RFC 6578 §3.7 permits
a server to reject the optional result limit. After that exact standard 507
error on an empty-token initial request, the client makes at most one alternate
request without the limit. The authenticated checkpoint retains omitted-limit
mode for subsequent requests. Client byte, time and page bounds remain intact;
an oversized inventory or delta fails without advancing the checkpoint.
This preserves one REPORT for later polls while supporting servers whose limit
handling cannot provide reliable native pagination. The rubber-duck agent
approved the refinement after a live Nextcloud delta test demonstrated that
limited results could skip changes when the server advanced to its current token.

Real Hermes validation prompted a final representation change. The client
twice altered a 380-character authenticated checkpoint while copying it, though
it successfully executed the new operations and recovered from both errors.
The rubber-duck reviewer agreed to replace embedded state with a 36-character
opaque session handle containing 128 random bits. The existing protector owns
immutable state in a store bounded by 1,024 entries and 8 MiB of serialized
state, with dictionary lookup and constant-time least-recently-used bookkeeping.
Identical state reuses a handle, so unchanged polls do not consume capacity.
Advancing retains prior handles for replay until ordinary eviction. Session,
configuration, initial-inventory and omitted-limit bindings remain intact;
an unknown or evicted handle fails before HTTP with `sync_reset_required`.
No persistent storage, automatic checkpoint substitution, or extra tool is
introduced. The review required cache-bound, replay, concurrency and repeated
real-agent continuation checks before opening the PR.

Full scheduling, ACL mutation, vendor calendar sharing/color, managed
attachments, DNS bootstrap, and VJOURNAL semantics remain outside this change.

### Follow-up: discovery redirects and account origins

Discovery keeps three probes: PROPFIND on the configured URL,
`/.well-known/caldav`, then `current-user-principal`. DNS SRV/TXT bootstrap
([RFC 6764 §3](https://www.rfc-editor.org/rfc/rfc6764.html#section-3)) remains backlog.

- PROPFIND and REPORT replay the method, body and `Depth` after 301, 302, 307
  and 308. [RFC 9110 §15.4.2–15.4.3](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.4.2)
  permits rewriting only POST to GET, so this matches the protocol. Conditional
  writes follow only 307 and 308.
- A 303 See Other is rejected with a typed discovery protocol error and no
  further request. [RFC 9110 §15.4.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.4.4)
  defines only a GET retrieval of the Location. GET cannot return DAV
  properties or report results. Replaying PROPFIND or REPORT would target a
  resource the server did not name as their target. Read GETs reject 303 too,
  keeping resource identity exact.
- The default remains strictly same-origin. `CALDAV_REDIRECT_HOSTS` opts into
  additional account origins for providers such as iCloud, whose
  `caldav.icloud.com` endpoint delegates to `pNN-caldav.icloud.com`.
  Entries are exact DNS host names or leading-dot suffixes matching strict
  subdomains. Values with schemes, ports, paths, wildcards, IP literals or single
  labels fail startup. A non-empty list also requires an HTTPS `CALDAV_URL`.
  Allowlisted origins must use HTTPS on the configured port. Scheme downgrades
  and port changes are never followed.
- An allowlisted host is an account origin for every operation, not only for
  the redirect. Discovered homes and Calendars on it are usable, and exact
  hrefs, query candidates, multiget members, creates, updates, deletes and
  MOVE use the same origin check. Those requests carry the configured
  credentials. A non-allowlisted cross-origin redirect or href is refused
  before any request, so it never receives credentials. Collection members,
  resources, write redirects and MOVE destinations must share the origin of
  their Calendar or source. A Move between Calendars on different account
  origins fails origin authorization before dispatch as invalid input.

Follow-up backlog:

- RFC 7986 collection-level `NAME`, `IMAGE`, `REFRESH-INTERVAL` and `SOURCE`
  properties. Calendar Color, Order and Time Zone were added later through
  Apple `calendar-color`/`calendar-order` and `CALDAV:calendar-timezone`.

## Time zone references (addendum, 2026-09-24)

[RFC 5545 §3.2.19](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.2.19)
requires a VTIMEZONE for every TZID, but
[RFC 7809](https://www.rfc-editor.org/rfc/rfc7809.html) lets CalDAV servers and
clients omit standard definitions, and many clients write Windows zone names.
Reads, queries, occurrence expansion, semantic patches and exact validation
share one resolver. A resource-local VTIMEZONE always wins. A TZID without one
resolves from tzdb as IANA, then through the CLDR Windows mapping bundled with
that tzdb data. It never uses the host or installation time zone. The resource
keeps its original TZID text. A mapped Windows reference adds the informational
`timezone_reference_resolved_externally` diagnostic. An unresolvable reference
adds the `timezone_reference_unresolved` warning. The resource remains
semantically readable, but dependent query instants stay `temporal_unresolved`.
Exact create and replace accept such a TZID only when it resolves.

Semantic authoring always writes IANA. A Windows `timeZoneId` input is mapped
before validation. Creates and patches emit a generated IANA VTIMEZONE for each
zone they introduce, and TZIDs already present in the resource stay unchanged.
The pinned Radicale 3.7.8 profile synthesizes its own VTIMEZONE for a bare IANA
TZID but stores a Windows TZID unchanged. The live conformance test therefore
covers external resolution with a Windows reference. Recurrence-set patch
overrides assert existing stored overrides, so their identities are not mapped.

A master stored under a Windows TZID still has these limitations, which predate
this addendum. Semantic patches compare temporal families by exact TZID text,
and the stored Windows text never equals a mapped IANA identifier. As a result:

- A start patch is a zone change, and zone changes on a recurring master are
  rejected.
- Every recurrence-set patch fails the family check against the master start.
  This covers RDATE and EXDATE additions, override assertions, and orphan
  reconciliations.
- Orphan-reconciliation identities are also validated by direct tzdb lookup,
  which rejects the Windows text.

Reads, queries, and non-temporal patches of such a resource work.

## Completion criteria

1. Record the rubber-duck agent's objections, resolutions, and agreement.
2. Implement the agreed scope with synchronized runtime schemas, live catalog,
   tool order, telemetry allowlists, documentation and bundled agent guidance.
3. Pass the repository's Release, full test/coverage, conformance-variant and
   Slopwatch gates, including adversarial protocol and mutation-outcome tests.
4. Exercise every catalog operation against disposable local services with
   Aspire telemetry. Record exact server versions, request counts, latency
   distributions, output sizes, authoritative postconditions and limitations.
   Attempt Radicale, Baïkal/SabreDAV and Nextcloud compatibility lanes; publish
   only capabilities observed on the tested runtime.
5. Run a real local Hermes workflow using isolated configuration. Record
   actual tool calls and any client continuation limitation without bypassing
   MCP confirmation requirements.
6. Obtain an independent code review with no unresolved findings before
   opening the PR. Address automatic Codex review findings and monitor its
   re-reviews until the requested approval reaction. Leave the PR unmerged.
