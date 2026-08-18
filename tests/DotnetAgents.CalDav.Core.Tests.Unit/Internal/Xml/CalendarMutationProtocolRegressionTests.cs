using System.Net;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarMutationProtocolRegressionTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("move")]
    public async Task EveryMutationProtocolRejectsCrossOriginMethodPreservingRedirectWithoutReplay(
        string operation)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://other.example/private.ics") }
            });
        }));

        var outcome = await ExecuteAsync(operation, client);

        outcome.ShouldBe("upstream_protocol_error");
        sendCount.ShouldBe(1);
    }

    private static async Task<string> ExecuteAsync(string operation, HttpClient client)
    {
        var configuredBaseUri = new Uri("https://example.com");
        return operation switch
        {
            "create" => (await new CalendarResourceCreateProtocol(client, configuredBaseUri).CreateAsync(
                new CalendarResourceCreateRequest(
                    "https://example.com/calendars/events/",
                    "https://example.com/calendars/events/a.ics",
                    new byte[] { 1 }),
                TestContext.Current.CancellationToken)).Code switch
            {
                CalendarResourceCreateCode.UpstreamProtocolError => "upstream_protocol_error",
                _ => "unexpected"
            },
            "update" => (await new CalendarResourceUpdateProtocol(client, configuredBaseUri).UpdateAsync(
                new CalendarResourceUpdateRequest(
                    "https://example.com/calendars/events/a.ics",
                    "\"r1\"",
                    new byte[] { 1 }),
                TestContext.Current.CancellationToken)).Code switch
            {
                CalendarResourceUpdateDispatchCode.UpstreamProtocolError => "upstream_protocol_error",
                _ => "unexpected"
            },
            "delete" => (await new CalendarResourceDeleteProtocol(client, configuredBaseUri).DeleteAsync(
                new CalendarResourceDeleteRequest(
                    "https://example.com/calendars/events/a.ics",
                    "\"r1\""),
                TestContext.Current.CancellationToken)).Code switch
            {
                CalendarResourceDeleteDispatchCode.UpstreamProtocolError => "upstream_protocol_error",
                _ => "unexpected"
            },
            "move" => (await new CalendarResourceMoveProtocol(client, configuredBaseUri).MoveAsync(
                new CalendarResourceMoveDispatchRequest(
                    "https://example.com/calendars/events/a.ics",
                    "https://example.com/calendars/archive/a.ics",
                    "\"r1\""),
                TestContext.Current.CancellationToken)).Code switch
            {
                CalendarResourceMoveDispatchCode.UpstreamProtocolError => "upstream_protocol_error",
                _ => "unexpected"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
