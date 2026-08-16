using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarResourceCreateProtocolTests
{
    [Theory]
    [InlineData("https://example.com/calendars/events", "caller/uid", "https://example.com/calendars/events/nsHSSPA0YMkwhwKMFnwJ9Oi9sx5sZI4m8LbvGvALNGc.ics")]
    [InlineData("https://example.com/calendars/events/", "caller\\uid", "https://example.com/calendars/events/KGbA5uA0LZp2Zgus89HvS4sSFMxwQ-DYTvrERkPiDNY.ics")]
    public void BuildResourceHref_DerivesOpaqueDirectResourceTargetIndependentOfUidText(
        string calendarHref,
        string uid,
        string expected)
    {
        CalendarResourceCreateProtocol.BuildResourceHref(calendarHref, uid).ShouldBe(expected);
        expected.ShouldNotContain("%2F", Case.Insensitive);
        expected.ShouldNotContain("%5C", Case.Insensitive);
    }

    [Fact]
    public async Task CreateAsync_ReplaysConditionalPutAcrossMethodPreservingRedirect()
    {
        var body = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var requests = new List<(string Href, string Method, string IfNoneMatch, byte[] Body)>();
        using var httpClient = new HttpClient(new Handler(async request =>
        {
            requests.Add((
                request.RequestUri!.AbsoluteUri,
                request.Method.Method,
                request.Headers.IfNoneMatch.Single().ToString(),
                await request.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)));
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("https://example.com/calendars/user/events/canonical.ics") }
                }
                : new HttpResponseMessage(HttpStatusCode.Created);
        }));
        var sut = new CalendarResourceCreateProtocol(httpClient, new Uri("https://example.com"));

        var result = await sut.CreateAsync(
            new CalendarResourceCreateRequest(
                "https://example.com/calendars/user/events/",
                "https://example.com/calendars/user/events/new.ics",
                body),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceCreateCode.Dispatched);
        result.ResourceHref.ShouldBe("https://example.com/calendars/user/events/canonical.ics");
        requests.Select(request => (request.Href, request.Method, request.IfNoneMatch)).ShouldBe([
            ("https://example.com/calendars/user/events/new.ics", "PUT", "*"),
            ("https://example.com/calendars/user/events/canonical.ics", "PUT", "*")
        ]);
        requests.ShouldAllBe(request => request.Body.SequenceEqual(body));
    }

    private sealed class Handler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
