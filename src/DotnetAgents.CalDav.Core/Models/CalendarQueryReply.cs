using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Closed Calendar Entity query request.</summary>
public abstract record CalendarEntityQueryRequest
{
    private CalendarEntityQueryRequest()
    {
    }

    /// <summary>Completes one semantic query and starts immutable pagination.</summary>
    public sealed record Start(CalendarEntityQuery Query, int PageSize = 50) : CalendarEntityQueryRequest;

    /// <summary>Reads only an authenticated position in an immutable Query Result Snapshot.</summary>
    public sealed record Continue(string Cursor, int? PageSize = null) : CalendarEntityQueryRequest;
}

/// <summary>Closed Occurrence query request.</summary>
public abstract record CalendarOccurrenceQueryRequest
{
    private CalendarOccurrenceQueryRequest()
    {
    }

    /// <summary>Completes recurrence evaluation once and starts immutable pagination.</summary>
    public sealed record Start(CalendarOccurrenceQuery Query, int PageSize = 50) : CalendarOccurrenceQueryRequest;

    /// <summary>Reads only an authenticated position in an immutable Query Result Snapshot.</summary>
    public sealed record Continue(string Cursor, int? PageSize = null) : CalendarOccurrenceQueryRequest;
}

/// <summary>Closed query result containing either one complete page or one typed failure.</summary>
public abstract record QueryReply<T>
{
    private QueryReply()
    {
    }

    public sealed record Page(QueryPage<T> Value) : QueryReply<T>;

    public sealed record Failure(QueryFailure Error) : QueryReply<T>;
}

/// <summary>One deterministic page from a completed query result.</summary>
public sealed record QueryPage<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<QueryDiagnostic> Diagnostics,
    string? NextCursor,
    JsonElement StructuredContent,
    string HumanText,
    int MeasuredCallToolResultBytes,
    string PaginationMode = "query_result_snapshot",
    TemporalEvaluationContext? TemporalEvaluationContext = null);

/// <summary>One final projected Calendar Entity result retained without authoritative content.</summary>
public sealed record CalendarEntityQueryItem(JsonElement Value);

/// <summary>One final projected Occurrence retained without authoritative content.</summary>
public sealed record CalendarOccurrenceQueryItem(JsonElement Value);

/// <summary>Bounded content-safe diagnostic frozen across all pages.</summary>
public sealed record QueryDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] string Severity);

/// <summary>Closed expected query failure.</summary>
public sealed record QueryFailure(
    QueryFailureCode Code,
    QueryFailureCategory Category,
    string Message,
    bool Retryable,
    QueryFailurePhase Phase,
    QueryExecutionLimits? Limits = null,
    IReadOnlyList<QueryAuthorizedCandidate>? AuthorizedCandidates = null,
    int? RetryAfterMs = null);

/// <summary>Closed expected query failure vocabulary.</summary>
public enum QueryFailureCode
{
    InvalidInput,
    CursorExpired,
    LimitExhausted,
    Busy,
    PayloadTooLarge,
    UpstreamProtocolError,
    UnsupportedCapability,
    ConcurrencyUnavailable,
    TemporalUnresolved,
    RecurrenceUnevaluable,
    UpstreamUnavailable,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    NotFound,
    Ambiguous,
    OutsideScope
}

/// <summary>Closed query failure category vocabulary.</summary>
public enum QueryFailureCategory
{
    Input,
    State,
    LimitsAndAdmission,
    Upstream,
    CapabilityAndProjection,
    Selection
}

/// <summary>Closed query failure phase vocabulary.</summary>
public enum QueryFailurePhase
{
    SchemaLexicalDiscriminator,
    Pagination,
    Execution,
    AdmissionAndPayload,
    SelectionDiscoveryCapability,
    TargetRevision,
    CompleteResourceSemantics,
    OriginScopeAuthorization
}

/// <summary>Truthful observations from a frozen query budget.</summary>
public sealed record QueryExecutionLimits(
    int? ResourcesInspected = null,
    int? CalendarCount = null,
    int? OccurrenceCount = null,
    int? ByteCount = null,
    int? ItemCount = null,
    int? SnapshotCount = null,
    QueryLimitDimension? Dimension = null,
    long? Observed = null,
    long? Limit = null);

/// <summary>Closed query execution-budget dimension vocabulary.</summary>
public enum QueryLimitDimension
{
    ResourceCount,
    AttemptCount,
    ByteCount,
    ElapsedTime
}

/// <summary>Content-safe Calendar candidate for an ambiguous or missing selection.</summary>
public sealed record QueryAuthorizedCandidate(
    string CalendarHref,
    string? DisplayName,
    EntityKindSupport EventSupport,
    EntityKindSupport TodoSupport,
    IReadOnlyList<CapabilityEvidence> EventEvidence,
    IReadOnlyList<CapabilityEvidence> TodoEvidence);
