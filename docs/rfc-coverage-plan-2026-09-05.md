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
- Interpret PROPPATCH status per requested property. A 207 response alone
  cannot prove success; ambiguous transport or malformed response truth cannot
  prove that a write did not commit.
- Parse all calendar homes, deduplicate identities, detect cycles, and stop at
  Calendar collections. Require an explicit creation destination when home
  selection is ambiguous.
- Apply a storage-only scheduling policy to relevant create/update/delete
  paths, checking both prior and proposed data for replacement and fresh
  OPTIONS evidence for collection deletion. Preserve native calendar-to-calendar
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
