using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed partial class CalDavClientTests
{
    private const string ICloudEndpoint = "https://caldav.icloud.test/";
    private const string ICloudHome = "https://p42-caldav.icloud.test/1234/calendars/";
    private const string ICloudCalendar = ICloudHome + "events/";

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task GetCalendarsAsync_FollowsRedirectToAllowlistedHostAndKeepsItsCalendars(HttpStatusCode status)
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return request.RequestUri!.AbsoluteUri switch
            {
                ICloudEndpoint => Redirect(status, "https://p42-caldav.icloud.test/"),
                "https://p42-caldav.icloud.test/" => HomeSet(ICloudHome),
                ICloudHome => DiscoveryXml(CollectionMember("events/", true)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }), ICloudOptions());

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem().Href.ShouldBe(ICloudCalendar);
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe(
        [
            ICloudEndpoint,
            "https://p42-caldav.icloud.test/",
            "https://caldav.icloud.test/.well-known/caldav",
            ICloudHome
        ]);
        requests.ShouldAllBe(request => request.Method.Method == "PROPFIND");
        requests[1].Headers.GetValues("Depth").ShouldBe(["0"]);
        (await requests[1].Content!.ReadAsStringAsync(CancellationToken.None)).ShouldContain("calendar-home-set");
    }

    [Fact]
    public async Task GetCalendarsAsync_AcceptsCalendarHomeSetOnAllowlistedHostWithoutRedirect()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            HomeSet("https://p42-caldav.icloud.test:443/1234/calendars/"),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            DiscoveryXml(CollectionMember(ICloudCalendar, true))
        ], requests), ICloudOptions());

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem().Href.ShouldBe(ICloudCalendar);
        requests[^1].RequestUri!.AbsoluteUri.ShouldBe(ICloudHome);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("https://icloud.test.evil.test/")]
    [InlineData("http://p42-caldav.icloud.test/")]
    [InlineData("http://caldav.icloud.test/")]
    [InlineData("https://p42-caldav.icloud.test:8443/")]
    public async Task GetCalendarsAsync_RefusesRedirectOutsideAccountOriginsWithoutSendingIt(string location)
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            Redirect(HttpStatusCode.PermanentRedirect, location),
            HomeSet(ICloudHome)
        ], requests), ICloudOptions());

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri.ShouldBe(ICloudEndpoint);
    }

    [Fact]
    public async Task GetCalendarsAsync_WithoutAllowlistRefusesTheICloudHostRedirect()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            Redirect(HttpStatusCode.MovedPermanently, "https://p42-caldav.icloud.test/"),
            HomeSet(ICloudHome)
        ], requests), ICloudEndpoint);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetCalendarsAsync_RejectsMemberOnAnotherAllowlistedOriginThanItsCollection()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            HomeSet(ICloudHome),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            DiscoveryXml(CollectionMember("https://p43-caldav.icloud.test/1234/calendars/events/", true))
        ], requests), ICloudOptions());

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetCalendarsAsync_RejectsSeeOtherOnTheConfiguredProbeWithoutAnotherRequest()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            Redirect(HttpStatusCode.SeeOther, "/calendars/user/"),
            HomeSet("/calendars/user/")
        ], requests), "https://example.com/");

        var failure = await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        failure.Message.ShouldContain("303");
        requests.ShouldHaveSingleItem().Method.Method.ShouldBe("PROPFIND");
    }

    [Fact]
    public async Task GetCalendarsAsync_RejectsSeeOtherOnTheWellKnownProbeWithoutReplayingPropfind()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            CreateEmptyMultiStatusResponse(),
            Redirect(HttpStatusCode.SeeOther, "/calendars/user/"),
            HomeSet("/calendars/user/")
        ], requests), "https://example.com/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe(
            ["https://example.com/", "https://example.com/.well-known/caldav"]);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_ReportsAgainstAllowlistedCalendarOrigin()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
            [ReportXml("/1234/calendars/events/a.ics")],
            requests), ICloudOptions());

        var hrefs = await sut.QueryCandidateHrefsAsync(
            ICloudCalendar, CalendarEntityKind.Event, null, null, CancellationToken.None);

        hrefs.ShouldBe([ICloudCalendar + "a.ics"]);
        requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri.ShouldBe(ICloudCalendar);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_RejectsCandidateOnAnotherAllowlistedOrigin()
    {
        var sut = CreateSut(CreateSequencedHandler(
            [ReportXml("https://p43-caldav.icloud.test/1234/calendars/events/a.ics")],
            []), ICloudOptions());

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            ICloudCalendar, CalendarEntityKind.Event, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_WithoutAllowlistRefusesTheICloudCalendarBeforeRequest()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([ReportXml("a.ics")], requests), ICloudEndpoint);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            ICloudCalendar, CalendarEntityKind.Event, null, null, CancellationToken.None));

        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCalendarResourceAsync_ReadsFromAllowlistedOrigin()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar")
            }
        ], requests), ICloudOptions());

        var read = await sut.GetCalendarResourceAsync(ICloudCalendar + "a.ics", CancellationToken.None);

        read.Code.ShouldBe(CalendarResourceReadCode.Success);
        requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri.ShouldBe(ICloudCalendar + "a.ics");
    }

    [Fact]
    public async Task ConditionalWrites_DispatchToAllowlistedOrigin()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(request.Method == HttpMethod.Put
                ? HttpStatusCode.Created
                : HttpStatusCode.NoContent);
        }), ICloudOptions());
        var body = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");

        (await sut.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(ICloudCalendar, ICloudCalendar + "new.ics", body),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceCreateCode.Dispatched);
        (await sut.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(ICloudCalendar + "a.ics", "\"r1\"", body),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceUpdateDispatchCode.Dispatched);
        (await sut.DeleteCalendarResourceAsync(
            new CalendarResourceDeleteRequest(ICloudCalendar + "a.ics", "\"r2\""),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceDeleteDispatchCode.Dispatched);
        (await sut.MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(ICloudCalendar + "a.ics", ICloudHome + "todos/a.ics", "\"r3\""),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceMoveDispatchCode.Dispatched);

        requests.Select(request => request.Method.Method).ShouldBe(["PUT", "PUT", "DELETE", "MOVE"]);
        requests.ShouldAllBe(request => request.RequestUri!.Host == "p42-caldav.icloud.test");
    }

    [Fact]
    public async Task MoveCalendarResourceAsync_RefusesDestinationOnAnotherAllowlistedOrigin()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([new HttpResponseMessage(HttpStatusCode.Created)], requests), ICloudOptions());

        var result = await sut.MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(
                ICloudCalendar + "a.ics",
                "https://p43-caldav.icloud.test/1234/calendars/todos/a.ics",
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.InvalidInput);
        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConditionalWrites_WithoutAllowlistRefuseTheICloudOriginBeforeRequest()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([new HttpResponseMessage(HttpStatusCode.Created)], requests), ICloudEndpoint);

        (await sut.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(ICloudCalendar + "a.ics", "\"r1\"", new byte[] { 0x42 }),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceUpdateDispatchCode.InvalidInput);
        (await sut.DeleteCalendarResourceAsync(
            new CalendarResourceDeleteRequest(ICloudCalendar + "a.ics", "\"r1\""),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceDeleteDispatchCode.InvalidInput);

        requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://p43-caldav.icloud.test/1234/calendars/events/a.ics")]
    [InlineData("https://evil.test/1234/calendars/events/a.ics")]
    public async Task UpdateCalendarResourceAsync_DoesNotFollowWriteRedirectOffTheCurrentOrigin(string location)
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(
        [
            Redirect(HttpStatusCode.TemporaryRedirect, location),
            new HttpResponseMessage(HttpStatusCode.NoContent)
        ], requests), ICloudOptions());

        var result = await sut.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(ICloudCalendar + "a.ics", "\"r1\"", new byte[] { 0x42 }),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceUpdateDispatchCode.UpstreamProtocolError);
        requests.ShouldHaveSingleItem();
    }

    private static CalDavOptions ICloudOptions() => new()
    {
        BaseUrl = ICloudEndpoint,
        RedirectHosts = ".icloud.test"
    };

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location) => new(status)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };

    private static HttpResponseMessage HomeSet(string homeHref) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent($"""
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>{homeHref}</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """, Encoding.UTF8, "application/xml")
    };

    private static HttpResponseMessage ReportXml(string href) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(
            $"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getetag>\"r1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
            Encoding.UTF8,
            "application/xml")
    };
}
