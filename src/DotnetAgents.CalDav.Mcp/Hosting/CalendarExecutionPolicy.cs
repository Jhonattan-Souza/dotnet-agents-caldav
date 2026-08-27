using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Applies the shared per-origin operation, mutation, queue, and progress bounds.</summary>
internal static class CalendarExecutionPolicy
{
    private static readonly TimeSpan InitialProgressDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadExecutionBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MutationExecutionBudget = TimeSpan.FromSeconds(60);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        async (request, cancellationToken) =>
        {
            var services = request.Services!;
            var admission = services.GetRequiredService<CalendarOperationAdmission>();
            var requestedToolName = request.Params?.Name;
            var toolName = CalendarTelemetry.NormalizeToolName(requestedToolName);
            var mutation = IsMutation(requestedToolName);
            var moveTool = requestedToolName is
                "calendar_resources.move" or "calendar_resources.exact_move";
            using var telemetry = CalendarTelemetry.StartOperation(
                toolName,
                EntityKind(requestedToolName));
            using var telemetryScope = CalendarTelemetry.Attach(telemetry);
            CalendarMoveTelemetrySnapshot? moveTelemetry = null;
            var report = request.Params?.ProgressToken is { } token
                ? (Func<ProgressNotificationValue, CancellationToken, Task>)((progress, progressCancellationToken) =>
                    request.Server.NotifyProgressAsync(token, progress, options: null, progressCancellationToken))
                : null;
            try
            {
                await using var execution = await AcquireWithProgressAsync(
                    services.GetRequiredService<TimeProvider>(),
                    admission,
                    mutation,
                    LegacyProgressReport(requestedToolName, report),
                    cancellationToken,
                    LegacyPhaseObserver(requestedToolName, telemetry)).ConfigureAwait(false);
                if (execution.Lease is null)
                {
                    var busy = CreateBusyResult(mutation);
                    CompleteTelemetry(telemetry, busy, moveTelemetry);
                    return busy;
                }
                StartLegacyDiscoveryPhase(requestedToolName, telemetry);
                using var progress = execution.AttachProgress();
                CallToolResult result;
                try
                {
                    result = await ExecuteWithinBudgetAsync(
                        services.GetRequiredService<TimeProvider>(),
                        mutation,
                        requestedToolName,
                        token => next(request, token),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    moveTelemetry = CalendarOperationProgress.CurrentMoveTelemetry;
                }
                CompleteTelemetry(telemetry, result, moveTelemetry);
                return result;
            }
            catch (Exception exception)
            {
                CompleteExceptionTelemetry(
                    telemetry,
                    exception,
                    mutation,
                    moveTool,
                    moveTelemetry,
                    cancellationToken);
                throw;
            }
        };

    private static Func<ProgressNotificationValue, CancellationToken, Task>? LegacyProgressReport(
        string? toolName,
        Func<ProgressNotificationValue, CancellationToken, Task>? report) =>
        IsSnapshotQuery(toolName) ? null : report;

    private static Action<CalendarOperationPhase>? LegacyPhaseObserver(
        string? toolName,
        CalendarTelemetryOperation? telemetry) => IsSnapshotQuery(toolName) || telemetry is null
            ? null
            : telemetry.StartPhase;

    private static void StartLegacyDiscoveryPhase(string? toolName, CalendarTelemetryOperation? telemetry)
    {
        if (!IsSnapshotQuery(toolName))
            telemetry?.StartPhase(CalendarOperationPhase.Discovery);
    }

    private static void CompleteExceptionTelemetry(
        CalendarTelemetryOperation? telemetry,
        Exception exception,
        bool mutation,
        bool moveTool,
        CalendarMoveTelemetrySnapshot? moveTelemetry,
        CancellationToken callerCancellationToken)
    {
        if (exception is InputRequiredException)
        {
            if (mutation)
                telemetry?.ObserveMutationStateIfAbsent(CalendarMutationState.NotAttempted);
            telemetry?.Complete(CalendarOperationOutcome.InputRequired, moveTelemetry);
        }
        else if (exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
        {
            CompleteCallerCancellation(telemetry, moveTool, moveTelemetry);
        }
        else if (exception is OperationCanceledException)
        {
            telemetry?.Fail(new TimeoutException());
        }
        else
        {
            telemetry?.Fail(exception);
        }
    }

    private static void CompleteCallerCancellation(
        CalendarTelemetryOperation? telemetry,
        bool moveTool,
        CalendarMoveTelemetrySnapshot? moveTelemetry)
    {
        if (moveTool && moveTelemetry is null or { Dispatch: CalendarMoveDispatchClassification.Unspecified })
        {
            telemetry?.ObserveMutationStateIfAbsent(CalendarMutationState.NotAttempted);
            moveTelemetry = new CalendarMoveTelemetrySnapshot(
                CalendarMoveDispatchClassification.NotAttempted,
                CalendarMoveCollisionClassification.Unspecified,
                CalendarMoveReconciliationClassification.NotRun);
        }
        else if (moveTool
            && moveTelemetry?.Dispatch == CalendarMoveDispatchClassification.NotAttempted)
        {
            telemetry?.ObserveMutationStateIfAbsent(CalendarMutationState.NotAttempted);
        }
        telemetry?.Complete(CalendarOperationOutcome.Cancelled, moveTelemetry);
    }

    private static async ValueTask<CallToolResult> ExecuteWithinBudgetAsync(
        TimeProvider timeProvider,
        bool mutation,
        string? toolName,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        CancellationToken callerCancellationToken)
    {
        if (IsSnapshotQuery(toolName))
            return await next(callerCancellationToken).ConfigureAwait(false);
        var budget = mutation ? MutationExecutionBudget : ReadExecutionBudget;
        using var deadline = new CancellationTokenSource(budget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            deadline.Token);
        try
        {
            return await next(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !callerCancellationToken.IsCancellationRequested)
        {
            if (mutation)
                CalendarTelemetry.ObserveMutationState(CalendarMutationState.Unknown);
            return CreateDeadlineResult(mutation);
        }
    }

    private static bool IsSnapshotQuery(string? toolName) => toolName is
        "calendar_entities.query" or "calendar_occurrences.query" or "todos.query";

    internal static async Task<CalendarExecutionLease> AcquireWithProgressAsync(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        bool mutation,
        Func<ProgressNotificationValue, CancellationToken, Task>? report,
        CancellationToken cancellationToken,
        Action<CalendarOperationPhase>? phaseObserver = null)
    {
        var execution = new CalendarExecutionLease(
            timeProvider,
            admission,
            report,
            cancellationToken,
            phaseObserver);
        try
        {
            execution.Lease = await admission.AcquireAsync(mutation, cancellationToken).ConfigureAwait(false);
            execution.MarkAdmitted();
            return execution;
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static bool IsMutation(string? toolName) => toolName is
        "calendars.create" or "calendars.delete" or "events.create" or "events.patch" or "todos.create" or "todos.patch" or "todos.complete"
        or "calendar_occurrences.add" or "calendar_occurrences.exclude"
        or "calendar_occurrences.restore_exclusion" or "calendar_occurrences.cancel"
        or "calendar_occurrences.restore_cancellation" or "calendar_resources.move"
        or "calendar_resources.delete" or "calendar_resources.exact_create"
        or "calendar_resources.exact_replace" or "calendar_resources.exact_move";

    private static CalendarTelemetryEntityKind? EntityKind(string? toolName) => toolName switch
    {
        "events.create" or "events.patch" => CalendarTelemetryEntityKind.Event,
        "todos.query" or "todos.create" or "todos.patch" or "todos.complete" =>
            CalendarTelemetryEntityKind.Todo,
        _ => null
    };

    private static void CompleteTelemetry(
        CalendarTelemetryOperation? telemetry,
        CallToolResult result,
        CalendarMoveTelemetrySnapshot? moveTelemetry) => telemetry?.Complete(
            result.IsError == true ? CalendarOperationOutcome.Error : CalendarOperationOutcome.Success,
            moveTelemetry);

    internal static async Task ReportProgressAsync(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        Func<ProgressNotificationValue, CancellationToken, Task> report,
        CancellationToken cancellationToken,
        Func<string>? currentPhase = null)
    {
        await Task.Delay(InitialProgressDelay, timeProvider, cancellationToken).ConfigureAwait(false);
        float notificationSequence = 0;
        while (true)
        {
            await admission.WaitForProgressSlotAsync(cancellationToken).ConfigureAwait(false);
            await report(
                new ProgressNotificationValue
                {
                    Progress = ++notificationSequence,
                    Message = currentPhase?.Invoke() ?? "discovery"
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<T> ExecuteProtectedReadAsync<T>(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        Func<CancellationToken, Task<T>> read,
        CancellationToken callerCancellationToken)
    {
        using var lease = await admission.AcquireAsync(
            mutation: false,
            callerCancellationToken).ConfigureAwait(false);
        if (lease is null)
            throw new InvalidOperationException("The configured Calendar origin is busy.");
        using var deadline = new CancellationTokenSource(ReadExecutionBudget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            deadline.Token);
        try
        {
            return await read(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !callerCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The protected Calendar resource read exceeded its execution budget.");
        }
    }

    internal static CallToolResult CreateBusyResult(bool mutation)
    {
        CalendarTelemetry.ObserveStructuredError(new CalendarStructuredErrorFacts(
            CalendarTelemetryErrorCode.Busy,
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarTelemetryErrorPhase.AdmissionAndPayload,
            true));
        if (mutation)
            CalendarTelemetry.ObserveMutationState(CalendarMutationState.NotAttempted);
        var structured = new Dictionary<string, object?>
        {
            ["code"] = "busy",
            ["message"] = "The configured Calendar origin is busy.",
            ["retryable"] = true,
            ["retryAfterMs"] = CalendarOperationAdmission.RetryAfterMilliseconds,
            ["phase"] = "admissionAndPayload",
            ["category"] = "limitsAndAdmission"
        };
        if (mutation)
            structured["mutationState"] = "not_attempted";
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            Content = [new TextContentBlock { Text = "Calendar operation admission failed." }]
        };
    }

    internal static CallToolResult CreateDeadlineResult(bool mutation)
    {
        CalendarTelemetry.ObserveStructuredError(new CalendarStructuredErrorFacts(
            CalendarTelemetryErrorCode.LimitExhausted,
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarTelemetryErrorPhase.Execution,
            false));
        var structured = new Dictionary<string, object?>
        {
            ["code"] = "limit_exhausted",
            ["message"] = "The Calendar operation exhausted its elapsed_time execution budget.",
            ["retryable"] = false,
            ["phase"] = "execution",
            ["category"] = "limitsAndAdmission",
            ["limits"] = new Dictionary<string, object?> { ["dimension"] = "elapsed_time" }
        };
        // This outer policy cannot prove whether a timed-out handler crossed its dispatch boundary.
        // Lower layers return a more specific state when they can; the only safe fallback here is unknown.
        if (mutation)
            structured["mutationState"] = "unknown";
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            Content = [new TextContentBlock { Text = "Calendar operation exceeded its execution budget." }]
        };
    }
}

internal sealed class CalendarExecutionLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _progressCancellation;
    private readonly TimeProvider _timeProvider;
    private readonly CalendarOperationAdmission _admission;
    private readonly Func<ProgressNotificationValue, CancellationToken, Task>? _report;
    private Task _progressTask = Task.CompletedTask;
    private readonly CalendarOperationProgress.State _progressState;

    public CalendarExecutionLease(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        Func<ProgressNotificationValue, CancellationToken, Task>? report,
        CancellationToken cancellationToken,
        Action<CalendarOperationPhase>? phaseObserver = null)
    {
        _timeProvider = timeProvider;
        _admission = admission;
        _report = report;
        _progressState = CalendarOperationProgress.CreateState(phaseObserver);
        _progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    internal CalendarOperationAdmission.Lease? Lease { get; set; }

    internal void MarkAdmitted()
    {
        if (Lease is null)
            return;
        _progressState.AdvanceTo(CalendarOperationPhase.Discovery);
        if (_report is not null)
        {
            _progressTask = CalendarExecutionPolicy.ReportProgressAsync(
                _timeProvider,
                _admission,
                _report,
                _progressCancellation.Token,
                () => _progressState.PhaseName);
        }
    }

    internal CalendarOperationProgress.ProgressScope AttachProgress() =>
        CalendarOperationProgress.Attach(_progressState);

    internal void SetPhase(CalendarOperationPhase phase) => _progressState.AdvanceTo(phase);

    public async ValueTask DisposeAsync()
    {
        Lease?.Dispose();
        await _progressCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_progressCancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.Assert(_progressCancellation.IsCancellationRequested);
        }
        _progressCancellation.Dispose();
    }
}

/// <summary>FIFO admission for the single configured CalDAV origin.</summary>
internal sealed class CalendarOperationAdmission(TimeProvider timeProvider)
{
    internal const int MaximumConcurrentOperations = 4;
    internal const int MaximumQueuedOperations = 16;
    internal const int RetryAfterMilliseconds = 2_000;
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(RetryAfterMilliseconds);
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _activeOperations;
    private bool _mutationActive;
    private DateTimeOffset _nextProgressAt;

    internal async Task WaitForProgressSlotAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var reservedAt = _nextProgressAt > now ? _nextProgressAt : now;
            _nextProgressAt = reservedAt + TimeSpan.FromMilliseconds(250);
            delay = reservedAt - now;
        }
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Lease?> AcquireAsync(bool mutation, CancellationToken cancellationToken)
    {
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            if (_waiters.Count == 0 && CanAdmit(mutation))
                return Admit(mutation);
            if (_waiters.Count >= MaximumQueuedOperations)
                return null;
            node = _waiters.AddLast(new Waiter(mutation));
        }

        try
        {
            return await node.Value.Source.Task.WaitAsync(MaximumWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    _waiters.Remove(node);
                    return null;
                }
            }
            return await node.Value.Source.Task;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    _waiters.Remove(node);
                    throw;
                }
            }
            var racedLease = await node.Value.Source.Task;
            racedLease.Dispose();
            throw;
        }
    }

    private bool CanAdmit(bool mutation) =>
        _activeOperations < MaximumConcurrentOperations && (!mutation || !_mutationActive);

    private Lease Admit(bool mutation)
    {
        _activeOperations++;
        _mutationActive |= mutation;
        return new Lease(this, mutation);
    }

    private void Release(bool mutation)
    {
        List<(Waiter Waiter, Lease Lease)> admitted = [];
        lock (_gate)
        {
            _activeOperations--;
            if (mutation)
                _mutationActive = false;
            while (_waiters.First is { } first && CanAdmit(first.Value.Mutation))
            {
                _waiters.RemoveFirst();
                admitted.Add((first.Value, Admit(first.Value.Mutation)));
            }
        }
        foreach (var item in admitted)
            item.Waiter.Source.TrySetResult(item.Lease);
    }

    internal sealed class Lease(CalendarOperationAdmission owner, bool mutation) : IDisposable
    {
        private CalendarOperationAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(mutation);
    }

    private sealed class Waiter(bool mutation)
    {
        public bool Mutation { get; } = mutation;
        public TaskCompletionSource<Lease> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
