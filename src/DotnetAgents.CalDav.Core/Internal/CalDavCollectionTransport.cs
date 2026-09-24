using DotnetAgents.CalDav.Core.Internal.Xml;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Production collection transport adapter over the HttpClient-backed CalDAV client.</summary>
internal sealed class CalDavCollectionTransport(CalDavClient client) : ICalendarCollectionTransport
{
    public Task<CalendarCollectionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken) =>
        client.DiscoverCalendarCollectionsAsync(cancellationToken);

    public Task<CalendarCollectionDispatchResult> CreateAsync(
        CalendarCollectionCreateDispatchRequest request,
        CancellationToken cancellationToken) =>
        NotDispatchedWithoutCredentialAsync(client.CreateCalendarCollectionAsync(request, cancellationToken));

    public Task<CalendarCollectionDispatchResult> DeleteAsync(
        string href,
        CancellationToken cancellationToken) =>
        NotDispatchedWithoutCredentialAsync(client.DeleteCalendarCollectionAsync(href, cancellationToken));

    private static async Task<CalendarCollectionDispatchResult> NotDispatchedWithoutCredentialAsync(
        Task<CalendarCollectionDispatchResult> dispatch)
    {
        try
        {
            return await dispatch.ConfigureAwait(false);
        }
        catch (CalDavAuthenticationException exception)
        {
            return new(CalendarMutationProtocolPrimitives.IsCredentialRejection(exception)
                ? CalendarCollectionDispatchCode.UpstreamUnauthorized
                : CalendarCollectionDispatchCode.UpstreamUnavailable);
        }
    }
}
