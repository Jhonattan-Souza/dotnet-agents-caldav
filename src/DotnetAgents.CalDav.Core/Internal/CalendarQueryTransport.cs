using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Production CalDAV adapter for the query transport seam.</summary>
internal sealed class CalendarQueryTransport(
    CalendarOperationDiscovery discovery,
    CalDavClient client) : ICalendarQueryTransport
{
    public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        discovery.DiscoverAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) => await client.QueryCandidateHrefsAsync(
            calendarHref,
            entityKind,
            from,
            to,
            cancellationToken).ConfigureAwait(false);

    public async Task<CalendarMultigetResult> MultigetAsync(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        CancellationToken cancellationToken)
    {
        try
        {
            var resources = await client.GetCalendarResourcesForQueryAsync(
                calendarHref,
                resourceHrefs,
                cancellationToken).ConfigureAwait(false);
            return new CalendarMultigetResult.Resources(resources);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return new CalendarMultigetResult.VerifiedUnavailable();
        }
    }

    public async Task<CalendarResourceRead> GetAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken) => await client.GetCalendarResourceDirectlyForQueryAsync(
            calendarHref,
            resourceHref,
            cancellationToken)
            .ConfigureAwait(false);
}
