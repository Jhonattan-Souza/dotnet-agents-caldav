using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
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

    [Fact]
    public void BuildCalendarQuery_Generic_ContainsVtodoFilter()
    {
        // Act
        var xml = DavRequestBuilder.BuildCalendarQuery();

        // Assert
        var doc = XDocument.Parse(xml);
        var query = doc.Element(CalDav + "calendar-query");
        query.ShouldNotBeNull();

        var prop = query.Element(Dav + "prop");
        prop.ShouldNotBeNull();
        prop.Element(Dav + "getetag").ShouldNotBeNull();
        prop.Element(CalDav + "calendar-data").ShouldNotBeNull();

        var filter = query.Element(CalDav + "filter");
        filter.ShouldNotBeNull();
        var vcalFilter = filter.Element(CalDav + "comp-filter");
        vcalFilter.ShouldNotBeNull();
        vcalFilter.Attribute("name")?.Value.ShouldBe("VCALENDAR");

        var vtodoFilter = vcalFilter.Element(CalDav + "comp-filter");
        vtodoFilter.ShouldNotBeNull();
        vtodoFilter.Attribute("name")?.Value.ShouldBe("VTODO");
    }

    [Fact]
    public void BuildCalendarQuery_Generic_NoStatusFilter()
    {
        // Act
        var xml = DavRequestBuilder.BuildCalendarQuery();

        // Assert
        var doc = XDocument.Parse(xml);
        var vtodoFilter = doc.Descendants(CalDav + "comp-filter")
            .First(f => f.Attribute("name")?.Value == "VTODO");

        vtodoFilter.Elements(CalDav + "prop-filter").ShouldBeEmpty();
    }

    [Fact]
    public void BuildCalendarQuery_CompletedOnly_ContainsCompletedStatusFilter()
    {
        // Act
        var xml = DavRequestBuilder.BuildCalendarQuery(completedOnly: true);

        // Assert
        var doc = XDocument.Parse(xml);
        var vtodoFilter = doc.Descendants(CalDav + "comp-filter")
            .First(f => f.Attribute("name")?.Value == "VTODO");

        var propFilter = vtodoFilter.Element(CalDav + "prop-filter");
        propFilter.ShouldNotBeNull();
        propFilter.Attribute("name")?.Value.ShouldBe("STATUS");
        var textMatch = propFilter.Element(CalDav + "text-match");
        textMatch.ShouldNotBeNull();
        textMatch.Value.ShouldBe("COMPLETED");
    }

    [Fact]
    public void BuildCalendarQuery_ContainsNamespaceDeclarations()
    {
        // Act
        var xml = DavRequestBuilder.BuildCalendarQuery();

        // Assert
        var doc = XDocument.Parse(xml);
        var query = doc.Element(CalDav + "calendar-query");
        query.ShouldNotBeNull();

        // Verify namespace declarations are present via the xmlns declarations
        var xmlWithNs = xml;
        xmlWithNs.ShouldContain("xmlns:d=\"DAV:\"");
        xmlWithNs.ShouldContain("xmlns:c=\"urn:ietf:params:xml:ns:caldav\"");
    }
}
