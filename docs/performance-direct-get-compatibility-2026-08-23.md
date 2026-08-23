# Bounded Direct GET Compatibility Mode observation

Date: 2026-08-23
Baseline revision: `05f4973c8d4c1423da1c55cde4dbb0ee3c89b2f8` (the #108 stack head; the original finding was measured from `ed316f9beb46a81d69e785006c0fe65d58c2298b`)
Implementation revision: `9e6cc842cfa626d24ec5833ad47ae03c1ad818d7`
Configuration: .NET 10 Release; deterministic typed query transport and HTTP handlers for the non-conforming path; digest-pinned Radicale 3.7.8 only for conforming multiget evidence

## Before

The 2026-08-21 observation found no direct-fallback wire trace because the pinned Radicale profile conformed to `calendar-multiget`. The baseline conclusion was therefore code-derived: an unavailable multiget caused one awaited GET per resource, so a five-resource fallback issued five GETs with maximum concurrency one. An incomplete or wrong-count multiget could also enter that path. No fallback duration was recorded, and this report does not manufacture one from the conforming server run.

The observed conforming baseline remains useful: a 1,200-resource Calendar Entity query used 26 REPORTs and zero GETs. It is not evidence for behavior of a non-conforming server and was not rerun for this change.

## After

Focused deterministic tests use the smallest corpus for each boundary:

| Scenario | Authoritative work oracle |
| --- | --- |
| Successful multiget with 1, 50, 51, and 200 resources | 1, 1, 2, and 4 REPORT batches; zero GETs; one complete strong-tagged result per requested href |
| Verified-unavailable fallback with 1, 4, 5, and 200 resources | exactly 1, 4, 5, and 200 logical GETs; canonical waves no wider than four |
| Known-unavailable fallback with 201 resources | zero GETs; `resource_count` limit evidence 201/200 |
| Two simultaneous four-resource operations | four total requests in flight for the shared origin, then the second wave; all permits reusable afterward |
| Retry and transfer boundaries | three wire attempts maximum; 4 MiB per response and 32 MiB aggregate after decompression, with partial failed bodies charged |
| Terminal failure or cancellation | current wave cleaned up, no later wave, deterministic lowest canonical failure, and no partial items |

The focused retriever, attempt-meter, capability, parser, DI-resilience, and telemetry matrices run in Release as unit tests; their elapsed time is recorded by the test runner only as supporting build evidence, never as a threshold. The concurrency cases use explicit barriers and work counters rather than delay-based timing assertions.

## Protocol and telemetry boundaries

Only one physical REPORT 405/501 or a canonical supported-report/calendar-data DAV precondition activates fallback. The next operation may use the bounded Capability State without another REPORT; a rediscovery or authorization/configuration generation change invalidates that fact, and a stale in-flight observation cannot repopulate it. Generic 400/403, timeout, transient error, malformed or non-UTF-8 XML, incomplete response, and response-set mismatch produce no fallback GET.

The built MCP stdio/OTLP witnesses `CalendarEntityStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` and `QueryCompatibilityMode_ExportsMixedAndCachedDirectGetTruthOverBuiltStdio` passed against this revision. Together they prove the public contract, rejected-REPORT Error child, recovered successful Operation, all three closed fetch modes, exact counters, direct-GET 404 classification, privacy, and clean streams. The first witness uses digest-pinned Radicale only to prove real conforming multiget and zero GETs; no claim about non-conforming-server support is derived from Radicale.

The authoritative Release gate passed 2,039 Core unit tests, 962 MCP unit tests, 100 integration tests, and both ten-test `strict-preconditions` and `alternate-time-zone` Radicale variants without skips. Its homogeneous Core+MCP+integration report covered 18,990 of 20,285 lines (93.6%) and 10,976 of 12,892 branches (85.1%); the branch numerator is 17 above the integer 85% minimum. Slopwatch reported zero issues.

## Cleanup and exclusions

Deterministic handlers, Activity listeners, cancellation sources, and service providers are disposed by their tests. Cancellation tests await the active wave and prove all shared permits are reusable. Serialized MCP, OTLP, and Radicale fixtures own and remove their processes, listeners, collectors, credentials, and Calendar resources.

No universal benchmark framework, wall-clock SLA, allocation gate, new configuration switch, release artifact, or 1,200-resource rerun was added.
