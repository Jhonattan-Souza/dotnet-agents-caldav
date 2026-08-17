using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarResourceMoveProtocolTests
{
    [Fact]
    public async Task MoveAsync_SendsOneExactConditionalNoOverwriteMove()
    {
        HttpRequestMessage? observed = null;
        using var httpClient = new HttpClient(new Handler(request =>
        {
            observed = Clone(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(
            new CalendarResourceMoveDispatchRequest(
                "https://example.com/calendars/user/tasks/reviewed.ics",
                "https://example.com/calendars/user/archive/moved.ics",
                "\"r1\""),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.Dispatched);
        observed.ShouldNotBeNull();
        observed.Method.Method.ShouldBe("MOVE");
        observed.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/calendars/user/tasks/reviewed.ics");
        observed.Headers.IfMatch.Single().ToString().ShouldBe("\"r1\"");
        observed.Headers.GetValues("Destination").Single()
            .ShouldBe("https://example.com/calendars/user/archive/moved.ics");
        observed.Headers.GetValues("Overwrite").Single().ShouldBe("F");
        observed.Content.ShouldBeNull();
    }

    [Theory]
    [InlineData("relative.ics", "https://example.com/archive/a.ics", "\"r1\"")]
    [InlineData("https://other.example/tasks/a.ics", "https://example.com/archive/a.ics", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "https://other.example/archive/a.ics", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics?secret=true", "https://example.com/archive/a.ics", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "https://example.com/tasks/a.ics", "\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "https://example.com/archive/a.ics", "")]
    [InlineData("https://example.com/tasks/a.ics", "https://example.com/archive/a.ics", "r1")]
    [InlineData("https://example.com/tasks/a.ics", "https://example.com/archive/a.ics", "W/\"r1\"")]
    [InlineData("https://example.com/tasks/a.ics", "https://example.com/archive/a.ics", "*")]
    public async Task MoveAsync_RejectsUnsafeEndpointsAndNonExactStrongTagBeforeDispatch(
        string sourceHref,
        string destinationHref,
        string entityTag)
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(
            new CalendarResourceMoveDispatchRequest(sourceHref, destinationHref, entityTag),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.InvalidInput);
        sendCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, CalendarResourceMoveDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.NotFound, CalendarResourceMoveDispatchCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, CalendarResourceMoveDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed, CalendarResourceMoveDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, CalendarResourceMoveDispatchCode.UpstreamUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CalendarResourceMoveDispatchCode.UpstreamForbidden)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CalendarResourceMoveDispatchCode.PayloadTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarResourceMoveDispatchCode.UpstreamRateLimited)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CalendarResourceMoveDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.NotImplemented, CalendarResourceMoveDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.InsufficientStorage, CalendarResourceMoveDispatchCode.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, CalendarResourceMoveDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CalendarResourceMoveDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.BadRequest, CalendarResourceMoveDispatchCode.UpstreamProtocolError)]
    public async Task MoveAsync_MapsHttpOutcome(
        HttpStatusCode statusCode,
        CalendarResourceMoveDispatchCode expectedCode)
    {
        using var httpClient = new HttpClient(new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(statusCode))));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(
            Request(),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task MoveAsync_MapsBoundedNoUidConflictDavErrorToDestinationConflict(
        HttpStatusCode statusCode)
    {
        using var httpClient = new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
                    "<c:no-uid-conflict/></d:error>")
            })));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.DestinationConflict);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    [InlineData(3, 3000)]
    public async Task MoveAsync_MapsDeltaRetryAfterToBoundedMilliseconds(
        int delaySeconds,
        int? expectedMilliseconds)
    {
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (delaySeconds >= 0)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(delaySeconds));
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.RetryAfterMilliseconds.ShouldBe(expectedMilliseconds);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 2000)]
    public async Task MoveAsync_MapsDateRetryAfterRelativeToInjectedClock(
        int offsetSeconds,
        int expectedMilliseconds)
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(offsetSeconds));
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceMoveProtocol(
            httpClient,
            new Uri("https://example.com"),
            new FrozenTimeProvider(now));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.RetryAfterMilliseconds.ShouldBe(expectedMilliseconds);
    }

    [Fact]
    public async Task MoveAsync_ClassifiesTransportFailureAfterSingleAttemptAsPossiblyDispatched()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            throw new HttpRequestException("private transport detail");
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.PossiblyDispatched);
        sendCount.ShouldBe(1);
    }

    [Fact]
    public async Task MoveAsync_PreservesDestinationAndConditionsAcrossSafeRedirect()
    {
        var requests = new List<(string Source, string Destination, string Overwrite, string IfMatch)>();
        using var httpClient = new HttpClient(new Handler(request =>
        {
            requests.Add((
                request.RequestUri!.AbsoluteUri,
                request.Headers.GetValues("Destination").Single(),
                request.Headers.GetValues("Overwrite").Single(),
                request.Headers.IfMatch.Single().ToString()));
            return Task.FromResult(requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://example.com/tasks/canonical.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.Created));
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.Dispatched);
        requests.ShouldBe([
            ("https://example.com/tasks/a.ics", "https://example.com/archive/a.ics", "F", "\"r1\""),
            ("https://example.com/tasks/canonical.ics", "https://example.com/archive/a.ics", "F", "\"r1\"")
        ]);
    }

    [Fact]
    public async Task MoveAsync_RejectsCrossOriginRedirectBeforeSecondDispatch()
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
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Fact]
    public async Task MoveAsync_RejectsRedirectThatAliasesDestinationBeforeSecondDispatch()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://example.com/archive/a.ics") }
            });
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Fact]
    public async Task MoveAsync_AcceptsSafeRelativeMethodPreservingRedirect()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(sendCount == 1
                ? new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri("canonical.ics", UriKind.Relative) }
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.Dispatched);
        sendCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("encoded-dot")]
    public async Task MoveAsync_RejectsMalformedRedirectBeforeSecondDispatch(string variant)
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            if (variant == "encoded-dot")
                response.Headers.Location = new Uri("%2e%2e/private.ics", UriKind.Relative);
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Fact]
    public async Task MoveAsync_StopsAfterMaximumMethodPreservingRedirects()
    {
        var sendCount = 0;
        using var httpClient = new HttpClient(new Handler(request =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri($"https://example.com/tasks/redirect-{sendCount}.ics") }
            });
        }));
        var sut = new CalendarResourceMoveProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.MoveAsync(Request(), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(4);
    }

    private static CalendarResourceMoveDispatchRequest Request() => new(
        "https://example.com/tasks/a.ics",
        "https://example.com/archive/a.ics",
        "\"r1\"");

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
