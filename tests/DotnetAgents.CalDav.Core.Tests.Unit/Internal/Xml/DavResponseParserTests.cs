using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public class DavResponseParserTests
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";

    [Fact]
    public void ParseTaskLists_ValidCalendarCollection_ReturnsTaskList()
    {
        // Arrange
        var xml = BuildMultistatusWithSingleResponse(
            href: "/calendars/user/tasks/",
            props:
            [
                new XElement(Dav + "displayname", "My Tasks"),
                new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                new XElement(CalDav + "supported-calendar-component-set",
                    new XElement(CalDav + "comp", new XAttribute("name", "VTODO")),
                    new XElement(CalDav + "comp", new XAttribute("name", "VEVENT")))
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Href.ShouldBe("/calendars/user/tasks/");
        result[0].DisplayName.ShouldBe("My Tasks");
        result[0].SupportedComponents.ShouldContain("VTODO");
        result[0].SupportedComponents.ShouldContain("VEVENT");
    }

    [Fact]
    public void ParseTaskLists_NonCalendarCollection_SkipsEntry()
    {
        // Arrange - response without calendar resourcetype
        var xml = BuildMultistatusWithSingleResponse(
            href: "/calendars/user/something/",
            props:
            [
                new XElement(Dav + "displayname", "Not a calendar"),
                new XElement(Dav + "resourcetype")
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseTaskLists_VeventOnlyCalendar_SkipsEntry()
    {
        // Arrange - calendar with only VEVENT support (no VTODO) should be excluded
        var xml = BuildMultistatusWithSingleResponse(
            href: "/calendars/user/events/",
            props:
            [
                new XElement(Dav + "displayname", "My Events"),
                new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                new XElement(CalDav + "supported-calendar-component-set",
                    new XElement(CalDav + "comp", new XAttribute("name", "VEVENT")))
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert - VEVENT-only calendars should not be returned as task lists
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseTaskLists_EmptyHref_SkipsEntry()
    {
        // Arrange
        var xml = BuildMultistatusWithSingleResponse(
            href: "",
            props:
            [
                new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar"))
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseTaskLists_MultipleCollections_ReturnsAll()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                BuildResponseElement("/calendars/user/work/",
                    new XElement(Dav + "displayname", "Work"),
                    new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                    new XElement(CalDav + "supported-calendar-component-set",
                        new XElement(CalDav + "comp", new XAttribute("name", "VTODO")))),
                BuildResponseElement("/calendars/user/personal/",
                    new XElement(Dav + "displayname", "Personal"),
                    new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                    new XElement(CalDav + "supported-calendar-component-set",
                        new XElement(CalDav + "comp", new XAttribute("name", "VTODO")),
                        new XElement(CalDav + "comp", new XAttribute("name", "VEVENT"))))
            );
        });

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result.Count.ShouldBe(2);
        result[0].DisplayName.ShouldBe("Work");
        result[1].DisplayName.ShouldBe("Personal");
        result[1].SupportedComponents.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseTaskLists_MissingDisplayName_DerivesFromHref()
    {
        // Arrange - no displayname, should use last segment of href
        var xml = BuildMultistatusWithSingleResponse(
            href: "/calendars/user/mytasks/",
            props:
            [
                new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                new XElement(CalDav + "supported-calendar-component-set",
                    new XElement(CalDav + "comp", new XAttribute("name", "VTODO")))
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe("mytasks");
    }

    [Fact]
    public void ParseTaskLists_WithOptionalProperties_IncludesDescriptionAndColor()
    {
        // Arrange
        var xml = BuildMultistatusWithSingleResponse(
            href: "/calendars/user/tasks/",
            props:
            [
                new XElement(Dav + "displayname", "My Tasks"),
                new XElement(Dav + "description", "Work tasks"),
                new XElement(AppleCs + "calendar-color", "#FF0000"),
                new XElement(Dav + "resourcetype", new XElement(CalDav + "calendar")),
                new XElement(CalDav + "supported-calendar-component-set",
                    new XElement(CalDav + "comp", new XAttribute("name", "VTODO")))
            ]);

        // Act
        var result = DavResponseParser.ParseTaskLists(xml);

        // Assert
        result[0].Description.ShouldBe("Work tasks");
        result[0].Color.ShouldBe("#FF0000");
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

    [Fact]
    public void ParseCalendarData_ValidResponse_ReturnsEntriesWithEtagAndData()
    {
        // Arrange
        var icalData = "BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:abc\r\nEND:VTODO\r\nEND:VCALENDAR";
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"etag-abc\""),
                            new XElement(CalDav + "calendar-data", icalData))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarData(xml);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Href.ShouldBe("/calendars/user/tasks/abc.ics");
        result[0].ETag.ShouldBe("etag-abc");
        result[0].ICalData.ShouldContain("BEGIN:VCALENDAR");
        result[0].ICalData.ShouldContain("BEGIN:VTODO");
        result[0].ICalData.ShouldContain("UID:abc");
        result[0].ICalData.ShouldContain("END:VTODO");
        result[0].ICalData.ShouldContain("END:VCALENDAR");
    }

    [Fact]
    public void ParseCalendarData_Non200Status_SkipsEntry()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 404 Not Found"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"etag-abc\""),
                            new XElement(CalDav + "calendar-data", "data"))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarData(xml);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCalendarData_MissingHref_SkipsEntry()
    {
        // Arrange - response without href
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"etag\""),
                            new XElement(CalDav + "calendar-data", "data"))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarData(xml);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCalendarData_MissingCalendarData_SkipsEntry()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"etag\""))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarData(xml);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCalendarData_QuotedEtag_StripsQuotes()
    {
        // Arrange
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"abc-etag-123\""),
                            new XElement(CalDav + "calendar-data", "data"))))
            );
        });

        // Act
        var result = DavResponseParser.ParseCalendarData(xml);

        // Assert
        result[0].ETag.ShouldBe("abc-etag-123");
    }

    [Fact]
    public void ParseCalendarData_SkipsResponse_WhenResponseLevelStatusIsNot200AndNestedPropStatIs200()
    {
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "status", "HTTP/1.1 404 Not Found"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", "\"etag\""),
                            new XElement(CalDav + "calendar-data", "data"))))
            );
        });

        var result = DavResponseParser.ParseCalendarData(xml);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCalendarData_ReturnsValueFromSubsequent200PropStat_WhenFirst200DoesNotContainProperty()
    {
        var xml = BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                new XElement(Dav + "response",
                    new XElement(Dav + "href", "/calendars/user/tasks/abc.ics"),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(Dav + "displayname", "Task Display Name"))),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                        new XElement(Dav + "prop",
                            new XElement(CalDav + "calendar-data", "BEGIN:VCALENDAR\r\nEND:VCALENDAR"))))
            );
        });

        var result = DavResponseParser.ParseCalendarData(xml);

        result.Count.ShouldBe(1);
        result[0].ICalData.Replace("\r\n", "\n").ShouldBe("BEGIN:VCALENDAR\nEND:VCALENDAR");
    }

    private static string BuildMultistatusWithSingleResponse(string href, XElement[] props)
    {
        return BuildMultistatusXml(doc =>
        {
            doc.Element(Dav + "multistatus")!.Add(
                BuildResponseElement(href, props)
            );
        });
    }

    private static XElement BuildResponseElement(string href, params XElement[] props)
    {
        return new XElement(Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(Dav + "propstat",
                new XElement(Dav + "status", "HTTP/1.1 200 OK"),
                new XElement(Dav + "prop", props)));
    }

    private static XElement BuildResponseElement(string href, XElement prop)
    {
        return BuildResponseElement(href, [prop]);
    }

    private static string BuildMultistatusXml(Action<XDocument> configure)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dav + "multistatus")
        );
        configure(doc);
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
