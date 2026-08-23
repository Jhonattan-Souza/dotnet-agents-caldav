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
| `CAL-OBS-005` | `CalendarTelemetryTests.ExportAllowlist_OnlyMarkedAbsenceProbeReclassifiesHttpNotFound`, `CalendarCreationModuleTests.ClosedCreateCommandsUseOnlyTheConstantWorkTransportPort`, and the exact-create stdio witness prove that only explicit absence probes reclassify 404 to `expected_absence` and Ok. |
| `CAL-OBS-007` | `CalendarTelemetryTests.ExportAllowlist_CountsRetriesAcrossIndependentRecoveredRequests`, `OpenTelemetryStdioIntegrationTests.TransientReadFailure_ExportsDistinctSafeHttpAttempts`, and `OpenTelemetryStdioIntegrationTests.ExhaustedReadRetries_KeepEveryAttemptAndOperationFailureTruthful` prove distinct failed and successful wire attempts, truthful resend counts, summed retries, recovered success, and exhausted failure without a recovery claim. |
| `CAL-OBS-009` | `CalendarExecutionPolicyTests.PublicToolFilter_EmitsParentedOperationPhaseAndSafeResultDimensions`, `CalendarExecutionPolicyTests.PublicToolFilter_UnsignalledCancellationIsControlledTimeoutFailure`, and `CalendarTelemetryTests.ExportAllowlist_RemovesPrivateLookingValuesEvenWhenLexicallyValid` prove structured and unhandled failures export only controlled code, category, phase, retryability, and error-type fields. |
| `CAL-OBS-010` | `CalendarTelemetryTests.ExportAllowlist_RemovesIdentifiersPayloadsUrlsAndExceptionDetails`, the invalid-client-dimension stdio test, and every stdio matrix witness prove allowlist privacy and zero exported exception events. |
| `CAL-OBS-011` | Every `OpenTelemetryStdioIntegrationTests` witness requires clean stdout and stderr; `StdioLoggingIntegrationTests.McpProcess_WithHangingOtlpCollector_PreservesToolResultAndTwoSecondShutdown` proves collector failure and shutdown isolation. |

The stdio witnesses parse exported OTLP protobuf, require clean stdout and
stderr, and assert that spans contain no exception events. The main opt-in and
invalid-client-dimension tests additionally search the encoded OTLP payloads
for private UIDs, hrefs, Entity Tags, cursor and MRTR state, credentials,
payload markers, trace state, exception details, and client-controlled labels.
`CAL-OBS-006` and `CAL-OBS-008` remain query-owned and are added by the query
retrieval work rather than this general telemetry branch.

## Shared performance evidence

| Requirement | Evidence |
| --- | --- |
| `CAL-EVIDENCE-011` | Focused deterministic regressions count discovery acquisitions and real PROPFIND boundaries; durations remain supporting observations only. |
| `CAL-EVIDENCE-012` | The smallest sufficient corpus is one in-scope Calendar and one Event patch. Radicale is not used because the regression is structural rather than server-dependent. |
| `CAL-EVIDENCE-013` | The [before/after report](performance-discovery-reuse-2026-08-23.md) records scenario, configuration, exact baseline and changed revisions, acquisition counts, supporting durations, and cleanup. |
| `CAL-EVIDENCE-014` | No universal benchmark platform, threshold, SLA, or process-wide cache was introduced. |
| `CAL-EVIDENCE-015` | The focused and complete suites preserve correctness, lossless Calendar evidence, operation cancellation, credential privacy, bounded scope/diagnostics, and authoritative later mutation outcomes. |
| `CAL-EVIDENCE-016` | This permanent catalog links accepted requirements to durable implementation and executable evidence. Superseded evidence should be replaced here rather than accumulated as a competing contract. |
