using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal interface ICalendarCreateTransport
{
    Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken);

    Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken);

    async Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return await GetCalendarResourceAsync(href, cancellationToken);
    }

    Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CalendarClientCreateTransport(ICalendarClient client) : ICalendarCreateTransport
{
    public Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken) =>
        client.GetCalendarsAsync(cancellationToken);

    public Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken) =>
        client.GetCalendarResourceAsync(href, cancellationToken);

    public async Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return await client.GetCalendarResourceAsync(href, cancellationToken);
    }

    public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken) => client.CreateCalendarResourceAsync(request, cancellationToken);
}
