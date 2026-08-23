using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Shared all-attempt transfer budget for one query compatibility-mode execution.</summary>
internal sealed class CalendarDirectGetBudget
{
    internal const int MaximumResourceBytes = 4 * 1024 * 1024;
    internal const long MaximumAggregateBytes = 32L * 1024 * 1024;
    internal const int MaximumAttemptsPerResource = 3;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _bodyReadGate = new(1, 1);
    private long _aggregateBytes;
    private QueryFailure? _failure;

    internal CalendarDirectGetReadMeter StartResource() => new(this);

    internal long AggregateBytes
    {
        get
        {
            lock (_gate)
                return _aggregateBytes;
        }
    }

    internal QueryFailure? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    internal QueryFailure? Charge(int bodyBytes)
    {
        lock (_gate)
        {
            _aggregateBytes = Math.Min(MaximumAggregateBytes + 1, _aggregateBytes + bodyBytes);
            if (_aggregateBytes > MaximumAggregateBytes)
            {
                _failure ??= Limit(QueryLimitDimension.ByteCount, _aggregateBytes, MaximumAggregateBytes);
            }
            return _failure;
        }
    }

    internal async ValueTask<int> ReadAndChargeAsync(
        CalendarDirectGetReadMeter meter,
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        await _bodyReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resourceCapacity = meter.RemainingBodyCapacityPlusOne;
            long aggregateCapacity;
            lock (_gate)
                aggregateCapacity = MaximumAggregateBytes + 1 - _aggregateBytes;
            var capacity = (int)Math.Min(destination.Length, Math.Min(resourceCapacity, aggregateCapacity));
            if (capacity <= 0)
            {
                QueryFailure failure;
                lock (_gate)
                {
                    _failure ??= CalendarDirectGetBudget.Limit(
                        QueryLimitDimension.ByteCount,
                        MaximumAggregateBytes + 1,
                        MaximumAggregateBytes);
                    failure = _failure;
                }
                meter.Fail(failure);
                return -1;
            }
            var read = await stream.ReadAsync(destination[..capacity], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return 0;
            QueryFailure? aggregateFailure;
            lock (_gate)
            {
                _aggregateBytes += read;
                if (_aggregateBytes > MaximumAggregateBytes)
                {
                    _failure ??= Limit(QueryLimitDimension.ByteCount, _aggregateBytes, MaximumAggregateBytes);
                }
                aggregateFailure = _failure;
            }
            meter.CommitBodyBytes(read, aggregateFailure);
            return read;
        }
        finally
        {
            _bodyReadGate.Release();
        }
    }

    internal static QueryFailure Limit(QueryLimitDimension dimension, long observed, long limit) =>
        CalendarQueryFailures.Limit(
            "Direct GET Compatibility Mode exhausted its execution budget.",
            new QueryExecutionLimits(Dimension: dimension, Observed: observed, Limit: limit));
}

/// <summary>Attempt meter retained for the full retry lifetime of one logical resource read.</summary>
internal sealed class CalendarDirectGetReadMeter(CalendarDirectGetBudget budget)
{
    private readonly object _gate = new();
    private int _attempts;
    private int _attemptBodyBytes;
    private QueryFailure? _failure;

    internal int Attempts
    {
        get
        {
            lock (_gate)
                return _attempts;
        }
    }

    internal QueryFailure? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    internal bool TryBeginAttempt()
    {
        lock (_gate)
        {
            if (_failure is not null)
                return false;
            if (budget.Failure is { } aggregateFailure)
            {
                _failure = aggregateFailure;
                return false;
            }
            if (_attempts == CalendarDirectGetBudget.MaximumAttemptsPerResource)
            {
                _failure ??= CalendarDirectGetBudget.Limit(
                    QueryLimitDimension.AttemptCount,
                    _attempts,
                    CalendarDirectGetBudget.MaximumAttemptsPerResource);
                return false;
            }
            _attempts++;
            _attemptBodyBytes = 0;
            CalendarQueryTelemetry.Add("caldav.query.direct_get_attempt_count");
            return true;
        }
    }

    internal void ChargeBody(int bodyBytes)
    {
        lock (_gate)
        {
            _attemptBodyBytes = Math.Min(CalendarDirectGetBudget.MaximumResourceBytes + 1, bodyBytes);
            if (bodyBytes > CalendarDirectGetBudget.MaximumResourceBytes)
            {
                _failure ??= CalendarQueryFailures.PayloadTooLarge(
                    "A Calendar Object Resource exceeds the safe payload limit.",
                    bodyBytes);
            }
            _failure ??= budget.Charge(bodyBytes);
        }
    }

    internal ValueTask<int> ReadAndChargeAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken) => budget.ReadAndChargeAsync(this, stream, destination, cancellationToken);

    internal int RemainingBodyCapacityPlusOne
    {
        get
        {
            lock (_gate)
                return _failure is null
                    ? CalendarDirectGetBudget.MaximumResourceBytes + 1 - _attemptBodyBytes
                    : 0;
        }
    }

    internal void CommitBodyBytes(int bodyBytes, QueryFailure? aggregateFailure)
    {
        lock (_gate)
        {
            _attemptBodyBytes += bodyBytes;
            if (_attemptBodyBytes > CalendarDirectGetBudget.MaximumResourceBytes)
            {
                _failure ??= CalendarQueryFailures.PayloadTooLarge(
                    "A Calendar Object Resource exceeds the safe payload limit.",
                    _attemptBodyBytes);
            }
            _failure ??= aggregateFailure;
        }
    }

    internal void Fail(QueryFailure failure)
    {
        lock (_gate)
            _failure ??= failure;
    }

    internal void RecordSyntheticAttempt(int bodyBytes)
    {
        _ = TryBeginAttempt();
        ChargeBody(bodyBytes);
    }
}

internal sealed class CalendarDirectGetAttemptLimitException : Exception;

internal sealed class CalendarDirectGetBudgetExceededException : Exception;
