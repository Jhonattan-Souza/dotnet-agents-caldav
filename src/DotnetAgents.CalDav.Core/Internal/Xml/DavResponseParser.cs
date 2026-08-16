using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>
/// Parses WebDAV multistatus XML responses into strongly-typed domain models.
/// </summary>
internal static class DavResponseParser
{
    private const long MaxXmlCharacters = 4L * 1024 * 1024;
    private const int MaxXmlDepth = 64;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace AppleCs = "http://apple.com/ns/ical/";

    /// <summary>Parses a multistatus XML body into a list of <see cref="TaskList"/> records.</summary>
    public static IReadOnlyList<TaskList> ParseTaskLists(string multistatusXml)
    {
        var doc = ParseDocument(multistatusXml);
        return doc.Descendants(Dav + "response")
            .Select(TryParseTaskList)
            .OfType<TaskList>()
            .ToList();
    }

    /// <summary>Parses every discovered Calendar collection with independent component evidence.</summary>
    public static IReadOnlyList<CalendarDescriptor> ParseCalendars(string multistatusXml)
    {
        var doc = ParseDocument(multistatusXml);
        return doc.Descendants(Dav + "response")
            .Select(TryParseCalendar)
            .OfType<CalendarDescriptor>()
            .ToList();
    }

    /// <summary>Parses a multistatus response to extract the calendar-home-set URL.</summary>
    public static string? ParseCalendarHomeSet(string multistatusXml)
    {
        var doc = ParseDocument(multistatusXml);
        return doc.Descendants(CalDav + "calendar-home-set")
            .Descendants(Dav + "href")
            .FirstOrDefault()?.Value?.Trim();
    }

    /// <summary>Parses a multistatus response to extract the current-user-principal URL.</summary>
    public static string? ParseCurrentUserPrincipal(string multistatusXml)
    {
        var doc = ParseDocument(multistatusXml);
        return doc.Descendants(Dav + "current-user-principal")
            .Descendants(Dav + "href")
            .FirstOrDefault()?.Value?.Trim();
    }

    /// <summary>
    /// Parses a multistatus REPORT response into href → (etag, icalData) tuples.
    /// </summary>
    public static IReadOnlyList<(string Href, string? ETag, string ICalData)> ParseCalendarData(string multistatusXml)
    {
        var doc = ParseDocument(multistatusXml);
        return doc.Descendants(Dav + "response")
            .Select(TryParseCalendarDataResponse)
            .Where(entry => entry.HasValue)
            .Select(entry => entry!.Value)
            .ToList();
    }

    /// <summary>Parses successful Calendar Object Resource hrefs from a REPORT multistatus.</summary>
    public static IReadOnlyList<string> ParseCalendarResourceHrefs(string multistatusXml)
    {
        var document = ParseDocument(multistatusXml);
        return document.Descendants(Dav + "response")
            .Select(TryParseCalendarResourceHref)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Recognizes the bounded CalDAV precondition that rejects a REPORT filter.</summary>
    public static bool IsSupportedFilterError(string responseXml)
    {
        try
        {
            var document = ParseDocument(responseXml);
            return document.Descendants().Any(element =>
                element.Name.Namespace == CalDav
                && element.Name.LocalName is "supported-filter" or "supported-collation");
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Recognizes the CalDAV precondition that prevents duplicate UIDs in one Calendar.</summary>
    public static bool IsNoUidConflictError(string responseXml)
    {
        try
        {
            var document = ParseDocument(responseXml);
            return document.Descendants().Any(element =>
                element.Name.Namespace == CalDav
                && element.Name.LocalName == "no-uid-conflict");
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string? TryParseCalendarResourceHref(XElement response)
    {
        var responseStatus = response.Element(Dav + "status");
        var responseSucceeded = responseStatus is not null && IsSuccessStatus(responseStatus.Value);
        var getEtagSucceeded = response.Elements(Dav + "propstat")
            .Any(propStat => IsSuccessfulProperty(propStat, Dav + "getetag"));

        if (!responseSucceeded && !getEtagSucceeded)
            return null;

        var href = response.Element(Dav + "href")?.Value?.Trim();
        if (string.IsNullOrEmpty(href))
            throw new XmlException("A successful WebDAV response is missing its href.");

        return href;
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

    private static XDocument ParseDocument(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters,
            MaxCharactersFromEntities = 0
        };
        using var textReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        if (document.Descendants().Any(element => element.Ancestors().Count() > MaxXmlDepth))
            throw new XmlException("The WebDAV response exceeds the safe XML depth limit.");
        return document;
    }

    private static CalendarDescriptor? TryParseCalendar(XElement response)
    {
        var href = response.Element(Dav + "href")?.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(href) || !IsCalendarCollection(response))
            return null;

        var displayNameProperty = GetSuccessfulProperty(response, Dav + "displayname");
        var displayName = displayNameProperty?.Value.Trim();
        var (name, provenance) = GetDisplayName(displayName, href, displayNameProperty is not null);
        var componentsProperty = GetSuccessfulProperty(response, CalDav + "supported-calendar-component-set");
        var components = componentsProperty is null
            ? []
            : componentsProperty.Descendants(CalDav + "comp")
                .Select(component => component.Attribute("name")?.Value)
                .OfType<string>()
                .ToArray();

        return new CalendarDescriptor
        {
            Href = href,
            DisplayName = name,
            DisplayNameProvenance = provenance,
            Description = GetPropValue(response, CalDav + "calendar-description"),
            Color = GetPropValue(response, AppleCs + "calendar-color"),
            EventSupport = GetComponentSupport(componentsProperty, components, "VEVENT"),
            TodoSupport = GetComponentSupport(componentsProperty, components, "VTODO"),
            EventEvidence = GetComponentEvidence(componentsProperty, components),
            TodoEvidence = GetComponentEvidence(componentsProperty, components)
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

    private static (string? Name, DisplayNameProvenance Provenance) GetDisplayName(
        string? displayName,
        string href,
        bool displayNameWasPresent)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
            return (displayName, DisplayNameProvenance.DavDisplayName);

        if (displayNameWasPresent)
            return (null, DisplayNameProvenance.Missing);

        var derivedName = href.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(derivedName)
            ? (null, DisplayNameProvenance.Missing)
            : (derivedName, DisplayNameProvenance.DerivedFromHref);
    }

    private static EntityKindSupport GetComponentSupport(
        XElement? componentsProperty,
        IReadOnlyCollection<string> components,
        string componentName)
    {
        if (componentsProperty is null)
            return EntityKindSupport.Unknown;

        return components.Contains(componentName, StringComparer.OrdinalIgnoreCase)
            ? EntityKindSupport.Advertised
            : EntityKindSupport.NotAdvertised;
    }

    private static IReadOnlyList<CapabilityEvidence> GetComponentEvidence(
        XElement? componentsProperty,
        IReadOnlyCollection<string> components)
    {
        if (componentsProperty is null)
            return [];

        return [new CapabilityEvidence("supported-calendar-component-set", string.Join(',', components))];
    }

    private static bool HasSuccessStatus(XElement response)
    {
        var status = response.Element(Dav + "status")?.Value;
        return status is null || IsSuccessStatus(status);
    }

    private static string? GetPropValue(XElement response, XName propertyName)
    {
        return GetSuccessfulProperty(response, propertyName)?.Value;
    }

    private static XElement? GetSuccessfulProperty(XElement response, XName propertyName)
    {
        var propStats = response.Descendants(Dav + "propstat");
        foreach (var propStat in propStats)
        {
            if (IsSuccessfulProperty(propStat, propertyName))
                return propStat.Element(Dav + "prop")!.Element(propertyName);
        }

        return null;
    }

    private static bool IsSuccessfulProperty(XElement propStat, XName propertyName)
    {
        var status = propStat.Element(Dav + "status")?.Value;
        return status is not null
            && IsSuccessStatus(status)
            && propStat.Element(Dav + "prop")?.Element(propertyName) is not null;
    }

    private static bool IsSuccessStatus(string rawStatus)
    {
        var parts = rawStatus.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !IsHttpVersion(parts[0])
            || parts[1].Length != 3
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var statusCode)
            || statusCode is < 100 or > 599)
        {
            throw new XmlException("The WebDAV response contains a malformed HTTP status line.");
        }

        return statusCode is >= 200 and <= 299;
    }

    private static bool IsHttpVersion(string value)
    {
        if (!value.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            return false;
        var version = value.AsSpan(5);
        var dot = version.IndexOf('.');
        return dot < 0
            ? !version.IsEmpty && version.ContainsAnyExceptInRange('0', '9') is false
            : dot > 0
                && dot < version.Length - 1
                && version[..dot].ContainsAnyExceptInRange('0', '9') is false
                && version[(dot + 1)..].ContainsAnyExceptInRange('0', '9') is false;
    }
}
