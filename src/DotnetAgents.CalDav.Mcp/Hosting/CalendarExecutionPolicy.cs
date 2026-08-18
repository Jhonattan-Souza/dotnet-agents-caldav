using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Applies the shared per-origin operation, mutation, queue, and progress bounds.</summary>
internal static class CalendarExecutionPolicy
{
    private static readonly TimeSpan InitialProgressDelay = TimeSpan.FromMilliseconds(500);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        async (request, cancellationToken) =>
        {
            var services = request.Services!;
            var admission = services.GetRequiredService<CalendarOperationAdmission>();
            var mutation = IsMutation(request.Params?.Name);
            var report = request.Params?.ProgressToken is { } token
                ? (Func<ProgressNotificationValue, CancellationToken, Task>)((progress, progressCancellationToken) =>
                    request.Server.NotifyProgressAsync(token, progress, options: null, progressCancellationToken))
                : null;
            await using var execution = await AcquireWithProgressAsync(
                services.GetRequiredService<TimeProvider>(),
                admission,
                mutation,
                report,
                cancellationToken).ConfigureAwait(false);
            if (execution.Lease is null)
                return CreateBusyResult(mutation);
            using var progress = execution.AttachProgress();
            return await next(request, cancellationToken).ConfigureAwait(false);
        };

    internal static async Task<CalendarExecutionLease> AcquireWithProgressAsync(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        bool mutation,
        Func<ProgressNotificationValue, CancellationToken, Task>? report,
        CancellationToken cancellationToken)
    {
        var execution = new CalendarExecutionLease(timeProvider, admission, report, cancellationToken);
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
        "events.create" or "events.patch" or "todos.create" or "todos.patch" or "todos.complete"
        or "calendar_occurrences.add" or "calendar_occurrences.exclude"
        or "calendar_occurrences.restore_exclusion" or "calendar_occurrences.cancel"
        or "calendar_occurrences.restore_cancellation" or "calendar_resources.move"
        or "calendar_resources.delete" or "calendar_resources.exact_create"
        or "calendar_resources.exact_replace" or "calendar_resources.exact_move";

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
        CalendarOperationAdmission admission,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        using var lease = await admission.AcquireAsync(mutation: false, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            throw new InvalidOperationException("The configured Calendar origin is busy.");
        return await read(cancellationToken).ConfigureAwait(false);
    }

    internal static CallToolResult CreateBusyResult(bool mutation)
    {
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
}

internal sealed class CalendarExecutionLease : IAsyncDisposable
{
    private readonly CancellationTokenSource _progressCancellation;
    private readonly Task _progressTask;
    private readonly CalendarOperationProgress.State _progressState;

    public CalendarExecutionLease(
        TimeProvider timeProvider,
        CalendarOperationAdmission admission,
        Func<ProgressNotificationValue, CancellationToken, Task>? report,
        CancellationToken cancellationToken)
    {
        _progressState = CalendarOperationProgress.CreateState();
        _progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _progressTask = report is null
            ? Task.CompletedTask
            : CalendarExecutionPolicy.ReportProgressAsync(
                timeProvider,
                admission,
                report,
                _progressCancellation.Token,
                () => _progressState.PhaseName);
    }

    internal CalendarOperationAdmission.Lease? Lease { get; set; }

    internal void MarkAdmitted()
    {
        if (Lease is not null)
            _progressState.AdvanceTo(CalendarOperationPhase.Discovery);
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
