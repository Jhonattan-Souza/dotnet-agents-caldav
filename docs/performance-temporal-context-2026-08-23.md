# Calendar Entity Temporal Evaluation Context observation

Date: 2026-08-23
Baseline revision: `05f4973c8d4c1423da1c55cde4dbb0ee3c89b2f8`
Implementation revision: `4430b2d7943f9df1ae985bd3943642a20fd22055`
Configuration: .NET 10 Release; deterministic scripted CalDAV port for zero-I/O and semantic work counts; real MCP stdio and digest-pinned Radicale 3.7.8 for the transport boundary

## Before

A bounded Calendar Entity Start over the deterministic one-Calendar, one-candidate date-only Event corpus reached one discovery acquisition, one candidate REPORT, and one multiget operation before returning `temporal_unresolved`; it retained zero snapshots. The work therefore scaled with the selected Calendar corpus even though the missing context was already knowable from the request and deployment configuration.

## After

Missing or invalid effective context is rejected before the CalDAV query transport is constructed. Over the same corpus, `CalendarQueryModuleTests.BoundedStartWithoutTemporalContextFailsBeforeAnyCalDavWork` records zero discovery acquisitions, zero candidate REPORTs, zero multiget operations, and zero retained snapshots: an exact Before/After change of `1/1/1/0` to `0/0/0/0`. A valid caller override takes precedence over configured `Europe/London` and reports `America/Sao_Paulo` with source `caller` on both Start and Continue while Continue performs no remote or semantic work.

The focused deep-module temporal corpus covers recurring and non-recurring Events and To-dos, UTC and IANA named-zone authority, floating and named-zone spring-gap/nonexistent and autumn-overlap/ambiguous decisions, one valid authoritative resource-local VTIMEZONE, source-preserved date-only values, 23-hour and 25-hour implicit Event civil days, recurring and non-recurring date-only To-do spans, lone-DUE point inclusion, half-open boundaries, and atomic unknown or conflicting resource-zone failure. `CalendarQueryModuleTests.ExplicitTemporalContextProducesHostZoneIndependentItemsAndBytes` launches the same deep-module oracle under `TZ=UTC` and `TZ=Pacific/Kiritimati` and requires identical projected items and exact measured result bytes. These are deterministic semantic and work-count observations, not duration thresholds.

`CalendarEntityQueryPageCodecTests.ActualAccountantAdmitsExactlyFourMiBAndRejectsOneByteMore` repeats the exact 4 MiB and one-byte-over boundary with non-empty retained Temporal Evaluation Context bytes. `CalendarEntityToolsTests.ActualSdkEnvelopeMatchesTheModuleAccountantWithTemporalContext` verifies that the module's `MeasuredCallToolResultBytes` equals serialization of the real SDK `CallToolResult`, while the SDK edge matrix independently verifies below, exact, and above-limit envelopes containing the same wire context shape.

The real stdio/Radicale/OTLP witness is intentionally serialized with the repository integration gate. `OpenTelemetryStdioIntegrationTests.CalendarEntityStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork` supplies `CALDAV_EVALUATION_TIME_ZONE` to the built MCP process, observes a context-bearing bounded result and clean stdout/stderr, verifies the pinned server request boundary, and searches exported telemetry for the absence of the zone, cursor, href, UID, Entity Tag, credentials, and resource content. `CalendarMcpStdioIntegrationTests.CalendarEntityQuery_ReturnsSchemaValidSnapshotsAndTypedFailureOverStdio` verifies the raw stdio environment mapping and returned schema against the pinned Radicale fixture.

## Boundary and cleanup

No 1,200-resource exercise, benchmark framework, allocation threshold, or latency SLA is introduced. The focused unit command is `dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --filter-class '*CalendarQueryModuleTests'`; the serialized stdio/Radicale methods are named above and the complete repository gate is `bash scripts/run-test-suite.sh`. Scripted providers and timers use `await using`; the MCP process, loopback OTLP collector, and Radicale fixture are disposed by their test owners; temporal rejection retains no Query Result Snapshot.
