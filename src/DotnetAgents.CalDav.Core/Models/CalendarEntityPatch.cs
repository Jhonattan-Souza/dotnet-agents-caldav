namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Explicit scalar mutation intent; omission remains preservation.</summary>
public enum CalendarScalarPatchOperation
{
    Set,
    Clear
}

/// <summary>One strongly typed scalar patch.</summary>
public sealed record CalendarScalarPatch<T>(CalendarScalarPatchOperation Operation, T? Value = default);

/// <summary>Explicit repeatable-field mutation intent.</summary>
public enum CalendarCollectionPatchOperation
{
    AddRemove,
    ReplaceAll
}

/// <summary>Closed repeatable fields whose occurrences can be patched independently.</summary>
public enum CalendarCollectionField
{
    Categories,
    Attendees,
    Participants,
    Contacts,
    Resources,
    RelatedTo,
    RequestStatuses,
    Alarms,
    Attachments,
    Comments,
    StyledDescriptions,
    Images,
    Conferences,
    Links,
    Concepts,
    StructuredDataUris,
    LocationUris,
    ResourceUris
}

/// <summary>Non-generic view used by the lossless occurrence editor.</summary>
public interface ICalendarCollectionPatch
{
    CalendarCollectionField Field { get; }

    CalendarCollectionPatchOperation Operation { get; }

    IReadOnlyList<object>? AddValues { get; }

    IReadOnlyList<object>? RemoveValues { get; }

    IReadOnlyList<object>? ReplacementValues { get; }
}

/// <summary>One strongly typed repeatable-field patch.</summary>
public sealed record CalendarCollectionPatch<T>(
    CalendarCollectionPatchOperation Operation,
    IReadOnlyList<T>? Add = null,
    IReadOnlyList<T>? Remove = null,
    IReadOnlyList<T>? Values = null,
    CalendarCollectionField Field = CalendarCollectionField.Categories) : ICalendarCollectionPatch
{
    IReadOnlyList<object>? ICalendarCollectionPatch.AddValues => Add?.Cast<object>().ToArray();

    IReadOnlyList<object>? ICalendarCollectionPatch.RemoveValues => Remove?.Cast<object>().ToArray();

    IReadOnlyList<object>? ICalendarCollectionPatch.ReplacementValues => Values?.Cast<object>().ToArray();
}

/// <summary>One Event patch after strict wire parsing.</summary>
public sealed record CalendarEventPatch(
    CalendarScalarPatch<string>? Summary = null,
    CalendarScalarPatch<string>? Description = null,
    CalendarScalarPatch<CalendarTemporalValue>? Start = null,
    CalendarScalarPatch<CalendarTemporalValue>? End = null,
    CalendarScalarPatch<CalendarTemporalValue>? Due = null,
    CalendarScalarPatch<string>? Duration = null,
    CalendarScalarPatch<string>? Location = null,
    CalendarScalarPatch<CalendarGeo>? Geo = null,
    CalendarScalarPatch<string>? Status = null,
    CalendarScalarPatch<string>? Transparency = null,
    CalendarScalarPatch<string>? Classification = null,
    CalendarScalarPatch<int>? Priority = null,
    CalendarScalarPatch<int>? PercentComplete = null,
    CalendarScalarPatch<string>? Url = null,
    CalendarScalarPatch<CalendarNamedUri>? Organizer = null,
    CalendarCollectionPatch<string>? Categories = null,
    IReadOnlyList<ICalendarCollectionPatch>? Collections = null,
    bool RecurrenceSetAddressed = false,
    bool RequiresConfirmation = false);

/// <summary>One To-do patch after strict wire parsing.</summary>
public sealed record CalendarTodoPatch(
    CalendarScalarPatch<string>? Summary = null,
    CalendarScalarPatch<string>? Description = null,
    CalendarScalarPatch<CalendarTemporalValue>? Start = null,
    CalendarScalarPatch<CalendarTemporalValue>? Due = null,
    CalendarScalarPatch<string>? Duration = null,
    CalendarScalarPatch<string>? Status = null,
    CalendarScalarPatch<int>? Priority = null,
    CalendarScalarPatch<int>? PercentComplete = null,
    CalendarScalarPatch<CalendarNamedUri>? Organizer = null,
    CalendarCollectionPatch<string>? Categories = null,
    IReadOnlyList<ICalendarCollectionPatch>? Collections = null,
    bool RecurrenceSetAddressed = false,
    bool RequiresConfirmation = false);

/// <summary>Explicit master or original Recurrence Identity mutation target.</summary>
public sealed record CalendarMutationTarget(
    string Scope,
    CalendarTemporalValue? RecurrenceIdentity = null);

/// <summary>Revision-bound Event semantic patch request.</summary>
public sealed record CalendarEventPatchRequest(
    CalendarResourceRevisionReference Snapshot,
    CalendarMutationTarget Target,
    CalendarEventPatch Patch);

/// <summary>Revision-bound To-do semantic patch request.</summary>
public sealed record CalendarTodoPatchRequest(
    CalendarResourceRevisionReference Snapshot,
    CalendarMutationTarget Target,
    CalendarTodoPatch Patch);

/// <summary>Closed semantic-patch outcomes.</summary>
public enum CalendarEntityPatchCode
{
    Success,
    NoChange,
    InvalidInput,
    InvalidCalendarData,
    NotFound,
    RemovalNotFound,
    RemovalAmbiguous,
    OutsideScope,
    EntityKindMismatch,
    OpaqueResource,
    RecurrenceUnevaluable,
    Conflict,
    ConcurrencyUnavailable,
    UnsupportedCapability,
    PayloadTooLarge,
    LimitExhausted,
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

/// <summary>Earliest completed validation or execution phase that produced a patch outcome.</summary>
public enum CalendarEntityPatchPhase
{
    SchemaLexicalDiscriminator,
    SelectionDiscoveryCapability,
    OriginScopeAuthorization,
    TargetRevision,
    CompleteResourceSemantics,
    AdmissionAndPayload,
    Execution,
    PostWriteVerificationOrReconciliation
}

/// <summary>Typed execution-budget dimension exhausted by a patch.</summary>
public enum CalendarEntityPatchLimitDimension
{
    ElapsedTime
}

/// <summary>Truthful semantic-patch result.</summary>
public sealed record CalendarEntityPatchResult(
    CalendarEntityPatchCode Code,
    CalendarMutationState MutationState,
    CalendarResourceSnapshot? Snapshot = null,
    int? RetryAfterMilliseconds = null,
    bool Retryable = false,
    CalendarEntityPatchPhase Phase = CalendarEntityPatchPhase.Execution,
    CalendarEntityPatchLimitDimension? LimitDimension = null)
{
    public static CalendarEntityPatchResult Success(CalendarResourceSnapshot snapshot) =>
        new(
            CalendarEntityPatchCode.Success,
            CalendarMutationState.Committed,
            snapshot,
            Phase: CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation);
}

/// <summary>Read-only semantic patch validation outcome and bound intended-result digest.</summary>
public sealed record CalendarEntityPatchReviewResult(
    CalendarEntityPatchResult? Outcome,
    ReadOnlyMemory<byte> IntentDigest = default);

/// <summary>One exact conditional PUT of a losslessly edited Calendar Object Resource.</summary>
public sealed record CalendarResourceUpdateRequest(
    string ResourceHref,
    string EntityTag,
    ReadOnlyMemory<byte> AuthoritativeUtf8);

/// <summary>Observable outcome of the one permitted conditional PUT dispatch.</summary>
public enum CalendarResourceUpdateDispatchCode
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

/// <summary>Low-level result that preserves whether PUT may have reached CalDAV.</summary>
public sealed record CalendarResourceUpdateDispatchResult(
    CalendarResourceUpdateDispatchCode Code,
    int? RetryAfterMilliseconds = null);
