using System.Net;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarResourceUpdateProtocolTests
{
    [Theory]
    [InlineData("relative.ics", "\"r1\"")]
    [InlineData("https://other.example/events/a.ics", "\"r1\"")]
    [InlineData("https://example.com/events/a.ics?secret=true", "\"r1\"")]
    [InlineData("https://example.com/events/a.ics", "")]
    [InlineData("https://example.com/events/a.ics", "r1")]
    [InlineData("https://example.com/events/a.ics", "W/\"r1\"")]
    [InlineData("https://example.com/events/a.ics", "*")]
    public async Task UpdateAsync_RejectsUnsafeHrefAndNonExactStrongTagBeforeDispatch(
        string href,
        string entityTag)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest(href, entityTag, new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.InvalidInput);
        sendCount.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAsync_RejectsEmptyAuthoritativeBodyBeforeDispatch()
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest(
                "https://example.com/events/a.ics",
                "\"r1\"",
                ReadOnlyMemory<byte>.Empty),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.InvalidInput);
        sendCount.ShouldBe(0);
    }

    [Fact]
    public async Task UpdateAsync_SendsExactConditionalPutBody()
    {
        HttpRequestMessage? observed = null;
        byte[]? body = null;
        using var client = new HttpClient(new Handler(async request =>
        {
            observed = request;
            body = await request.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));
        var authoritative = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray();

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", authoritative),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.Dispatched);
        observed!.Method.ShouldBe(HttpMethod.Put);
        observed.Headers.IfMatch.Single().ToString().ShouldBe("\"r1\"");
        observed.Content!.Headers.ContentType!.ToString().ShouldBe("text/calendar; charset=utf-8");
        body.ShouldBe(authoritative);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, CalendarResourceUpdateDispatchCode.Dispatched)]
    [InlineData(HttpStatusCode.Created, CalendarResourceUpdateDispatchCode.Dispatched)]
    [InlineData(HttpStatusCode.NoContent, CalendarResourceUpdateDispatchCode.Dispatched)]
    [InlineData(HttpStatusCode.Accepted, CalendarResourceUpdateDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.MultiStatus, CalendarResourceUpdateDispatchCode.UpstreamProtocolError)]
    [InlineData(HttpStatusCode.NotFound, CalendarResourceUpdateDispatchCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, CalendarResourceUpdateDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed, CalendarResourceUpdateDispatchCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, CalendarResourceUpdateDispatchCode.UpstreamUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CalendarResourceUpdateDispatchCode.UpstreamForbidden)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CalendarResourceUpdateDispatchCode.PayloadTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarResourceUpdateDispatchCode.UpstreamRateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, CalendarResourceUpdateDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CalendarResourceUpdateDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.NotImplemented, CalendarResourceUpdateDispatchCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.InsufficientStorage, CalendarResourceUpdateDispatchCode.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CalendarResourceUpdateDispatchCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.BadRequest, CalendarResourceUpdateDispatchCode.UpstreamProtocolError)]
    public async Task UpdateAsync_MapsMutationRelevantStatuses(
        HttpStatusCode status,
        CalendarResourceUpdateDispatchCode expected)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expected);
    }

    [Fact]
    public async Task UpdateAsync_replays_the_exact_put_across_a_safe_method_preserving_redirect()
    {
        var requests = new List<(Uri Uri, string IfMatch, byte[] Body)>();
        using var client = new HttpClient(new Handler(async request =>
        {
            requests.Add((
                request.RequestUri!,
                request.Headers.IfMatch.Single().ToString(),
                await request.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://example.com/canonical/a.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));
        var authoritative = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray();

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", authoritative),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.Dispatched);
        requests.Select(request => request.Uri.AbsoluteUri).ShouldBe([
            "https://example.com/events/a.ics",
            "https://example.com/canonical/a.ics"
        ]);
        requests.ShouldAllBe(request => request.IfMatch == "\"r1\"" && request.Body.SequenceEqual(authoritative));
    }

    [Theory]
    [InlineData("https://other.example/a.ics")]
    [InlineData("https://example.com/a%2Fescape.ics")]
    [InlineData("https://user@example.com/a.ics")]
    public async Task UpdateAsync_rejects_unsafe_redirect_before_a_second_put(string location)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            {
                Headers = { Location = new Uri(location) }
            });
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamProtocolError);
        calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    public async Task UpdateAsync_RejectsNonMethodPreservingRedirectWithoutSecondPut(HttpStatusCode statusCode)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Headers = { Location = new Uri("https://example.com/canonical/a.ics") }
            });
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(3, CalendarResourceUpdateDispatchCode.Dispatched, 4)]
    [InlineData(4, CalendarResourceUpdateDispatchCode.UpstreamProtocolError, 4)]
    public async Task UpdateAsync_EnforcesExactThreeRedirectCeiling(
        int redirectResponses,
        CalendarResourceUpdateDispatchCode expectedCode,
        int expectedSendCount)
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(sendCount <= redirectResponses
                ? new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri($"https://example.com/events/{sendCount}.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        sendCount.ShouldBe(expectedSendCount);
    }

    [Fact]
    public async Task UpdateAsync_RejectsMethodPreservingRedirectWithoutLocation()
    {
        var sendCount = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            sendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamProtocolError);
        sendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(3, 3_000)]
    [InlineData(3_000_000, int.MaxValue)]
    public async Task UpdateAsync_RateLimitPreservesBoundedDeltaRetryAfter(
        int delaySeconds,
        int expectedMilliseconds)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(delaySeconds));
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamRateLimited);
        result.RetryAfterMilliseconds.ShouldBe(expectedMilliseconds);
    }

    [Theory]
    [InlineData(3, 3_000)]
    [InlineData(-3, 0)]
    public async Task UpdateAsync_RateLimitMeasuresHttpDateRetryAfterFromInjectedClock(
        int offsetSeconds,
        int expectedMilliseconds)
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                now.AddSeconds(offsetSeconds));
            return Task.FromResult(response);
        }));
        var sut = new CalendarResourceUpdateProtocol(
            client,
            new Uri("https://example.com"),
            new FrozenTimeProvider(now));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamRateLimited);
        result.RetryAfterMilliseconds.ShouldBe(expectedMilliseconds);
    }

    [Fact]
    public async Task UpdateAsync_transport_failure_after_send_is_possibly_dispatched_and_not_retried()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            throw new HttpRequestException("connection lost");
        }));
        var sut = new CalendarResourceUpdateProtocol(client, new Uri("https://example.com"));

        var result = await sut.UpdateAsync(
            new CalendarResourceUpdateRequest("https://example.com/events/a.ics", "\"r1\"", new byte[] { 1 }),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.PossiblyDispatched);
        calls.ShouldBe(1);
    }

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
