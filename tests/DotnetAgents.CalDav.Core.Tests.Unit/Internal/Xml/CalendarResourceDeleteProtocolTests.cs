using System.Net;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarResourceDeleteProtocolTests
{
    [Theory]
    [InlineData("relative.ics", "\"r1\"")]
    [InlineData("https://other.example/tasks/a.ics", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics?secret=true", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "")]
    [InlineData("https://example.com/tasks/a.ics", "r1")]
    [InlineData("https://example.com/tasks/a.ics", "W/\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "*")]
    public async Task DeleteAsync_RejectsUnsafeHrefAndNonExactStrongTagBeforeDispatch(
        string href,
        string entityTag)
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest(href, entityTag),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.InvalidInput);
        sendCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CalendarResourceDeleteDispatchCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, CalendarResourceDeleteDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed, CalendarResourceDeleteDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, CalendarResourceDeleteDispatchCode.UpstreamUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CalendarResourceDeleteDispatchCode.UpstreamForbidden)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CalendarResourceDeleteDispatchCode.PayloadTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarResourceDeleteDispatchCode.UpstreamRateLimited)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CalendarResourceDeleteDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.NotImplemented, CalendarResourceDeleteDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.InsufficientStorage, CalendarResourceDeleteDispatchCode.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, CalendarResourceDeleteDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CalendarResourceDeleteDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.BadRequest, CalendarResourceDeleteDispatchCode.UpstreamProtocolError)]
    public async Task DeleteAsync_MapsDefinitiveHttpOutcome(
        HttpStatusCode statusCode,
        CalendarResourceDeleteDispatchCode expectedCode)
    {
        using var httpClient = new HttpClient(new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(statusCode))));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest("https://example.com/tasks/a.ics", "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public async Task DeleteAsync_ClassifiesTransportFailureAfterSingleAttemptAsPossiblyDispatched()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            throw new HttpRequestException("private transport detail");
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest("https://example.com/tasks/a.ics", "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.PossiblyDispatched);
        sendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(3, 3_000)]
    [InlineData(3_000_000, int.MaxValue)]
    public async Task DeleteAsync_RateLimitPreservesBoundedDeltaRetryAfter(
        int delaySeconds,
        int expectedMilliseconds)
    {
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(delaySeconds));
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest("https://example.com/tasks/a.ics", "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.UpstreamRateLimited);
        result.RetryAfterMilliseconds.ShouldBe(expectedMilliseconds);
    }

    [Fact]
    public async Task DeleteAsync_SendsOneExactStrongConditionalDeleteAcrossSafeRedirect()
    {
        var requests = new List<(string Href, string Method, string IfMatch, bool HasContent)>();
        using var httpClient = new HttpClient(new Handler(request =>
        {
            requests.Add((
                request.RequestUri!.AbsoluteUri,
                request.Method.Method,
                request.Headers.IfMatch.Single().ToString(),
                request.Content is not null));
            return Task.FromResult(requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://example.com/calendars/user/tasks/canonical.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest(
                "https://example.com/calendars/user/tasks/reviewed.ics",
                "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.Dispatched);
        requests.ShouldBe([
            ("https://example.com/calendars/user/tasks/reviewed.ics", "DELETE", "\"r1\"", false),
            ("https://example.com/calendars/user/tasks/canonical.ics", "DELETE", "\"r1\"", false)
        ]);
    }

    [Fact]
    public async Task DeleteAsync_RejectsCrossOriginRedirectBeforeSecondDispatch()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://other.example/tasks/a.ics") }
            });
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest("https://example.com/tasks/a.ics", "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(3, CalendarResourceDeleteDispatchCode.Dispatched, 4)]
    [InlineData(4, CalendarResourceDeleteDispatchCode.UpstreamProtocolError, 4)]
    public async Task DeleteAsync_EnforcesExactThreeRedirectCeiling(
        int redirectResponses,
        CalendarResourceDeleteDispatchCode expectedCode,
        int expectedSendCount)
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(sendCount <= redirectResponses
                ? new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri($"https://example.com/tasks/{sendCount}.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceDeleteProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.DeleteAsync(
            new CalendarResourceDeleteRequest("https://example.com/tasks/a.ics", "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        sendCount.ShouldBe(expectedSendCount);
    }

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
