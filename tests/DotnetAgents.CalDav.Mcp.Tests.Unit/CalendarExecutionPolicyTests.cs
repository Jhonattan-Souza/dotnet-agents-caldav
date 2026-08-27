using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarExecutionPolicyTests
{
    [Fact]
    public async Task PublicToolFilter_MrtrInputRequiredIsExpectedControlFlow()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "mrtr-telemetry-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "calendar_resources.exact_move" });
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            SetMoveTelemetry(
                CalendarMoveDispatchClassification.NotAttempted,
                CalendarMoveCollisionClassification.Unspecified,
                CalendarMoveReconciliationClassification.NotRun);
            throw new InputRequiredException(new Dictionary<string, InputRequest>(), "private-state");
        });

        await Should.ThrowAsync<InputRequiredException>(() =>
            filtered(context, TestContext.Current.CancellationToken).AsTask());

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operation.GetTagItem("caldav.outcome").ShouldBe("input_required");
        operation.GetTagItem("caldav.mutation.state").ShouldBe("not_attempted");
        operation.GetTagItem("caldav.move.dispatch").ShouldBe("not_attempted");
        operation.GetTagItem("caldav.move.collision").ShouldBeNull();
        operation.GetTagItem("caldav.move.reconciliation").ShouldBe("not_run");
        operation.GetTagItem("error.type").ShouldBeNull();
        operation.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unspecified")]
    [InlineData("not_attempted")]
    [InlineData("rejected")]
    [InlineData("dispatched")]
    [InlineData("possibly_dispatched")]
    public async Task PublicToolFilter_ExactMoveCallerCancellationPreservesTruthfulMoveProgress(string progress)
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "exact-move-cancellation-telemetry-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "calendar_resources.exact_move" });
        using var callerCancellation = new CancellationTokenSource();
        var scenario = ExactMoveCancellationProgress[progress];
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            if (scenario.Dispatch is { } dispatch)
                SetMoveTelemetry(
                    dispatch,
                    scenario.Collision,
                    scenario.Reconciliation);
            callerCancellation.Cancel();
            throw new OperationCanceledException(callerCancellation.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            filtered(context, callerCancellation.Token).AsTask());

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operation.GetTagItem("caldav.outcome").ShouldBe("cancelled");
        operation.GetTagItem("caldav.mutation.state").ShouldBe(scenario.ExpectedMutationState);
        operation.GetTagItem("caldav.move.dispatch").ShouldBe(scenario.ExpectedDispatch);
        operation.GetTagItem("caldav.move.collision").ShouldBe(scenario.ExpectedCollision);
        operation.GetTagItem("caldav.move.reconciliation").ShouldBe(scenario.ExpectedReconciliation);
        operation.GetTagItem("error.type").ShouldBeNull();
        operation.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    private static void SetMoveTelemetry(
        CalendarMoveDispatchClassification dispatch,
        CalendarMoveCollisionClassification collision,
        CalendarMoveReconciliationClassification reconciliation)
    {
        switch (dispatch)
        {
            case CalendarMoveDispatchClassification.NotAttempted:
                InvokeProgressSetter("SetMoveNotAttempted", collision);
                break;
            case CalendarMoveDispatchClassification.Rejected:
                InvokeProgressSetter("SetMoveRejected", collision);
                break;
            case CalendarMoveDispatchClassification.Dispatched:
                InvokeProgressSetter("SetMoveDispatched");
                InvokeProgressSetter("SetMoveReconciliation", reconciliation);
                break;
            case CalendarMoveDispatchClassification.PossiblyDispatched:
                InvokeProgressSetter("SetMovePossiblyDispatched");
                InvokeProgressSetter("SetMoveReconciliation", reconciliation);
                break;
        }
    }

    private static IReadOnlyDictionary<string, ExactMoveCancellationScenario> ExactMoveCancellationProgress { get; } =
        new Dictionary<string, ExactMoveCancellationScenario>(StringComparer.Ordinal)
        {
            ["none"] = new(null, default, default, "not_attempted", null, "not_run", "not_attempted"),
            ["unspecified"] = new(
                CalendarMoveDispatchClassification.Unspecified,
                CalendarMoveCollisionClassification.Unspecified,
                CalendarMoveReconciliationClassification.NotRun,
                "not_attempted",
                null,
                "not_run",
                "not_attempted"),
            ["not_attempted"] = new(
                CalendarMoveDispatchClassification.NotAttempted,
                CalendarMoveCollisionClassification.Unspecified,
                CalendarMoveReconciliationClassification.NotRun,
                "not_attempted",
                null,
                "not_run",
                "not_attempted"),
            ["rejected"] = new(
                CalendarMoveDispatchClassification.Rejected,
                CalendarMoveCollisionClassification.DestinationHref,
                CalendarMoveReconciliationClassification.NotRun,
                "rejected",
                "destination_href",
                "not_run",
                null),
            ["dispatched"] = new(
                CalendarMoveDispatchClassification.Dispatched,
                CalendarMoveCollisionClassification.None,
                CalendarMoveReconciliationClassification.NotRun,
                "dispatched",
                "none",
                "not_run",
                null),
            ["possibly_dispatched"] = new(
                CalendarMoveDispatchClassification.PossiblyDispatched,
                CalendarMoveCollisionClassification.None,
                CalendarMoveReconciliationClassification.ObservationUnavailable,
                "possibly_dispatched",
                "none",
                "observation_unavailable",
                null)
        };

    private sealed record ExactMoveCancellationScenario(
        CalendarMoveDispatchClassification? Dispatch,
        CalendarMoveCollisionClassification Collision,
        CalendarMoveReconciliationClassification Reconciliation,
        string ExpectedDispatch,
        string? ExpectedCollision,
        string ExpectedReconciliation,
        string? ExpectedMutationState);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicToolFilter_ExactMoveUnhandledFaultIsPrivateAndDoesNotChangeControlFlow(
        bool withTelemetry)
    {
        var stopped = new List<Activity>();
        using var listener = withTelemetry ? new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        } : null;
        if (listener is not null)
            ActivitySource.AddActivityListener(listener);
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "exact-move-fault-telemetry-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "calendar_resources.exact_move" });
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
            throw new IOException("private exact move transport detail"));

        await Should.ThrowAsync<IOException>(() =>
            filtered(context, TestContext.Current.CancellationToken).AsTask());

        if (withTelemetry)
        {
            var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
            operation.GetTagItem("caldav.outcome").ShouldBe("error");
            operation.GetTagItem("error.type").ShouldBe("connection_error");
            operation.Tags.ShouldNotContain(tag =>
                string.Equals(tag.Value, "private exact move transport detail", StringComparison.Ordinal));
            operation.Status.ShouldBe(ActivityStatusCode.Error);
        }
        else
        {
            stopped.ShouldBeEmpty();
        }
    }

    private static void InvokeProgressSetter<T>(string name, T value) where T : struct =>
        typeof(CalendarOperationProgress)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [value]);

    private static void InvokeProgressSetter(string name) =>
        typeof(CalendarOperationProgress)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);

    [Fact]
    public async Task PublicToolFilter_UnsignalledCancellationIsControlledTimeoutFailure()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "unsignalled-cancellation-telemetry-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "calendars.list" });
        var filtered = CalendarExecutionPolicy.CallTool((_, _) => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            filtered(context, CancellationToken.None).AsTask());

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operation.GetTagItem("caldav.outcome").ShouldBe("error");
        operation.GetTagItem("error.type").ShouldBe("timeout");
        operation.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task PublicToolFilter_EmitsParentedOperationPhaseAndSafeResultDimensions()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DotnetAgents.CalDav",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var mcpActivity = new Activity("tools/call").Start();
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "execution-policy-telemetry-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "todos.complete" });
        var result = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                code = "conflict",
                category = "state",
                phase = "targetRevision",
                retryable = false,
                mutationState = "not_committed"
            })
        };

        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            CalendarTelemetry.ObserveStructuredError(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.Conflict,
                CalendarTelemetryErrorCategory.State,
                CalendarTelemetryErrorPhase.TargetRevision,
                Retryable: false));
            CalendarTelemetry.ObserveMutationState(CalendarMutationState.NotCommitted);
            return ValueTask.FromResult(result);
        });
        await filtered(context, TestContext.Current.CancellationToken);

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        var discovery = stopped.Single(activity => activity.OperationName == "caldav.phase.discovery");
        operation.ParentId.ShouldBe(mcpActivity.Id);
        discovery.ParentId.ShouldBe(operation.Id);
        operation.GetTagItem("caldav.tool.name").ShouldBe("todos.complete");
        operation.GetTagItem("caldav.entity.kind").ShouldBe("todo");
        operation.GetTagItem("caldav.outcome").ShouldBe("error");
        operation.GetTagItem("caldav.error.code").ShouldBe("conflict");
        operation.GetTagItem("caldav.error.category").ShouldBe("state");
        operation.GetTagItem("caldav.error.phase").ShouldBe("targetRevision");
        operation.GetTagItem("caldav.error.retryable").ShouldBe(false);
        operation.GetTagItem("caldav.mutation.state").ShouldBe("not_committed");
        operation.GetTagItem("error.type").ShouldBe("caldav.conflict");
        operation.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task PublicToolFilter_UsesOnlyReportedClosedOptionalFacts()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "execution-policy-result-dimensions",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = "todos.complete" });
        var cases = new (CalendarStructuredErrorFacts? Error, CalendarMutationState? Mutation)[]
        {
            (null, null),
            (new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.Busy,
                CalendarTelemetryErrorCategory.LimitsAndAdmission,
                CalendarTelemetryErrorPhase.AdmissionAndPayload,
                Retryable: true), CalendarMutationState.NotAttempted),
            (new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.Conflict,
                CalendarTelemetryErrorCategory.State,
                CalendarTelemetryErrorPhase.Execution,
                Retryable: false), CalendarMutationState.NotCommitted),
            (null, CalendarMutationState.Committed),
            (null, CalendarMutationState.Unknown)
        };

        foreach (var @case in cases)
        {
            var result = new CallToolResult
            {
                IsError = true,
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    code = "private_untrusted_result",
                    mutationState = "private_untrusted_state"
                })
            };
            var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
            {
                if (@case.Error is { } error)
                    CalendarTelemetry.ObserveStructuredError(error);
                if (@case.Mutation is { } mutation)
                    CalendarTelemetry.ObserveMutationState(mutation);
                return ValueTask.FromResult(result);
            });
            await filtered(context, CancellationToken.None);
        }

        var operations = stopped.Where(activity => activity.OperationName == "caldav.operation").ToArray();
        operations.Length.ShouldBe(cases.Length);
        operations[0].GetTagItem("caldav.error.retryable").ShouldBeNull();
        operations[0].GetTagItem("caldav.mutation.state").ShouldBeNull();
        operations[1].GetTagItem("caldav.error.retryable").ShouldBe(true);
        operations[1].GetTagItem("caldav.mutation.state").ShouldBe("not_attempted");
        operations[2].GetTagItem("caldav.error.retryable").ShouldBe(false);
        operations[2].GetTagItem("caldav.mutation.state").ShouldBe("not_committed");
        operations[3].GetTagItem("caldav.error.code").ShouldBeNull();
        operations[3].GetTagItem("caldav.mutation.state").ShouldBe("committed");
        operations[4].GetTagItem("caldav.mutation.state").ShouldBe("unknown");
    }

    [Fact]
    public void PublicToolFilter_HasNoJsonTelemetryTruthReconstructionHelpers()
    {
        var helperNames = typeof(CalendarExecutionPolicy)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();

        helperNames.ShouldNotContain("StringProperty");
        helperNames.ShouldNotContain("SafeMutationState");
        helperNames.ShouldNotContain("SafeBoolean");
        typeof(TelemetryActivityAllowlistProcessor)
            .GetMethod("ClassifyHttpObservation", BindingFlags.Static | BindingFlags.NonPublic)
            .ShouldBeNull();
        var processorHelpers = typeof(TelemetryActivityAllowlistProcessor)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        processorHelpers.ShouldNotContain("ObserveRetry");
        processorHelpers.ShouldNotContain("FindOperation");
        processorHelpers.ShouldNotContain("GetRetryAggregation");
        processorHelpers.ShouldNotContain("ApplyRetryAggregation");
        typeof(TelemetryActivityAllowlistProcessor)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Select(type => type.Name)
            .ShouldNotContain(name => name.Contains("RetryAggregation", StringComparison.Ordinal));
        foreach (var queryTool in new[]
                 {
                     typeof(CalendarEntityTools),
                     typeof(CalendarOccurrenceTools),
                     typeof(CalendarTodoTools)
                 })
        {
            var queryHelpers = queryTool
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Select(method => method.Name)
                .ToArray();
            queryHelpers.ShouldNotContain("Code");
            queryHelpers.ShouldNotContain("Category");
            queryHelpers.ShouldNotContain("Phase");
            queryHelpers.ShouldNotContain("ErrorWithoutBounding");
            queryHelpers.ShouldNotContain("CreatePayloadLimitError");
        }
        var exactWriteHelpers = typeof(ExactCalendarResourceWriteTools)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();
        exactWriteHelpers.ShouldNotContain("EnsureBoundedResult");
        exactWriteHelpers.ShouldNotContain("Phase");
        var collectionHelpers = typeof(CalendarCollectionTools)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        collectionHelpers.ShouldNotContain("Describe");
        collectionHelpers.ShouldNotContain("MapHttpCode");
        collectionHelpers.ShouldNotContain("MutationState");
        typeof(CalendarResourceTools)
            .GetMethod("CreateBoundedSuccess", BindingFlags.Static | BindingFlags.NonPublic)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("calendars.list")]
    [InlineData("calendar_resources.get")]
    [InlineData("calendar_resources.exact_get")]
    public async Task PublicToolFilter_StopsEveryReadAtThirtySecondsWithTypedZeroItemFailure(
        string toolName)
    {
        var result = await InvokeAfterElapsedAsync(
            toolName,
            TimeSpan.FromSeconds(30),
            completeOperation: false);
        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("dimension").GetString()
            .ShouldBe("elapsed_time");
        result.StructuredContent.Value.TryGetProperty("mutationState", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("events.create")]
    [InlineData("calendars.create")]
    [InlineData("calendars.delete")]
    [InlineData("events.patch")]
    [InlineData("todos.create")]
    [InlineData("todos.patch")]
    [InlineData("todos.complete")]
    [InlineData("calendar_occurrences.add")]
    [InlineData("calendar_occurrences.exclude")]
    [InlineData("calendar_occurrences.restore_exclusion")]
    [InlineData("calendar_occurrences.cancel")]
    [InlineData("calendar_occurrences.restore_cancellation")]
    [InlineData("calendar_resources.move")]
    [InlineData("calendar_resources.delete")]
    [InlineData("calendar_resources.exact_create")]
    [InlineData("calendar_resources.exact_replace")]
    [InlineData("calendar_resources.exact_move")]
    public async Task PublicToolFilter_StopsEveryMutationAtSixtySecondsWithConservativeUnknownState(
        string toolName)
    {
        var result = await InvokeAfterElapsedAsync(
            toolName,
            TimeSpan.FromSeconds(60),
            completeOperation: false);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("dimension").GetString()
            .ShouldBe("elapsed_time");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task PublicToolFilter_ReadDeadline_ProvesBelowAtAndAboveBoundary()
    {
        var below = await InvokeAfterElapsedAsync(
            "calendars.list",
            TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1),
            completeOperation: true);
        var at = await InvokeAfterElapsedAsync(
            "calendars.list",
            TimeSpan.FromSeconds(30),
            completeOperation: false);
        var above = await InvokeAfterElapsedAsync(
            "calendars.list",
            TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1),
            completeOperation: false);

        below.IsError.ShouldBe(false);
        AssertElapsedTimeDeadline(at, mutation: false);
        AssertElapsedTimeDeadline(above, mutation: false);
    }

    [Theory]
    [InlineData("calendar_entities.query")]
    [InlineData("calendar_occurrences.query")]
    [InlineData("todos.query")]
    public async Task MigratedSnapshotQueryHasNoHostDeadlineOrLegacyPhase(string toolName)
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);

        var result = await InvokeAfterElapsedAsync(
            toolName,
            TimeSpan.FromSeconds(31),
            completeOperation: true);

        result.IsError.ShouldBe(false);
        stopped.ShouldNotContain(activity => activity.OperationName.StartsWith("caldav.phase.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicToolFilter_MutationDeadline_ProvesBelowAtAndAboveBoundary()
    {
        var below = await InvokeAfterElapsedAsync(
            "events.create",
            TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1),
            completeOperation: true);
        var at = await InvokeAfterElapsedAsync(
            "events.create",
            TimeSpan.FromSeconds(60),
            completeOperation: false);
        var above = await InvokeAfterElapsedAsync(
            "events.create",
            TimeSpan.FromSeconds(60) + TimeSpan.FromTicks(1),
            completeOperation: false);

        below.IsError.ShouldBe(false);
        AssertElapsedTimeDeadline(at, mutation: true);
        AssertElapsedTimeDeadline(above, mutation: true);
    }

    [Theory]
    [InlineData("calendars.list")]
    [InlineData("calendars.create")]
    [InlineData("calendars.delete")]
    [InlineData("calendar_entities.query")]
    [InlineData("calendar_occurrences.query")]
    [InlineData("todos.query")]
    [InlineData("calendar_resources.get")]
    [InlineData("calendar_resources.exact_get")]
    [InlineData("events.create")]
    [InlineData("events.patch")]
    [InlineData("todos.create")]
    [InlineData("todos.patch")]
    [InlineData("todos.complete")]
    [InlineData("calendar_occurrences.add")]
    [InlineData("calendar_occurrences.exclude")]
    [InlineData("calendar_occurrences.restore_exclusion")]
    [InlineData("calendar_occurrences.cancel")]
    [InlineData("calendar_occurrences.restore_cancellation")]
    [InlineData("calendar_resources.move")]
    [InlineData("calendar_resources.delete")]
    [InlineData("calendar_resources.exact_create")]
    [InlineData("calendar_resources.exact_replace")]
    [InlineData("calendar_resources.exact_move")]
    public async Task PublicToolFilter_PropagatesCallerCancellationBeforeEveryOperationDeadline(
        string toolName)
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(admission)
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "execution-policy-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = toolName });
        using var callerCancellation = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filtered = CalendarExecutionPolicy.CallTool(async (_, token) =>
        {
            var completion = new TaskCompletionSource<CallToolResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            operationStarted.SetResult();
            return await completion.Task;
        });

        var pending = filtered(context, callerCancellation.Token).AsTask();
        await operationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        callerCancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(pending);
        var nextStarted = false;
        var nextContext = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(2L), Method = "tools/call" },
            new CallToolRequestParams { Name = toolName });
        var nextFilter = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            nextStarted = true;
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        });

        var next = await nextFilter(nextContext, TestContext.Current.CancellationToken);

        next.IsError.ShouldBe(false);
        nextStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task PublicToolFilter_MutationQueueIsFifoAndTheSeventeenthWaiterReceivesBusy()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        using var services = CreateServices(time, admission);
        await using var transport = CreateTransport("mutation-fifo-test");
        await using var server = CreateServer(transport, services);
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = InvokeFilter(server, "events.create", 1, _ =>
        {
            activeEntered.TrySetResult();
            return new ValueTask<CallToolResult>(releaseActive.Task);
        });
        await activeEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var executionOrder = new List<int>();
        var queued = Enumerable.Range(1, CalendarOperationAdmission.MaximumQueuedOperations)
            .Select(index => InvokeFilter(server, "events.create", index + 1, _ =>
            {
                executionOrder.Add(index);
                return ValueTask.FromResult(new CallToolResult { IsError = false });
            }))
            .ToArray();
        var overflow = await InvokeFilter(server, "events.create", 99, _ =>
            ValueTask.FromResult(new CallToolResult { IsError = false }));

        overflow.IsError.ShouldBe(true);
        overflow.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        overflow.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        releaseActive.SetResult(new CallToolResult { IsError = false });

        await active;
        await Task.WhenAll(queued);
        executionOrder.ShouldBe(Enumerable.Range(1, CalendarOperationAdmission.MaximumQueuedOperations));
    }

    [Fact]
    public async Task PublicToolFilter_ReturnsBusyAfterTwoSecondReadAdmissionTimeoutWithoutDispatchingTheWaiter()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        using var services = CreateServices(time, admission);
        await using var transport = CreateTransport("read-admission-timeout-test");
        await using var server = CreateServer(transport, services);
        var release = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = Enumerable.Range(1, CalendarOperationAdmission.MaximumConcurrentOperations)
            .Select(id => InvokeFilter(server, "calendars.list", id, _ => new ValueTask<CallToolResult>(release.Task)))
            .ToArray();
        var dispatched = false;
        var waiter = InvokeFilter(server, "calendars.list", 99, _ =>
        {
            dispatched = true;
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        });

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await waiter;

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        dispatched.ShouldBeFalse();
        release.SetResult(new CallToolResult { IsError = false });
        await Task.WhenAll(active);
    }

    [Fact]
    public async Task PublicToolFilter_CancelledMutationWaiterCannotReleaseTheActiveMutation()
    {
        var admission = new CalendarOperationAdmission(TimeProvider.System);
        using var services = CreateServices(TimeProvider.System, admission);
        await using var transport = CreateTransport("mutation-waiter-cancellation-test");
        await using var server = CreateServer(transport, services);
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = InvokeFilter(server, "events.create", 1, _ =>
        {
            activeEntered.TrySetResult();
            return new ValueTask<CallToolResult>(releaseActive.Task);
        });
        await activeEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancelledContext = CreateFilterContext(server, "events.create", 2);
        var cancelledFilter = CalendarExecutionPolicy.CallTool((_, _) =>
            ValueTask.FromResult(new CallToolResult { IsError = false }));
        var cancelledWaiter = cancelledFilter(cancelledContext, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(cancelledWaiter);

        var laterStarted = false;
        var later = InvokeFilter(server, "events.create", 3, _ =>
        {
            laterStarted = true;
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        });
        laterStarted.ShouldBeFalse();
        releaseActive.SetResult(new CallToolResult { IsError = false });

        await active;
        await later;
        laterStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task PublicToolFilter_ActiveMutationExceptionReleasesTheSlotForLaterMutation()
    {
        var admission = new CalendarOperationAdmission(TimeProvider.System);
        using var services = CreateServices(TimeProvider.System, admission);
        await using var transport = CreateTransport("mutation-exception-cleanup-test");
        await using var server = CreateServer(transport, services);

        var failed = InvokeFilter(server, "events.create", 1, _ =>
            ValueTask.FromException<CallToolResult>(new IOException("private transport detail")));
        await Should.ThrowAsync<IOException>(failed);

        var laterStarted = false;
        var later = await InvokeFilter(server, "events.create", 2, _ =>
        {
            laterStarted = true;
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        });

        later.IsError.ShouldBe(false);
        laterStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task RequestedProgress_StartsAtFiveHundredMillisecondsAndReportsTruthfulAggregatePhaseChanges()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var reports = new List<ProgressNotificationValue>();
        using var cancellation = new CancellationTokenSource();
        await using var execution = await CalendarExecutionPolicy.AcquireWithProgressAsync(
            time,
            admission,
            mutation: false,
            (value, _) =>
            {
                reports.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token);
        execution.Lease.ShouldNotBeNull();

        time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Yield();
        reports.ShouldBeEmpty();
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForAsync(() => reports.Count == 1);
        execution.SetPhase(CalendarOperationPhase.Fetch);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitForAsync(() => reports.Count == 2);
        execution.SetPhase(CalendarOperationPhase.Filter);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitForAsync(() => reports.Count == 3);
        execution.SetPhase(CalendarOperationPhase.Expand);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitForAsync(() => reports.Count == 4);
        execution.SetPhase(CalendarOperationPhase.Reconcile);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitForAsync(() => reports.Count == 5);

        reports.Count.ShouldBe(5);
        reports.Select(report => report.Progress).ShouldBe([1, 2, 3, 4, 5]);
        reports.Select(report => report.Message).ShouldBe([
            "discovery",
            "fetch",
            "filter",
            "expand",
            "reconcile"]);
        cancellation.Cancel();
    }

    [Fact]
    public async Task RequestedProgress_IsCappedAtFourNotificationsPerSecondAcrossFourConcurrentCalls()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var reports = new List<ProgressNotificationValue>();
        using var cancellation = new CancellationTokenSource();
        var reporting = Enumerable.Range(0, 4).Select(_ => CalendarExecutionPolicy.ReportProgressAsync(
            time,
            admission,
            (value, _) =>
            {
                reports.Add(value);
                return Task.CompletedTask;
            },
            cancellation.Token)).ToArray();

        time.Advance(TimeSpan.FromMilliseconds(500));
        await WaitForAsync(() => reports.Count == 1);
        time.Advance(TimeSpan.FromMilliseconds(750));
        await WaitForAsync(() => reports.Count == 4);

        reports.Count.ShouldBe(4);
        cancellation.Cancel();
        foreach (var task in reporting)
            await Should.ThrowAsync<OperationCanceledException>(task);
    }

    [Fact]
    public async Task QueuedCallSuppressesAdmissionProgressBeforeBusyAtTwoSeconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var active = new List<CalendarOperationAdmission.Lease>();
        for (var index = 0; index < CalendarOperationAdmission.MaximumConcurrentOperations; index++)
            active.Add((await admission.AcquireAsync(mutation: false, CancellationToken.None))!);
        var reports = new List<ProgressNotificationValue>();
        var pending = CalendarExecutionPolicy.AcquireWithProgressAsync(
            time,
            admission,
            mutation: false,
            (value, _) =>
            {
                reports.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Yield();
        reports.ShouldBeEmpty();
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        reports.ShouldBeEmpty();
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1500));
        await using var execution = await pending;

        execution.Lease.ShouldBeNull();
        reports.ShouldBeEmpty();
        foreach (var lease in active)
            lease.Dispose();
    }

    [Fact]
    public async Task ProtectedResourceRead_SharesTheFourOperationAdmissionLimitWithTools()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var activeTools = new List<CalendarOperationAdmission.Lease>();
        for (var index = 0; index < CalendarOperationAdmission.MaximumConcurrentOperations; index++)
            activeTools.Add((await admission.AcquireAsync(mutation: false, CancellationToken.None))!);
        var serviceCalled = false;
        var protectedRead = CalendarExecutionPolicy.ExecuteProtectedReadAsync(
            time,
            admission,
            _ =>
            {
                serviceCalled = true;
                return Task.FromResult(42);
            },
            CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(2));
        var exception = await Should.ThrowAsync<InvalidOperationException>(protectedRead);

        exception.Message.ShouldBe("The configured Calendar origin is busy.");
        serviceCalled.ShouldBeFalse();
        foreach (var lease in activeTools)
            lease.Dispose();
    }

    [Fact]
    public async Task ProtectedResourceRead_StopsAtThirtySecondsAndReleasesItsAdmissionLease()
    {
        var at = await InvokeProtectedReadDeadlineAsync(TimeSpan.FromSeconds(30));
        var above = await InvokeProtectedReadDeadlineAsync(
            TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1));

        at.Message.ShouldBe("The protected Calendar resource read exceeded its execution budget.");
        above.Message.ShouldBe("The protected Calendar resource read exceeded its execution budget.");
    }

    [Fact]
    public async Task ProtectedResourceRead_AllowsCompletionImmediatelyBelowThirtySeconds()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedRead = CalendarExecutionPolicy.ExecuteProtectedReadAsync(
            time,
            admission,
            async token =>
            {
                operationStarted.SetResult();
                return await completion.Task.WaitAsync(token);
            },
            CancellationToken.None);

        await operationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        protectedRead.IsCompleted.ShouldBeFalse();
        completion.SetResult(42);

        (await protectedRead).ShouldBe(42);
        using var next = await admission.AcquireAsync(mutation: false, CancellationToken.None);
        next.ShouldNotBeNull();
    }

    [Fact]
    public void MutationClassification_CoversEveryFrozenTool()
    {
        var reads = new[]
        {
            "calendars.list", "calendar_entities.query", "calendar_occurrences.query",
            "todos.query", "calendar_resources.get", "calendar_resources.exact_get"
        };
        var mutations = new[]
        {
            "calendars.create", "calendars.delete",
            "events.create", "events.patch", "todos.create", "todos.patch", "todos.complete",
            "calendar_occurrences.add", "calendar_occurrences.exclude",
            "calendar_occurrences.restore_exclusion", "calendar_occurrences.cancel",
            "calendar_occurrences.restore_cancellation", "calendar_resources.move",
            "calendar_resources.delete", "calendar_resources.exact_create",
            "calendar_resources.exact_replace", "calendar_resources.exact_move"
        };

        reads.ShouldAllBe(toolName => !CalendarExecutionPolicy.IsMutation(toolName));
        mutations.ShouldAllBe(toolName => CalendarExecutionPolicy.IsMutation(toolName));
        (reads.Length + mutations.Length).ShouldBe(23);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BusyResult_IncludesMutationStateOnlyForMutationTools(bool mutation, bool expectedMutationState)
    {
        var result = CalendarExecutionPolicy.CreateBusyResult(mutation);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent.ShouldNotBeNull();
        structured.GetProperty("code").GetString().ShouldBe("busy");
        structured.TryGetProperty("mutationState", out var mutationState).ShouldBe(expectedMutationState);
        if (expectedMutationState)
            mutationState.GetString().ShouldBe("not_attempted");
    }

    private static ServiceProvider CreateServices(TimeProvider timeProvider, CalendarOperationAdmission admission) =>
        new ServiceCollection()
            .AddSingleton(timeProvider)
            .AddSingleton(admission)
            .BuildServiceProvider();

    private static StreamServerTransport CreateTransport(string name) => new(
        new MemoryStream(),
        new MemoryStream(),
        name,
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static McpServer CreateServer(StreamServerTransport transport, IServiceProvider services) =>
        McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);

    private static Task<CallToolResult> InvokeFilter(
        McpServer server,
        string toolName,
        long requestId,
        Func<CancellationToken, ValueTask<CallToolResult>> next)
    {
        return CalendarExecutionPolicy.CallTool((_, token) => next(token))(
            CreateFilterContext(server, toolName, requestId),
            TestContext.Current.CancellationToken).AsTask();
    }

    private static RequestContext<CallToolRequestParams> CreateFilterContext(
        McpServer server,
        string toolName,
        long requestId) => new(
        server,
        new JsonRpcRequest { Id = new RequestId(requestId), Method = "tools/call" },
        new CallToolRequestParams { Name = toolName });

    private static async Task<CallToolResult> InvokeAfterElapsedAsync(
        string toolName,
        TimeSpan elapsed,
        bool completeOperation)
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<CalendarOperationAdmission>()
            .BuildServiceProvider();
        await using var transport = new StreamServerTransport(
            new MemoryStream(),
            new MemoryStream(),
            "execution-policy-test",
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await using var server = McpServer.Create(
            transport,
            new McpServerOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
            new CallToolRequestParams { Name = toolName });
        using var callerCancellation = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CallToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var filtered = CalendarExecutionPolicy.CallTool(async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                deadlineObserved.TrySetResult();
                completion.TrySetCanceled(token);
            });
            operationStarted.SetResult();
            return await completion.Task;
        });

        var pending = filtered(context, callerCancellation.Token).AsTask();
        await operationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        context.Services!.GetRequiredService<TimeProvider>().ShouldBeSameAs(time);
        time.TimerCount.ShouldBe(toolName is "calendar_entities.query" or "calendar_occurrences.query" or "todos.query"
            ? 0
            : 1);
        time.Advance(elapsed);
        if (completeOperation)
        {
            completion.TrySetResult(new CallToolResult { IsError = false });
        }
        else
        {
            await deadlineObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        return await pending;
    }

    private static async Task<TimeoutException> InvokeProtectedReadDeadlineAsync(TimeSpan elapsed)
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedRead = CalendarExecutionPolicy.ExecuteProtectedReadAsync(
            time,
            admission,
            async token =>
            {
                var completion = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() =>
                {
                    deadlineObserved.TrySetResult();
                    completion.TrySetCanceled(token);
                });
                operationStarted.SetResult();
                return await completion.Task;
            },
            CancellationToken.None);

        await operationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(elapsed);
        await deadlineObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        var exception = await Should.ThrowAsync<TimeoutException>(protectedRead);
        using var next = await admission.AcquireAsync(mutation: false, CancellationToken.None);
        next.ShouldNotBeNull();
        return exception;
    }

    private static void AssertElapsedTimeDeadline(CallToolResult result, bool mutation)
    {
        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("limits").GetProperty("dimension").GetString()
            .ShouldBe("elapsed_time");
        if (mutation)
            structured.GetProperty("mutationState").GetString().ShouldBe("unknown");
        else
            structured.TryGetProperty("mutationState", out _).ShouldBeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Yield();
        condition().ShouldBeTrue();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public int TimerCount => _timers.Count;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
