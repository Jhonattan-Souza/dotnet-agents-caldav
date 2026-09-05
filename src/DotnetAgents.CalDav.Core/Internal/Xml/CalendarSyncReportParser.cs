using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Accepts a sync page atomically, including the server's native truncation marker.</summary>
internal static class CalendarSyncReportParser
{
    private static readonly XNamespace Dav = "DAV:";
    internal const int MaximumSyncTokenCharacters = 8192;

    internal static CalendarSyncPage Parse(
        byte[] body,
        string calendarHref,
        string priorToken,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var root = ReadXml(body);
        if (root.Name != Dav + "multistatus")
            throw InvalidResponse();
        var syncToken = ReadSyncToken(root);
        var changes = new List<CalendarResourceChange>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var hasMore = false;
        foreach (var response in root.Elements(Dav + "response"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var href = ReadHref(response, calendarHref);
            if (!identities.Add(href))
                throw InvalidResponse();
            if (href == calendarHref)
                hasMore = ReadTruncation(response);
            else
                AddChange(changes, response, href, pageSize);
        }
        if ((hasMore || changes.Count > 0) && syncToken == priorToken)
            throw InvalidResponse();
        return new(changes, syncToken, hasMore);
    }

    internal static XElement ReadXml(byte[] body)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        return XDocument.Load(reader).Root ?? throw InvalidResponse();
    }

    private static string ReadSyncToken(XElement root)
    {
        var token = ReadSingleText(root, "sync-token");
        if (token.Length > MaximumSyncTokenCharacters || token.Any(char.IsWhiteSpace)
            || !Uri.IsWellFormedUriString(token, UriKind.Absolute))
            throw InvalidResponse();
        return token;
    }

    private static string ReadHref(XElement response, string calendarHref)
    {
        var raw = ReadSingleText(response, "href");
        if (raw.Length > 8192 || raw.Contains('\\') || raw.Any(char.IsWhiteSpace)
            || HasEncodedSeparator(raw) || HasDotSegments(raw))
            throw InvalidResponse();
        var calendar = new Uri(calendarHref, UriKind.Absolute);
        if (!Uri.TryCreate(calendar, raw, out var uri) || !HasSafeIdentity(uri, calendar))
            throw InvalidResponse();
        if (uri.AbsoluteUri == calendarHref)
            return calendarHref;
        if (!uri.AbsolutePath.StartsWith(calendar.AbsolutePath, StringComparison.Ordinal))
            throw InvalidResponse();
        var member = uri.AbsolutePath[calendar.AbsolutePath.Length..];
        if (member.Length == 0 || member.Contains('/'))
            throw InvalidResponse();
        return uri.AbsoluteUri;
    }

    private static bool HasEncodedSeparator(string value) => value.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    private static bool HasDotSegments(string value) => value.Split('/').Any(segment => segment is "." or "..");

    private static bool HasSafeIdentity(Uri uri, Uri calendar) =>
        string.Equals(uri.Scheme, calendar.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, calendar.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == calendar.Port && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool ReadTruncation(XElement response)
    {
        if (response.Elements(Dav + "propstat").Any() || ReadResponseStatus(response) != 507)
            throw InvalidResponse();
        return true;
    }

    private static void AddChange(
        List<CalendarResourceChange> changes,
        XElement response,
        string href,
        int pageSize)
    {
        if (changes.Count >= pageSize)
            throw PageLimitExceeded(pageSize);
        var statuses = response.Elements(Dav + "status").ToArray();
        if (statuses.Length > 0)
        {
            if (response.Elements(Dav + "propstat").Any() || ReadResponseStatus(response) != 404)
                throw InvalidResponse();
            // A response-level 404 means removal from view, including lost access. A
            // property-level 404 below never carries that meaning.
            changes.Add(new(href, "removed"));
            return;
        }
        changes.Add(new(href, "changed", ReadEntityTag(response)));
    }

    private static int ReadResponseStatus(XElement response) => DavResponseParser.ParseStatusCode(ReadSingleText(response, "status"));

    private static string ReadEntityTag(XElement response)
    {
        string? entityTag = null;
        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            var status = ReadResponseStatus(propstat);
            var properties = propstat.Elements(Dav + "prop").ToArray();
            if (properties.Length != 1)
                throw InvalidResponse();
            var etags = properties[0].Elements(Dav + "getetag").ToArray();
            if (etags.Length == 0)
                continue;
            if (status is < 200 or > 299 || entityTag is not null || etags.Length != 1)
                throw InvalidResponse();
            entityTag = ReadEntityTagValue(etags[0]);
        }
        return entityTag ?? throw InvalidResponse();
    }

    private static string ReadEntityTagValue(XElement property)
    {
        var value = property.Value;
        if (property.HasElements || value.Length is < 2 or > 4096 || value.Any(char.IsControl)
            || !EntityTagHeaderValue.TryParse(value, out var parsed) || parsed.Tag == "*")
            throw InvalidResponse();
        return parsed.ToString();
    }

    private static string ReadSingleText(XElement parent, string localName)
    {
        var values = parent.Elements(Dav + localName).ToArray();
        if (values.Length != 1 || values[0].HasElements || values[0].Value.Length == 0)
            throw InvalidResponse();
        return values[0].Value;
    }

    private static CalendarProtocolException PageLimitExceeded(int pageSize) => new(
        "limit_exhausted", pageSize < 500
            ? "The native sync response exceeded pageSize. Retry with a larger pageSize up to 500, retaining the same checkpoint when continuing. No checkpoint was advanced."
            : "The native sync response exceeded the maximum pageSize of 500. No checkpoint was advanced; retain the prior checkpoint.");

    private static CalendarProtocolException InvalidResponse() => new(
        "upstream_protocol_error", "The native sync response has missing, conflicting, or unsafe state. No checkpoint was advanced.");
}

internal sealed record CalendarSyncPage(IReadOnlyList<CalendarResourceChange> Changes, string SyncToken, bool HasMore);
