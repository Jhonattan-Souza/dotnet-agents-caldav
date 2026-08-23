using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Closed CalDAV seam for one server-authoritative Calendar Object Resource Move.</summary>
internal interface ICalendarMoveTransport
{
    Task<CalendarMoveDiscoveryResult> DiscoverCalendarsAsync(CancellationToken cancellationToken);

    Task<CalendarResourceRead> ReadSourceAsync(
        string sourceCalendarHref,
        string href,
        CancellationToken cancellationToken);

    Task<CalendarResourceRead> ProbeDestinationPresenceAsync(
        string destinationCalendarHref,
        string href,
        CancellationToken cancellationToken);

    Task<CalendarResourceRead> ObserveResourceAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken);

    Task<CalendarResourceMoveDispatchResult> DispatchAsync(
        string sourceCalendarHref,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken);
}

internal interface ICalendarMoveResourceTransport
{
    Task<CalendarResourceRead> ReadMoveResourceAsync(
        string authorizedCalendarHref,
        string href,
        bool absenceProbe,
        CancellationToken cancellationToken);

    Task<CalendarResourceRead> ProbeMoveResourcePresenceAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken);

    Task<CalendarResourceMoveDispatchResult> DispatchMoveAsync(
        string sourceCalendarHref,
        string destinationCalendarHref,
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
