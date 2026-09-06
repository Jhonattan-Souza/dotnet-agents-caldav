# RFC collection operations: live validation, 2026-09-05 to 2026-09-06

The [RFC coverage plan](rfc-coverage-plan-2026-09-05.md) selected four native
collection tools, bounded discovery across calendar homes, and a storage-only
scheduling boundary. The implementation passed the repository gates and an
independent code review. This report records observed behavior of the tested
server versions; it does not infer support from product names or advertisements.
Operation trace counts require a unique match to a recorded call. Captured span
totals also include startup activity; the final runs distinguish accepted-call
spans from startup and retained diagnostic traces.

## Revisions and environment

Baseline: `180312ae14a1ec97cf507dc37b5175968391cfed`. Baseline and candidate
were published separately and exercised as real persistent MCP stdio processes.
The machine used Linux x64, .NET SDK 10.0.100/runtime 10.0.0, and an AMD Ryzen 7
7735HS (8 cores, 16 logical CPUs). All server ports bound to loopback. Fixtures
used generated credentials, isolated calendars, and temporary containers.

| Runtime | Pinned image digest |
| --- | --- |
| Radicale 3.7.8 | `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80` |
| Baikal 0.10.1 | `ckulka/baikal:0.10.1-apache@sha256:658e9582f8d418392b4dfe7e816835b003694c8cb54ed0ba598eb5727b58fe22` |
| Nextcloud 34.0.3 | `nextcloud:34.0.3-apache@sha256:b97df9e0e1ee3c8c6cc009cb3f12ddce915d624d543b3bb93882025fe323a407` |
| Aspire dashboard 13.4.2 | `mcr.microsoft.com/dotnet/aspire-dashboard:13.4.2@sha256:76d05882595dd43e708d6ef3e269d98ca763694c0c822bbe98edc99790eaad1b` |

Compact-checkpoint candidate SHA-256 identities used for the recorded runs:

- MCP: `d83a82a6d23610e31dd146495ae04939769bd047ca3d3d973b16b84466bbe405`
- Core: `0bc03d31439b425c9e913e1b9b901a51253922bae9aa82b6ce2c660eef754f98`
- Product/build input snapshot (175 files): `0d15c5eed46bfb92c9615066747fab73631fa38d357782c7f67adf9724f1f458`

First review-fix build SHA-256 identities:

- MCP: `2941b2680db6ac303ce95beac9fa7717d7e7add1e95cb1851e208c285916b25c`
- Core: `a70abb338521b83d8773cc329fcedccbf58297ce098a7c66f247d7cc05d0d976`
- Product/build input snapshot (175 files): `1158bf2d4861c75f0afdbe57e6d68147cd97b24f787e86e11394464a51a9725c`

Second review-fix build SHA-256 identities:

- MCP: `360c2be61709841c933bb31bb44ac2db5e4277fa7a5ec7b96f21aa62dff0fc57`
- Core: `dde37de2588295ef6bf1e9ae44f4f2a02dc801971f3b42e8c7da3745c7aa69f8`
- Product/build input snapshot (175 files): `581c9ab682a10b263eed12c03e1b838e648602ac41240dc52b88d30a66748940`

Third review-fix build SHA-256 identities:

- MCP: `02d9a8708d83e6767cbddde39888b652222b5ca7f35a716be2e2f782742fc6c2`
- Core: `092be69dd04d01689623fee3b70e8161a9acbe0a2662fcaaddaeceb2f3c115f4`
- Product/build input snapshot (175 files): `bb36978a15259d138bb3298b67f4a92672bd4ea72cb200642bef9dab4e281cc8`

Fourth review-fix build SHA-256 identities:

- MCP: `548be13a046eefa259d23f36e9f0af08df6de5139c1e9847a3c672bcdc3cd145`
- Core: `6674df38e828f167db95728d91b042a07442bcb906380b1484d6fbde382c76dd`
- Product/build input snapshot (177 files): `bc7cc962f9519687767a80f78a00e8123ea21fba661f37a98385eb7087b541c6`

Fifth review's XML encoding build SHA-256 identities:

- MCP: `afc01dc0083b8c56e6f3c5c04d3646a4b015896452cfb87c7d5cafecda830e33`
- Core: `92ce9ff6e764f14db49c80def9e04bc0fb398879418dfcb90e0b69fbfc43115d`
- Product/build input snapshot (180 files): `544e53ce1f0a83229c55fca1004d99558a92dd38a7b513043418f874e2004168`

Final build, including Unicode metadata limits:

- MCP: `7f132bf22affd4d3d0bcae7c63da03965083a177fb6046a225b4b0dcbf5b92c1`
- Core: `a19c07f93011f2fd23407f7d71cae8847ed4ba70a419791647f748cfc54feb1a`
- Product/build input snapshot (180 files): `9a8d3b28fdd4dd85441cbb462b965f8e0838bce469f508a36ca2507cbe91fe03`

Independent probes used Release assemblies from this final source:

- MCP: `48dd0032727f8e2d1828564bf815464c82a02e7265979a7588589cad08a1fdaf`
- Core: `bb4d6960235edd6124ebfc58333ef61b730d6b1b5995778f625686898ae8bb0f`

The Radicale lane enabled its existing verified Move profile. Baikal and
Nextcloud left the profile unset. Nextcloud used exact configured Calendar
scope and a fresh disposable user after repeated setup reached its default
calendar-creation rate limit. Server limits were left at their defaults.

## Automated gates

- Clean, nonincremental Release build: zero warnings and errors.
- Full suite: Core 3,118; MCP 1,136; integration 113; strict-preconditions 11;
  alternate-time-zone 11. All 4,389 passed; zero skipped tests.
- Aggregate coverage: 94.6% line and 86.4% branch; both required gates passed.
- Slopwatch: zero issues across 307 files, including the method-complexity gate.
- Package `0.0.0-rfcvalidation.9`: verified NuGet and symbols archives, matching
  root/tool MCP metadata, exact bundled skill, local tool install, and one
  passing package smoke test. Source metadata versions remain `0.0.0`.
- Bundled skill validation, Python harness compilation, and `git diff --check`
  passed.

The independent reviewer found and then verified fixes for quadratic timezone
nesting, optional VFREEBUSY window properties, and forbidden recurrence fields
in VFREEBUSY. Later reviews covered sync-limit negotiation, retry scope and the
Hermes-compatible input schema. The final review reported no findings. The
compact store also passed a separate eight-worker, 4,096-state concurrency/replay
probe without exceeding its limits.

The first PR CI run exposed an empty reconciliation catch that the local lint
run had missed. The same source produced the warning outside the Codex worktree.
The handler now explicitly returns the existing reconciliation outcome, and a
clean source copy was used for full Slopwatch validation. Automatic Codex review
also prompted complete effective-language readback, rejection of conflicting
sync truncation errors, and exclusion of native 507 from circuit-breaker
failure counts.
The compact-checkpoint build's performance and Hermes observations retain
their earlier source identity. Later checks identify their review-fix build.

The first review-fix build passed another 198 live calls, matched to 198 Aspire operation
traces and 1,153 spans. Each runtime completed 29 metadata/report calls and a
checkpoint restart rejection. Nextcloud completed 100 initial syncs with exactly
200 REPORTs, then successful inspection and resource readback. The earlier build
failed on initial sync 51 and blocked those unrelated reads while a direct
backend GET still returned 200. Baikal completed three one-item continuation
pages without gaps or duplicates, then an empty poll and a larger replay of the
original checkpoint. All 175 product inputs stayed unchanged during the final
runs, every MCP process exited cleanly, and seeded fixture counts were restored.
These were functional regression runs during build/test activity; they add no
latency claims. Its separate independent 23-case parser probe included Baikal's
recorded response.

The second code review identified grouped participation names, escaped timezone
identifiers, misplaced free/busy data, slashless collection identities, and
unhandled resilience exceptions. The corrections keep scheduling checks aligned
with accepted stored resources, decode validated TZID TEXT once, reject misplaced
availability evidence, and normalize only the authorized collection's omitted
trailing slash. Read tools return typed transient errors for timeout, circuit,
and queue rejection. A write timeout remains indeterminate; a request rejected
before dispatch is not attempted; failed readback after acknowledgement preserves
the committed-but-unverified outcome. The independent before/after probe passed
all 38 cases on the corrected product assemblies; the prior build failed 32.

That build passed 142 further functional/fault calls, plus four comparisons
using an earlier build; all 146 calls matched Aspire traces. Native grouped URN
attendees on Baikal and Nextcloud remained editable Event projections, but
patch and confirmed delete reached fresh OPTIONS checks and stopped before
writes. Grouped mailto resources projected as opaque; Exact inputs retained
their existing stricter wire validation. Those earlier rejections were not
counted as proof of the scheduling check.

Separate loopback fault injection forwarded a metadata write, verified the
backend's acknowledgement, and held the response beyond the 10-second attempt
timeout. The corrected MCP returned `indeterminate`/`unknown`, with one write
and no replay. Injecting 100 HTTP 503 responses opened the actual circuit:
read tools then returned typed errors, and metadata patch was `not_attempted`
without HTTP dispatch. Synthetic free/busy, timezone and href responses checked
the parser fixes through MCP; they are labelled as fault injection, not native
server compatibility. Queue rejection has unit and independent-probe coverage;
it was not injected live. All fixture counts were restored.

The third review tightened metadata HTTP status parsing, including rejection
of embedded second status lines. Invalid acknowledgement evidence cannot confirm
a property write. Cancellation observed after preflight and before dispatch
propagates before the uncertain-write region. Caller cancellation retains its
existing contract; the tool's private deadline reports `not_attempted`.
Structured failures now derive their phase from operation progress, preserving
discovery, execution and reconciliation distinctions across asynchronous calls.
Invalid OPTIONS status codes leave scheduling evidence unknown without losing
valid metadata. Negative advertised limits were already rejected by
`NumberStyles.None`; new tests cover all three numeric properties and overflow.
The independent probe passed all 60 cases, including the cancellation boundary
with exactly one completed preflight and no write or reconciliation request.

The third build passed 127 native/fault calls plus four earlier-build comparisons;
all 131 matched Aspire traces containing 891 spans. The 988 captured spans also
include 23 spans from three retained diagnostic traces and 74 startup/other spans.
Each runtime repeated the four
new tools and checkpoint restart checks. All 67 checked error results matched
their operation trace's phase, code and mutation state. Fault injection reproduced the prior
acceptance of a malformed HTTP version, a false committed outcome for a multiline
acknowledgement, and an untyped error for OPTIONS status 600. The corrected build
rejected ambiguous evidence and kept OPTIONS observations within the schema.
Schema checks covered 33 complete error results and two complete successful
OPTIONS results; two earlier checks covered only the scheduling object. Four
harness assertion diagnostics remain separately labelled in the evidence.

Holding preflight responses exercised the actual private tool deadline: it
returned `limit_exhausted` / `execution` / `not_attempted` at 30.17 seconds after
three read attempts, with zero PROPPATCH or reconciliation requests. Independent
backend metadata stayed unchanged. This tests deadline handling; the narrower
cancellation race after a completed preflight has deterministic unit and
independent-probe coverage. Seeded fixture counts were restored after the runs.

The fourth review found that sync still accepted line breaks through the shared
status parser, while the DAV compliance whitelist rejected valid extension
tokens. Status validation now rejects internal CR/LF and accepts only SP/HT as
field separators across metadata, discovery, multiget and sync. Regressions
place malformed evidence after an earlier valid sync row, then verify whole-page
failure, unchanged checkpoint state, corrected retry and prior-handle replay.

Scheduling checks and metadata inspection now share one bounded DAV header
parser. It accepts the token and coded-URL forms in [RFC 4918 §10.1](https://datatracker.ietf.org/doc/html/rfc4918#section-10.1),
including commas inside coded URLs. URI identifiers use [RFC 3986 generic syntax](https://datatracker.ietf.org/doc/html/rfc3986#section-4.3)
without network access or scheme-specific URL restrictions. The parser tolerates
up to 32 empty list elements across fields, requires at least one actual class
as evidence, and caps combined field values at 64 KiB. These bounds retain
[HTTP recipient list handling](https://www.rfc-editor.org/rfc/rfc9110.html#section-5.6.1.2)
without accepting malformed tails. A separate review claim about extra metadata
properties was incorrect: output already projects the fixed eleven requested
properties. Tests now prove that behavior with 12 and 100 unrequested properties.
All 97 independent checks passed; the earlier build failed 60 of them.

That build passed 175 current native/fault calls and 31 earlier-build comparisons.
All 206 accepted calls matched unique operation traces containing 1,554 spans;
the 1,612 captured spans also include 58 startup/other spans and no diagnostic
operation traces. All 49 checked error results matched their accepted traces.
Full schema checks covered 107 returned terminal payloads (77 current and 30
earlier); redacted native protocol payloads were not reconstructed for this check.

A known-member comparison demonstrated the sync failure's consequence: applying
the earlier build's malformed 404 response removed an existing member from a
local Python inventory, while independent backend GET still returned 200 and
unchanged content. The corrected build rejected the page without a checkpoint;
corrected retry and prior-handle replay returned the complete delta. This check
did not delete the resource through MCP. Valid DAV extension headers allowed
the intended stored-URN mutation and collection deletion on Radicale, while
scheduling advertisements and malformed headers stopped before writes. Coded
URI validation made zero requests to the test endpoint. Native four-tool checks
and representative legacy reads passed on all three runtimes; fixtures were
restored afterward.

The fifth review found forced UTF-8 decoding in discovery. The correction applies
[RFC 7303 §3.2](https://datatracker.ietf.org/doc/html/rfc7303#section-3.2)
across XML readers: a byte-order mark takes precedence over the HTTP charset,
followed by XML encoding detection. Metadata, sync, PROPPATCH acknowledgements,
and DAV error conditions retain their response charset. Discovery counts actual
decompressed bytes and parses each response once. Multiget retains streaming,
bounded encoding lookahead, resource/envelope limits, and cancellation during
network reads. Strict validation rejects invalid declaration-selected bytes
that the runtime would otherwise replace, including ASCII and a UTF-8 alias.
The 89-case independent probe passed completely; the earlier build failed 72.

[XML 1.0 §4.3.3](https://www.w3.org/TR/xml/#charencoding) requires UTF-8 and
UTF-16 support. Additional encodings depend on the runtime. It rejects valid
BOM-less big-endian XML declared only as `utf-32`; an explicit `utf-32BE`
declaration or a byte-order mark works. UTF-32 support is optional under
[RFC 7303 Appendix C.3](https://datatracker.ietf.org/doc/html/rfc7303#appendix-C.3).

The encoding build repeated all 27 tools on each runtime and three checkpoint
restart checks, then passed 61 encoding/fault calls and 11 earlier-build
comparisons. All 342 accepted calls matched unique traces containing 3,320 spans;
their 3,429 captured spans include 109 startup/other spans. Full output-schema
checks covered 70 terminal results, and 62 error results matched their traces'
phase, code and mutation state. The faults covered encoding precedence, Unicode
names/hrefs/ETags, malformed bytes after an earlier valid multiget resource,
gzip, decompressed discovery limits, sync retry/replay, and encoded DAV errors.
Ambiguous property acknowledgements preserved uncertain write outcomes.

Separately, 71 diagnostic calls produced 849 captured spans: 843 call spans and
six startup/other spans. A 67-call Radicale run stopped after its sole PROPPATCH
lost the HTTP response (`response_ended`, no status, no retry). One reconciliation
PROPFIND succeeded; the tool correctly remained `indeterminate` / `unknown`.
The original runner did not persist that failed process's identity, so its
expected launched build is recorded without claiming actual process attestation.
Two additional harness mistakes concerned an existing bounded GET fallback and
an earlier build's unsupported Latin1 corrective response. Corrected runs retain
separate identities. The shipped harness now saves available process identity
after context exit even when a scenario or shutdown fails; failure-path checks
verified this change. Native fixture metadata and resource counts were restored.

A final contract check found that metadata text limits counted UTF-16 units,
rejecting schema-valid supplementary characters. The final build counts Unicode
scalar values with bounded early exit, retaining XML character validation and
the constant-time fast path for short values. Its Release assemblies passed 20
additional independent checks; the encoding build failed six. Those same Release
assemblies also passed the 89 encoding checks, for 109 independent checks against
the final source with no remaining findings.

The final build passed 24 native metadata calls plus six comparisons with the
encoding build. All 30 calls matched traces containing 162 spans; 174 captured
spans include 12 startup spans and no diagnostics. All 30 output schemas and 24
input character-constraint checks passed. Each server persisted and returned
exact 256/4,096-scalar values; 257/4,097-scalar inputs failed before HTTP. All 12
refusal results matched their trace phases. Nine writes, including restoration,
each dispatched one PROPPATCH. Seeded metadata and resource counts were restored.

## Every catalog operation

The live driver called all 27 candidate tools on each runtime in the initial
candidate and again in the XML encoding build, including raw Exact operations.
Both runs produced the counts below. MRTR fixture confirmations were completed through MCP.
Independent HTTP reads verified mutation postconditions. Expected errors were
asserted explicitly; calling every tool does not mean every runtime supports
every successful mutation.

| Runtime | Candidate calls | Distinct tools | Independent mutation checks |
| --- | ---: | ---: | ---: |
| Radicale | 91 | 27 | 22 |
| Baikal | 88 | 27 | 19 |
| Nextcloud, exact scope | 88 | 27 | 19 |

Each lane also rejected a checkpoint in a newly started MCP process. After the
compact checkpoint change, each lane passed another 20 report calls plus a
restart rejection. A further Nextcloud wire probe checked negotiated limits and
safe overflow recovery. Every affected call matched an Aspire operation trace.
The earlier full-catalog build had identical non-sync implementations; the
evidence retains its separate assembly identity. The baseline exercised all 23
prior tools: 62 calls on Radicale and 59 each on
Baikal and Nextcloud. The candidate scenarios additionally covered metadata
set/remove/preservation/language, native busy/tentative/clipped periods,
transparent and cancelled events, initial inventory, empty polls,
create/update/removal deltas, tampered checkpoints and bounded-page recovery.

All three runtimes completed Event and To-do creation and edits, completion,
resource reads/deletes, recurrence mutations, entity/occurrence/To-do queries
and their continuations, Calendar creation, ordinary Exact create/replace,
metadata inspection, and sync inventory/deltas. The exceptions below are part
of the verified result.

| Behavior | Radicale | Baikal | Nextcloud |
| --- | --- | --- | --- |
| Metadata set and description remove | Verified | Verified | Verified |
| Display-name remove | Server synthesizes a name; committed but unverified | Verified absent | Verified absent |
| Description language | Text persists; language unverified | Text persists; language unverified | Text persists; language unverified |
| Native free/busy | Malformed upstream response rejected | Expected periods and empty window verified | Expected periods and empty window verified |
| Sync inventory and changes | Verified, oversized pages rejected | Verified, native pagination retained | Verified, optional limit negotiated away |
| Semantic/Exact Move | Verified with existing profile | Unsupported profile | Unsupported profile |
| Collection DELETE and participation-bearing write | Scheduling absent in fresh OPTIONS; verified | Scheduling advertised; blocked before write | Scheduling advertised; blocked before write |

## Protocol findings that affected the implementation

Radicale returned HTTP 200 free/busy content containing multiple VFREEBUSY
components and standalone FBTYPE properties instead of FREEBUSY periods.
The MCP returns `upstream_protocol_error` without claiming availability.
[RFC 4791 §7.10](https://www.rfc-editor.org/rfc/rfc4791.html#section-7.10)
and [RFC 5545 §3.6.4](https://www.rfc-editor.org/rfc/rfc5545.html#section-3.6.4)
define the required report content. Radicale also returned historical removal
entries in an initial inventory, contrary to the server requirement in
[RFC 6578 §3.4](https://www.rfc-editor.org/rfc/rfc6578.html#section-3.4).
Those entries retain removal-from-view semantics and cannot invent a resource.

All three runtimes persisted description text but omitted `xml:lang` from
property readback. A requested description language consequently remained
`committed_but_unverified`.

Baikal and Nextcloud returned property mutation hrefs without the final
collection slash and used 204 for property removal. The client accepts that
collection identity equivalence and completed property statuses, then checks
requested values through authoritative readback. An acknowledged write with
mismatching readback remains `committed_but_unverified`.

Nextcloud rejected optional limits on initial sync with the standard 507
`number-of-matches-within-limits` error. A separate native delta probe showed
that limiting three changes to one returned a current token without a
truncation marker; polling that token lost the other two changes. Following
[RFC 6578 §3.7](https://www.rfc-editor.org/rfc/rfc6578.html#section-3.7), the
client negotiates omission only after that exact initial error and remembers
it in the authenticated checkpoint. A wire probe verified two initial REPORTs
(one limited, one unlimited) and one unlimited REPORT for each later poll.

The MCP still enforces the requested page size, at most 500 entries, 4 MiB
response bytes and one 30-second invocation deadline. With three new changes
and `pageSize: 1`, the Nextcloud call failed without a new checkpoint. Retrying
the same checkpoint with `pageSize: 100` returned all three; the following poll
was empty. A server that cannot paginate an inventory or delta larger than 500
entries therefore cannot return it through this bounded tool.

Baikal returned a collection-self 507 without a `DAV:error` element when
paginating a three-change delta. [RFC 6578 §3.6](https://www.rfc-editor.org/rfc/rfc6578.html#section-3.6)
requires clients to handle that status but only recommends the error element.
The client accepts its absence after validating the whole page and advancing
token. Supplied malformed or recognized contradictory errors still fail
without a new checkpoint. Unknown extensions retain the handling required by
[RFC 4918 §§14.5 and 17](https://www.rfc-editor.org/rfc/rfc4918.html#section-14.5).

Nextcloud's trash hierarchy contains a collection whose Depth 1 PROPFIND
returns 501. Unrestricted discovery reports that failure; exact configured
Calendar scope prunes unrelated branches and passed the successful lane.
There is no vendor-specific trash-name exception.

## Performance observations

The eight initial measurement runs covered 933 calls. Six compact-checkpoint
sync runs added 186, and direct runs on the second, third, fourth and final
review-fix builds added 237 each: **2,067 measured performance calls**, each matched to
Aspire. One read retry occurred on the third build and recovered successfully;
no write was replayed. The final 237 had zero retries. Their accepted operation
traces contain 1,393 spans, plus 12 startup/other spans for 1,405 captured total.
Those calls used 397 HTTP attempts across 12 clean MCP processes: 221 successes
and 16 expected Radicale free/busy protocol failures. All 48 metadata patches
committed with exactly one PROPPATCH each. An offline exporter label-comparison
mistake was corrected without repeating or discarding live measurements.
Runs were sequential; builds, the test suite and Hermes inference were kept
outside the timing windows.

The HTTP-observer runs used three fresh MCP processes per operation and scope,
with five repeated calls after the first call. They counted request and response
body bytes, including any server content encoding but excluding headers. The
observer buffers responses and opens a backend connection per request, so its
latency includes material measurement overhead. All 696 observer calls matched
Aspire attempt counts. Separate direct runs used one fresh process per operation
and 15 repeated calls. First-call measurements exclude MCP startup and protocol
initialization; the records include both first-call and repeated-call results.

The table gives direct p50 / p95 milliseconds on the final build
at 100 resources with explicit Calendar scope, 15 repeated calls per cell.
Earlier measurements remain in the machine-readable evidence with their
separate source and assembly identities.

| Operation | Radicale | Baikal | Nextcloud |
| --- | ---: | ---: | ---: |
| Inspect | 6.57 / 10.00 | 10.17 / 13.90 | 33.33 / 39.34 |
| Metadata patch | 12.73 / 17.00 | 20.29 / 29.80 | 56.64 / 61.68 |
| Free/busy | 65.34 / 67.30 (rejected) | 32.18 / 45.81 | 29.30 / 612.43 |
| Initial sync | 40.38 / 47.52 | 18.01 / 41.40 | 45.00 / 57.03 |
| Unchanged sync | 16.15 / 17.54 | 5.12 / 6.71 | 17.39 / 19.09 |

Radicale free/busy timing measures rejection of malformed content. The final
Nextcloud free/busy p95 includes a 612.43 ms call with one REPORT and no retry.
The fourth build's 45.92 ms inspection and 606.42 ms free/busy samples remain
in their original distributions. The third build's 287.19 ms metadata
patch had an interrupted reconciliation PROPFIND (`response_ended`) that
recovered on one read retry, with one PROPPATCH and success/committed. That
observation and the earlier Nextcloud free/busy tails of 626.46 and 606.13 ms
remain in the evidence with their original build identities.
These are local observations with small sample sizes, not latency guarantees.

At 500 resources, the compact build's measurements before the PR review fixes were:

| Runtime | Initial p50 / p95 ms | Unchanged p50 / p95 ms | HTTP attempts: initial / unchanged |
| --- | ---: | ---: | ---: |
| Radicale | 172.80 / 190.39 | 61.36 / 62.83 | 1 / 1 |
| Baikal | 66.32 / 88.78 | 5.13 / 6.68 | 1 / 1 |
| Nextcloud | 96.64 / 122.68 | 16.96 / 17.74 | 2 / 1 |

All measured initial reports returned exactly the seeded 100 or 500 resources;
all unchanged reports returned zero changes. Radicale server work grows with
collection size even for an unchanged poll. A fixed client request count does
not imply fixed server computation.

With explicit scope, inspection used two requests, metadata patch normally three
(read, one PROPPATCH, readback), and free/busy one REPORT. The recovered readback
above added one PROPFIND to that patch call. Established sync
checkpoints used one REPORT. Nextcloud initial sync used two because of the
standard limit negotiation. No unchanged poll repeated discovery.

At 500 resources, Baikal inventory response bodies totaled 139,634 bytes versus
244 for an unchanged poll. Nextcloud initial response bodies totaled 146,951
bytes, including its limit rejection, versus 313 for an unchanged poll. These
wire observations precede the compact handle change; native REPORT behavior
was unchanged. Final handles are 36 characters, compared with 380 in the Hermes
case that motivated the refinement.

Client CPU distributions, observed resident memory, MCP response sizes, native
payload bytes and all request counts are retained per scenario. Resident memory
is the MCP process working set; it does not measure server or Aspire memory.

## Real Hermes usage

Hermes used its existing `openai/gpt-5.6-luna` model through OpenRouter, with
medium reasoning, an isolated home, and the compact-checkpoint MCP build. The user's
source configuration stayed unchanged. Copied provider credentials were removed
on exit; only generated fixture resources were mutated.

The first candidate workflow made 19 calls: 16 successes, two safely rejected
checkpoint-copy errors, and one unsupported client continuation. The repeated
copy errors led to the agreed compact handle refinement. The final workflow
made 18 calls: **17 successful calls**, plus the same MRTR client limitation.
All five checkpoint uses were exact 36-character copies, with zero sync errors.
The run verified initial inventory, no-change, create/update deltas, replay of
an earlier handle, and reuse for identical state. It also verified metadata
set/restore, a patched Event and the exact native busy interval. Aspire matched
all 18 calls (159 spans).

A separate candidate To-do workflow completed 13 calls successfully, including
all three query continuations and To-do create, patch, completion and readback.
Its fourteenth call reached the same MRTR limitation. The raw MCP driver
completed these protected operations in the full catalog scenarios.

The installed Hermes bridge raises an error when it receives `InputRequiredResult`:
it needs `allow_input_required=True`, followed by a call carrying `input_responses`
and `request_state`. The MCP confirmation contract was preserved. Independent
readback verified writes; fixture-only cleanup restored the seeded 24 Events,
24 To-dos and empty archive.

## Reproduce

Use [the observation harness instructions](../scripts/observations/rfc-coverage/README.md)
to publish a candidate, start pinned fixtures, seed calendars, exercise the
catalog, export Aspire traces, join measurements, and run Hermes. The harness
requires every catalog tool to be covered and exactly one matching operation
trace per measured call. Performance runs with the HTTP observer also require
its attempt count to agree with Aspire.

Private fixture manifests, copied provider credentials and raw Hermes logs
stay outside the repository. The [sanitized JSONL evidence](observations/rfc-coverage-2026-09-05.jsonl)
contains outcomes, source identities, matched trace counts and measurement
distributions. Its first record names the distribution columns. Each line is
one JSON record; no credentials, resource bodies or raw checkpoints are included.

The local checks use the same order as CI:

```bash
dotnet tool restore
dotnet restore
dotnet build -c Release --no-restore --no-incremental
bash scripts/run-test-suite.sh
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```
