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
        var responses = doc.Descendants(Dav + "response");
        var result = new List<TaskList>();

        foreach (var response in responses)
        {
            var href = response.Element(Dav + "href")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(href))
                continue;

            // Check if this is a calendar collection (has calendar resourcetype)
            var resourceType = response.Descendants(Dav + "resourcetype").FirstOrDefault();
            if (resourceType?.Element(CalDav + "calendar") is null)
                continue;

            // Check if VTODO is supported
            var supportedComponents = response.Descendants(CalDav + "comp");
            var supportsVtodo = supportedComponents.Any(c =>
                string.Equals(c.Attribute("name")?.Value, "VTODO", StringComparison.OrdinalIgnoreCase));

            // Skip calendars that don't support VTODO
            if (!supportsVtodo)
                continue;

            var componentNameList = supportedComponents
                .Select(c => c.Attribute("name")?.Value)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();

            var displayName = GetPropValue(response, Dav + "displayname")
                ?? href.TrimEnd('/').Split('/').Last(); // CalDAV hrefs are expected to be path-like here.
            var description = GetPropValue(response, Dav + "description");
            var color = GetPropValue(response, AppleCs + "calendar-color");

            result.Add(new TaskList
            {
                Href = href,
                DisplayName = displayName,
                Description = description,
                Color = color,
                SupportedComponents = componentNameList
            });
        }

        return result;
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
        var responses = doc.Descendants(Dav + "response");
        var result = new List<(string Href, string? ETag, string ICalData)>();

        foreach (var response in responses)
        {
            var href = response.Element(Dav + "href")?.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(href))
                continue;

            // Check for 200 OK status
            var status = response.Element(Dav + "status")?.Value;
            if (status is not null && !status.Contains("200"))
                continue;

            var etag = GetPropValue(response, Dav + "getetag");
            var calendarData = GetPropValue(response, CalDav + "calendar-data");

            if (calendarData is not null)
            {
                result.Add((href, etag?.Trim('"'), calendarData));
            }
        }

        return result;
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
