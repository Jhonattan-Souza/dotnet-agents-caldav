using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public class DavResponseParserTests
{
    [Fact]
    public void ParseCalendarResourceHrefs_AcceptsOnlySuccessfulResponseOrGetEtagPropstatAndDeduplicates()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/cal/a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>
              <d:response><d:href>/cal/a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>
              <d:response><d:href>/cal/response-404.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
              <d:response><d:href>/cal/response-403.ics</d:href><d:status>HTTP/1.1 403 Forbidden</d:status></d:response>
              <d:response><d:href>/cal/etag-200.ics</d:href><d:propstat><d:prop><d:getetag>"r1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/cal/etag-404.ics</d:href><d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>
              <d:response><d:href>/cal/wrong-property.ics</d:href><d:propstat><d:prop><d:displayname>x</d:displayname></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat><d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>
            </d:multistatus>
            """;

        var result = DavResponseParser.ParseCalendarResourceHrefs(xml);

        result.ShouldBe(["/cal/a.ics", "/cal/etag-200.ics"]);
    }

    [Theory]
    [InlineData("<d:response><d:href>/cal/a.ics</d:href><d:status>not-http</d:status></d:response>")]
    [InlineData("<d:response><d:href>/cal/a.ics</d:href><d:status>HTTP/banana 200 OK</d:status></d:response>")]
    [InlineData("<d:response><d:href>/cal/a.ics</d:href><d:status>HTTP/1.x 200 OK</d:status></d:response>")]
    [InlineData("<d:response><d:href>/cal/a.ics</d:href><d:propstat><d:prop><d:getetag/></d:prop><d:status>HTTP/1.1 two-hundred OK</d:status></d:propstat></d:response>")]
    [InlineData("<d:response><d:status>HTTP/1.1 200 OK</d:status></d:response>")]
    public void ParseCalendarResourceHrefs_RejectsMalformedSuccessfulResponse(string response)
    {
        var xml = $"<d:multistatus xmlns:d=\"DAV:\">{response}</d:multistatus>";

        Should.Throw<System.Xml.XmlException>(() => DavResponseParser.ParseCalendarResourceHrefs(xml));
    }

    [Fact]
    public void ParseCalendars_RejectsDtdAndEntityDeclarations()
    {
        const string xml = "<!DOCTYPE multistatus [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><multistatus xmlns='DAV:'>&xxe;</multistatus>";

        Should.Throw<System.Xml.XmlException>(() => DavResponseParser.ParseCalendars(xml));
    }

    [Fact]
    public void ParseCalendars_RejectsExcessiveElementDepth()
    {
        var xml = "<multistatus xmlns='DAV:'>" + string.Concat(Enumerable.Repeat("<x>", 65))
            + string.Concat(Enumerable.Repeat("</x>", 65)) + "</multistatus>";

        Should.Throw<System.Xml.XmlException>(() => DavResponseParser.ParseCalendars(xml));
    }

    [Fact]
    public void ParseCalendars_RejectsExcessiveCharacterCount()
    {
        var xml = "<multistatus xmlns='DAV:'><x>" + new string('a', 4 * 1024 * 1024) + "</x></multistatus>";

        Should.Throw<System.Xml.XmlException>(() => DavResponseParser.ParseCalendars(xml));
    }
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";

    [Fact]
    public void ParseCalendars_PreservesEveryCalendarAndIndependentComponentEvidence()
    {
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                BuildResponseElement("/calendars/user/third/",
                    new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                    new XElement(CalDav + "calendar-description", "Calendar description"),
                    new XElement(CalDav + "supported-calendar-component-set",
                        new XElement(CalDav + "comp", new XAttribute("name", "VEVENT")))),
                BuildResponseElement("/calendars/user/unknown/",
                    new XElement(Dav + "displayname", "  "),
                    new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar"))));
        });

        var result = DavResponseParser.ParseCalendars(xml);

        result.Count.ShouldBe(2);
        result[0].Href.ShouldBe("/calendars/user/third/");
        result[0].DisplayName.ShouldBe("third");
        result[0].DisplayNameProvenance.ShouldBe(DisplayNameProvenance.DerivedFromHref);
        result[0].Description.ShouldBe("Calendar description");
        result[0].EventSupport.ShouldBe(EntityKindSupport.Advertised);
        result[0].TodoSupport.ShouldBe(EntityKindSupport.NotAdvertised);
        result[0].EventEvidence.Single().Value.ShouldBe("VEVENT");
        result[1].DisplayName.ShouldBeNull();
        result[1].DisplayNameProvenance.ShouldBe(DisplayNameProvenance.Missing);
        result[1].EventSupport.ShouldBe(EntityKindSupport.Unknown);
        result[1].TodoSupport.ShouldBe(EntityKindSupport.Unknown);
        result[1].EventEvidence.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCalendars_PreservesUnavailablePropertyEvidence()
    {
        var response = BuildResponseElement(
            "/calendars/user/events/",
            new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")));
        response.Add(new XElement(
            Dav + "propstat",
            new XElement(Dav + "status", "HTTP/1.1 404 Not Found"),
            new XElement(
                Dav + "prop",
                new XElement(Dav + "displayname"),
                new XElement(CalDav + "supported-calendar-component-set"))));
        var xml = BuildMultistatusXml(document => document.Element(Dav + "multistatus")!.Add(response));

        var result = DavResponseParser.ParseCalendars(xml);

        result.ShouldHaveSingleItem().UnavailableProperties.ShouldBe([
            new CalendarUnavailableProperty("DAV:", "displayname", 404),
            new CalendarUnavailableProperty(
                "urn:ietf:params:xml:ns:caldav",
                "supported-calendar-component-set",
                404)
        ]);
    }

    [Fact]
    public void ParseCalendarHomeSet_ValidResponse_ReturnsHref()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/principals/user/"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(CalDav + "calendar-home-set",
                                new XElement(Dav + "href", "/calendars/user/")))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarHomeSet(xml);

        // Assert
        result.ShouldBe("/calendars/user/");
    }

    [Fact]
    public void ParseCalendarHomeSet_NoHomeSet_ReturnsNull()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/principals/user/"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop")))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarHomeSet(xml);

        // Assert
        result.ShouldBeNull();
    }

    private static string BuildMultistatusWithSingleResponse(string href, XElement[] properties) =>
        BuildMultistatusXml(document => document.Element(Dav + "multistatus")!.Add(
            BuildResponseElement(href, properties)));

    private static XElement BuildResponseElement(string href, params XElement[] properties) =>
        new(Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(Dav + "propstat",
                new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                new XElement(Dav + "prop", properties)));

    private static string BuildMultistatusXml(Action<XDocument> configure)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dav + "multistatus"));
        configure(document);
        return document.ToString(SaveOptions.DisableFormatting);
    }

}
