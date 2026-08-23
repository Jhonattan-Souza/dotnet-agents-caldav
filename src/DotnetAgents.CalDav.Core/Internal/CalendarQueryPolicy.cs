using System.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Single policy authority for bounded query Starts and transport limits.</summary>
internal sealed class CalendarQueryPolicy(TimeProvider timeProvider)
{
    public const int MaximumMultigetBatchSize = 50;
    public const int MaximumDirectGetResources = 200;
    public const int MaximumDirectGetConcurrency = 4;
    internal const int MaximumOccurrences = 5000;
    internal static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(10);

    internal DateTimeOffset GetSnapshotExpiry() => timeProvider.GetUtcNow().Add(SnapshotLifetime);

    internal async Task<QueryReply<TItem>> ExecuteStartAsync<TCompleted, TItem>(
        CancellationToken callerCancellationToken,
        string calendarLimitMessage,
        Func<CalendarQueryExecution, Task<TCompleted>> completeAsync,
        Func<TCompleted, CancellationToken, QueryReply<TItem>> publish)
    {
        var startedAt = timeProvider.GetUtcNow();
        using var deadline = new CancellationTokenSource(ExecutionTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken,
            deadline.Token);
        var execution = new CalendarQueryExecution(
            timeProvider,
            startedAt.Add(ExecutionTimeout),
            linked.Token);
        try
        {
            var completed = await completeAsync(execution).ConfigureAwait(false);
            execution.ThrowIfDeadlineExpired();
            return publish(completed, linked.Token);
        }
        catch (Exception exception)
        {
            if (!TryClassify(
                    exception,
                    deadline.IsCancellationRequested,
                    callerCancellationToken.IsCancellationRequested,
                    calendarLimitMessage,
                    out var failure))
            {
                throw;
            }
            return new QueryReply<TItem>.Failure(failure);
        }
    }

    private static bool TryClassify(
        Exception exception,
        bool deadlineCancelled,
        bool callerCancelled,
        string calendarLimitMessage,
        out QueryFailure failure)
    {
        failure = exception switch
        {
            CalendarDiscoveryLimitException limit => CalendarQueryFailures.Limit(
                calendarLimitMessage,
                new QueryExecutionLimits(CalendarCount: limit.CalendarCount)),
            HttpRequestException http => CalendarQueryFailures.FromHttp(http.StatusCode),
            OperationCanceledException when deadlineCancelled && !callerCancelled =>
                CalendarQueryFailures.ElapsedLimit(),
            OperationCanceledException when !callerCancelled => CalendarQueryFailures.UpstreamUnavailable(),
            TimeoutException or TimeoutRejectedException or BrokenCircuitException =>
                CalendarQueryFailures.UpstreamUnavailable(),
            XmlException or CalendarDiscoveryProtocolException => CalendarQueryFailures.Protocol(),
            CalendarDiscoveryUnsupportedCapabilityException => CalendarQueryFailures.UnsupportedCapability(),
            CalendarQueryStartDeadlineException => CalendarQueryFailures.ElapsedLimit(),
            _ => null!
        };
        return failure is not null;
    }

    private sealed class CalendarQueryStartDeadlineException : Exception;

    internal sealed class CalendarQueryExecution(
        TimeProvider timeProvider,
        DateTimeOffset deadline,
        CancellationToken token)
    {
        internal CancellationToken Token => token;

        internal void ThrowIfDeadlineExpired()
        {
            token.ThrowIfCancellationRequested();
            if (timeProvider.GetUtcNow() >= deadline)
                throw new CalendarQueryStartDeadlineException();
        }
    }
}
