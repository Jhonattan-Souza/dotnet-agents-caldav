# Server-authoritative Move focused evidence

Date: 2026-08-23

## Semantic Move evidence

Baseline revision: `6a9a8883e9e8e2ae92fb762b4073e8b8e615971d`

Changed implementation revision: `1c1b3c8da64903a0f722482cb9384757218e0a56`

Final #114 stack revision: `334234903c66e7e3572687eb7b990de61161f378`

Changed semantic observation anchor: `a0d9cc217516d135a94aed3e72d47464b3ec657b`

## Claim

Semantic Move no longer searches a destination Calendar for a matching UID.
It discovers the authorized Calendars once, reads the authoritative source,
probes only the exact destination href, dispatches one conditional MOVE, and
observes only the source and destination for bilateral reconciliation. Its
request and involved-resource work is independent of unrelated destination
cardinality.

This is structural evidence, not a latency threshold or service-level claim.
Durations below are supporting observations from one local run. Exact request
counts and privacy boundaries are the acceptance evidence.

## Runtime and corpus

Both revisions used .NET SDK `10.0.100`, .NET host/runtime `10.0.0`, Linux
`x64`, Omarchy `4.0.0`, kernel `7.2.0-1-cachyos`, and an AMD Ryzen 7 7735HS.
They used the same committed witness sources from the changed revision:

The deterministic handler has no server image, so its server digest is N/A.
Page-assembly allocation is N/A for Semantic and Exact Move because neither
change assembles a query result page.

- `SemanticMoveWorkEvidenceTests.SameHttpCorpusObservesBaselineScanAndChangedConstantWork`
  drives the real `CalDavClient` and `CalendarService` through one deterministic
  `HttpMessageHandler`. It supplies two scoped VTODO Calendars, one strong-tagged
  source `reviewed.ics` with UID `reviewed-move`, an absent generated destination,
  and 1, 50, or 600 strong-tagged unrelated valid VTODO resources. The handler
  answers the two legacy kind REPORTs and every candidate GET, so the baseline
  scan is observed rather than derived.
- `SemanticMoveRadicaleSizeEvidenceTests.PinnedRadicaleObservesMoveWorkAtOneFiftyAndSixHundredResources`
  incrementally materializes and verifies 1, 50, and 600 unrelated destination
  VTODO resources, moves a fresh strong-tagged source at each size, verifies
  representative unrelated resources outside the timed trace, deletes the
  moved target, and finally deletes both temporary Calendars.

The real-server witness used Radicale `3.7.8`, Python `3.14.7`, vobject
`0.9.9`, UTC, `strict_preconditions=false`, runtime architecture `x86_64`,
index digest
`sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`,
and resolved platform-manifest digest
`sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71`.

The committed source files were copied byte-for-byte into the detached
baseline worktree. Their SHA-256 values were
`9335dfac99d95c711eac9fe86d4f2623105012bf138636d31bdbaf8ae947108d`
for the deterministic witness and
`de080adc022b5912b82cb3e6cc3b2b50bd2145d5a79f3b0bfde8cf8d8fdb41f1`
for the pinned-Radicale witness.

## Observed deterministic HTTP work

| Destination resources | Revision | Duration ms | Requests | PROPFIND | REPORT | GET | Unrelated GET | MOVE |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 1.863 | 10 | 2 | 2 | 5 | 1 | 1 |
| 1 | changed | 1.225 | 7 | 2 | 0 | 4 | 0 | 1 |
| 50 | baseline | 141.993 | 59 | 2 | 2 | 54 | 50 | 1 |
| 50 | changed | 119.408 | 7 | 2 | 0 | 4 | 0 | 1 |
| 600 | baseline | 141.634 | 609 | 2 | 2 | 604 | 600 | 1 |
| 600 | changed | 1.636 | 7 | 2 | 0 | 4 | 0 | 1 |

The baseline performs `N + 9` HTTP requests and `N + 4` GETs: two discovery
PROPFINDs, two kind REPORTs, `N` candidate GETs, four involved-resource GETs,
and one MOVE. The changed revision performs exactly seven requests at every
size: two discovery PROPFINDs, one source GET, one exact-destination presence
probe, one MOVE, and two reconciliation GETs. It performs no REPORT, multiget,
or unrelated GET.

## Observed pinned-Radicale work

| Destination resources | Revision | Duration ms | Requests | PROPFIND | REPORT | Source GET | Destination GET | Unrelated GET | MOVE |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 253.4708 | 13 | 5 | 2 | 2 | 2 | 1 | 1 |
| 1 | changed | 237.5580 | 10 | 5 | 0 | 2 | 2 | 0 | 1 |
| 50 | baseline | 163.0861 | 62 | 5 | 2 | 2 | 2 | 50 | 1 |
| 50 | changed | 39.8478 | 10 | 5 | 0 | 2 | 2 | 0 | 1 |
| 600 | baseline | 1335.7807 | 612 | 5 | 2 | 2 | 2 | 600 | 1 |
| 600 | changed | 135.0164 | 10 | 5 | 0 | 2 | 2 | 0 | 1 |

The extra three PROPFINDs are Radicale's real discovery shape. The changed
wire trace remains constant at ten requests, while the baseline is `N + 12`.
Both use zero multiget requests. Corpus construction, cardinality verification,
representative non-interference reads, and cleanup occur outside the timed Move
trace.

## Exact Move focused follow-up

Baseline revision: `334234903c66e7e3572687eb7b990de61161f378`

Changed implementation revision: `cf71366aece23fa07f6519a0955e68f7cf843ea6`

Final #115 stack revision and changed evidence anchor: `61f2607383807f96464f33350e608180c1abee49`

Exact Move now performs two constant-work MRTR preparations rather than three
destination UID scans. The initial call returns only a protected non-executable
review binding. The confirmed call acquires fresh authorization and discovery,
revalidates the strong source revision and direct destination absence, compares
the prior intent, consumes one internal plan, dispatches one MOVE, and performs
bilateral reconciliation. Exact destination fidelity compares authoritative
bytes, including for Opaque resources and same-Calendar renames.

Both Exact revisions used the runtime and host recorded above. The committed
changed-revision witness sources were copied byte-for-byte into the detached
baseline worktree. Their SHA-256 values are
`1aa714b5205e367a3eaa2e4449a7589c2e36af49127046934e44e0552baafe6b`
for `ExactMoveMrtrWorkEvidenceTests.cs` and
`6d7585a6195b12c783d79efbcf99fe458dced28f2cf741cf11fccf30748e8cfc`
for `ExactMoveMrtrRadicaleSizeEvidenceTests.cs`.

The deterministic witness drives the real `CalDavClient` and
`CalendarService` through the complete two-round `ICalendarService` MRTR seam.
It supplies two scoped VTODO Calendars, one strong-tagged source, one absent
destination, and 1, 50, or 600 strong-tagged unrelated valid VTODO resources.
The baseline mode answers all six kind REPORTs and all `3N` candidate GETs, so
the former scans are measured rather than derived. The changed mode must issue
no REPORT or unrelated-resource GET.

### Exact Move deterministic HTTP observations

| Destination resources | Revision | Duration ms | Requests | PROPFIND | REPORT | Source GET | Destination GET | Unrelated GET | MOVE |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 192.202 | 22 | 4 | 6 | 4 | 4 | 3 | 1 |
| 1 | changed | 2.107 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |
| 50 | baseline | 47.531 | 169 | 4 | 6 | 4 | 4 | 150 | 1 |
| 50 | changed | 1.994 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |
| 600 | baseline | 485.264 | 1,819 | 4 | 6 | 4 | 4 | 1,800 | 1 |
| 600 | changed | 2.479 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |

The deterministic baseline is `3N + 19` requests: four discovery PROPFINDs,
six REPORTs, `3N + 8` involved or candidate GETs, and one MOVE. The changed
trace is 11 requests at every size: four discovery PROPFINDs, six involved-
resource GETs, and one MOVE. The discovery counts separately prove one fresh
acquisition in each MRTR call. Both modes use zero multiget and zero HEAD.

The same deterministic seam also fixes unrelated-resource shape independently
of Calendar size. At 1, 50, and 600 resources, an opaque unrelated corpus keeps
the baseline `3N + 19` scan while every changed trace remains 11 requests with
zero unrelated GETs. An oversized unrelated corpus makes the baseline stop as
`payload_too_large`/`not_attempted`, and a weak-ETag unrelated corpus makes it
stop as `concurrency_unavailable`/`not_attempted`; each stopping trace is seven
requests: two PROPFINDs, two REPORTs, one source GET, one destination GET, one
unrelated GET, and no MOVE. Both shapes still produce the same 11-request
successful changed trace at all three sizes because unrelated resources are not
read. Weak-ETag evidence is deterministic-only: Radicale 3.7.8 emits strong
Entity Tags for the accepted resources below, so the pinned-server witness does
not claim a server-authored weak Entity Tag.

### Exact Move pinned-Radicale observations

The dedicated Radicale witness seeds and verifies 1, then 50, then 600
unrelated destination VTODO resources. At each size it observes the
server-authored source bytes and strong Entity Tag outside the timed trace,
moves a fresh source, proves byte-exact destination content and source absence,
checks representative unrelated resources, deletes the moved target, and
finally deletes both temporary Calendars. It uses the same literal Radicale,
Python, vobject, UTC, variant, architecture, index digest, and platform digest
recorded above.

| Destination resources | Revision | Duration ms | Requests | PROPFIND | REPORT | Source GET | Destination GET | Unrelated GET | MOVE |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 355.1021 | 28 | 10 | 6 | 4 | 4 | 3 | 1 |
| 1 | changed | 320.3831 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |
| 50 | baseline | 463.9283 | 175 | 10 | 6 | 4 | 4 | 150 | 1 |
| 50 | changed | 72.2156 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |
| 600 | baseline | 3780.7293 | 1,825 | 10 | 6 | 4 | 4 | 1,800 | 1 |
| 600 | changed | 196.1332 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |

The Radicale baseline is `3N + 25` requests and the changed trace is exactly 17
requests. The extra six PROPFINDs are Radicale's discovery shape. Corpus setup,
cardinality verification, representative non-interference reads, and cleanup
are outside the timer and classified trace. Durations are supporting local
observations only; request and involved-resource counts are the regression
contract.

The server witness additionally replaces one ordinary destination VTODO with
a Radicale-accepted VEVENT that retains `CALSCALE:X-CUSTOM`, has authoritative
UTF-8 content larger than 4 MiB, and has a strong server-authored Entity Tag.
The opaque projection and byte size are asserted outside the trace. This makes
the legacy scan fail closed before dispatch while the changed revision remains
Calendar-size independent:

| Destination resources | Revision | Duration ms | Requests | PROPFIND | REPORT | Source GET | Destination GET | Unrelated GET | MOVE | Outcome |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | baseline | 75.8564 | 10 | 5 | 2 | 1 | 1 | 1 | 0 | `payload_too_large` / `not_attempted` |
| 1 | changed | 74.8774 | 17 | 10 | 0 | 3 | 3 | 0 | 1 | `success` / `committed` |
| 50 | baseline | 91.0921 | 10 | 5 | 2 | 1 | 1 | 1 | 0 | `payload_too_large` / `not_attempted` |
| 50 | changed | 80.4829 | 17 | 10 | 0 | 3 | 3 | 0 | 1 | `success` / `committed` |
| 600 | baseline | 247.0800 | 10 | 5 | 2 | 1 | 1 | 1 | 0 | `payload_too_large` / `not_attempted` |
| 600 | changed | 219.2864 | 17 | 10 | 0 | 3 | 3 | 0 | 1 | `success` / `committed` |

### Exact Move reproduction

The anchored observations were run sequentially. The committed witnesses were
copied into a detached baseline and built there:

```bash
git worktree add --detach /tmp/dotnet-agents-caldav-issue115-baseline 334234903c66e7e3572687eb7b990de61161f378
cp tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/ExactMoveMrtrWorkEvidenceTests.cs /tmp/dotnet-agents-caldav-issue115-baseline/tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/
cp tests/DotnetAgents.CalDav.IntegrationTests/ExactMoveMrtrRadicaleSizeEvidenceTests.cs /tmp/dotnet-agents-caldav-issue115-baseline/tests/DotnetAgents.CalDav.IntegrationTests/
dotnet restore
dotnet build -c Release --no-restore -m:1 /nodeReuse:false
```

From the baseline worktree:

```bash
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrWorkEvidenceTests' \
  --results-directory /tmp/issue115-evidence-cf71366/baseline-core \
  --report-trx --report-trx-filename baseline-core.trx \
  --minimum-expected-tests 13 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 --output Detailed --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrRadicaleSizeEvidenceTests' \
  --results-directory /tmp/issue115-evidence-cf71366/baseline-radicale \
  --report-trx --report-trx-filename baseline-radicale.trx \
  --minimum-expected-tests 2 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 --output Detailed --no-ansi
```

From changed implementation revision `cf71366aece23fa07f6519a0955e68f7cf843ea6`:

```bash
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrWorkEvidenceTests' \
  --results-directory /tmp/issue115-evidence-cf71366/current-core \
  --report-trx --report-trx-filename current-core.trx \
  --minimum-expected-tests 13 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 --output Detailed --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrRadicaleSizeEvidenceTests' \
  --results-directory /tmp/issue115-evidence-cf71366/current-radicale \
  --report-trx --report-trx-filename current-radicale.trx \
  --minimum-expected-tests 2 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 --output Detailed --no-ansi
```

All four Exact runs passed. Each Core run executed 13 tests and each Radicale
run executed two. The four TRX files were retained through transcription and
then removed with the successful evidence root; the detached baseline worktree
was removed as well. The Radicale witness performs exact Calendar/resource
cleanup in `finally`, and fixture disposal removes the disposable container.

## Semantic Move reproduction

The observations were produced sequentially. After checking out the changed
revision, a detached baseline was created and the two committed witness files
were copied into it unchanged:

```bash
git worktree add --detach /tmp/issue114-baseline-6a9a888 6a9a8883e9e8e2ae92fb762b4073e8b8e615971d
cp tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/SemanticMoveWorkEvidenceTests.cs /tmp/issue114-baseline-6a9a888/tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/
cp tests/DotnetAgents.CalDav.IntegrationTests/SemanticMoveRadicaleSizeEvidenceTests.cs /tmp/issue114-baseline-6a9a888/tests/DotnetAgents.CalDav.IntegrationTests/
dotnet restore
dotnet build -c Release --no-restore -m:1 /nodeReuse:false
```

The following commands were run from the detached baseline worktree. The
minimum-test, no-skip, strict-zero-test, single-module, and result-artifact
flags make the observation fail closed:

```bash
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*SemanticMoveWorkEvidenceTests' \
  --minimum-expected-tests 4 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 \
  --results-directory /tmp/issue114-evidence-baseline-success \
  --report-trx --report-trx-filename baseline-core.trx \
  --output Detailed --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*SemanticMoveRadicaleSizeEvidenceTests' \
  --minimum-expected-tests 1 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 \
  --results-directory /tmp/issue114-evidence-baseline-success \
  --report-trx --report-trx-filename baseline-radicale.trx \
  --output Detailed --no-ansi
```

The same commands were then run from changed revision
`1c1b3c8da64903a0f722482cb9384757218e0a56`, changing the evidence mode and
artifact names only:

```bash
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*SemanticMoveWorkEvidenceTests' \
  --minimum-expected-tests 4 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 \
  --results-directory /tmp/issue114-evidence-current-success \
  --report-trx --report-trx-filename current-core.trx \
  --output Detailed --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*SemanticMoveRadicaleSizeEvidenceTests' \
  --minimum-expected-tests 1 --fail-skips on --zero-tests-policy strict \
  --max-parallel-test-modules 1 \
  --results-directory /tmp/issue114-evidence-current-success \
  --report-trx --report-trx-filename current-radicale.trx \
  --output Detailed --no-ansi
```

The four runs passed: each deterministic run executed four tests and each
pinned-Radicale run executed one test. TRX artifacts were retained until every
raw value above was transcribed. The Radicale witness deletes each moved target,
deletes both temporary collections in `finally`, and fixture disposal removes
the disposable container.

## Protocol, fidelity, and outcome evidence

- `CalendarResourceMoveProtocolTests` proves strong `If-Match`, `Overwrite: F`,
  no body, one dispatch, Calendar-scoped safe redirects, and bounded
  `CALDAV:no-uid-conflict` classification. Unexpected successful HTTP statuses
  remain possibly dispatched and require reconciliation.
- `CalendarResourceMoveFidelityTests` proves that Semantic Move compares a
  complete lossless semantic component/property/parameter/value multiset,
  including root properties, duplicates, derived fields, and nested supporting
  components. It admits lexical folding, ordering, registered token case, and
  default `VALUE` normalization only after grammar validation. Exact Move still
  compares authoritative bytes.
- `CalendarMoveModuleTests` covers `Dispatched` and `PossiblyDispatched`
  bilateral truth, exact phase reporting, pre-dispatch precedence, cancellation
  isolation, semantic normalization/divergence, and the narrow
  `discover/read-source/probe/dispatch/observe` trace.
- `CalendarExactMoveServiceTests` proves two fresh MRTR preparations, one-use
  plan consumption, scoped transport authorization, Opaque exact fidelity,
  ordinary-failure precedence, dispatch-boundary ambiguity, and the shared
  complete bilateral truth matrix.
- `RadicaleConformanceHarnessTests` retains the occupied-href, same-kind and
  cross-kind UID conflict, stale revision, and probe-to-MOVE race matrix.
- `OpenTelemetryStdioIntegrationTests` retains the real stdio/OTLP outcome and
  privacy matrix without exporting hrefs, UIDs, Entity Tags, headers, content,
  credentials, exception details, or events.

## Scope boundary

Issues `#114` and `#115` remove scan work from Semantic and Exact Move while
preserving their distinct fidelity rules. Global performance evidence
consolidation remains assigned to issue `#116`. The architectural rationale is
recorded in [ADR 0006](adr/0006-server-authoritative-semantic-move.md), and the
canonical requirement mapping is in
[requirement-to-evidence.md](requirement-to-evidence.md).

## #116 evidence-root disposition and cleanup

Before cleanup, #116 classified every retained #114 root. The authoritative
final success root `/tmp/issue114-standards-final-Zi4qjd` contained 2,211 Core,
923 MCP, 106 Integration, and 11 tests in each strict and alternate conformance
variant, all Passed. Its homogeneous report covered 19,826 of 21,094 lines
(93.99%) and 11,299 of 13,239 branches (85.35%). Those values and the focused
Occurrence/To-do durations above were transcribed before removal.

`issue114-move-service` (80 Failed, four Passed) and
`issue114-radicale-size-current` (one Failed) were classified as real,
superseded diagnostic runs and were never used as passing evidence. The empty
or superseded focused roots were `issue114-evidence-baseline`,
`issue114-evidence-changed`, `issue114-evidence-current`,
`issue114-evidence-changed-2`, `issue114-observation-changed`,
`issue114-core-evidence-current`, `issue114-radicale-size-current-2`, and
`issue114-coverage-recapture-IcxZTh`. `issue114-full-gate-7aVUpc` had green TRX
rows but failed its aggregate branch gate at 84.9581%; `issue114-final-gate-Yr9eIu`
was incomplete because it lacked Integration and aggregate evidence and retained
an orphan stream. Neither was classified as a successful gate.
`issue114-authoritative-final-0BPf2h` was the superseded successful gate. The
seven `caldav115-coverage.*` roots and one `caldav115-homogeneous.*` root were
found as successful intermediate coverage captures, not distinct semantic
observations; no `issue115*` root remained.

After that classification and transcription, only those exact roots were
removed and their absence was verified. No generic `caldav-tests.*` directory,
credential store, branch ref, or stash was deleted. The detached Semantic and
Exact baseline worktrees were already absent; the protected stash remained
`f5e4f63914d50b96ddba0b59f37d4cd2f5785141`.
