using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Internal.Xml;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarMultigetResponseParserTests
{
    private const string Href = "https://cal.example/calendars/work/a.ics";

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task RequestedResponseCountMustStayInsideTheClosedBatch(int requestedCount)
    {
        using var content = XmlContent(Multistatus(ResponseStatus()));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            CalendarMultigetResponseParser.ParseAsync(
                content,
                requestedCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GzipResponsePreservesOneUnambiguousFailureTruth()
    {
        var bytes = Compress(Encoding.UTF8.GetBytes(Multistatus(ResponseStatus())));
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentEncoding.Add("gzip");

        var resources = await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken);

        resources.Single().Href.ShouldBe(Href);
        resources.Single().StatusCode.ShouldBe((int)HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(InvalidStructures))]
    public async Task ParserRejectsIncompleteDuplicateOrAmbiguousDavTruth(string xml)
    {
        using var content = XmlContent(xml);

        await Should.ThrowAsync<XmlException>(() => CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(ValidExtensionShapes))]
    public async Task ParserSkipsBoundedExtensionsWithoutChangingFailureTruth(string extension)
    {
        using var content = XmlContent(Multistatus(
            $"<d:response>{extension}<d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"));

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.StatusCode.ShouldBe(404);
        resource.Href.ShouldBe(Href);
    }

    [Fact]
    public async Task EqualResponseAndPropstatFailureStatusesRemainOneFailureTruth()
    {
        using var content = XmlContent(Multistatus($"""
            <d:response><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status>
              <d:propstat><d:prop/><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
            </d:response>
            """));

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task EmptySuccessfulScalarsRemainExplicitPropertyTruth()
    {
        using var content = XmlContent(Multistatus($"""
            <d:response><d:href>{Href}</d:href><d:propstat><d:prop>
              <d:getetag/><c:calendar-data/>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            """));

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.StatusCode.ShouldBe(200);
        resource.EntityTag.ShouldBe(string.Empty);
        resource.CalendarData.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task ScalarTextMayBeSegmentedAcrossTextCdataAndWhitespace()
    {
        using var content = XmlContent($"""

            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>https://cal.example/calendars/work/<![CDATA[a.ics]]></d:href>
                <d:status>HTTP/1.1 <![CDATA[404]]> Not Found</d:status>
              </d:response>
            </d:multistatus>
            """);

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.Href.ShouldBe(Href);
        resource.StatusCode.ShouldBe(404);
    }

    [Theory]
    [InlineData("<d:getetag>   </d:getetag>")]
    [InlineData("<d:getetag xml:space='preserve'>   </d:getetag>")]
    public async Task WhitespaceScalarKindsRemainExplicitEmptyEntityTagTruth(string entityTag)
    {
        using var content = XmlContent(Multistatus($"""
            <d:response><d:href>{Href}</d:href><d:propstat><d:prop>
              {entityTag}<c:calendar-data>opaque</c:calendar-data>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            """));

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.StatusCode.ShouldBe(200);
        resource.EntityTag.ShouldBe(string.Empty);
        resource.CalendarData.ShouldBe("opaque");
    }

    [Fact]
    public async Task BoundedUnknownPropstatAndPropertyChildrenDoNotCreateDavTruth()
    {
        using var content = XmlContent(Multistatus($"""
            <d:response><d:href>{Href}</d:href><d:propstat>
              <x:metadata xmlns:x="urn:extension"/>
              <d:prop><x:metadata xmlns:x="urn:extension">opaque</x:metadata></d:prop>
              <d:status>HTTP/1.1 404 Not Found</d:status>
            </d:propstat></d:response>
            """));

        var resource = (await CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken)).Single();

        resource.StatusCode.ShouldBe(404);
        resource.EntityTag.ShouldBeNull();
        resource.CalendarData.ShouldBeNull();
    }

    [Fact]
    public async Task CalendarDataAboveFourMebibytesIsRejectedDuringStreamingRead()
    {
        var calendarData = new string('x', (4 * 1024 * 1024) + 1);
        using var content = XmlContent(Multistatus($"""
            <d:response><d:href>{Href}</d:href><d:propstat><d:prop>
              <d:getetag>&quot;r1&quot;</d:getetag><c:calendar-data>{calendarData}</c:calendar-data>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            """));

        await Should.ThrowAsync<XmlException>(() => CalendarMultigetResponseParser.ParseAsync(
            content,
            requestedResourceCount: 1,
            TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> InvalidStructures => new()
    {
        string.Empty,
        "<?probe private?><d:multistatus xmlns:d='DAV:'/>",
        "<!--private--><d:multistatus xmlns:d='DAV:'/>",
        "<d:multistatus xmlns:d='urn:not-dav'/>",
        Multistatus($"<d:response><?probe private?><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus($"<d:response><!--private--><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus($"<d:response><d:href/><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus("<d:response><d:href> </d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus($"<d:response><d:href><d:collection/></d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus("<d:response><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status><d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href><d:propstat><d:prop><c:calendar-data>one</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat><d:propstat><d:prop><c:calendar-data>two</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 404 Not Found</d:status><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>"),
        Multistatus($"<d:response><d:href>{Href}</d:href><d:propstat><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>")
    };

    public static TheoryData<string> ValidExtensionShapes => new()
    {
        "<x:empty xmlns:x='urn:extension'/>",
        "<x:container xmlns:x='urn:extension'><x:nested>text<![CDATA[data]]></x:nested></x:container>"
    };

    private static string ResponseStatus() =>
        $"<d:response><d:href>{Href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>";

    private static string Multistatus(string response) =>
        $"<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>{response}</d:multistatus>";

    private static StringContent XmlContent(string xml) => new(xml, Encoding.UTF8, "application/xml");

    private static byte[] Compress(byte[] bytes)
    {
        using var destination = new MemoryStream();
        using (var gzip = new GZipStream(destination, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(bytes);
        return destination.ToArray();
    }
}
