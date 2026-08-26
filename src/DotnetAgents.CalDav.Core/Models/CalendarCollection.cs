namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Creates one CalDAV Calendar collection.</summary>
public sealed record CalendarCollectionCreateRequest(
    string DisplayName,
    IReadOnlyList<CalendarEntityKind> EntityKinds,
    string? DestinationHref = null);

/// <summary>Closed outcomes for Calendar collection creation.</summary>
public enum CalendarCollectionCreateCode
{
    Success,
    InvalidInput,
    OutsideScope,
    Conflict,
    DestinationConflict,
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

/// <summary>Truthful result of one Calendar collection creation.</summary>
public sealed record CalendarCollectionCreateResult(
    CalendarCollectionCreateCode Code,
    CalendarMutationState MutationState,
    CalendarDescriptor? Calendar = null,
    bool Retryable = false,
    int? RetryAfterMilliseconds = null)
{
    public static CalendarCollectionCreateResult Success(CalendarDescriptor calendar) => new(
        CalendarCollectionCreateCode.Success,
        CalendarMutationState.Committed,
        calendar);
}

/// <summary>Deletes one exact Calendar collection href.</summary>
public sealed record CalendarCollectionDeleteRequest(string Href);

/// <summary>Stable, non-executable evidence reviewed before a collection delete.</summary>
public sealed record CalendarCollectionDeleteReviewBinding(string Href, string DescriptorDigest);

/// <summary>Read-only collection delete review result.</summary>
public sealed record CalendarCollectionDeleteReviewResult(
    CalendarCollectionDeleteResult? Outcome,
    CalendarCollectionDeleteReviewBinding? Binding,
    CalendarDescriptor? Calendar);

/// <summary>Closed outcomes for Calendar collection deletion.</summary>
public enum CalendarCollectionDeleteCode
{
    Success,
    InvalidInput,
    NotFound,
    OutsideScope,
    Conflict,
    UnsupportedCapability,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError,
    ConfirmationMismatch,
    CommittedButUnverified,
    Indeterminate
}

/// <summary>Truthful result of one Calendar collection deletion.</summary>
public sealed record CalendarCollectionDeleteResult(
    CalendarCollectionDeleteCode Code,
    CalendarMutationState MutationState,
    CalendarDescriptor? Calendar = null,
    bool Retryable = false,
    int? RetryAfterMilliseconds = null)
{
    public static CalendarCollectionDeleteResult Success(CalendarDescriptor calendar) => new(
        CalendarCollectionDeleteCode.Success,
        CalendarMutationState.Committed,
        calendar);
}
