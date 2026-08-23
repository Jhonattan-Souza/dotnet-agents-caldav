namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Shared canonical href and origin policy for every Move surface.</summary>
internal static class CalendarMoveHrefPolicy
{
    public static bool IsSafeCalendarHref(string href, string baseUrl) =>
        TryParseSafeHref(href, requireTrailingSlash: true, out var candidate)
        && HasSameOrigin(new Uri(baseUrl, UriKind.Absolute), candidate);

    public static bool TryParseSafeResourceHref(string href, out Uri uri) =>
        TryParseSafeHref(href, requireTrailingSlash: false, out uri);

    public static bool IsDirectResourceOf(Uri resource, string calendarHref) =>
        TryParseSafeHref(calendarHref, requireTrailingSlash: true, out var calendar)
        && HasSameOrigin(resource, calendar)
        && resource.AbsolutePath.StartsWith(calendar.AbsolutePath, StringComparison.Ordinal)
        && IsSinglePathSegment(resource.AbsolutePath[calendar.AbsolutePath.Length..]);

    public static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool TryParseSafeHref(string href, bool requireTrailingSlash, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query)
            || candidate.AbsolutePath.EndsWith('/') != requireTrailingSlash
            || candidate.AbsolutePath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        uri = candidate;
        return true;
    }

    private static bool IsSinglePathSegment(string relative) =>
        relative.Length > 0 && !relative.Contains('/');
}
