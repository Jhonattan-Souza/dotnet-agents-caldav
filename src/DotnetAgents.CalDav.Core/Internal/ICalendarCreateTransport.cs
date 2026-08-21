using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal interface ICalendarCreateTransport
{
    Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken);

    Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken);

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

    public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken) => client.CreateCalendarResourceAsync(request, cancellationToken);
}
