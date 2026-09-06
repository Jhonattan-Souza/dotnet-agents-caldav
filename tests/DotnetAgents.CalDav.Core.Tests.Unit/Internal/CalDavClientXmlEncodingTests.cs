using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed partial class CalDavClientTests
{
    [Theory]
    [InlineData("utf-16", null, true)]
    [InlineData("utf-16be", null, true)]
    [InlineData("utf-16", "us-ascii", true)]
    [InlineData("utf-8", "x-unknown-charset", true)]
    [InlineData("utf-16be", "utf-16be", false)]
    [InlineData("iso-8859-1", "iso-8859-1", false)]
    public async Task Discovery_UsesXmlEncodingPrecedenceForHomeAndCalendarProperties(string encoding, string? charset, bool bom)
    {
        const string home = "https://example.com/home/";
        var requests = new List<HttpRequestMessage>();
        var listing = CollectionMember("events/", true).Replace("</d:resourcetype>",
            "</d:resourcetype><d:displayname>café</d:displayname>", StringComparison.Ordinal);
        var responses = new List<HttpResponseMessage>
        {
            EncodedXmlResponse(EncodedDiscoveryBody(HomeProperty(home)), encoding, charset, bom),
            EncodedXmlResponse(EncodedDiscoveryBody(listing), encoding, charset, bom)
        };
        var sut = CreateSut(CreateSequencedHandler(responses, requests), home);

        var result = await sut.GetCalendarsAsync(TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem().DisplayName.ShouldBe("café");
        result[0].Href.ShouldBe(home + "events/");
        requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discovery_UsesDeclarationOnlyEncodingWithoutUtf8Roundtrip()
    {
        const string home = "https://example.com/home/";
        var requests = new List<HttpRequestMessage>();
        var listing = CollectionMember("events/", true).Replace("</d:resourcetype>",
            "</d:resourcetype><d:displayname>café</d:displayname>", StringComparison.Ordinal);
        var sut = CreateSut(CreateSequencedHandler([
            EncodedXmlResponse(EncodedDiscoveryBody(HomeProperty(home), "iso-8859-1"), "iso-8859-1", null),
            EncodedXmlResponse(EncodedDiscoveryBody(listing, "iso-8859-1"), "iso-8859-1", null)
        ], requests), home);

        (await sut.GetCalendarsAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().DisplayName.ShouldBe("café");
        requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Discovery_CountsUtf16BodyBytesBeforeReturningPartialCalendars()
    {
        const string home = "https://example.com/home/";
        var members = string.Concat(Enumerable.Range(0, 5).Select(index => CollectionMember($"nested-{index}/", false)));
        var responses = new List<HttpResponseMessage> { CreateCalendarHomeSetResponse(home), DiscoveryXml(members) };
        var padded = EncodedDiscoveryBody("<x xmlns='urn:padding'>" + new string('a', 2 * 1024 * 1024 - 1024) + "</x>");
        responses.AddRange(Enumerable.Range(0, 5).Select(_ => EncodedXmlResponse(padded, "utf-16", "utf-16")));
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(CreateSequencedHandler(responses, requests), home);

        var error = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => sut.GetCalendarsAsync(TestContext.Current.CancellationToken));

        error.Dimension.ShouldBe("byte_count");
        error.Observed.ShouldBeGreaterThan(16 * 1024 * 1024);
        requests.Count.ShouldBe(7);
    }

    [Theory]
    [InlineData("utf-16be")]
    [InlineData("iso-8859-1")]
    public async Task Query_UsesHttpCharsetForSuccessfulReportAndSupportedFilterError(string encoding)
    {
        const string calendar = "https://example.com/home/events/";
        var requests = new List<HttpRequestMessage>();
        var error = "<?xml version='1.0' encoding='us-ascii'?><d:error xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>"
            + "<c:supported-filter/><d:responsedescription>café</d:responsedescription></d:error>";
        var result = EncodedDiscoveryBody("<d:response><d:href>" + calendar + "a.ics</d:href>"
            + "<d:status>HTTP/1.1 200 OK</d:status><d:responsedescription>café</d:responsedescription></d:response>");
        var sut = CreateSut(CreateSequencedHandler([
            EncodedXmlResponse(error, encoding, encoding, status: HttpStatusCode.Forbidden),
            EncodedXmlResponse(result, encoding, encoding)
        ], requests));
        var from = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        var hrefs = await sut.QueryCandidateHrefsAsync(calendar, CalendarEntityKind.Event, from, from.AddDays(1), TestContext.Current.CancellationToken);

        hrefs.ShouldBe([calendar + "a.ics"]);
        requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Multiget_RecognizesUtf16UnsupportedErrorWithoutAttemptingResourceGet()
    {
        const string calendar = "https://example.com/home/events/";
        var requests = new List<HttpRequestMessage>();
        var body = "<?xml version='1.0' encoding='us-ascii'?><d:error xmlns:d='DAV:'><d:supported-report/></d:error>";
        var sut = CreateSut(CreateSequencedHandler([
            EncodedXmlResponse(body, "utf-16", "us-ascii", bom: true, status: HttpStatusCode.Forbidden)
        ], requests));

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() => sut.GetCalendarResourcesForQueryAsync(
            calendar, [calendar + "a.ics"], TestContext.Current.CancellationToken));

        requests.ShouldHaveSingleItem().Method.Method.ShouldBe("REPORT");
    }

    [Fact]
    public async Task Discovery_UnreadableEncodingAndXmlDepthStillFailBeforeTraversal()
    {
        foreach (var body in new[] { "<d:multistatus xmlns:d='DAV:'/>",
            "<d:multistatus xmlns:d='DAV:'>" + string.Concat(Enumerable.Repeat("<x>", 65))
                + string.Concat(Enumerable.Repeat("</x>", 65)) + "</d:multistatus>" })
        {
            var requests = new List<HttpRequestMessage>();
            var charset = body.Contains("<x>", StringComparison.Ordinal) ? "utf-16be" : "x-unknown-charset";
            var sut = CreateSut(CreateSequencedHandler([EncodedXmlResponse(body, "utf-16be", charset)], requests));

            await Should.ThrowAsync<XmlException>(() => sut.GetCalendarsAsync(TestContext.Current.CancellationToken));
            requests.Count.ShouldBe(1);
        }
    }

    private static string HomeProperty(string home) => "<d:response><d:href>" + home + "</d:href><d:propstat><d:prop>"
        + "<c:calendar-home-set><d:href>" + home + "</d:href></c:calendar-home-set></d:prop>"
        + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static string EncodedDiscoveryBody(string responses, string declared = "us-ascii") =>
        "<?xml version='1.0' encoding='" + declared + "'?><d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>"
        + responses + "</d:multistatus>";

    private static HttpResponseMessage EncodedXmlResponse(string body, string encoding, string? charset, bool bom = false,
        HttpStatusCode status = HttpStatusCode.MultiStatus)
    {
        var codec = Encoding.GetEncoding(encoding);
        var bytes = (bom ? codec.GetPreamble() : []).Concat(codec.GetBytes(body)).ToArray();
        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = charset } }
            }
        };
    }
}
