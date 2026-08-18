using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Core.Internal;
using ModelContextProtocol;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarExecutionPolicyTests
{
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
    public async Task QueuedCallReportsAtFiveHundredMillisecondsBeforeBusyAtTwoSeconds()
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
        await WaitForAsync(() => reports.Count == 1);
        reports[0].Message.ShouldBe("admission");
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1500));
        await using var execution = await pending;

        execution.Lease.ShouldBeNull();
        reports.Count.ShouldBeInRange(1, 4);
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Yield();
        condition().ShouldBeTrue();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
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
