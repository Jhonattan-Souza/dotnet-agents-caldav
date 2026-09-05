using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal interface ICalendarCreateTransport
{
    Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);

    Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken);

    async Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return await GetCalendarResourceAsync(href, cancellationToken);
    }

    /// <summary>Checks fresh server evidence before a storage-only participation mutation.</summary>
    Task<bool> IsStorageOnlyMutationAllowedAsync(
        string calendarHref,
        ReadOnlyMemory<byte> priorUtf8,
        ReadOnlyMemory<byte> proposedUtf8,
        CancellationToken cancellationToken) => Task.FromResult(
            !CalendarSchedulingSafety.RequiresCheck(priorUtf8, proposedUtf8));

    Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CalendarClientCreateTransport(
    CalendarOperationDiscovery discovery,
    ICalendarClient client) : ICalendarCreateTransport
{
    public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        discovery.DiscoverAsync(cancellationToken);

    public Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken) =>
        client.GetCalendarResourceAsync(href, cancellationToken);

    public async Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return await client.GetCalendarResourceAsync(href, cancellationToken);
    }

    public Task<bool> IsStorageOnlyMutationAllowedAsync(
        string calendarHref, ReadOnlyMemory<byte> priorUtf8, ReadOnlyMemory<byte> proposedUtf8,
        CancellationToken cancellationToken) => client.IsStorageOnlyMutationAllowedAsync(
            calendarHref, priorUtf8, proposedUtf8, cancellationToken);

    public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken) => client.CreateCalendarResourceAsync(request, cancellationToken);
}
