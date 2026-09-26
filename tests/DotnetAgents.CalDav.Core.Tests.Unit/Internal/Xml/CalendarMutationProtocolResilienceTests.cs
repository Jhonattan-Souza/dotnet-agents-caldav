using System.Net;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarMutationProtocolResilienceTests
{
    public static TheoryData<string, string, string> Rejections()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var operation in new[] { "create", "update", "delete", "move" })
        {
            data.Add(operation, "broken_circuit", "rejected_before_send");
            data.Add(operation, "isolated_circuit", "rejected_before_send");
            data.Add(operation, "rate_limiter", "rejected_before_send");
            data.Add(operation, "timeout", "possibly_dispatched");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task EveryMutationProtocolClassifiesResilienceRejectionsByWhetherTheAttemptCouldHaveBeenSent(
        string operation,
        string rejection,
        string expected)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            throw Rejection(rejection);
        }));

        var outcome = await ExecuteAsync(operation, client);

        outcome.ShouldBe(expected);
        sendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("move")]
    public async Task EveryMutationProtocolTreatsARejectionAfterARedirectAsNotCommitted(string operation)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            if (++sendCount > 1)
                throw new BrokenCircuitException();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://example.com/calendars/events/redirected.ics") }
            });
        }));

        var outcome = await ExecuteAsync(operation, client);

        outcome.ShouldBe("rejected_before_send");
        sendCount.ShouldBe(2);
    }

    private static Exception Rejection(string rejection) => rejection switch
    {
        "broken_circuit" => new BrokenCircuitException(),
        "isolated_circuit" => new IsolatedCircuitException(),
        "rate_limiter" => new RateLimiterRejectedException(),
        "timeout" => new TimeoutRejectedException(TimeSpan.FromSeconds(10)),
        _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, null)
    };

    private static async Task<string> ExecuteAsync(string operation, HttpClient client)
    {
        var accountOrigins = new CalDavAccountOrigins(new Uri("https://example.com"));
        return operation switch
        {
            "create" => Name((await new CalendarResourceCreateProtocol(client, accountOrigins).CreateAsync(
                new CalendarResourceCreateRequest(
                    "https://example.com/calendars/events/",
                    "https://example.com/calendars/events/a.ics",
                    new byte[] { 1 }),
                TestContext.Current.CancellationToken)).Code.ToString()),
            "update" => Name((await new CalendarResourceUpdateProtocol(client, accountOrigins).UpdateAsync(
                new CalendarResourceUpdateRequest(
                    "https://example.com/calendars/events/a.ics",
                    "\"r1\"",
                    new byte[] { 1 }),
                TestContext.Current.CancellationToken)).Code.ToString()),
            "delete" => Name((await new CalendarResourceDeleteProtocol(client, accountOrigins).DeleteAsync(
                new CalendarResourceDeleteRequest(
                    "https://example.com/calendars/events/a.ics",
                    "\"r1\""),
                TestContext.Current.CancellationToken)).Code.ToString()),
            "move" => Name((await new CalendarResourceMoveProtocol(client, accountOrigins).MoveAsync(
                new CalendarResourceMoveDispatchRequest(
                    "https://example.com/calendars/events/a.ics",
                    "https://example.com/calendars/archive/a.ics",
                    "\"r1\""),
                TestContext.Current.CancellationToken)).Code.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static string Name(string code) => code switch
    {
        nameof(CalendarResourceUpdateDispatchCode.RejectedBeforeSend) => "rejected_before_send",
        nameof(CalendarResourceUpdateDispatchCode.PossiblyDispatched) => "possibly_dispatched",
        _ => code
    };

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
