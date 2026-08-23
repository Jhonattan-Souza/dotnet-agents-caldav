namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Allowed destination modes for one semantic Calendar Object Resource move.</summary>
public sealed record CalendarMoveDestination(
    CalendarEntityScopeMode Mode,
    CalendarReference? Calendar = null)
{
    public static CalendarMoveDestination Default { get; } = new(CalendarEntityScopeMode.Default);

    public static CalendarMoveDestination Selected(CalendarReference calendar) =>
        new(CalendarEntityScopeMode.Selected, calendar);
}

/// <summary>Revision-bound intent to move one resource to a selected Calendar.</summary>
public sealed record CalendarResourceMoveRequest(
    CalendarResourceRevisionReference Revision,
    CalendarMoveDestination Destination);

/// <summary>Closed semantic-move outcomes at the Calendar Service boundary.</summary>
public enum CalendarResourceMoveCode
{
    Success,
    InvalidInput,
    NotFound,
    Ambiguous,
    OutsideScope,
    EntityKindMismatch,
    UnsupportedCapability,
    OpaqueResource,
    Conflict,
    DestinationConflict,
    ConcurrencyUnavailable,
    LimitExhausted,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError,
    FidelityFailure,
    CommittedButUnverified,
    CommittedButConcurrencyUnavailable,
    Indeterminate
}

/// <summary>Typed execution-budget dimension exhausted by a semantic move.</summary>
public enum CalendarResourceMoveLimitDimension
{
    ElapsedTime
}

/// <summary>Earliest validation or execution phase that produced a semantic-move outcome.</summary>
public enum CalendarResourceMovePhase
{
    SchemaLexicalDiscriminator,
    OriginScopeAuthorization,
    SelectionDiscoveryCapability,
    TargetRevision,
    CompleteResourceSemantics,
    AdmissionAndPayload,
    Execution,
    PostWriteVerificationOrReconciliation
}

/// <summary>Truthful semantic-move result with observed authorized state when available.</summary>
public sealed record CalendarResourceMoveResult(
    CalendarResourceMoveCode Code,
    CalendarMutationState MutationState,
    CalendarResourceSnapshot? Snapshot = null,
    IReadOnlyList<CalendarDescriptor>? AuthorizedCandidates = null,
    int? RetryAfterMilliseconds = null,
    bool Retryable = false,
    CalendarResourceMoveLimitDimension? LimitDimension = null,
    CalendarResourceMovePhase? Phase = null,
    int? CalendarCount = null)
{
    public static CalendarResourceMoveResult Success(CalendarResourceSnapshot snapshot) =>
        new(
            CalendarResourceMoveCode.Success,
            CalendarMutationState.Committed,
            snapshot,
            Phase: CalendarResourceMovePhase.PostWriteVerificationOrReconciliation);
}

/// <summary>One exact atomic MOVE request at the CalDAV transport boundary.</summary>
public sealed record CalendarResourceMoveDispatchRequest(
    string SourceHref,
    string DestinationHref,
    string EntityTag);

/// <summary>Observable outcome of the one permitted MOVE dispatch.</summary>
public enum CalendarResourceMoveDispatchCode
{
    Dispatched,
    PossiblyDispatched,
    Conflict,
    DestinationConflict,
    NotFound,
    InvalidInput,
    UnsupportedCapability,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError
}

/// <summary>Low-level result that preserves whether MOVE may have reached CalDAV.</summary>
public sealed record CalendarResourceMoveDispatchResult(
    CalendarResourceMoveDispatchCode Code,
    int? RetryAfterMilliseconds = null,
    CalendarResourceMoveDispatchCollisionKind CollisionKind = CalendarResourceMoveDispatchCollisionKind.None);

/// <summary>Server-authoritative collision evidence attached to a rejected MOVE dispatch.</summary>
public enum CalendarResourceMoveDispatchCollisionKind
{
    None,
    DestinationHref,
    Uid,
    Unclassified
}
