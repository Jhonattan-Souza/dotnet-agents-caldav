using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Production CalDAV adapter for the query transport seam.</summary>
internal sealed class CalendarQueryTransport(CalendarOperationDiscovery discovery) : ICalendarQueryTransport
{
    public async Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken)
    {
        var scoped = await discovery.GetScopedResultAsync(cancellationToken).ConfigureAwait(false);
        return new CalendarQueryDiscovery(
            scoped,
            discovery.ResolveDefault(CalendarEntityKind.Event),
            discovery.ResolveDefault(CalendarEntityKind.Todo));
    }

    public async Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) => await discovery.QueryCalendarResourceHrefsAsync(
            calendarHref,
            entityKind,
            from,
            to,
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<CalendarResourceRead>> MultigetAsync(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        CancellationToken cancellationToken) => await discovery.GetCalendarResourcesForQueryAsync(
            calendarHref,
            resourceHrefs,
            cancellationToken).ConfigureAwait(false);

    public async Task<CalendarResourceRead> GetAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken) => await discovery.GetCalendarResourceForQueryAsync(
            calendarHref,
            resourceHref,
            cancellationToken).ConfigureAwait(false);
}
