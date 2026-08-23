using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Closed CalDAV seam for one server-authoritative Calendar Object Resource Move.</summary>
internal interface ICalendarMoveTransport
{
    Task<CalendarMoveDiscoveryResult> DiscoverCalendarsAsync(CancellationToken cancellationToken);

    Task<CalendarResourceRead> ReadSourceAsync(string href, CancellationToken cancellationToken);

    Task<CalendarResourceRead> ProbeDestinationPresenceAsync(string href, CancellationToken cancellationToken);

    Task<CalendarResourceRead> ObserveResourceAsync(string href, CancellationToken cancellationToken);

    Task<CalendarResourceMoveDispatchResult> DispatchAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken);
}

internal sealed record CalendarMoveDiscoveryResult(
    CalendarDiscoveryResult ScopedDiscovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault)
{
    internal CalendarSelectionResult ResolveDefault(CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => EventDefault,
        CalendarEntityKind.Todo => TodoDefault,
        _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
    };
}

internal interface ICalendarResourcePresenceTransport
{
    Task<CalendarResourceRead> ProbeCalendarResourcePresenceAsync(
        string href,
        CancellationToken cancellationToken);
}
