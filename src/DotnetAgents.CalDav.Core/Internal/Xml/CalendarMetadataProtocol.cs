using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal static class CalendarMetadataProtocol
{
    internal static readonly XNamespace Dav = "DAV:";
    internal static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XName[] Properties =
    [
        Dav + "resourcetype", Dav + "displayname", CalDav + "calendar-description",
        Dav + "supported-report-set", Dav + "current-user-privilege-set",
        CalDav + "max-resource-size", CalDav + "max-instances", CalDav + "max-attendees-per-instance",
        CalDav + "min-date-time", CalDav + "max-date-time", CalDav + "calendar-timezone"
    ];

    internal static string InspectBody() => new XElement(Dav + "propfind",
        new XElement(Dav + "prop", Properties.Select(name => new XElement(name))))
        .ToString(SaveOptions.DisableFormatting);

    internal static CalendarMetadataObservation ParseMetadata(
        string href,
        CalendarProtocolResponse response,
        CancellationToken cancellationToken = default)
    {
        RequireStatus(response, 207);
        var properties = ReadProperties(href, response.Body, response.CharSet);
        var resourceType = Value(properties, Dav + "resourcetype");
        if (resourceType is null)
            throw PropertyFailure(properties.GetValueOrDefault(Dav + "resourcetype")?.StatusCode);
        if (!resourceType.Elements(CalDav + "calendar").Any() || !resourceType.Elements(Dav + "collection").Any())
            throw new CalendarProtocolException("unsupported_capability", "The target does not identify a CalDAV Calendar collection.");

        return new CalendarMetadataObservation(href, properties, new CalendarMetadataSnapshot(
            href,
            Value(properties, Dav + "displayname")?.Value,
            Value(properties, CalDav + "calendar-description")?.Value,
            Language(Value(properties, CalDav + "calendar-description")),
            PropertyState(properties, Dav + "supported-report-set"),
            Reports(Value(properties, Dav + "supported-report-set")),
            PropertyState(properties, Dav + "current-user-privilege-set"),
            Privileges(Value(properties, Dav + "current-user-privilege-set")),
            new CalendarAdvertisedLimits(
                Integer(properties, CalDav + "max-resource-size"),
                Integer(properties, CalDav + "max-instances"),
                Integer(properties, CalDav + "max-attendees-per-instance"),
                DateTimeLimit(properties, CalDav + "min-date-time"),
                DateTimeLimit(properties, CalDav + "max-date-time")),
            TimeZoneIds(Value(properties, CalDav + "calendar-timezone"), cancellationToken),
            Properties.Select(name => new CalendarPropertyObservation(
                name.NamespaceName, name.LocalName, properties.GetValueOrDefault(name)?.StatusCode)).ToArray(),
            new CalendarSchedulingObservation("unknown", null)));
    }

    internal static IReadOnlyDictionary<XName, CalendarMetadataProperty> ReadProperties(string href, byte[] body, string? charset = null)
    {
        var root = ParseXml(body, charset).Root;
        if (root?.Name != Dav + "multistatus")
            throw ProtocolError();
        var responses = root.Elements(Dav + "response").ToArray();
        if (responses.Length != 1 || !IsTarget(href, responses[0]))
            throw ProtocolError();
        var response = responses[0];
        var responseStatus = response.Elements(Dav + "status").ToArray();
        if (responseStatus.Length > 0)
            throw StatusFailure(ReadSingleStatus(responseStatus));
        return ReadPropertyStatuses(response);
    }

    private static IReadOnlyDictionary<XName, CalendarMetadataProperty> ReadPropertyStatuses(XElement response)
    {
        var result = new Dictionary<XName, CalendarMetadataProperty>();
        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            var status = ReadSingleStatus(propstat.Elements(Dav + "status").ToArray());
            var properties = propstat.Elements(Dav + "prop").ToArray();
            if (properties.Length != 1)
                throw ProtocolError();
            foreach (var property in properties[0].Elements())
            {
                if (!result.TryAdd(property.Name, new CalendarMetadataProperty(status, property)))
                    throw ProtocolError();
            }
        }
        if (result.Count == 0)
            throw ProtocolError();
        return result;
    }

    private static bool IsTarget(string href, XElement response)
    {
        var identities = response.Elements(Dav + "href").ToArray();
        return identities.Length == 1
            && Uri.TryCreate(new Uri(href, UriKind.Absolute), identities[0].Value.Trim(), out var candidate)
            && (string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal)
                || href.EndsWith('/') && string.Equals(candidate.AbsoluteUri, href[..^1], StringComparison.Ordinal));
    }

    private static int ReadSingleStatus(XElement[] elements)
    {
        if (elements.Length != 1 || elements[0].HasElements)
            throw ProtocolError();
        try
        {
            return DavResponseParser.ParseStatusCode(elements[0].Value);
        }
        catch (XmlException)
        {
            throw ProtocolError();
        }
    }

    private static XDocument ParseXml(byte[] body, string? charset)
    {
        var document = XmlResponseReader.Load(body, charset, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        }, LoadOptions.PreserveWhitespace);
        if (document.Descendants().Any(element => element.Ancestors().Take(65).Count() > 64))
            throw ProtocolError();
        return document;
    }

    private static XElement? Value(IReadOnlyDictionary<XName, CalendarMetadataProperty> properties, XName name) =>
        properties.TryGetValue(name, out var property) && property.StatusCode == 200 ? property.Element : null;

    private static string PropertyState(IReadOnlyDictionary<XName, CalendarMetadataProperty> properties, XName name) =>
        properties.TryGetValue(name, out var property)
            ? property.StatusCode == 200 ? "available" : "unavailable"
            : "unknown";

    private static IReadOnlyList<CalendarProtocolName> Reports(XElement? property) => QualifiedNames(
        property?.Elements(Dav + "supported-report").SelectMany(report => report.Elements(Dav + "report"))
            .SelectMany(report => report.Elements()) ?? []);

    private static IReadOnlyList<CalendarProtocolName> Privileges(XElement? property) => QualifiedNames(
        property?.Elements(Dav + "privilege").SelectMany(privilege => privilege.Elements()) ?? []);

    private static IReadOnlyList<CalendarProtocolName> QualifiedNames(IEnumerable<XElement> elements)
    {
        var names = elements.Select(element => new CalendarProtocolName(element.Name.NamespaceName, element.Name.LocalName))
            .Distinct().Take(65).ToArray();
        if (names.Length > 64)
            throw new CalendarProtocolException("limit_exhausted", "Calendar capability evidence exceeded its entry limit.");
        return names;
    }

    internal static string? Language(XElement? property)
    {
        var language = property?.AncestorsAndSelf()
            .Select(element => element.Attribute(XNamespace.Xml + "lang")?.Value).FirstOrDefault(value => value is not null);
        return string.IsNullOrEmpty(language) ? null : language;
    }

    private static long? Integer(IReadOnlyDictionary<XName, CalendarMetadataProperty> properties, XName name)
    {
        var value = Value(properties, name)?.Value.Trim();
        if (value is null)
            return null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw ProtocolError();
    }

    private static string? DateTimeLimit(IReadOnlyDictionary<XName, CalendarMetadataProperty> properties, XName name)
    {
        var value = Value(properties, name)?.Value.Trim();
        if (value is null)
            return null;
        return DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : throw ProtocolError();
    }

    private static IReadOnlyList<string> TimeZoneIds(XElement? property, CancellationToken cancellationToken) =>
        property is null ? [] : CalendarMetadataTimeZoneReader.Read(property.Value, cancellationToken);

    internal static void RequireStatus(CalendarProtocolResponse response, int expected)
    {
        if (response.StatusCode != expected)
            throw StatusFailure(response.StatusCode);
    }

    internal static CalendarProtocolException StatusFailure(int status) => status switch
    {
        401 => new("upstream_unauthorized", "The Calendar operation was not authorized."),
        403 => new("upstream_forbidden", "The Calendar operation was forbidden."),
        404 => new("not_found", "The Calendar is missing or inaccessible."),
        405 or 501 => new("unsupported_capability", "The Calendar server does not support this operation."),
        409 or 412 => new("conflict", "The Calendar property update conflicted with server state."),
        413 => new("payload_too_large", "The Calendar response exceeded its byte limit."),
        429 => new("upstream_rate_limited", "The Calendar server is rate limiting requests.", true),
        >= 500 => new("upstream_unavailable", "The Calendar server is temporarily unavailable.", true),
        _ => ProtocolError()
    };

    private static CalendarProtocolException PropertyFailure(int? status) => status is 401 or 403
        ? StatusFailure(status.Value) : ProtocolError();

    internal static CalendarProtocolException ProtocolError() =>
        new("upstream_protocol_error", "The Calendar server returned inconsistent property or response evidence.");
}

internal sealed record CalendarMetadataProperty(int StatusCode, XElement Element);

internal sealed record CalendarMetadataObservation(
    string Href,
    IReadOnlyDictionary<XName, CalendarMetadataProperty> Properties,
    CalendarMetadataSnapshot Snapshot);
