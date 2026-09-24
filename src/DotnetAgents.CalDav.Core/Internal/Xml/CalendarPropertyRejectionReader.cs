using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>
/// Reads per-property failure evidence from a failed MKCALENDAR body (RFC 4791 §5.3.1,
/// RFC 5689 §3). Unusable or non-property evidence yields no rejections, so the caller keeps
/// its HTTP-status classification.
/// </summary>
internal static class CalendarPropertyRejectionReader
{
    private const int MaximumCharacters = 1024 * 1024;
    private const int MaximumDepth = 16;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    internal static IReadOnlyList<CalendarPropertyRejection> Read(
        byte[] body,
        string? charset,
        IReadOnlyDictionary<XName, string> requested)
    {
        if (body.Length == 0)
            return [];
        try
        {
            // A successfully loaded XML document always has a root element.
            var root = XmlResponseReader.Load(body, charset, new XmlReaderSettings
            {
                MaxCharactersInDocument = MaximumCharacters,
                MaxCharactersFromEntities = 0
            }).Root!;
            return IsFailureRoot(root) && WithinDepth(root) ? Rejections(root, requested) : [];
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException)
        {
            return [];
        }
    }

    private static bool IsFailureRoot(XElement root) =>
        root.Name == CalDav + "mkcalendar-response" || root.Name == Dav + "mkcol-response" || root.Name == Dav + "multistatus";

    private static bool WithinDepth(XElement root) =>
        !root.Descendants().Any(element => element.Ancestors().Take(MaximumDepth + 1).Count() > MaximumDepth);

    private static IReadOnlyList<CalendarPropertyRejection> Rejections(
        XElement root,
        IReadOnlyDictionary<XName, string> requested)
    {
        var rejections = new List<CalendarPropertyRejection>();
        foreach (var propstat in root.Descendants(Dav + "propstat"))
        {
            var status = propstat.Elements(Dav + "status").ToArray();
            if (status.Length != 1)
                return [];
            var code = DavResponseParser.ParseStatusCode(status[0].Value);
            if (code is >= 200 and < 300)
                continue;
            rejections.AddRange(propstat.Elements(Dav + "prop").Elements()
                .Where(property => requested.ContainsKey(property.Name))
                .Select(property => new CalendarPropertyRejection(requested[property.Name], code)));
        }
        return rejections.Any(rejection => rejection.StatusCode != 424)
            ? rejections.Distinct().OrderBy(rejection => rejection.Property, StringComparer.Ordinal).ToArray()
            : [];
    }
}
