using System.Globalization;
using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed partial class CalDavClient
{
    internal static readonly HttpRequestOptionsKey<bool> NativeReportKey = new("DotnetAgents.CalDav.NativeReport");

    internal async Task<string> AuthorizeProtocolCalendarAsync(
        string href,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAbsoluteResourceHref(href, out var uri) || !uri.AbsolutePath.EndsWith('/'))
            throw new CalendarProtocolException("invalid_input", "Provide a canonical absolute Calendar href ending in '/'.");

        var scope = CalendarDiscoveryPolicy.ParseScope(_options.Value.CalendarHrefs);
        if (scope.Count > 0)
        {
            if (!scope.Contains(href, StringComparer.Ordinal))
                throw new CalendarProtocolException("outside_scope", "The Calendar is outside the configured Calendar Scope.");
            return href;
        }

        var calendars = await GetCalendarsAsync(cancellationToken).ConfigureAwait(false);
        if (!calendars.Any(calendar => string.Equals(calendar.Href, href, StringComparison.Ordinal)))
            throw new CalendarProtocolException("not_found", "The exact Calendar was not found in Calendar discovery.");
        return href;
    }

    internal async Task<CalendarProtocolResponse> SendProtocolRequestAsync(
        string href,
        string method,
        string? body,
        int? depth,
        CancellationToken cancellationToken)
    {
        if (!TryValidateAbsoluteResourceHref(href, out var uri))
            throw new CalendarProtocolException("invalid_input", "The operation requires a canonical href on the configured origin.");

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        if (request.Method.Method == "REPORT")
            request.Options.Set(NativeReportKey, true);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        if (depth is not null)
            request.Headers.Add("Depth", depth.Value.ToString(CultureInfo.InvariantCulture));

        // The href has already been authorized. A redirect cannot authorize another target,
        // particularly when the Calendar is readable only through an explicit href allowlist.
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var content = await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (content.Content is null)
            throw new HttpRequestException("The Calendar response exceeded its byte limit.", null, HttpStatusCode.RequestEntityTooLarge);

        return new CalendarProtocolResponse(
            (int)response.StatusCode,
            response.RequestMessage?.RequestUri?.AbsoluteUri ?? href,
            content.Content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.TryGetValues("DAV", out var compliance) ? compliance.ToArray() : [],
            response.Content.Headers.ContentType?.CharSet);
    }
}

internal sealed record CalendarProtocolResponse(
    int StatusCode,
    string RequestHref,
    byte[] Body,
    string? ContentType,
    IReadOnlyList<string> DavCompliance,
    string? CharSet = null);
