# Server-authoritative Move focused evidence

Date: 2026-08-23

## Semantic Move evidence

Baseline revision: `6a9a8883e9e8e2ae92fb762b4073e8b8e615971d`

Changed implementation revision: `1c1b3c8da64903a0f722482cb9384757218e0a56`

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

Changed implementation revision: `766898ab6e5fad5890c61427a77a07d570ef42b9`

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
`8bdeff6e6f88d755095a1f169c03fbf1c30efdc63d0c4d26b35b0250316d3a85`
for `ExactMoveMrtrWorkEvidenceTests.cs` and
`7cf833b9b4df51c81b08a7938a370d9b5bb5c2ca7b3d694a83c3f17be29622b1`
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
| 1 | baseline | 186.355 | 22 | 4 | 6 | 4 | 4 | 3 | 1 |
| 1 | changed | 3.870 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |
| 50 | baseline | 43.976 | 169 | 4 | 6 | 4 | 4 | 150 | 1 |
| 50 | changed | 180.810 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |
| 600 | baseline | 460.386 | 1,819 | 4 | 6 | 4 | 4 | 1,800 | 1 |
| 600 | changed | 2.841 | 11 | 4 | 0 | 3 | 3 | 0 | 1 |

The deterministic baseline is `3N + 19` requests: four discovery PROPFINDs,
six REPORTs, `3N + 8` involved or candidate GETs, and one MOVE. The changed
trace is 11 requests at every size: four discovery PROPFINDs, six involved-
resource GETs, and one MOVE. The discovery counts separately prove one fresh
acquisition in each MRTR call. Both modes use zero multiget and zero HEAD.

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
| 1 | baseline | 329.3614 | 28 | 10 | 6 | 4 | 4 | 3 | 1 |
| 1 | changed | 300.2858 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |
| 50 | baseline | 433.4922 | 175 | 10 | 6 | 4 | 4 | 150 | 1 |
| 50 | changed | 60.5085 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |
| 600 | baseline | 3535.6835 | 1,825 | 10 | 6 | 4 | 4 | 1,800 | 1 |
| 600 | changed | 195.7318 | 17 | 10 | 0 | 3 | 3 | 0 | 1 |

The Radicale baseline is `3N + 25` requests and the changed trace is exactly 17
requests. The extra six PROPFINDs are Radicale's discovery shape. Corpus setup,
cardinality verification, representative non-interference reads, and cleanup
are outside the timer and classified trace. Durations are supporting local
observations only; request and involved-resource counts are the regression
contract.

### Exact Move reproduction

The anchored observations were run sequentially. The committed witnesses were
copied into a detached baseline and built there:

```bash
git worktree add --detach /tmp/dotnet-agents-caldav-issue115-baseline 334234903c66e7e3572687eb7b990de61161f378
cp tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/ExactMoveMrtrWorkEvidenceTests.cs /tmp/dotnet-agents-caldav-issue115-baseline/tests/DotnetAgents.CalDav.Core.Tests.Unit/Services/
cp tests/DotnetAgents.CalDav.IntegrationTests/ExactMoveMrtrRadicaleSizeEvidenceTests.cs /tmp/dotnet-agents-caldav-issue115-baseline/tests/DotnetAgents.CalDav.IntegrationTests/
dotnet restore
dotnet build tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --no-restore
dotnet build tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj -c Release --no-restore
```

From the baseline worktree:

```bash
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrWorkEvidenceTests' \
  --results-directory /tmp/issue115-evidence-766898a/baseline-core \
  --report-trx --report-trx-filename baseline-core.trx \
  --minimum-expected-tests 4 --fail-skips on --zero-tests-policy strict --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=legacy-scan RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrRadicaleSizeEvidenceTests' \
  --results-directory /tmp/issue115-evidence-766898a/baseline-radicale \
  --report-trx --report-trx-filename baseline-radicale.trx \
  --minimum-expected-tests 2 --fail-skips on --zero-tests-policy strict --no-ansi
```

From changed implementation revision `766898ab6e5fad5890c61427a77a07d570ef42b9`:

```bash
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative dotnet test \
  --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrWorkEvidenceTests' \
  --results-directory /tmp/issue115-evidence-766898a/current-core \
  --report-trx --report-trx-filename current-core.trx \
  --minimum-expected-tests 4 --fail-skips on --zero-tests-policy strict --no-ansi
CALDAV_MOVE_EVIDENCE_MODE=server-authoritative RADICALE_CONFORMANCE_VARIANT=baseline dotnet test \
  --project tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj \
  -c Release --no-build --no-restore \
  --filter-class '*ExactMoveMrtrRadicaleSizeEvidenceTests' \
  --results-directory /tmp/issue115-evidence-766898a/current-radicale \
  --report-trx --report-trx-filename current-radicale.trx \
  --minimum-expected-tests 2 --fail-skips on --zero-tests-policy strict --no-ansi
```

All four Exact runs passed. Each Core run executed four tests and each Radicale
run executed two. The four TRX files remain retained under
`/tmp/issue115-evidence-766898a`; the Radicale witness performs exact cleanup in
`finally`, and fixture disposal removes the disposable container.

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
