using DotnetAgents.CalDav.Core.Internal;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarTransportFailureTests
{
    public static TheoryData<string, bool, bool, bool> Classifications() => new()
    {
        { "broken_circuit", true, false, true },
        { "isolated_circuit", true, false, true },
        { "rate_limiter", true, false, true },
        { "timeout_rejected", false, true, true },
        { "http", false, true, false },
        { "io", false, true, true },
        { "timeout", false, true, true },
        { "canceled", false, true, false },
        { "invalid_operation", false, false, false }
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Classifies_each_transport_and_resilience_failure(
        string kind,
        bool rejectedBeforeSend,
        bool possiblySent,
        bool unavailable)
    {
        var exception = Create(kind);

        CalendarTransportFailure.IsRejectedBeforeSend(exception).ShouldBe(rejectedBeforeSend);
        CalendarTransportFailure.IsPossiblySent(exception).ShouldBe(possiblySent);
        CalendarTransportFailure.IsUnavailable(exception).ShouldBe(unavailable);
    }

    private static Exception Create(string kind) => kind switch
    {
        "broken_circuit" => new BrokenCircuitException(),
        "isolated_circuit" => new IsolatedCircuitException(),
        "rate_limiter" => new RateLimiterRejectedException(),
        "timeout_rejected" => new TimeoutRejectedException(TimeSpan.FromSeconds(10)),
        "http" => new HttpRequestException("connection lost"),
        "io" => new IOException("connection reset"),
        "timeout" => new TimeoutException(),
        "canceled" => new OperationCanceledException(),
        _ => new InvalidOperationException()
    };
}
