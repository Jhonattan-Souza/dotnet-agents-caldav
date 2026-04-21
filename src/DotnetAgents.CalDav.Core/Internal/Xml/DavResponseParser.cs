using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>
/// Parses WebDAV multistatus XML responses into strongly-typed domain models.
/// </summary>
internal static class DavResponseParser
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";

    /// <summary>Parses a multistatus XML body into a list of <see cref="TaskList"/> records.</summary>
    public static IReadOnlyList<TaskList> ParseTaskLists(string multistatusXml)
    {
        var doc = XDocument.Parse(multistatusXml);
        return doc.Descendants(Dav + "response")
            .Select(TryParseTaskList)
            .OfType<TaskList>()
            .ToList();
    }

    /// <summary>Parses a multistatus response to extract the calendar-home-set URL.</summary>
    public static string? ParseCalendarHomeSet(string multistatusXml)
    {
        var doc = XDocument.Parse(multistatusXml);
        return doc.Descendants(CalDav + "calendar-home-set")
            .Descendants(Dav + "href")
            .FirstOrDefault()?.Value?.Trim();
    }

    /// <summary>Parses a multistatus response to extract the current-user-principal URL.</summary>
    public static string? ParseCurrentUserPrincipal(string multistatusXml)
    {
        var doc = XDocument.Parse(multistatusXml);
        return doc.Descendants(Dav + "current-user-principal")
            .Descendants(Dav + "href")
            .FirstOrDefault()?.Value?.Trim();
    }

    /// <summary>
    /// Parses a multistatus REPORT response into href → (etag, icalData) tuples.
    /// </summary>
    public static IReadOnlyList<(string Href, string? ETag, string ICalData)> ParseCalendarData(string multistatusXml)
    {
        var doc = XDocument.Parse(multistatusXml);
        return doc.Descendants(Dav + "response")
            .Select(TryParseCalendarDataResponse)
            .Where(entry => entry.HasValue)
            .Select(entry => entry!.Value)
            .ToList();
    }

    private static TaskList? TryParseTaskList(XElement response)
    {
        var href = response.Element(Dav + "href")?.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(href))
            return null;

        if (!IsCalendarCollection(response))
            return null;

        var supportedComponents = GetSupportedComponentNames(response);
        if (!SupportsVtodo(supportedComponents))
            return null;

        return new TaskList
        {
            Href = href,
            DisplayName = GetTaskListDisplayName(response, href),
            Description = GetPropValue(response, Dav + "description"),
            Color = GetPropValue(response, AppleCs + "calendar-color"),
            SupportedComponents = supportedComponents
        };
    }

    private static (string Href, string? ETag, string ICalData)? TryParseCalendarDataResponse(XElement response)
    {
        var href = response.Element(Dav + "href")?.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(href) || !HasSuccessStatus(response))
            return null;

        var calendarData = GetPropValue(response, CalDav + "calendar-data");
        if (calendarData is null)
            return null;

        return (href, GetPropValue(response, Dav + "getetag")?.Trim('"'), calendarData);
    }

    private static bool IsCalendarCollection(XElement response) =>
        response.Descendants(Dav + "resourcetype").FirstOrDefault()?.Element(CalDav + "calendar") is not null;

    private static List<string> GetSupportedComponentNames(XElement response) =>
        response.Descendants(CalDav + "comp")
            .Select(component => component.Attribute("name")?.Value)
            .OfType<string>()
            .ToList();

    private static bool SupportsVtodo(IReadOnlyCollection<string> supportedComponents) =>
        supportedComponents.Any(component => string.Equals(component, "VTODO", StringComparison.OrdinalIgnoreCase));

    private static string GetTaskListDisplayName(XElement response, string href) =>
        GetPropValue(response, Dav + "displayname")
        ?? href.TrimEnd('/').Split('/').Last();

    private static bool HasSuccessStatus(XElement response)
    {
        var status = response.Element(Dav + "status")?.Value;
        return status is null || status.Contains("200");
    }

    private static string? GetPropValue(XElement response, XName propertyName)
    {
        var propStats = response.Descendants(Dav + "propstat");
        foreach (var propStat in propStats)
        {
            var status = propStat.Element(Dav + "status")?.Value;
            if (status is not null && status.Contains("200"))
            {
                var prop = propStat.Element(Dav + "prop")?.Element(propertyName);
                if (prop is not null)
                    return prop.Value;
            }
        }

        return null;
    }
}
