# Telemetry requirement evidence

This catalog records the stable behavioral witnesses for the general CalDAV
telemetry contract implemented by issue #107. It is a human-readable index,
not a requirement-to-TRX gate; behavioral tests and the installed stdio server
remain authoritative.

## Operation outcome matrix

| Operation outcome | Mutation State | Activity status | Error classification |
| --- | --- | --- | --- |
| `success` | absent, `not_attempted`, or `committed` as evidenced | Unset | absent |
| `input_required` | `not_attempted` | Unset | absent |
| `cancelled` | absent | Unset | absent |
| `error` | `not_committed`, `committed`, or `unknown` as evidenced | Error | controlled vocabulary only |

`CalendarTelemetryTests.Operation_EmitsClosedOutcomeAndIndependentMutationStateMatrix`
is the complete in-process matrix witness. `CalendarExecutionPolicyTests`
add the MRTR, caller cancellation, unsignalled timeout, structured failure, and
unhandled failure policy witnesses.

## Requirement catalog

| Requirement | Behavioral evidence |
| --- | --- |
| CAL-OBS-001 | `CalendarTelemetryTests.Operation_EmitsStableParentedPhaseWaterfallWithSafeDimensions` and `OpenTelemetryStdioIntegrationTests.OptIn_ExportsSafeParentedWaterfallLogsAndMcpMetricsOverLoopbackOtlp` prove one Operation span per tool call with safe tool, entity-kind, and parented phase dimensions. |
| CAL-OBS-002 | `CalendarTelemetryTests.Operation_EmitsClosedOutcomeAndIndependentMutationStateMatrix` proves the closed Operation outcomes, exact Activity statuses, and their independence from Mutation State. |
| CAL-OBS-003 | `CalendarTelemetryTests.Operation_StructuredCommittedFailureExportsOnlyControlledFailureDimensions` and `OpenTelemetryStdioIntegrationTests.CommittedCreateWithoutStrongRevision_ExportsControlledCommittedFailureOverStdio` prove evidence-backed Mutation State, including Error Operations that truthfully remain `committed`. The main opt-in stdio witness proves committed success. |
| CAL-OBS-004 | `CalendarExecutionPolicyTests.PublicToolFilter_MrtrInputRequiredIsExpectedControlFlow` and `OpenTelemetryStdioIntegrationTests.ExactCreateReview_ExportsExpectedAbsenceAndInputRequiredOverRawStdio` prove MRTR is `input_required`, Unset, `not_attempted`, and exception-free. |
| CAL-OBS-005 | `CalendarTelemetryTests.ExportAllowlist_OnlyMarkedAbsenceProbeReclassifiesHttpNotFound`, `CalendarCreationModuleTests.ClosedCreateCommandsUseOnlyTheConstantWorkTransportPort`, and the exact-create stdio witness prove that only explicit absence probes reclassify 404 to `expected_absence` and Ok. |
| CAL-OBS-007 | `CalendarTelemetryTests.ExportAllowlist_CountsRetriesAcrossIndependentRecoveredRequests`, `OpenTelemetryStdioIntegrationTests.TransientReadFailure_ExportsDistinctSafeHttpAttempts`, and `OpenTelemetryStdioIntegrationTests.ExhaustedReadRetries_KeepEveryAttemptAndOperationFailureTruthful` prove distinct failed and successful wire attempts, truthful resend counts, summed retries, recovered success, and exhausted failure without a recovery claim. |
| CAL-OBS-009 | `CalendarExecutionPolicyTests.PublicToolFilter_EmitsParentedOperationPhaseAndSafeResultDimensions`, `CalendarExecutionPolicyTests.PublicToolFilter_UnsignalledCancellationIsControlledTimeoutFailure`, and `CalendarTelemetryTests.ExportAllowlist_RemovesPrivateLookingValuesEvenWhenLexicallyValid` prove structured and unhandled failures export only controlled code, category, phase, retryability, and error type fields. |
| CAL-OBS-010 | `CalendarTelemetryTests.ExportAllowlist_RemovesIdentifiersPayloadsUrlsAndExceptionDetails`, the invalid-client-dimension stdio test, and every stdio matrix witness prove allowlist privacy and exception-event stripping. |
| CAL-OBS-011 | Every `OpenTelemetryStdioIntegrationTests` witness requires clean stdout and stderr; `StdioLoggingIntegrationTests.McpProcess_WithHangingOtlpCollector_PreservesToolResultAndTwoSecondShutdown` proves collector failure and shutdown isolation. |

The stdio witnesses parse exported OTLP protobuf, require clean stdout and
stderr, and assert that spans contain no exception events. The main opt-in and
invalid-client-dimension tests additionally search the encoded OTLP payloads
for private UIDs, hrefs, entity tags, cursor and MRTR state, credentials,
payload markers, trace state, exception details, and client-controlled labels.
Collector isolation remains covered by
`StdioLoggingIntegrationTests.McpProcess_WithHangingOtlpCollector_PreservesToolResultAndTwoSecondShutdown`.

CAL-OBS-006 and CAL-OBS-008 are deliberately absent: query-specific fetch
counters belong to issue #109.
