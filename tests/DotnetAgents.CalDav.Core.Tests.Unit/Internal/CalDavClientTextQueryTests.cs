using System.Net;
using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalDavClientTextQueryTests
{
    private const string CalendarHref = "https://example.com/calendars/user/events/";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    [Fact]
    public async Task UnionsEveryBranchIntoCanonicalCandidatesWithoutTimeRangeOrCalendarData()
    {
        var requests = new List<(string Method, string Depth, string Body)>();
        var sut = CreateSut(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            requests.Add((request.Method.Method, string.Join(',', request.Headers.GetValues("Depth")), body));
            var branch = XDocument.Parse(body).Descendants(CalDav + "prop-filter").First().Attribute("name")!.Value;
            return MultiStatus(branch switch
            {
                "SUMMARY" => ["/calendars/user/events/", "/calendars/user/events/b.ics"],
                "DESCRIPTION" => ["https://example.com/calendars/user/events/a.ics", "/calendars/user/events/b.ics"],
                "CATEGORIES" => ["/calendars/user/events/c.ics"],
                _ => []
            });
        });

        var result = await sut.QueryTextCandidateHrefsAsync(
            CalendarHref,
            CalendarEntityKind.Event,
            Prefilter(new CalendarTextFilter("dentist", ["Health"])),
            CancellationToken.None);

        result.ShouldBeOfType<CalendarTextCandidateResult.Hrefs>().Values.ShouldBe([
            CalendarHref + "a.ics",
            CalendarHref + "b.ics",
            CalendarHref + "c.ics"
        ]);
        requests.Select(request => request.Method).ShouldAllBe(method => method == "REPORT");
        requests.Select(request => request.Depth).ShouldAllBe(depth => depth == "1");
        requests.Select(request => XDocument.Parse(request.Body).Descendants(CalDav + "prop-filter")
                .Select(filter => filter.Attribute("name")!.Value + "=" + filter.Value))
            .ShouldBe([
                ["SUMMARY=dentist", "CATEGORIES=health"],
                ["DESCRIPTION=dentist", "CATEGORIES=health"],
                ["LOCATION=dentist", "CATEGORIES=health"],
                ["CATEGORIES=dentist", "CATEGORIES=health"]
            ]);
        requests.ShouldAllBe(request => !request.Body.Contains("time-range", StringComparison.Ordinal)
            && !request.Body.Contains("calendar-data", StringComparison.Ordinal)
            && request.Body.Contains("name=\"VEVENT\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "supported-filter")]
    [InlineData(HttpStatusCode.Forbidden, "supported-collation")]
    [InlineData(HttpStatusCode.BadRequest, "supported-filter")]
    [InlineData(HttpStatusCode.MethodNotAllowed, null)]
    [InlineData(HttpStatusCode.NotImplemented, null)]
    public async Task VerifiedTextMatchUnavailabilityIsRetainedPerCalendarAndKind(
        HttpStatusCode status,
        string? precondition)
    {
        var requestCount = 0;
        var sut = CreateSut(request =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(precondition is null
                    ? string.Empty
                    : $"<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:{precondition}/></d:error>")
            });
        });
        var prefilter = Prefilter(new CalendarTextFilter("dentist"));

        var first = await sut.QueryTextCandidateHrefsAsync(
            CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None);
        var retained = await sut.QueryTextCandidateHrefsAsync(
            CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None);
        var otherKind = await sut.QueryTextCandidateHrefsAsync(
            CalendarHref, CalendarEntityKind.Todo, prefilter, CancellationToken.None);

        first.ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();
        retained.ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();
        otherKind.ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task TextMatchUnavailabilityDoesNotDowngradeTheCandidateQuery()
    {
        var bodies = new List<string>();
        var sut = CreateSut(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            bodies.Add(body);
            return body.Contains("text-match", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-filter/></d:error>")
                }
                : MultiStatus(["/calendars/user/events/a.ics"]);
        });

        var text = await sut.QueryTextCandidateHrefsAsync(
            CalendarHref,
            CalendarEntityKind.Event,
            Prefilter(new CalendarTextFilter("dentist")),
            CancellationToken.None);
        var candidates = await sut.QueryCandidateHrefsAsync(
            CalendarHref,
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None);

        text.ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();
        candidates.ShouldBe([CalendarHref + "a.ics"]);
        bodies.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConfigurationChangeForgetsTextMatchUnavailability()
    {
        var requestCount = 0;
        var options = new CalDavOptions { BaseUrl = "https://example.com/", Username = "first" };
        var sut = CreateSut(_ =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
        }, options);
        var prefilter = Prefilter(new CalendarTextFilter("dentist"));

        (await sut.QueryTextCandidateHrefsAsync(CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None))
            .ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();
        options.Username = "second";
        (await sut.QueryTextCandidateHrefsAsync(CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None))
            .ShouldBeOfType<CalendarTextCandidateResult.VerifiedUnavailable>();

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<d:error xmlns:d=\"DAV:\"><d:need-privileges/></d:error>")]
    public async Task UnrelatedForbiddenFailsWithoutRetainingUnavailability(string body)
    {
        var requestCount = 0;
        var sut = CreateSut(_ =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(body)
            });
        });
        var prefilter = Prefilter(new CalendarTextFilter("dentist"));

        (await Should.ThrowAsync<HttpRequestException>(() => sut.QueryTextCandidateHrefsAsync(
            CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Should.ThrowAsync<HttpRequestException>(() => sut.QueryTextCandidateHrefsAsync(
            CalendarHref, CalendarEntityKind.Event, prefilter, CancellationToken.None));

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("https://example.com/calendars/user/other/a.ics")]
    [InlineData("https://example.com/calendars/user/events/nested/a.ics")]
    [InlineData("https://elsewhere.example/calendars/user/events/a.ics")]
    public async Task CandidateOutsideTheAuthorizedCalendarIsAProtocolFailure(string href)
    {
        var sut = CreateSut(_ => Task.FromResult(MultiStatus([href])));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryTextCandidateHrefsAsync(
            CalendarHref,
            CalendarEntityKind.Event,
            Prefilter(new CalendarTextFilter(Categories: ["Health"])),
            CancellationToken.None));
    }

    [Fact]
    public async Task UnsafeCalendarHrefFailsBeforeAnyRequest()
    {
        var requestCount = 0;
        var sut = CreateSut(_ =>
        {
            requestCount++;
            return Task.FromResult(MultiStatus([]));
        });

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryTextCandidateHrefsAsync(
            "https://elsewhere.example/calendars/user/events/",
            CalendarEntityKind.Event,
            Prefilter(new CalendarTextFilter("dentist")),
            CancellationToken.None));

        requestCount.ShouldBe(0);
    }

    private static CalendarTextPrefilter Prefilter(CalendarTextFilter filter)
    {
        CalendarTextCriteria.TryCreate(filter, out var criteria).ShouldBeTrue();
        return criteria.ShouldNotBeNull().Prefilter;
    }

    private static HttpResponseMessage MultiStatus(IEnumerable<string> hrefs) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(
            "<d:multistatus xmlns:d=\"DAV:\">"
            + string.Concat(hrefs.Select(href => $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>"
                + "<d:getetag>\"r1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>"
                + "</d:response>"))
            + "</d:multistatus>",
            Encoding.UTF8,
            "application/xml")
    };

    private static CalDavClient CreateSut(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        CalDavOptions? options = null) => new(
        new HttpClient(new DelegateHandler(handler)),
        Options.Create(options ?? new CalDavOptions { BaseUrl = "https://example.com/" }),
        Substitute.For<ILogger<CalDavClient>>());

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await handler(request).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
    }
}
