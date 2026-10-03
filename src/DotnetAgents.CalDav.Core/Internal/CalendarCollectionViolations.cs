using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Fixed reasons a Calendar collection create or delete is refused before any CalDAV write.</summary>
internal static class CalendarCollectionViolations
{
    private const int MaximumDisplayNameCharacters = 256;

    /// <summary>Names the first create input rule broken, in the order the module checks them.</summary>
    internal static CalendarRequestViolation? CreateInput(CalendarCollectionCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return new("/displayName", "display_name_blank", "displayName must contain non-whitespace text.");
        if (request.DisplayName.Trim().Length > MaximumDisplayNameCharacters)
            return new("/displayName", "display_name_too_long", "displayName can have at most 256 characters.");
        if (request.EntityKinds is not { Count: >= 1 and <= 2 } kinds
            || kinds.Distinct().Count() != kinds.Count
            || kinds.Any(kind => kind is not (CalendarEntityKind.Event or CalendarEntityKind.Todo)))
            return new("/entityKinds", "entity_kinds_invalid", "entityKinds must list event, todo, or both, once each.");
        return InitialProperty(request);
    }

    private static CalendarRequestViolation? InitialProperty(CalendarCollectionCreateRequest request)
    {
        if (request.Color is not null && !CalendarCollectionPropertyValues.IsWritableColor(request.Color))
            return new("/color", "color_invalid", "color must be #RRGGBB.");
        if (request.Order is not null && !CalendarCollectionPropertyValues.IsWritableOrder(request.Order))
            return new("/order", "order_invalid", "order must be a non-negative integer.");
        return request.TimeZoneId is not null && !CalendarCollectionPropertyValues.IsTimeZoneId(request.TimeZoneId)
            ? new("/timeZone", "time_zone_invalid", "timeZone must be a known IANA time zone identifier.")
            : null;
    }

    internal static CalendarRequestViolation DisplayNameTaken { get; } = new(
        "/displayName", "display_name_taken", "A Calendar with this display name already exists; choose another name.");

    internal static CalendarRequestViolation DestinationRequired { get; } = new(
        "/destinationHref", "destination_href_required",
        "This account does not have exactly one Calendar Home; pass destinationHref directly under the intended home.");

    internal static CalendarRequestViolation DestinationRequiredByScope { get; } = new(
        "/destinationHref", "destination_href_required_by_scope",
        "With CALDAV_CALENDAR_HREFS set, pass a destinationHref that the configured scope lists.");

    internal static CalendarRequestViolation DestinationInvalid { get; } = new(
        "/destinationHref", "destination_href_invalid",
        "destinationHref must be a canonical collection href ending in / directly under a Calendar Home and inside the configured scope.");

    internal static CalendarRequestViolation CollectionHrefInvalid { get; } = new(
        "/href", "collection_href_invalid",
        "href must be a canonical Calendar collection href on the account origin, ending in /, without a query, fragment, or encoded separators.");
}
