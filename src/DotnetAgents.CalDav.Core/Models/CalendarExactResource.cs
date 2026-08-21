namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Complete caller-authored resource creation at one explicit destination href.</summary>
public sealed record CalendarExactCreateRequest(
    string DestinationHref,
    ReadOnlyMemory<byte> AuthoritativeUtf8);

/// <summary>Stable evidence produced by one exact-create review.</summary>
public sealed record CalendarExactCreateReviewBinding(
    string DestinationHref,
    string EntityUid,
    CalendarEntityKind EntityKind,
    ReadOnlyMemory<byte> IntentDigest,
    string PolicyVersion);

/// <summary>Exact-create intent that Core has validated against current destination state.</summary>
public sealed class CalendarReviewedExactCreate
{
    internal CalendarReviewedExactCreate(
        string calendarHref,
        CalendarExactCreateReviewBinding binding,
        ReadOnlyMemory<byte> authoritativeUtf8)
    {
        CalendarHref = calendarHref;
        Binding = binding;
        AuthoritativeUtf8 = authoritativeUtf8.ToArray();
    }

    public CalendarExactCreateReviewBinding Binding { get; }

    internal string CalendarHref { get; }

    internal ReadOnlyMemory<byte> AuthoritativeUtf8 { get; }

    internal static CalendarReviewedExactCreate CreateForTest(
        CalendarExactCreateReviewBinding binding,
        ReadOnlyMemory<byte> authoritativeUtf8)
    {
        var separator = binding.DestinationHref.LastIndexOf('/');
        var calendarHref = binding.DestinationHref[..(separator + 1)];
        return new CalendarReviewedExactCreate(calendarHref, binding, authoritativeUtf8);
    }
}

/// <summary>Read-only exact-create validation evidence and its executable reviewed intent.</summary>
public sealed record CalendarExactCreateReviewResult(
    CalendarExactResourceResult? Outcome,
    CalendarExactCreateReviewBinding? Binding,
    CalendarReviewedExactCreate? ReviewedCreate);

/// <summary>Complete caller-authored replacement of one reviewed resource revision.</summary>
public sealed record CalendarExactReplaceRequest(
    CalendarResourceRevisionReference Revision,
    ReadOnlyMemory<byte> AuthoritativeUtf8);

/// <summary>Atomic relocation of one reviewed resource to one explicit destination href.</summary>
public sealed record CalendarExactMoveRequest(
    CalendarResourceRevisionReference Revision,
    string DestinationHref);

/// <summary>Closed outcomes shared by exact Calendar Object Resource writes.</summary>
public enum CalendarExactResourceCode
{
    Success,
    NoChange,
    InvalidInput,
    InvalidCalendarData,
    NotFound,
    OutsideScope,
    EntityKindMismatch,
    UnsupportedCapability,
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

/// <summary>Earliest exact-write phase that produced an outcome.</summary>
public enum CalendarExactResourcePhase
{
    SchemaLexicalDiscriminator,
    OriginScopeAuthorization,
    SelectionDiscoveryCapability,
    TargetRevision,
    CompleteResourceSemantics,
    Execution,
    PostWriteVerificationOrReconciliation
}

/// <summary>Truthful result of one exact Calendar Object Resource write.</summary>
public sealed record CalendarExactResourceResult(
    CalendarExactResourceCode Code,
    CalendarMutationState MutationState,
    CalendarResourceSnapshot? Snapshot = null,
    bool Retryable = false,
    int? RetryAfterMilliseconds = null,
    CalendarExactResourcePhase Phase = CalendarExactResourcePhase.Execution,
    CalendarEntityCreateExecutionLimits? Limits = null)
{
    public static CalendarExactResourceResult Success(CalendarResourceSnapshot snapshot) => new(
        CalendarExactResourceCode.Success,
        CalendarMutationState.Committed,
        snapshot,
        Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation);
}

/// <summary>Read-only validation evidence bound into an exact-write confirmation.</summary>
public sealed record CalendarExactResourceReviewResult(
    CalendarExactResourceResult? Outcome,
    CalendarResourceRevisionReference? BindingRevision,
    ReadOnlyMemory<byte> IntentDigest);
