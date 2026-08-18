using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>
/// Builds WebDAV/CalDAV XML request bodies (PROPFIND, REPORT calendar-query, etc.).
/// </summary>
internal static class DavRequestBuilder
{
    // CalDAV servers evaluate time-range against the complete resource, including recurrences and
    // detached overrides. This is only a representation envelope for DATE values and UTC offsets;
    // it is deliberately not a recurrence lookback window.
    private static readonly TimeSpan CandidatePlanningMargin = TimeSpan.FromDays(2);
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
                    new XElement(CalDav + "calendar-description"),
                    new XElement(Dav + "description"),
                    new XElement(AppleCs + "calendar-color"),
                    new XElement(CalServer + "getctag")
                )
            )
        );
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Builds a minimal Calendar Entity candidate REPORT for one requested kind.</summary>
    public static string BuildCalendarEntityQuery(
        CalendarEntityKind entityKind,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var entityFilter = new XElement(CalDav + "comp-filter",
            new XAttribute("name", entityKind == CalendarEntityKind.Event ? "VEVENT" : "VTODO"));
        if (from is not null && to is not null && TryGetSafeReportRange(from.Value, to.Value, out var reportFrom, out var reportTo))
        {
            entityFilter.Add(new XElement(CalDav + "time-range",
                new XAttribute("start", FormatUtcSecond(reportFrom)),
                new XAttribute("end", FormatUtcSecond(reportTo))));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(CalDav + "calendar-query",
                new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
                new XElement(Dav + "prop", new XElement(Dav + "getetag")),
                new XElement(CalDav + "filter",
                    new XElement(CalDav + "comp-filter",
                        new XAttribute("name", "VCALENDAR"),
                        entityFilter))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Builds a Calendar multiget request for one bounded authoritative resource batch.</summary>
    public static string BuildCalendarMultiget(IReadOnlyList<string> resourceHrefs)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(CalDav + "calendar-multiget",
                new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
                new XElement(Dav + "prop",
                    new XElement(Dav + "getetag"),
                    new XElement(CalDav + "calendar-data")),
                resourceHrefs.Select(resourceHref => new XElement(Dav + "href", resourceHref))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static bool TryGetSafeReportRange(
        DateTimeOffset from,
        DateTimeOffset to,
        out DateTimeOffset reportFrom,
        out DateTimeOffset reportTo)
    {
        if (from.Ticks < DateTimeOffset.MinValue.Ticks + CandidatePlanningMargin.Ticks
            || to.Ticks > DateTimeOffset.MaxValue.Ticks - CandidatePlanningMargin.Ticks)
        {
            reportFrom = default;
            reportTo = default;
            return false;
        }
        from -= CandidatePlanningMargin;
        to += CandidatePlanningMargin;

        var fromRemainder = from.Ticks % TimeSpan.TicksPerSecond;
        if (fromRemainder == 0)
        {
            if (from.Ticks < DateTimeOffset.MinValue.Ticks + TimeSpan.TicksPerSecond)
            {
                reportFrom = default;
                reportTo = default;
                return false;
            }
            reportFrom = from.AddSeconds(-1);
        }
        else
        {
            reportFrom = from.AddTicks(-fromRemainder);
        }

        var toRemainder = to.Ticks % TimeSpan.TicksPerSecond;
        var ticksToAdd = toRemainder == 0 ? TimeSpan.TicksPerSecond : TimeSpan.TicksPerSecond - toRemainder;
        if (to.Ticks > DateTimeOffset.MaxValue.Ticks - ticksToAdd)
        {
            reportTo = default;
            return false;
        }
        reportTo = to.AddTicks(ticksToAdd);
        return true;
    }

    private static string FormatUtcSecond(DateTimeOffset value) => value.UtcDateTime.ToString(
        "yyyyMMdd'T'HHmmss'Z'",
        System.Globalization.CultureInfo.InvariantCulture);
}
