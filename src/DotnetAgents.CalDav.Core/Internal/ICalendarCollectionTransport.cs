using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Small transport port used by the Calendar collection deep module.</summary>
internal interface ICalendarCollectionTransport
{
    Task<CalendarCollectionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken);

    Task<CalendarCollectionDispatchResult> CreateAsync(
        CalendarCollectionCreateDispatchRequest request,
        CancellationToken cancellationToken);

    Task<CalendarCollectionDispatchResult> DeleteAsync(
        string href,
        CancellationToken cancellationToken);
}

internal sealed record CalendarCollectionDiscoverySnapshot(
    string HomeSetHref,
    IReadOnlyList<CalendarDescriptor> Items);

internal sealed record CalendarCollectionCreateDispatchRequest(
    string Href,
    string DisplayName,
    IReadOnlyList<CalendarEntityKind> EntityKinds);

internal sealed record CalendarCollectionDispatchResult(
    CalendarCollectionDispatchCode Code,
    int? StatusCode = null,
    int? RetryAfterMilliseconds = null);

internal enum CalendarCollectionDispatchCode
{
    Dispatched,
    PossiblyDispatched,
    NotFound,
    Conflict,
    UnsupportedCapability,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    ProtocolError
}
