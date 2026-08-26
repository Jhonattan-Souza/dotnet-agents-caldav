namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Production collection transport adapter over the HttpClient-backed CalDAV client.</summary>
internal sealed class CalDavCollectionTransport(CalDavClient client) : ICalendarCollectionTransport
{
    public Task<CalendarCollectionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken) =>
        client.DiscoverCalendarCollectionsAsync(cancellationToken);

    public Task<CalendarCollectionDispatchResult> CreateAsync(
        CalendarCollectionCreateDispatchRequest request,
        CancellationToken cancellationToken) =>
        client.CreateCalendarCollectionAsync(request, cancellationToken);

    public Task<CalendarCollectionDispatchResult> DeleteAsync(
        string href,
        CancellationToken cancellationToken) =>
        client.DeleteCalendarCollectionAsync(href, cancellationToken);
}
