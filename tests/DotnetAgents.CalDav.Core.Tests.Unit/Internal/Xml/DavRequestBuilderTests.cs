using System.Xml.Linq;
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

}
