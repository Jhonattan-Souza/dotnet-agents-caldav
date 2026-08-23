# Bounded Direct GET Compatibility Mode observation

Date: 2026-08-23
Baseline revision: `05f4973c8d4c1423da1c55cde4dbb0ee3c89b2f8` (the #108 stack head; the original finding was measured from `ed316f9beb46a81d69e785006c0fe65d58c2298b`)
Implementation revision: `077fe285b898998afe0bb6ed9e08f0cc52bb5612`
Final #109 stack revision: `9487bc0607d849beef0e9231c5355e156fea9d4e`
Runtime: .NET SDK `10.0.100` (`b0f34d51fc`), .NET runtime `10.0.0`, Linux `7.2.0-1-cachyos` `x86_64`, Omarchy `4.0.0`, Release
Server identity: deterministic non-conforming transport, digest N/A; conforming witness Radicale `3.7.8`, index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, resolved platform digest `sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71`

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

The isolated observation uses equivalent five-resource valid-Event corpora, the same selected Calendar cardinality, and an explicit unavailable-multiget outcome on both sides. The historical fixture uses one constant minimal Event body, while the changed permanent test uses per-href UIDs and a PRODID; the comparison is therefore work-cardinality evidence, not byte-identical payload evidence. At baseline `ed316f9beb46a81d69e785006c0fe65d58c2298b`, observation-only fixture `Issue116LegacyDirectGetDurationTests.FiveUnavailableMultigetResourcesUseFiveSequentialDirectReads` drove the real `CalendarService` and recorded five direct reads, maximum concurrency one, and 193 ms. At changed source revision `61f2607383807f96464f33350e608180c1abee49`, permanent regression `CalendarQueryDirectGetTests.FiveFallbackResourcesRunAsOneWaveOfFourThenOne` recorded the same five logical reads in a four-then-one wave, maximum concurrency four, and 358 ms. Both focused TRX rows Passed under Release with `--minimum-expected-tests 1 --fail-skips on --zero-tests-policy strict`; these single samples support only the work-shape observation and make no latency claim. Page-assembly allocation is N/A because this change alters authoritative retrieval, not page assembly.

The source-controlled [focused duration runner](../scripts/observations/query-focused-duration/run.sh) reproduced both rows in the authoritative successful root `/tmp/issue116-duration-final6` with `bash scripts/observations/query-focused-duration/run.sh /tmp/issue116-duration-final6`. The runner SHA-256 was `8043560ddc1a6fdc8eab816d12e20e829922a6a821e1b58f7c20a55553169b21`; its [Direct GET baseline fixture](../scripts/observations/query-focused-duration/direct-baseline.cs) SHA-256 was `c128f3083c7c3fbbb3a371921288d10f660ed30c9aa32b0baa63c570d9b885c3`. `runner-metadata.json` recorded the full revisions, .NET SDK `10.0.100`, host `10.0.0`, `linux-x64`, platform and `dotnet --info` SHA-256 `4b5db075d981af86febf77f2411c45fe1f7d16f758a3f1fdbdecd5bc8b7122a8`, fixture hashes, test names, outcomes, and durations beside the six TRX artifacts; the root was removed only after transcription.

## Protocol and telemetry boundaries

Only one physical REPORT 405/501 or a canonical supported-report/calendar-data DAV precondition activates fallback. The next operation may use the bounded Capability State without another REPORT; a rediscovery or authorization/configuration generation change invalidates that fact, and a stale in-flight observation cannot repopulate it. Generic 400/403, timeout, transient error, malformed or non-UTF-8 XML, incomplete response, and response-set mismatch produce no fallback GET.

The built MCP stdio/OTLP witnesses `OpenTelemetryStdioIntegrationTests.TodoStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` and `OpenTelemetryStdioIntegrationTests.QueryCompatibilityMode_ExportsMixedAndCachedDirectGetTruthOverBuiltStdio` passed on the integrated stack. Together they prove the public contract, rejected-REPORT Error child, recovered successful Operation, all three closed fetch modes, exact counters, direct-GET 404 classification, privacy, and clean streams. The first witness uses digest-pinned Radicale only to prove real conforming multiget and zero GETs; the second uses the scripted non-conforming server for Entity Start, zero-wire Continue, mixed retrieval, and cached direct fallback. No non-conforming-server claim is derived from Radicale.

The authoritative Release gate passed 2,039 Core unit tests, 962 MCP unit tests, 100 integration tests, and both ten-test `strict-preconditions` and `alternate-time-zone` Radicale variants without skips. Its homogeneous Core+MCP+integration report covered 18,990 of 20,285 lines (93.6%) and 10,976 of 12,892 branches (85.1%); the branch numerator is 17 above the integer 85% minimum. Slopwatch reported zero issues.

## Cleanup and exclusions

Deterministic handlers, Activity listeners, cancellation sources, and service providers are disposed by their tests. Cancellation tests await the active wave and prove all shared permits are reusable. Serialized MCP, OTLP, and Radicale fixtures own and remove their processes, listeners, collectors, credentials, temporary Calendars, and report artifacts.

The focused-duration roots were classified before exact cleanup: `issue116-duration-final6` was the authoritative complete ten-row Passed observation transcribed above; `final2`, `final3`, `final4`, and `final5` were complete Passed but superseded; `final` was an incomplete local-sandbox named-pipe abort with no TRX; the earlier temporal and To-do result roots and clean `issue116-duration-changed` scratch clone were superseded inputs. Only those named `/tmp/issue116-duration-*` roots were removed after transcription; no generic test-artifact directory was touched.

No universal benchmark framework, wall-clock SLA, allocation gate, new configuration switch, release artifact, or 1,200-resource rerun was added.
