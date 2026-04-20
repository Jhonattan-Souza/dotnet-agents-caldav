using System.Xml.Linq;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>
/// Builds WebDAV/CalDAV XML request bodies (PROPFIND, REPORT calendar-query, etc.).
/// </summary>
internal static class DavRequestBuilder
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";
    private static readonly XNamespace CalServer = "http://calendarserver.org/ns/";

    /// <summary>Builds a PROPFIND request body to discover calendar-home-set and current-user-principal.</summary>
    public static string BuildPropFindCalendarHomeSet()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dav + "propfind",
                new XElement(Dav + "prop",
                    new XElement(CalDav + "calendar-home-set"),
                    new XElement(Dav + "current-user-principal")
                )
            )
        );
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Builds a PROPFIND request body for calendar collection properties.</summary>
    public static string BuildPropFindCalendarProperties()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Dav + "propfind",
                new XElement(Dav + "prop",
                    new XElement(Dav + "displayname"),
                    new XElement(Dav + "resourcetype"),
                    new XElement(CalDav + "supported-calendar-component-set"),
                    new XElement(Dav + "description", new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName)),
                    new XElement(AppleCs + "calendar-color"),
                    new XElement(CalServer + "getctag")
                )
            )
        );
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Builds a REPORT calendar-query body for VTODO items.
    /// Optionally filters by completion status and date range.
    /// </summary>
    public static string BuildCalendarQuery(bool completedOnly = false)
    {
        var vtodoFilter = new XElement(CalDav + "comp-filter",
            new XAttribute("name", "VTODO")
        );

        if (completedOnly)
        {
            vtodoFilter.Add(new XElement(CalDav + "prop-filter",
                new XAttribute("name", "STATUS"),
                new XElement(CalDav + "text-match", "COMPLETED")
            ));
        }

        var filter = new XElement(CalDav + "filter",
            new XElement(CalDav + "comp-filter",
                new XAttribute("name", "VCALENDAR"),
                vtodoFilter
            )
        );

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(CalDav + "calendar-query",
                new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
                new XElement(Dav + "prop",
                    new XElement(Dav + "getetag"),
                    new XElement(CalDav + "calendar-data")
                ),
                filter
            )
        );

        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
