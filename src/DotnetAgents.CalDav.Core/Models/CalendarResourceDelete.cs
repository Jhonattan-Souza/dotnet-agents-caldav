namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Self-contained identity and strong revision required to delete one resource.</summary>
public sealed record CalendarResourceRevisionReference(
    string Href,
    string EntityUid,
    CalendarEntityKind EntityKind,
    string EntityTag);

/// <summary>Successful proof that one reviewed Calendar Object Resource is absent.</summary>
public sealed record CalendarResourceDeletionReceipt(
    string Href,
    string EntityUid,
    CalendarEntityKind EntityKind,
    string ConsumedEntityTag);

/// <summary>Closed deletion outcomes at the Calendar Service boundary.</summary>
public enum CalendarResourceDeleteCode
{
    Success,
    InvalidInput,
    NotFound,
    OutsideScope,
    EntityKindMismatch,
    OpaqueResource,
    Conflict,
    ConcurrencyUnavailable,
    UnsupportedCapability,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError,
    CommittedButUnverified,
    Indeterminate
}

/// <summary>Truthful deletion result, including current authorized state when available.</summary>
public sealed record CalendarResourceDeleteResult(
    CalendarResourceDeleteCode Code,
    CalendarMutationState MutationState,
    CalendarResourceDeletionReceipt? DeletionReceipt = null,
    CalendarResourceSnapshot? CurrentSnapshot = null,
    int? RetryAfterMilliseconds = null,
    bool Retryable = false)
{
    public static CalendarResourceDeleteResult Success(CalendarResourceDeletionReceipt receipt) =>
        new(CalendarResourceDeleteCode.Success, CalendarMutationState.Committed, receipt);
}

/// <summary>One exact conditional DELETE request at the CalDAV transport boundary.</summary>
public sealed record CalendarResourceDeleteRequest(string ResourceHref, string EntityTag);

/// <summary>Observable outcome of the one permitted conditional DELETE dispatch.</summary>
public enum CalendarResourceDeleteDispatchCode
{
    Dispatched,
    PossiblyDispatched,
    NotFound,
    Conflict,
    InvalidInput,
    UnsupportedCapability,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError
}

/// <summary>Low-level result that preserves whether DELETE may have reached CalDAV.</summary>
public sealed record CalendarResourceDeleteDispatchResult(
    CalendarResourceDeleteDispatchCode Code,
    int? RetryAfterMilliseconds = null);
