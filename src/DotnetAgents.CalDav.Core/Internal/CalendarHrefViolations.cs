using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Fixed reasons a caller-supplied resource href is refused before any CalDAV request.</summary>
internal static class CalendarHrefViolations
{
    /// <summary>Returns the first rule a candidate breaks, in the order the canonical-href check applies them.</summary>
    internal static CalendarRequestViolation Canonical(string pointer, string href, Uri? candidate) => candidate switch
    {
        null => new(pointer, "href_not_absolute", "href must be an absolute http or https URL."),
        _ when !candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) =>
            new(pointer, "href_scheme_unsupported", "href must use http or https."),
        _ when !string.IsNullOrEmpty(candidate.UserInfo) =>
            new(pointer, "href_has_userinfo", "href must not contain user information."),
        _ when !string.IsNullOrEmpty(candidate.Fragment) || !string.IsNullOrEmpty(candidate.Query) =>
            new(pointer, "href_has_query_or_fragment", "href must not contain a query or fragment."),
        _ when !string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal) =>
            new(pointer, "href_not_canonical", "href must be passed exactly as a query or read returned it, in canonical form."),
        _ => new(pointer, "href_encoded_separator", "href must not contain an encoded path separator.")
    };

    internal static CalendarRequestViolation ForeignOrigin(string pointer) =>
        new(pointer, "href_foreign_origin", "href must use the configured CalDAV account's origin.");

    internal static CalendarRequestViolation OutsideConfiguredScope(string pointer) =>
        new(pointer, "outside_configured_scope", "href is not directly inside a Calendar listed in CALDAV_CALENDAR_HREFS.");

    internal static CalendarRequestViolation NoOwningCalendar(string pointer) =>
        new(pointer, "no_owning_calendar", "No discovered Calendar directly contains this href; use it exactly as a query returned it.");
}
