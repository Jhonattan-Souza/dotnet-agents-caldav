using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public class CalendarSyncReportParserTests
{
    private const string CalendarHref = "https://cal.example/calendars/user/events/";
    private const string MemberPath = "/calendars/user/events/a.ics";
    private const string TruncationStatus = "<d:status>HTTP/1.1 507 Insufficient Storage</d:status>";
    private const string TruncationError = "<d:error><d:number-of-matches-within-limits/></d:error>";

    [Fact]
    public void ReturnsChangedMetadataAndOnlyResponseLevelRemovals()
    {
        var page = Parse(Wrap(Changed(MemberPath, "&quot;r1&quot;")
            + Changed("b%20c.ics", "W/&quot;r2&quot;")
            + Response("deleted.ics", "<d:status>HTTP/1.1 404 Not Found</d:status>")));

        page.Changes.ShouldBe(new[]
        {
            new CalendarResourceChange(CalendarHref + "a.ics", "changed", "\"r1\""),
            new CalendarResourceChange(CalendarHref + "b%20c.ics", "changed", "W/\"r2\""),
            new CalendarResourceChange(CalendarHref + "deleted.ics", "removed")
        });
        page.HasMore.ShouldBeFalse();
        page.SyncToken.ShouldBe("urn:sync:next");
    }

    [Theory]
    [InlineData("")]
    [InlineData(TruncationError)]
    public void NativeSelf507AndAdvancingTokenAllowContinuation(string error)
    {
        var page = Parse(Wrap(Changed(MemberPath) + Response(CalendarHref,
            TruncationStatus + error)), pageSize: 1);

        page.HasMore.ShouldBeTrue();
        page.Changes.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("/calendars/user/events")]
    [InlineData("https://cal.example/calendars/user/events")]
    [InlineData("https://CAL.EXAMPLE:443/calendars/user/events")]
    public void SlashlessCollectionIdentityRetainsNativePagination(string href)
    {
        var page = Parse(Wrap(Changed(MemberPath) + Response(href, TruncationStatus)), pageSize: 1);

        page.HasMore.ShouldBeTrue();
        page.Changes.Single().Href.ShouldBe(CalendarHref + "a.ics");
        page.SyncToken.ShouldBe("urn:sync:next");
    }

    [Theory]
    [InlineData(CalendarHref, "/calendars/user/events")]
    [InlineData("/calendars/user/events", CalendarHref)]
    public void SlashlessAndSlashedSelfResponsesAreOneDuplicateIdentity(string firstHref, string secondHref)
    {
        var body = Wrap(Response(firstHref, TruncationStatus) + Response(secondHref, TruncationStatus));

        Should.Throw<CalendarProtocolException>(() => Parse(body)).Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("/calendars/user/events-other")]
    [InlineData("/calendars/user/events.ics")]
    [InlineData("/calendars/user/events//")]
    [InlineData("/calendars/user/Events")]
    [InlineData("https://other.example/calendars/user/events")]
    [InlineData("http://cal.example/calendars/user/events")]
    [InlineData("https://cal.example:8443/calendars/user/events")]
    [InlineData("https://user:secret@cal.example/calendars/user/events")]
    [InlineData("/calendars/user/events?query=1")]
    [InlineData("/calendars/user/events#fragment")]
    [InlineData("/calendars/user/%2E/events")]
    [InlineData("/calendars/user%2Fevents")]
    public void SlashNormalizationCannotAuthorizeAnotherTruncationIdentity(string href)
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(Response(href, TruncationStatus))))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void UnchangedReportMayReuseItsTokenButChangedOrTruncatedReportCannot()
    {
        Parse(Wrap(string.Empty), priorToken: "urn:sync:next").Changes.ShouldBeEmpty();
        foreach (var responses in new[]
        {
            Changed(MemberPath),
            Response(CalendarHref, TruncationStatus),
            Response(CalendarHref, TruncationStatus + TruncationError)
        })
            Should.Throw<CalendarProtocolException>(() => Parse(Wrap(responses), priorToken: "urn:sync:next"))
                .Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void ResponseCountMustRespectRequestedPageSizeWithoutLocalSlicing()
    {
        var content = Wrap(Changed(MemberPath) + Changed("b.ics"));
        Should.Throw<CalendarProtocolException>(() => Parse(content, pageSize: 1)).Code.ShouldBe("limit_exhausted");
    }

    [Theory]
    [InlineData("<d:error/>")]
    [InlineData("<d:error>failure</d:error>")]
    [InlineData("<d:error>failure<x:diagnostic xmlns:x='urn:other'/></d:error>")]
    [InlineData("<d:error><d:valid-sync-token/></d:error>")]
    [InlineData("<d:error><d:supported-report/></d:error>")]
    [InlineData("<d:error><d:sync-traversal-supported/></d:error>")]
    [InlineData("<d:error><d:need-privileges/></d:error>")]
    [InlineData("<d:error><d:quota-not-exceeded/></d:error>")]
    [InlineData("<d:error><d:sufficient-disk-space/></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits>quota failure</d:number-of-matches-within-limits></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits> </d:number-of-matches-within-limits></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits/><d:number-of-matches-within-limits/></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits/><d:quota-not-exceeded/></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits/></d:error><d:error><d:number-of-matches-within-limits/></d:error>")]
    public void MalformedOrContradictoryCollection507FailsDespiteChangesAndAnAdvancingToken(string error)
    {
        var body = Wrap(Changed(MemberPath) + Response(CalendarHref, TruncationStatus + error), "urn:sync:advanced");

        Should.Throw<CalendarProtocolException>(() => Parse(body, priorToken: "urn:sync:prior", pageSize: 1))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("<d:error>\n <d:number-of-matches-within-limits/>\n <x:message xmlns:x='urn:diagnostic'>Report was limited.</x:message>\n</d:error>")]
    [InlineData("<d:error><d:unknown-condition/></d:error>")]
    [InlineData("<d:error><x:number-of-matches-within-limits xmlns:x='urn:other'/></d:error>")]
    [InlineData("<d:error><x:quota-not-exceeded xmlns:x='urn:other'/></d:error>")]
    [InlineData("<d:error><d:wrapper><d:number-of-matches-within-limits/></d:wrapper></d:error>")]
    [InlineData("<d:error><x:wrapper xmlns:x='urn:other'><d:quota-not-exceeded/></x:wrapper></d:error>")]
    [InlineData("<x:error xmlns:x='urn:other'><d:quota-not-exceeded/></x:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits><d:quota-not-exceeded/></d:number-of-matches-within-limits></d:error>")]
    [InlineData("<d:error><d:number-of-matches-within-limits><x:diagnostic xmlns:x='urn:other'>limited</x:diagnostic></d:number-of-matches-within-limits></d:error>")]
    public void Self507IgnoresUnknownExtensionsWithoutRequiringTheOptionalMarker(string error)
    {
        var body = Wrap(Changed(MemberPath) + Response(CalendarHref, TruncationStatus + error), "urn:sync:advanced");

        var page = Parse(body, priorToken: "urn:sync:prior", pageSize: 1);

        page.HasMore.ShouldBeTrue();
        page.Changes.Count.ShouldBe(1);
        page.SyncToken.ShouldBe("urn:sync:advanced");
    }

    [Fact]
    public void MaximumPageOverflowRetainsStateWithoutSuggestingALargerUnsupportedPageSize()
    {
        var content = Wrap(string.Concat(Enumerable.Range(0, 501).Select(index => Changed($"{index}.ics"))));

        var error = Should.Throw<CalendarProtocolException>(() => Parse(content, pageSize: 500));

        error.Code.ShouldBe("limit_exhausted");
        error.Message.ShouldContain("maximum pageSize of 500");
        error.Message.ShouldContain("retain the prior checkpoint");
        error.Message.ShouldNotContain("larger pageSize");
    }

    [Theory]
    [InlineData("<d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>")]
    [InlineData("<d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag></d:prop><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat>")]
    [InlineData("<d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat>")]
    [InlineData("<d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag></d:prop></d:propstat>")]
    [InlineData("<d:propstat><d:prop/><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat>")]
    [InlineData("<d:status>HTTP/1.1 200 OK</d:status>")]
    [InlineData("<d:status>HTTP/1.1 403 Forbidden</d:status>")]
    [InlineData("<d:status>HTTP/1.1 507 Insufficient Storage</d:status>")]
    [InlineData("<d:status>HTTP/1.1 404 Not Found</d:status><d:status>HTTP/1.1 200 OK</d:status>")]
    [InlineData("<d:status><d:status>HTTP/1.1 404 Not Found</d:status></d:status>")]
    [InlineData("<d:status/>")]
    [InlineData("")]
    public void FailedOrMissingMemberStateNeverAdvancesCheckpoint(string memberContent)
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(Response(MemberPath, memberContent))))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("missing-quotes")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("&quot;r1&quot;,&quot;r2&quot;")]
    [InlineData("&quot;r1&#10;r2&quot;")]
    [InlineData("<d:getetag>&quot;r1&quot;</d:getetag>")]
    public void MalformedEntityTagCannotBecomeAnObservedRevision(string etag)
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(Changed(MemberPath, etag))))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/a.ics")]
    [InlineData("http://cal.example/calendars/user/events/a.ics")]
    [InlineData("https://cal.example:8443/calendars/user/events/a.ics")]
    [InlineData("https://user:secret@cal.example/calendars/user/events/a.ics")]
    [InlineData("/calendars/user/other/a.ics")]
    [InlineData("/calendars/user/events/nested/a.ics")]
    [InlineData("/calendars/user/events/nested/")]
    [InlineData("../events/a.ics")]
    [InlineData("./a.ics")]
    [InlineData("a%2fb.ics")]
    [InlineData("%2E%2E/a.ics")]
    [InlineData("a%5Cb.ics")]
    [InlineData("a\\b.ics")]
    [InlineData("a.ics?query=1")]
    [InlineData("a.ics#fragment")]
    [InlineData("a b.ics")]
    [InlineData("")]
    public void RejectsUnsafeOrOutOfCollectionResourceIdentities(string href)
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(Changed(href)))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void RejectsMissingNestedDuplicateOrOverlongHrefs()
    {
        var missing = Changed(MemberPath).Replace($"<d:href>{MemberPath}</d:href>", string.Empty, StringComparison.Ordinal);
        var nested = Changed("<d:href>a.ics</d:href>");
        var duplicate = Changed(MemberPath).Replace("</d:href>", "</d:href><d:href>b.ics</d:href>", StringComparison.Ordinal);
        foreach (var response in new[] { missing, nested, duplicate, Changed(new string('a', 8193)) })
            Should.Throw<CalendarProtocolException>(() => Parse(Wrap(response))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void RejectsDuplicateIdentitiesAndConflictingResponseAndPropertyStatus()
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(Changed(MemberPath) + Changed("a.ics"))))
            .Code.ShouldBe("upstream_protocol_error");
        var conflicting = Changed(MemberPath).Replace("</d:response>", "<d:status>HTTP/1.1 404 Not Found</d:status></d:response>", StringComparison.Ordinal);
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(conflicting))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void RejectsDuplicateOrConflictingGetetagObservations()
    {
        var etag = "<d:getetag>&quot;r1&quot;</d:getetag>";
        var duplicateProperty = Changed(MemberPath).Replace(etag, etag + etag, StringComparison.Ordinal);
        var duplicatePropstat = Changed(MemberPath).Replace("</d:response>", Propstat(etag) + "</d:response>", StringComparison.Ordinal);
        foreach (var response in new[] { duplicateProperty, duplicatePropstat, Changed(MemberPath, new string('a', 4097)) })
            Should.Throw<CalendarProtocolException>(() => Parse(Wrap(response))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void IgnoresUnrequestedPropertyFailureAndNestedExtensionResponses()
    {
        var extra = "<d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>";
        var member = Changed(MemberPath).Replace("</d:response>", extra + "</d:response>", StringComparison.Ordinal);
        var extension = "<x:extension xmlns:x='urn:extension'>" + Changed("https://other.example/a.ics") + "</x:extension>";

        Parse(Wrap(member + extension)).Changes.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(CalendarHref)]
    [InlineData("/calendars/user/events")]
    public void RejectsCollectionSelfResponsesWithoutUnambiguousTruncation(string href)
    {
        foreach (var response in new[]
        {
            Changed(href), Response(href, "<d:status>HTTP/1.1 200 OK</d:status>"),
            Response(href, "<d:status>HTTP/1.1 404 Not Found</d:status>"),
            Response(href, "<d:status>HTTP/1.1 507 Insufficient Storage</d:status><d:propstat/>")
        })
            Should.Throw<CalendarProtocolException>(() => Parse(Wrap(response))).Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("<d:sync-token/>")]
    [InlineData("")]
    [InlineData("<d:sync-token>urn:sync:a</d:sync-token><d:sync-token>urn:sync:b</d:sync-token>")]
    [InlineData("<d:sync-token>not-a-uri</d:sync-token>")]
    [InlineData("<d:sync-token>urn:sync:a b</d:sync-token>")]
    [InlineData("<d:sync-token><d:sync-token>urn:sync:a</d:sync-token></d:sync-token>")]
    public void RejectsMissingOrConflictingNativeTokens(string tokenElement)
    {
        var body = "<d:multistatus xmlns:d='DAV:'>" + tokenElement + "</d:multistatus>";
        Should.Throw<CalendarProtocolException>(() => Parse(body)).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void RejectsWrongRootOversizedTokenAndMalformedStatus()
    {
        Should.Throw<CalendarProtocolException>(() => Parse("<d:error xmlns:d='DAV:'/>")).Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(string.Empty, "urn:sync:" + new string('a', 8192))))
            .Code.ShouldBe("upstream_protocol_error");
        Should.Throw<XmlException>(() => Parse(Wrap(Response(MemberPath, "<d:status>404</d:status>"))));
    }

    [Fact]
    public void RejectsDoctypeAndHonorsCancellation()
    {
        Should.Throw<XmlException>(() => Parse("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///etc/passwd'>]>" + Wrap(string.Empty)));
        Should.Throw<OperationCanceledException>(() => CalendarSyncReportParser.Parse(
            Encoding.UTF8.GetBytes(Wrap(Changed(MemberPath))), CalendarHref, string.Empty, 100, new CancellationToken(true)));
    }

    private static CalendarSyncPage Parse(string body, string priorToken = "", int pageSize = 100) =>
        CalendarSyncReportParser.Parse(Encoding.UTF8.GetBytes(body), CalendarHref, priorToken, pageSize, CancellationToken.None);

    private static string Wrap(string responses, string token = "urn:sync:next") =>
        "<d:multistatus xmlns:d='DAV:'>" + responses + "<d:sync-token>" + token + "</d:sync-token></d:multistatus>";

    private static string Changed(string href, string etag = "&quot;r1&quot;") => Response(href, Propstat("<d:getetag>" + etag + "</d:getetag>"));

    private static string Propstat(string properties) => "<d:propstat><d:prop>" + properties + "</d:prop>"
        + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat>";

    private static string Response(string href, string content) => "<d:response><d:href>" + href + "</d:href>" + content + "</d:response>";
}
