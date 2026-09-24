using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// Classifies failures raised by the Calendar HTTP client and its standard resilience pipeline
/// (rate limiter, total timeout, retry, circuit breaker, attempt timeout).
/// </summary>
internal static class CalendarTransportFailure
{
    /// <summary>
    /// The rate limiter and an open or isolated circuit reject before the attempt reaches the HTTP
    /// handler. Mutation methods are never retried, so a rejected mutation dispatch was not sent.
    /// </summary>
    public static bool IsRejectedBeforeSend(Exception exception) =>
        exception is BrokenCircuitException or RateLimiterRejectedException;

    /// <summary>
    /// Failures after which a mutation dispatch may have reached the server. The total and attempt
    /// timeouts cancel an attempt that can already be in flight.
    /// </summary>
    public static bool IsPossiblySent(Exception exception) => exception is HttpRequestException
        or IOException
        or TimeoutException
        or OperationCanceledException
        or TimeoutRejectedException;

    /// <summary>
    /// Transient failures of a read that carries no HTTP status: I/O, timeouts, and every resilience
    /// strategy rejection. The MCP collection tools keep a matching list for discovery failures.
    /// </summary>
    public static bool IsUnavailable(Exception exception) => exception is IOException
        or TimeoutException
        or TimeoutRejectedException
        or BrokenCircuitException
        or RateLimiterRejectedException;
}
