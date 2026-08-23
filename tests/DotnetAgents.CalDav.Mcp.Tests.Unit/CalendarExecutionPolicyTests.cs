using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using System.Diagnostics;
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
            new CallToolRequestParams { Name = "calendar_resources.delete" });
        var filtered = CalendarExecutionPolicy.CallTool((_, _) => throw new InputRequiredException(
            new Dictionary<string, InputRequest>(),
            "private-state"));

        await Should.ThrowAsync<InputRequiredException>(() =>
            filtered(context, TestContext.Current.CancellationToken).AsTask());

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operation.GetTagItem("caldav.outcome").ShouldBe("input_required");
        operation.GetTagItem("caldav.mutation.state").ShouldBe("not_attempted");
        operation.GetTagItem("error.type").ShouldBeNull();
        operation.Status.ShouldBe(ActivityStatusCode.Unset);
    }

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

        var filtered = CalendarExecutionPolicy.CallTool((_, _) => ValueTask.FromResult(result));
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
    public async Task PublicToolFilter_NormalizesMalformedAndClosedOptionalResultDimensions()
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
        JsonElement?[] structuredCases =
        [
            null,
            JsonSerializer.SerializeToElement(Array.Empty<object>()),
            JsonSerializer.SerializeToElement(new
            {
                code = 1,
                category = true,
                phase = Array.Empty<object>(),
                retryable = "private",
                mutationState = "private"
            }),
            JsonSerializer.SerializeToElement(new { retryable = true, mutationState = "not_attempted" }),
            JsonSerializer.SerializeToElement(new { retryable = false, mutationState = "not_committed" }),
            JsonSerializer.SerializeToElement(new { mutationState = "committed" }),
            JsonSerializer.SerializeToElement(new { mutationState = "unknown" })
        ];

        foreach (var structured in structuredCases)
        {
            var result = new CallToolResult { IsError = true, StructuredContent = structured };
            var filtered = CalendarExecutionPolicy.CallTool((_, _) => ValueTask.FromResult(result));
            await filtered(context, CancellationToken.None);
        }

        var operations = stopped.Where(activity => activity.OperationName == "caldav.operation").ToArray();
        operations.Length.ShouldBe(structuredCases.Length);
        operations[0].GetTagItem("caldav.error.retryable").ShouldBeNull();
        operations[1].GetTagItem("caldav.mutation.state").ShouldBeNull();
        operations[2].GetTagItem("caldav.error.code").ShouldBeNull();
        operations[3].GetTagItem("caldav.error.retryable").ShouldBe(true);
        operations[3].GetTagItem("caldav.mutation.state").ShouldBe("not_attempted");
        operations[4].GetTagItem("caldav.error.retryable").ShouldBe(false);
        operations[4].GetTagItem("caldav.mutation.state").ShouldBe("not_committed");
        operations[5].GetTagItem("caldav.mutation.state").ShouldBe("committed");
        operations[6].GetTagItem("caldav.mutation.state").ShouldBe("unknown");
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
        using var next = await admission.AcquireAsync(
            mutation: CalendarExecutionPolicy.IsMutation(toolName),
            CancellationToken.None);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task Admission_EnforcesFourOperationsOneMutationAndOneSharedFifoQueue()
    {
        var admission = new CalendarOperationAdmission(TimeProvider.System);
        using var mutation = (await admission.AcquireAsync(mutation: true, CancellationToken.None))!;
        using var readOne = (await admission.AcquireAsync(mutation: false, CancellationToken.None))!;
        using var readTwo = (await admission.AcquireAsync(mutation: false, CancellationToken.None))!;
        using var readThree = (await admission.AcquireAsync(mutation: false, CancellationToken.None))!;
        var secondMutation = admission.AcquireAsync(mutation: true, CancellationToken.None).AsTask();
        var queuedRead = admission.AcquireAsync(mutation: false, CancellationToken.None).AsTask();

        readOne.Dispose();
        await Task.Yield();
        secondMutation.IsCompleted.ShouldBeFalse();
        queuedRead.IsCompleted.ShouldBeFalse();

        mutation.Dispose();
        using var admittedMutation = await secondMutation;
        admittedMutation.ShouldNotBeNull();
        using var admittedRead = await queuedRead;
        admittedRead.ShouldNotBeNull();
    }

    [Fact]
    public async Task Admission_QueuesExactlySixteenCallsAndRejectsTheSeventeenth()
    {
        var admission = new CalendarOperationAdmission(TimeProvider.System);
        var active = new List<CalendarOperationAdmission.Lease>();
        for (var index = 0; index < CalendarOperationAdmission.MaximumConcurrentOperations; index++)
            active.Add((await admission.AcquireAsync(mutation: false, CancellationToken.None))!);
        var queued = Enumerable.Range(0, CalendarOperationAdmission.MaximumQueuedOperations)
            .Select(_ => admission.AcquireAsync(mutation: false, CancellationToken.None).AsTask())
            .ToArray();

        var overflow = await admission.AcquireAsync(mutation: false, CancellationToken.None);

        overflow.ShouldBeNull();
        queued.ShouldAllBe(task => !task.IsCompleted);
        foreach (var lease in active)
            lease.Dispose();
        foreach (var pending in queued)
            (await pending)!.Dispose();
    }

    [Fact]
    public async Task Admission_ReturnsBusyAtTwoSecondsWithoutSleeping()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarOperationAdmission(time);
        var active = new List<CalendarOperationAdmission.Lease>();
        for (var index = 0; index < CalendarOperationAdmission.MaximumConcurrentOperations; index++)
            active.Add((await admission.AcquireAsync(mutation: false, CancellationToken.None))!);
        var pending = admission.AcquireAsync(mutation: false, CancellationToken.None).AsTask();

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await pending;

        result.ShouldBeNull();
        foreach (var lease in active)
            lease.Dispose();
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
            "calendar_resources.get", "calendar_resources.exact_get"
        };
        var mutations = new[]
        {
            "events.create", "events.patch", "todos.create", "todos.patch", "todos.complete",
            "calendar_occurrences.add", "calendar_occurrences.exclude",
            "calendar_occurrences.restore_exclusion", "calendar_occurrences.cancel",
            "calendar_occurrences.restore_cancellation", "calendar_resources.move",
            "calendar_resources.delete", "calendar_resources.exact_create",
            "calendar_resources.exact_replace", "calendar_resources.exact_move"
        };

        reads.ShouldAllBe(toolName => !CalendarExecutionPolicy.IsMutation(toolName));
        mutations.ShouldAllBe(toolName => CalendarExecutionPolicy.IsMutation(toolName));
        (reads.Length + mutations.Length).ShouldBe(20);
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
