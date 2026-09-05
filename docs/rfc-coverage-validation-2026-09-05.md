# RFC collection operations: live validation, 2026-09-05

The [RFC coverage plan](rfc-coverage-plan-2026-09-05.md) selected four native
collection tools, bounded discovery across calendar homes, and a storage-only
scheduling boundary. The implementation passed the repository gates and an
independent code review. This report records observed behavior of the tested
server versions; it does not infer support from product names or advertisements.

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

Final candidate SHA-256 identities:

- MCP: `d83a82a6d23610e31dd146495ae04939769bd047ca3d3d973b16b84466bbe405`
- Core: `0bc03d31439b425c9e913e1b9b901a51253922bae9aa82b6ce2c660eef754f98`
- Product/build input snapshot (175 files): `0d15c5eed46bfb92c9615066747fab73631fa38d357782c7f67adf9724f1f458`

The Radicale lane enabled its existing verified Move profile. Baikal and
Nextcloud left the profile unset. Nextcloud used exact configured Calendar
scope and a fresh disposable user after repeated setup reached its default
calendar-creation rate limit. Server limits were left at their defaults.

## Automated gates

- Clean, nonincremental Release build: zero warnings and errors.
- Full suite: Core 2,701; MCP 1,083; integration 113; strict-preconditions 11;
  alternate-time-zone 11. All 3,919 passed; zero skipped tests.
- Aggregate coverage: 94.5% line and 85.9% branch; both required gates passed.
- Slopwatch: zero issues, including the method-complexity gate.
- Package `0.0.0-rfcvalidation.2`: verified NuGet and symbols archives, matching
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

## Every catalog operation

The live driver called all 27 candidate tools on each runtime, including raw
Exact operations. MRTR fixture confirmations were completed through MCP.
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

Nextcloud's trash hierarchy contains a collection whose Depth 1 PROPFIND
returns 501. Unrestricted discovery reports that failure; exact configured
Calendar scope prunes unrelated branches and passed the successful lane.
There is no vendor-specific trash-name exception.

## Performance observations

The eight initial measurement runs covered 933 calls. After the compact
checkpoint refinement, six affected sync runs added 186 calls: **1,119 measured
performance calls**, each matched to Aspire, with zero transport retries.
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

The table gives direct p50 / p95 milliseconds at 100 resources with explicit
Calendar scope, 15 repeated calls per cell. Non-sync rows use the build before
compact handles; those implementations were unchanged. Sync rows use the final
compact build. Every run has separate source and assembly identities in the
machine-readable evidence.

| Operation | Radicale | Baikal | Nextcloud |
| --- | ---: | ---: | ---: |
| Inspect | 6.98 / 9.62 | 8.53 / 11.39 | 33.83 / 48.14 |
| Metadata patch | 12.51 / 16.28 | 19.72 / 23.24 | 57.16 / 69.67 |
| Free/busy | 65.61 / 70.34 (rejected) | 25.52 / 43.78 | 29.84 / 600.26 |
| Initial sync | 39.46 / 49.38 | 17.17 / 41.93 | 45.51 / 56.42 |
| Unchanged sync | 16.40 / 19.36 | 4.66 / 6.69 | 16.82 / 18.00 |

Radicale free/busy timing measures rejection of malformed content. Nextcloud
free/busy included one 600.26 ms first repeated call with 410 ms of MCP process
CPU; the other 14 repeats took 28.83–36.59 ms. That sample remains in the stated
p95. These are local observations with small sample sizes, not latency guarantees.

At 500 resources, final sync measurements were:

| Runtime | Initial p50 / p95 ms | Unchanged p50 / p95 ms | HTTP attempts: initial / unchanged |
| --- | ---: | ---: | ---: |
| Radicale | 172.80 / 190.39 | 61.36 / 62.83 | 1 / 1 |
| Baikal | 66.32 / 88.78 | 5.13 / 6.68 | 1 / 1 |
| Nextcloud | 96.64 / 122.68 | 16.96 / 17.74 | 2 / 1 |

All final initial reports returned exactly the seeded 100 or 500 resources;
all unchanged reports returned zero changes. Radicale server work grows with
collection size even for an unchanged poll. A fixed client request count does
not imply fixed server computation.

With explicit scope, inspection used two requests, metadata patch three
(read, one PROPPATCH, readback), and free/busy one REPORT. Established sync
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
medium reasoning, an isolated home, and the final published MCP. The user's
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
