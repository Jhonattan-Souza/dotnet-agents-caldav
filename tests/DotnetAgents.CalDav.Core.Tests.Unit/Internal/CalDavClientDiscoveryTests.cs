using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Configuration;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed partial class CalDavClientTests
{
    [Fact]
    public async Task Discovery_ConfiguredScopeSkipsUnrelatedExtensionsAndFollowsRelevantUnknownCollectionTypes()
    {
        const string home = "https://example.com/home/";
        var requests = new List<HttpRequestMessage>();
        var unrelated = CollectionMember("unrelated/", false).Replace("<d:collection/>",
            "<d:collection/><x:special xmlns:x=\"urn:example:extension\"/>", StringComparison.Ordinal);
        var relevant = CollectionMember("nested/", false).Replace("<d:collection/>",
            "<d:collection/><x:special xmlns:x=\"urn:example:extension\"/>", StringComparison.Ordinal);
        var sut = CreateSut(CreateSequencedHandler([
            CreateCalendarHomeSetResponse(home),
            DiscoveryXml(unrelated + relevant),
            DiscoveryXml(CollectionMember("events/", true))
        ], requests), new CalDavOptions { BaseUrl = home, CalendarHrefs = home + "nested/events/" });

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        result.ShouldHaveSingleItem().Href.ShouldBe(home + "nested/events/");
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe([home, home, home + "nested/"]);
    }

    [Fact]
    public async Task Discovery_UnrestrictedUnknownCollectionFailureDoesNotReturnPartialCalendars()
    {
        const string home = "https://example.com/home/";
        var requests = new List<HttpRequestMessage>();
        var extension = CollectionMember("extension/", false).Replace("<d:collection/>",
            "<d:collection/><x:special xmlns:x=\"urn:example:extension\"/>", StringComparison.Ordinal);
        var sut = CreateSut(CreateSequencedHandler([
            CreateCalendarHomeSetResponse(home),
            DiscoveryXml(CollectionMember("events/", true) + extension),
            new HttpResponseMessage(HttpStatusCode.NotImplemented)
        ], requests), home);

        var failure = await Should.ThrowAsync<HttpRequestException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        failure.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe([home, home, home + "extension/"]);
    }

    [Fact]
    public async Task Discovery_DoesNotTraverseStandardSchedulingCollections()
    {
        const string home = "https://example.com/home/";
        var inbox = CollectionMember("inbox/", false).Replace("<d:collection/>",
            "<d:collection/><c:schedule-inbox/>", StringComparison.Ordinal);
        var outbox = CollectionMember("outbox/", false).Replace("<d:collection/>",
            "<d:collection/><c:schedule-outbox/>", StringComparison.Ordinal);
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([
            CreateCalendarHomeSetResponse(home),
            DiscoveryXml(CollectionMember("events/", true) + inbox + outbox)
        ], requests), home);

        (await sut.GetCalendarsAsync(CancellationToken.None)).ShouldHaveSingleItem().Href.ShouldBe(home + "events/");
        requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discovery_RequestLimitReportsAttemptedRequestsRatherThanZeroCalendars()
    {
        const string home = "https://example.com/home/";
        var members = string.Concat(Enumerable.Range(0, 65).Select(index => CollectionMember($"nested-{index}/", false)));
        var responses = new List<HttpResponseMessage> { CreateCalendarHomeSetResponse(home), DiscoveryXml(members) };
        responses.AddRange(Enumerable.Range(0, 62).Select(_ => CreateEmptyMultiStatusResponse()));
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(responses, requests), home);

        var failure = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        failure.Dimension.ShouldBe("request_count");
        failure.Observed.ShouldBe(65);
        failure.Limit.ShouldBe(64);
        failure.HasCalendarCount.ShouldBeFalse();
        requests.Count.ShouldBe(64);
    }

    [Fact]
    public async Task Discovery_AggregateBytesFailWithoutReturningEarlierCalendars()
    {
        const string home = "https://example.com/home/";
        var members = string.Concat(Enumerable.Range(0, 5).Select(index => CollectionMember($"nested-{index}/", false)));
        var responses = new List<HttpResponseMessage> { CreateCalendarHomeSetResponse(home), DiscoveryXml(members) };
        var padding = "<x xmlns=\"urn:example\">" + new string('x', 4 * 1024 * 1024 - 1024) + "</x>";
        responses.AddRange(Enumerable.Range(0, 5).Select(_ => DiscoveryXml(padding)));
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(responses, requests), home);

        var failure = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(CancellationToken.None));

        failure.Dimension.ShouldBe("byte_count");
        failure.Observed.ShouldBeGreaterThan(16 * 1024 * 1024);
        failure.Limit.ShouldBe(16 * 1024 * 1024);
        failure.HasCalendarCount.ShouldBeFalse();
        requests.Count.ShouldBe(7);
    }

    [Fact]
    public async Task Discovery_WalksEveryHomeAndNestedCollectionWithCanonicalDeduplication()
    {
        const string home = "https://example.com/calendars/user/";
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([
            DiscoveryXml("<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set>"
                + "<d:href>/calendars/user/</d:href><d:href>/shared/</d:href><d:href>https://example.com/shared/</d:href>"
                + "</c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"),
            DiscoveryXml(CollectionMember(home, false) + CollectionMember("nested/", false) + CollectionMember("events/", true)),
            DiscoveryXml(CollectionMember("shared-events/", true)),
            DiscoveryXml(CollectionMember("deep-calendar/", true))
        ], requests), home);

        var result = await sut.DiscoverCalendarCollectionsAsync(CancellationToken.None);

        result.HomeSetHrefs.ShouldBe([home, "https://example.com/shared/"]);
        result.Items.Select(item => item.Href).ShouldBe([
            home + "events/", home + "nested/deep-calendar/", "https://example.com/shared/shared-events/"
        ]);
        requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Discovery_RejectsTooManyHomesBeforeTraversing()
    {
        var hrefs = string.Concat(Enumerable.Range(0, 17).Select(index => $"<d:href>/home/{index}/</d:href>"));
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([
            DiscoveryXml("<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set>" + hrefs
                + "</c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>")
        ], requests));

        await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(CancellationToken.None));
        requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Discovery_RejectsTraversalDepthWithoutReturningPartialCalendars()
    {
        const string home = "https://example.com/home/";
        var requests = new List<HttpRequestMessage>();
        var responses = new List<HttpResponseMessage> { CreateCalendarHomeSetResponse(home) };
        responses.AddRange(Enumerable.Range(0, 9).Select(_ => DiscoveryXml(
            CollectionMember("event/", true) + CollectionMember("nested/", false))));
        var sut = CreateSut(CreateSequencedHandler(responses, requests), home);

        var failure = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(CancellationToken.None));
        failure.Dimension.ShouldBe("depth");
        failure.Observed.ShouldBe(9);
        failure.Limit.ShouldBe(8);
        failure.HasCalendarCount.ShouldBeFalse();
        requests.Count.ShouldBe(10);
    }

    [Fact]
    public async Task Discovery_RejectsResponseBeyondByteBudget()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler([
            new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent(new string('x', 4 * 1024 * 1024 + 1)) }
        ], requests));

        await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(CancellationToken.None));
        requests.Count.ShouldBe(1);
    }

    private static string CollectionMember(string href, bool calendar) =>
        $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/>"
        + (calendar ? "<c:calendar/>" : string.Empty)
        + "</d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static HttpResponseMessage DiscoveryXml(string responses) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
            + responses + "</d:multistatus>", Encoding.UTF8, "application/xml")
    };
}
