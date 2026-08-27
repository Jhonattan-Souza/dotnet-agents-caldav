using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Closed CalDAV seam for one server-authoritative Calendar Object Resource Move.</summary>
internal interface ICalendarMoveTransport
{
    Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);

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

internal sealed class CalendarClientMoveTransport(
    CalendarOperationDiscovery discovery,
    ICalendarClient client) : ICalendarMoveTransport
{
    public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        discovery.DiscoverAsync(cancellationToken);

    public Task<CalendarResourceRead> ReadSourceAsync(
        string sourceCalendarHref,
        string href,
        CancellationToken cancellationToken) => client is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.ReadMoveResourceAsync(sourceCalendarHref, href, absenceProbe: false, cancellationToken)
            : Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability));

    public Task<CalendarResourceRead> ProbeDestinationPresenceAsync(
        string destinationCalendarHref,
        string href,
        CancellationToken cancellationToken) => client is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.ProbeMoveResourcePresenceAsync(destinationCalendarHref, href, cancellationToken)
            : Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability));

    public async Task<CalendarResourceRead> ObserveResourceAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return client is ICalendarMoveResourceTransport moveTransport
            ? await moveTransport.ReadMoveResourceAsync(
                authorizedCalendarHref,
                href,
                absenceProbe: true,
                cancellationToken)
            : new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability);
    }

    public Task<CalendarResourceMoveDispatchResult> DispatchAsync(
        string sourceCalendarHref,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => client is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.DispatchMoveAsync(
                sourceCalendarHref,
                destinationCalendarHref,
                request,
                cancellationToken)
            : Task.FromResult(new CalendarResourceMoveDispatchResult(
                CalendarResourceMoveDispatchCode.UnsupportedCapability));
}
