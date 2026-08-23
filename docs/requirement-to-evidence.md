# Requirement-to-evidence catalog

This catalog links the active performance requirements to durable repository evidence. It is a review aid, not an executable test-results gate; the normal Release test suite remains authoritative.

## Operation-scoped CalDAV discovery reuse

| Requirement | Implementation evidence | Verification evidence |
| --- | --- | --- |
| `CAL-DISC-007` | `CalendarOperationDiscovery` owns an immutable key containing origin, principal identity, an opaque operation-local authorization/configuration generation, discovery endpoint, normalized Calendar Scope, default names, and request timeout. The key never contains a password or token. | `CalendarServiceTests.GetCalendarsAsync_OperationContextIsOpaqueAndIsolatesCredentialRotation`, `CalendarServiceTests.CalendarDiscoveryKey_RelevantConfigurationChangesAreDistinct`, and `CalendarMcpRawStdioTests.CalendarList_EachStdioToolCallPerformsFreshDiscovery` |
| `CAL-DISC-008` | One lazy task publishes a complete success, failure, or operation-cancellation outcome to every same-key consumer. | `CalendarServiceTests.GetCalendarsAsync_ConcurrentConsumersShareOneCompleteAcquisition`, `CalendarServiceTests.GetCalendarsAsync_SharedFailureIsMemoizedForTheToolCall`, and `CalendarServiceTests.GetCalendarsAsync_OperationCancellationIsTheSharedSameKeyOutcome` |
| `CAL-DISC-009` | The retained result freezes only authorized, in-scope Calendar descriptors, scope diagnostics, canonical hrefs, display-name provenance, raw Entity Kind evidence, explicit unavailable-property evidence, and precomputed default selection. It contains no credentials, calendar resources, query results, Entity Tags, or capability state. | `CalendarServiceTests.GetCalendarsAsync_RetainsOnlyCompleteInScopeDescriptors`, `CalendarServiceTests.GetCalendarsAsync_FreezesDiscoveryEvidenceForTheToolCall`, and `DavResponseParserTests.ParseCalendars_PreservesUnavailablePropertyEvidence` |
| `CAL-DISC-010` | The coordinator lifetime is the transient `ICalendarService`/MCP target invocation. A later tool invocation, including a confirmed MRTR call, constructs a new coordinator and discovers again. | `CalendarMcpRawStdioTests.CalendarList_EachStdioToolCallPerformsFreshDiscovery` proves six PROPFIND requests for two calls; `CalendarMcpRawStdioTests.CalendarResourceDelete_NativeSdkCompletesMrtrOverStdioWithoutDocker` proves three review PROPFINDs plus three confirmed-call PROPFINDs. |
| `CAL-DISC-011` | The first lazy acquisition captures the enclosing operation token. Later same-key awaiters observe that task and cannot replace its cancellation ownership. A later tool call owns a different acquisition. | `CalendarServiceTests.GetCalendarsAsync_SharedCancellationStopsTheSingleAcquisition`, `CalendarServiceTests.GetCalendarsAsync_LaterSameKeyTokenDoesNotOverrideTheOperationToken`, `CalendarServiceTests.GetCalendarsAsync_OperationCancellationIsTheSharedSameKeyOutcome`, and `CalendarServiceTests.GetCalendarsAsync_CancelledToolCallDoesNotPoisonAnotherToolCall` |
| `CAL-DISC-012` | Capability observations remain on the existing transport and are invalidated by authoritative rediscovery; they are not copied into the operation result. Later reads, conditional dispatch, and reconciliation continue to determine the current mutation outcome. | `CalDavClientTests.QueryCalendarResourceHrefsAsync_ExplicitRediscoveryInvalidatesUnavailableCapability`, `CalDavClientTests.QueryCalendarResourceHrefsAsync_StaleInFlightObservationCannotRepopulateAfterRediscovery`, `CalendarResourceMoveServiceTests.MoveResourceAsync_DestinationCollisionFromConcurrentMoveRaceIsNotCommitted`, and `CalendarEntityPatchMatrixTests.Verification_and_reconciliation_failures_preserve_truth_without_retry` |
| `CAL-DISC-013` | One internal coordinator wraps the existing `ICalendarClient`/`ICalendarCreateTransport` boundary used by query, source/destination authorization, mutation, and reconciliation engines. No process cache or second provider abstraction was added. | `CalendarEntityPatchServiceTests.PatchEventAsync_ChangesOnlySummaryAndLastModifiedWithExactReviewedRevision` and `CalendarResourceMoveServiceTests.MoveResourceAsync_UsesAtomicMoveAndVerifiesDestinationAndSource` each assert one discovery acquisition across their multi-phase operation. |

## Logical Operation and HTTP-attempt telemetry

| Operation outcome | Mutation State | Activity status | Error classification |
| --- | --- | --- | --- |
| `success` | absent, `not_attempted`, or `committed` as evidenced | Unset | absent |
| `input_required` | `not_attempted` | Unset | absent |
| `cancelled` | absent | Unset | absent |
| `error` | `not_committed`, `committed`, or `unknown` as evidenced | Error | controlled vocabulary only |

| Requirement | Implementation and verification evidence |
| --- | --- |
| `CAL-OBS-001` | `CalendarTelemetryTests.Operation_EmitsStableParentedPhaseWaterfallWithSafeDimensions` and `OpenTelemetryStdioIntegrationTests.OptIn_ExportsSafeParentedWaterfallLogsAndMcpMetricsOverLoopbackOtlp` prove one Operation span per tool call with safe tool, Entity Kind, and parented phase dimensions. |
| `CAL-OBS-002` | `CalendarTelemetryTests.Operation_EmitsClosedOutcomeAndIndependentMutationStateMatrix` proves the closed Operation outcomes, exact Activity statuses, and their independence from Mutation State. |
| `CAL-OBS-003` | `CalendarTelemetryTests.Operation_StructuredCommittedFailureExportsOnlyControlledFailureDimensions` and `OpenTelemetryStdioIntegrationTests.CommittedCreateWithoutStrongRevision_ExportsControlledCommittedFailureOverStdio` prove evidence-backed Mutation State, including Error Operations that truthfully remain `committed`; the main opt-in stdio witness proves committed success. |
| `CAL-OBS-004` | `CalendarExecutionPolicyTests.PublicToolFilter_MrtrInputRequiredIsExpectedControlFlow` and `OpenTelemetryStdioIntegrationTests.ExactCreateReview_ExportsExpectedAbsenceAndInputRequiredOverRawStdio` prove MRTR is `input_required`, Unset, `not_attempted`, and exception-free. |
| `CAL-OBS-005` | `CalendarTelemetryTests.ExportAllowlist_OnlyMarkedAbsenceProbeReclassifiesHttpNotFound`, `CalendarCreationModuleTests.ClosedCreateCommandsUseOnlyTheConstantWorkTransportPort`, the exact-create stdio witness, and `OpenTelemetryStdioIntegrationTests.ConfirmedDelete_ExportsExpectedAbsenceWithoutErrorOverStdio` prove that only explicit absence probes reclassify 404 to `expected_absence` and Ok. |
| `CAL-OBS-006` | `CalendarTelemetryTests.ExportAllowlist_PreservesQueryReadPurposeOnEveryWireOutcome` proves the purpose marker on every direct query GET outcome and retry; its 404 case alone preserves status 404 while exporting `resource_disappeared`, Ok, and no `error.type`. `CalendarQueryDirectGetTests.DiscardedPartialMultigetAbsenceIsCountedOnceFromFinalFallbackTruth` proves the aggregate counts final canonical disappearances rather than discarded intermediate observations. `OpenTelemetryStdioIntegrationTests.QueryCompatibilityMode_ExportsMixedAndCachedDirectGetTruthOverBuiltStdio` carries the 200/404 classifications through the built MCP and real OTLP exporter. |
| `CAL-OBS-007` | `CalendarTelemetryTests.ExportAllowlist_CountsRetriesAcrossIndependentRecoveredRequests`, `OpenTelemetryStdioIntegrationTests.TransientReadFailure_ExportsDistinctSafeHttpAttempts`, and `OpenTelemetryStdioIntegrationTests.ExhaustedReadRetries_KeepEveryAttemptAndOperationFailureTruthful` prove distinct failed and successful wire attempts, truthful resend counts, summed retries, recovered success, and exhausted failure without a recovery claim. |
| `CAL-OBS-008` | `CalendarQueryModuleTests.QueryTelemetryReportsActualWorkAndContinuationOnlyReadsSnapshot`, `CalendarQueryTelemetryTests.ConcurrentFallbackWorkPreservesMixedModeAndExactClosedCounters`, and `CalendarTelemetryTests.QueryTelemetryAllowlistKeepsEveryClosedFallbackModeAndReason` prove the closed `multiget`, `direct_get_fallback`, and `mixed` modes, the `multiget_unavailable` reason, exact operation-local counters under concurrent reads, and the privacy allowlist. `OpenTelemetryStdioIntegrationTests.CalendarEntityStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` proves conforming Radicale uses real multiget and zero GETs; `QueryCompatibilityMode_ExportsMixedAndCachedDirectGetTruthOverBuiltStdio` proves the mixed and cached direct modes with physical REPORT/GET counts. Fetch mode and reason appear only after actual retrieval work. |
| `CAL-OBS-009` | `CalendarExecutionPolicyTests.PublicToolFilter_EmitsParentedOperationPhaseAndSafeResultDimensions`, `CalendarExecutionPolicyTests.PublicToolFilter_UnsignalledCancellationIsControlledTimeoutFailure`, and `CalendarTelemetryTests.ExportAllowlist_RemovesPrivateLookingValuesEvenWhenLexicallyValid` prove structured and unhandled failures export only controlled code, category, phase, retryability, and error-type fields. |
| `CAL-OBS-010` | `CalendarTelemetryTests.ExportAllowlist_RemovesIdentifiersPayloadsUrlsAndExceptionDetails`, the invalid-client-dimension stdio test, and every stdio matrix witness prove allowlist privacy and zero exported exception events. |
| `CAL-OBS-011` | Every `OpenTelemetryStdioIntegrationTests` witness requires clean stdout and stderr; `StdioLoggingIntegrationTests.McpProcess_WithHangingOtlpCollector_PreservesToolResultAndTwoSecondShutdown` proves collector failure and shutdown isolation. |

The stdio witnesses parse exported OTLP protobuf, require clean stdout and
stderr, and assert that spans contain no exception events. The main opt-in and
invalid-client-dimension tests additionally search the encoded OTLP payloads
for private UIDs, hrefs, Entity Tags, cursor and MRTR state, credentials,
payload markers, trace state, exception details, and client-controlled labels.
The query retrieval witnesses additionally distinguish physical wire attempts
from cached capability decisions: a dispatched REPORT contributes its requested
slots, while a Capability State hit with no REPORT contributes zero.

## Calendar Entity deep query module and snapshots

| Requirement | Implementation evidence | Verification evidence |
| --- | --- | --- |
| `CAL-QUERY-001` | Public Core `ICalendarQueryModule.QueryEntitiesAsync` accepts a closed request and returns `QueryReply<CalendarEntityQueryItem>` without MCP or CalDAV types. Occurrence and To-do methods remain scoped to their owning tickets. | `CalendarQueryModuleTests`; `CalendarEntityToolsTests.QueryRawAsync_PassesThroughTheModuleBuiltStructuredPageWithoutReprojection` |
| `CAL-QUERY-002` | `CalendarEntityTools` performs raw/schema conversion only; `CalendarEntityQueryStartExecutor` owns the single query deadline and every later budget. The host deadline is disabled only for the migrated tool. | `CalendarExecutionPolicyTests.MigratedEntityQueryHasNoHostDeadlineOrLegacyPhase`; module limit and byte tests |
| `CAL-QUERY-003` | `ICalendarService` and `CalendarService` no longer expose Calendar Entity query; the MCP adapter depends directly on `ICalendarQueryModule`. | Structural search gate; `CalDavHostBuilderTests.BuildHost_ActivatesCalendarEntityToolsThroughTheSdkConstructionPath` |
| `CAL-QUERY-004` | Internal `ICalendarQueryTransport` has exactly discovery, candidate REPORT, multiget, and direct GET operations; #108 never calls GET. Production and scripted adapters implement it. | `CalendarQueryModuleTests` scripted adapter counters and `RepeatedStartsOnOneModuleAcquireFreshProductionDiscovery` |
| `CAL-QUERY-005` | Start and Continue executors have distinct constructors; Continue has no transport or evaluator dependency. | Structural constructor assertions and `ContinueReadsTheImmutableResultWithoutRepeatingCalDavWork` |
| `CAL-QUERY-006` | One concrete singleton `CalendarQuerySnapshotStore` exposes narrow reader/writer capabilities; there is no store interface or registry. | `CalendarQuerySnapshotStoreTests` |
| `CAL-QUERY-007` | The public request and reply families are closed records over Calendar Entity results; no query-plan extension seam is exposed. | Public API/package structural tests |
| `CAL-QUERY-008` | Calendar Entity query is removed from the legacy service path; MCP prefix assembly, host query deadline, and legacy query progress are bypassed. | `CalendarExecutionPolicyTests`; `CalendarEntityToolsTests` |
| `CAL-QUERY-009` | Start completes retrieval/evaluation/projection; Continue authenticates and admits a retained page only. | Scripted transport work counters; real stdio/Radicale Start-to-Continue witness |
| `CAL-QUERY-010` | Live schema defines mutually exclusive closed Start and Continue branches with page sizes 1-200 and a 2,048-character cursor limit. | `CalDavHostBuilderTests.BuildHost_AdvertisesFrozenEntityQuerySchemasAndPrivateCacheHint`; `QueryRawAsync_EnforcesTheExactCursorCharacterBoundary` |
| `CAL-QUERY-011` | Snapshot expiry is fixed ten minutes from first-page construction; a new Start creates fresh discovery and state. | `SnapshotLifetimeStartsWhenTheFirstPageIsBuilt`; `RepeatedStartsOnOneModuleAcquireFreshProductionDiscovery` |
| `CAL-QUERY-012` | AES-GCM cursors bind tool, snapshot, position, expiry, and keyed credential/configuration context with deterministic replay. | `CalendarQueryCursorCodecTests`; `CursorReplayVariablePagesAndAuthenticationAreDeterministic` |
| `CAL-QUERY-013` | Entity pages repeat frozen diagnostics and return `query_result_snapshot` with nullable `nextCursor`. | Live catalog; page-codec tests; stdio Start-to-Continue witness |
| `CAL-QUERY-014` | Filtering precedes a global ordinal Calendar href then Resource href sort. | `CanonicalCalendarTraversalMakesFailurePrecedenceIndependentOfKindOrder` and ordered page assertions |
| `CAL-QUERY-015` | Stored items have one encoded representation; page planning uses cumulative exact bytes and one fixed-envelope serialization before one final materialization. | `CalendarEntityQueryPageCodecTests` 1/50/200 work oracle and exact boundary; actual SDK 4 MiB edge test |
| `CAL-QUERY-016` | Per-snapshot policy is 5,000 items/32 MiB; store policy is 16 snapshots/128 MiB with provisional reservation before a cursor is returned. | `CalendarQuerySnapshotPolicyTests`; independent store slot/byte boundary tests |
| `CAL-QUERY-017` | Per-snapshot overflow returns `limit_exhausted`; aggregate exhaustion returns retryable `busy` using the nearest committed or reserved expiry. | Snapshot policy and active-reservation store tests |
| `CAL-QUERY-018` | The store retains only projected encoded items and bounded diagnostics; no authoritative resource bytes or duplicated `JsonElement` representation are retained. | Stored-type structural test and retained-byte accounting tests |
| `CAL-QUERY-019` | Lease rollback and timer disposal clear snapshots, reservations, and bytes on cancellation, publication failure, expiry, and process disposal; query telemetry is closed and identifier-free. | `CallerCancellationAfterReservationPublishesAndRetainsNothing`; `SnapshotPublicationFailureIsTypedAndRollsBackEveryStoreCounter`; expiry/disposal and telemetry privacy tests |

The dated [before/after observation](performance-query-snapshots-2026-08-23.md)
records the baseline request/serialization shape and the focused zero-work
continuation, exact-byte, capacity, real stdio, pinned-Radicale, and OTLP
acceptance boundaries.

## Bounded multiget and Direct GET Compatibility Mode

| Requirement | Implementation evidence | Verification evidence |
| --- | --- | --- |
| `CAL-DAV-005` | `CalendarQueryResourceRetriever` plans 50-resource multiget batches. `CalDavClient` returns a closed internal `Resources | VerifiedUnavailable` outcome and accepts only 405, 501, `DAV:supported-report`, or `CALDAV:supported-calendar-data` as verification. | `SuccessfulMultigetUsesFiftyResourceBatchesAndZeroGets`; `CalendarMultigetCachesOnlyClosedVerifiedUnavailableOutcomes`; `AddCalDavCalendars_DoesNotRetryDefinitiveUnsupportedReportBeforeDirectGet` |
| `CAL-DAV-006` | `CalendarQueryCapabilityState` is a 256-entry stop-caching singleton keyed by opaque canonical authorization/configuration context and guarded by a generation. Generic failures remain typed protocol or upstream failures. | `CalendarMultigetNeverCachesGenericOrTransientReportFailure`; invalid UTF-8, malformed DAV error, cache-full, canonical credential, configuration-change, rediscovery, and stale-in-flight tests |
| `CAL-DAV-007` | Retrieval indexes complete multiget batches by safe canonical href and commits them only after exact set validation. Only explicit returned 404 disappears. | Missing, duplicate, nested, unsafe, unrequested, wrong-count, reversed-order, mixed-status, and discarded-partial-disappearance regressions in `CalDavClientTests` and `CalendarQueryDirectGetTests` |
| `CAL-DAV-008` | The streaming multiget parser joins one unambiguous `calendar-data` and strong `getetag` truth per href. Direct GET uses one bounded body and strong ETag from the same response, with final identity validation. | Complementary/conflicting propstat, weak/missing ETag, 4 MiB, strict UTF-8, response-envelope, redirect identity, and exact-order tests |
| `CAL-DAV-009` | `CalendarDirectGetBudget` meters 200 logical resources, 32 MiB of decompressed bodies across every physical attempt, 4 MiB per attempt body, three physical attempts, and the module's 30-second deadline. The HTTP handler begins an attempt before dispatch and charges partial bodies incrementally. | `CalendarDirectGetBudgetTests`; `CalendarHttpAttemptHandlerTests`; `ThreeRealAttemptTimeoutsBecomeTheClosedAttemptCountLimit`; elapsed-limit module tests |
| `CAL-DAV-010` | One process-wide origin permit pool is held for a logical read across redirects and retries. The retriever schedules canonical waves of four, awaits the failing wave, selects its lowest canonical failure, and starts no later wave. | `FiveFallbackResourcesRunAsOneWaveOfFourThenOne`; `ConcurrentQueriesShareTheFourPerOriginPermit`; `SameWaveFailuresChooseCanonicalHrefAndNeverScheduleLaterWave`; fourth-attempt and concurrent-body tests |
| `CAL-DAV-011` | Fallback planning rejects 201 resources before scheduling a GET. Public limit evidence uses the closed generic dimensions `resource_count`, `attempt_count`, `byte_count`, and `elapsed_time` with observed and limit values; elapsed values are milliseconds. | `KnownUnavailableWithTwoHundredOneCandidatesFailsBeforeTheFirstGet`; exact resource, attempt, byte, and elapsed boundary tests; live catalog schema tests |
| `CAL-DAV-012` | External cancellation stops later waves, cancels the current wave, awaits cleanup, and returns no partial page. Explicit 404 is retained as the only non-terminal resource outcome. | `ExternalCancellationAwaitsCurrentWaveCleanupAndReleasesEveryPermit`; 404 and post-cancellation permit-reuse assertions |
| `CAL-DAV-013` | Retrieval mode is automatic and internal. Operation-local telemetry derives `multiget`, `direct_get_fallback`, or `mixed` from successful work and exports no public mode field or success diagnostic. | deterministic module mode/counter tests, closed allowlist tests, and MCP schema/catalog structural assertions |

These requirements narrow `CAL-DAV-001` for query resource retrieval: there is
no Depth-1 crawl or response-mismatch fallback. The dated
[Direct GET observation](performance-direct-get-compatibility-2026-08-23.md)
records the code-derived sequential baseline, focused deterministic work
counts, and the applicable real-server boundary.

## Configured Temporal Evaluation Context

| Requirement | Implementation evidence | Verification evidence |
| --- | --- | --- |
| `CAL-TIME-005` | `CalendarEntityQueryStartExecutor` resolves caller override, configured fallback, or typed `invalid_input` before constructing the CalDAV query transport. | `BoundedStartWithoutTemporalContextFailsBeforeAnyCalDavWork`, `CallerTemporalContextWinsAndIsFrozenAcrossContinuation`, and the raw stdio temporal witness |
| `CAL-TIME-006` | Bounded Calendar Entity Starts require a context; unbounded Starts leave configured fallback unused and reject an explicit unused override. The same typed context is the closed contract for later Occurrence and To-do module cutovers. | `UnboundedStartLeavesConfiguredTemporalContextUnusedAndUnreported`, `UnboundedStartRejectsUnusedCallerTemporalOverrideBeforeAnyCalDavWork`, and strict MCP Start/Continue conversion tests |
| `CAL-TIME-007` | `ValidateCalDavOptions` validates the deployment IANA identifier on startup and caller validation uses the same TZDB authority before I/O. No host-zone API participates in resolution. | `ValidateCalDavOptions_RejectsInvalidConfiguredEvaluationTimeZone`, `RunAsync_InvalidConfiguredEvaluationTimeZoneFailsStartupWithoutEchoingTheValue`, `InvalidCallerTemporalContextFailsBeforeAnyCalDavWork`, and `ExplicitTemporalContextProducesHostZoneIndependentItemsAndBytes` under `TZ=UTC` and `TZ=Pacific/Kiritimati` subprocesses |
| `CAL-TIME-008` | `TemporalEvaluationContext` records only `timeZone` and caller/configuration source once per applicable page; `CalendarQuerySnapshot` freezes its authoritative wire bytes for Continue and exact page accounting. | `CallerTemporalContextWinsAndIsFrozenAcrossContinuation`, `RetainedSnapshotCountsTheExactContextWireBytesOnce`, the context-present 4 MiB page boundary, live catalog tests, and stdio schema/result evidence |
| `CAL-TIME-009` | `CalendarTemporalResolver` evaluates UTC, IANA named-zone, resource-local VTIMEZONE, floating, and date-only forms without modifying `CalendarProperty` source slices or projected values. | `NamedIanaGapAndOverlapMatrixCoversEventAndTodoRecurringAndNonRecurring`, `ValidResourceLocalVTimeZoneIsAuthoritativeOverEvaluationContext`, floating gap/overlap tests, and source-form assertions |
| `CAL-TIME-010` | `CalendarEntityTemporalMatcher` applies half-open positive spans, point inclusion, one civil-day implicit Event end, and civil-date To-do boundaries for recurring and non-recurring entities. | Event 23/25-hour, `RecurringDateOnlyTodoUsesCivilBoundariesAcrossTwentyThreeAndTwentyFiveHourDays`, To-do intervening-span, lone-DUE boundary, and half-open tests |
| `CAL-TIME-011` | Unknown or conflicting resource-local zones return one non-retryable `temporal_unresolved` failure before page publication. | `UnknownResourceLocalZoneFailsAtomicallyWithoutRetainingASnapshot` and `ConflictingResourceLocalZonesFailAtomicallyWithoutRetainingASnapshot` |
| `CAL-TIME-012` | Snapshot content freezes the context and cursor key context authenticates the configured zone with the credentials and other relevant configuration. | `CursorContextBindsConfiguredTemporalEvaluationContextWithoutDisclosingTheChange` and continuation replay tests |

The dated [temporal before/after observation](performance-temporal-context-2026-08-23.md)
records the zero-I/O admission change, focused temporal corpus, stdio/Radicale
boundary, privacy assertions, and cleanup.

## Shared performance evidence

| Requirement | Evidence |
| --- | --- |
| `CAL-EVIDENCE-011` | Focused deterministic regressions count discovery acquisitions and real PROPFIND boundaries, plus multiget REPORT slots, direct GET resources and attempts, waves, and transfer limits. Durations remain supporting observations only. |
| `CAL-EVIDENCE-012` | The smallest sufficient corpus is selected per boundary. One Calendar and one Event patch prove discovery reuse; deterministic 1/4/5/50/51/200/201-resource corpora prove orchestration and limits. Digest-pinned Radicale is reserved for conforming multiget and zero-GET behavior. |
| `CAL-EVIDENCE-013` | The [discovery report](performance-discovery-reuse-2026-08-23.md) and [Direct GET report](performance-direct-get-compatibility-2026-08-23.md) record scenario, configuration, exact baseline and changed revision anchors, work counts, supporting durations where observed, server qualification, and cleanup. |
| `CAL-EVIDENCE-014` | No universal benchmark platform, threshold, SLA, or process-wide cache was introduced. |
| `CAL-EVIDENCE-015` | The focused and complete suites preserve correctness, lossless Calendar evidence, strong revision truth, all-or-nothing results, operation cancellation, credential privacy, bounded scope/diagnostics, and authoritative later mutation outcomes. |
| `CAL-EVIDENCE-016` | This permanent catalog links accepted requirements to durable implementation and executable evidence. The query retrieval entries explicitly narrow `CAL-DAV-001`; query snapshots supersede `CAL-MCP-007` pagination and `CAL-BOUND-008` result admission, while their owning sections retain the temporal and dated non-recurring To-do supersession links. Superseded evidence is replaced here rather than accumulated as a competing contract. |
