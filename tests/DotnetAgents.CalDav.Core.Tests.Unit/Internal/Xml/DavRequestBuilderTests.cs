using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public class DavRequestBuilderTests
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";
    private static readonly XNamespace CalServer = "http://calendarserver.org/ns/";

    [Fact]
    public void BuildCalendarEntityQuery_AddsEscapedAsciiCasemapTextMatchesAfterTheTimeRange()
    {
        var xml = DavRequestBuilder.BuildCalendarEntityQuery(
            CalendarEntityKind.Todo,
            DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T10:00:00Z"),
            [
                new CalendarTextPropertyMatch("SUMMARY", "a<b&c"),
                new CalendarTextPropertyMatch("CATEGORIES", "health")
            ]);

        var entityFilter = XDocument.Parse(xml).Descendants(CalDav + "comp-filter")
            .Single(element => element.Attribute("name")!.Value == "VTODO");
        var children = entityFilter.Elements().ToArray();
        children.Select(child => child.Name.LocalName).ShouldBe(["time-range", "prop-filter", "prop-filter"]);
        children[1].Attribute("name")!.Value.ShouldBe("SUMMARY");
        children[2].Attribute("name")!.Value.ShouldBe("CATEGORIES");
        var textMatches = entityFilter.Descendants(CalDav + "text-match").ToArray();
        textMatches.Select(match => match.Value).ShouldBe(["a<b&c", "health"]);
        textMatches.ShouldAllBe(match => match.Attribute("collation")!.Value == "i;ascii-casemap"
            && match.Attribute("negate-condition") == null);
        xml.ShouldContain("a&lt;b&amp;c");
        xml.ShouldNotContain("calendar-data");
    }

    [Fact]
    public void BuildCalendarEntityQuery_WithoutPropertyMatchesKeepsTheMinimalShape()
    {
        var xml = DavRequestBuilder.BuildCalendarEntityQuery(CalendarEntityKind.Event, propertyMatches: []);

        xml.ShouldBe(DavRequestBuilder.BuildCalendarEntityQuery(CalendarEntityKind.Event));
        xml.ShouldNotContain("prop-filter");
    }

    [Fact]
    public void BuildPropFindCalendarHomeSet_ContainsPropfindAndCalendarHomeSet()
    {
        // Act
        var xml = DavRequestBuilder.BuildPropFindCalendarHomeSet();

        // Assert
        var doc = XDocument.Parse(xml);
        var propfind = doc.Element(Dav + "propfind");
        propfind.ShouldNotBeNull();
        var prop = propfind.Element(Dav + "prop");
        prop.ShouldNotBeNull();
        var homeSet = prop.Element(CalDav + "calendar-home-set");
        homeSet.ShouldNotBeNull();
    }

    [Fact]
    public void BuildPropFindCalendarHomeSet_IsValidXml()
    {
        // Act
        var xml = DavRequestBuilder.BuildPropFindCalendarHomeSet();

        // Assert - should not throw
        var doc = XDocument.Parse(xml);
        doc.ShouldNotBeNull();
    }

    [Fact]
    public void BuildPropFindCalendarProperties_ContainsAllExpectedElements()
    {
        // Act
        var xml = DavRequestBuilder.BuildPropFindCalendarProperties();

        // Assert
        var doc = XDocument.Parse(xml);
        var prop = doc.Element(Dav + "propfind")?.Element(Dav + "prop");
        prop.ShouldNotBeNull();
        prop.Element(Dav + "displayname").ShouldNotBeNull();
        prop.Element(Dav + "resourcetype").ShouldNotBeNull();
        prop.Element(CalDav + "supported-calendar-component-set").ShouldNotBeNull();
        prop.Element(CalDav + "calendar-description").ShouldNotBeNull();
        prop.Element(Dav + "description").ShouldNotBeNull();
        prop.Element(AppleCs + "calendar-color").ShouldNotBeNull();
        prop.Element(AppleCs + "calendar-order").ShouldNotBeNull();
        prop.Element(CalServer + "getctag").ShouldNotBeNull();
    }

    [Fact]
    public void BuildPropFindCalendarProperties_IsValidXml()
    {
        // Act
        var xml = DavRequestBuilder.BuildPropFindCalendarProperties();

        // Assert
        var doc = XDocument.Parse(xml);
        doc.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, "VEVENT")]
    [InlineData(CalendarEntityKind.Todo, "VTODO")]
    public void BuildMkCalendar_InitializesDisplayNameAndRequestedComponent(
        CalendarEntityKind entityKind,
        string componentName)
    {
        var xml = DavRequestBuilder.BuildMkCalendar("Work & Home", [entityKind]);
        var document = XDocument.Parse(xml);
        var root = document.Root;

        root!.Name.ShouldBe(CalDav + "mkcalendar");
        root.Element(Dav + "set")!.Element(Dav + "prop")!
            .Element(Dav + "displayname")!.Value.ShouldBe("Work & Home");
        root.Descendants(CalDav + "comp").Single().Attribute("name")!.Value.ShouldBe(componentName);
    }

    [Fact]
    public void BuildMkCalendar_MixedComponentSetIsDeterministic()
    {
        var xml = DavRequestBuilder.BuildMkCalendar(
            "Mixed",
            [CalendarEntityKind.Event, CalendarEntityKind.Todo]);
        var components = XDocument.Parse(xml).Descendants(CalDav + "comp")
            .Select(element => element.Attribute("name")!.Value)
            .ToArray();

        components.ShouldBe(["VEVENT", "VTODO"]);
    }

    [Fact]
    public void BuildMkCalendar_WithoutInitialPropertiesSetsOnlyNameAndComponents()
    {
        var prop = XDocument.Parse(DavRequestBuilder.BuildMkCalendar("Plain", [CalendarEntityKind.Event]))
            .Descendants(Dav + "prop").Single();

        prop.Elements().Select(element => element.Name).ShouldBe([
            Dav + "displayname", CalDav + "supported-calendar-component-set"]);
    }

    [Fact]
    public void BuildMkCalendar_InitializesColorOrderAndTimeZoneInTheSameAtomicSet()
    {
        const string timeZone = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n";
        var xml = DavRequestBuilder.BuildMkCalendar("Styled", [CalendarEntityKind.Todo],
            new CalendarCollectionInitialProperties("#FF2968", 0, timeZone));
        var document = XDocument.Parse(xml);
        var set = document.Root!.Elements(Dav + "set").Single();

        set.Descendants(AppleCs + "calendar-color").Single().Value.ShouldBe("#FF2968");
        set.Descendants(AppleCs + "calendar-order").Single().Value.ShouldBe("0");
        // Entitized carriage returns survive XML end-of-line normalization, so the server
        // receives the RFC 5545 CRLF line endings.
        xml.ShouldContain("BEGIN:VCALENDAR&#xD;\nEND:VCALENDAR&#xD;\n");
        set.Descendants(CalDav + "calendar-timezone").Single().Value.ShouldBe(timeZone);
        xml.ShouldNotStartWith("<?xml");
    }

    [Fact]
    public void BuildMkCalendar_OmitsUnrequestedInitialProperties()
    {
        var xml = DavRequestBuilder.BuildMkCalendar("Ordered", [CalendarEntityKind.Event],
            new CalendarCollectionInitialProperties(null, 7, null));
        var prop = XDocument.Parse(xml).Descendants(Dav + "prop").Single();

        prop.Element(AppleCs + "calendar-order")!.Value.ShouldBe("7");
        prop.Element(AppleCs + "calendar-color").ShouldBeNull();
        prop.Element(CalDav + "calendar-timezone").ShouldBeNull();
    }
}
