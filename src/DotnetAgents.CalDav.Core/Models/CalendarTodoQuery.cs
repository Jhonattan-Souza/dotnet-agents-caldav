namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Bounded semantic query for compact To-do results.</summary>
public sealed record CalendarTodoQuery(
    CalendarEntityScope Scope,
    IReadOnlyList<CalendarTodoCompletionState>? CompletionStates = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? EvaluationTimeZone = null,
    DateTimeOffset? DueFrom = null,
    DateTimeOffset? DueTo = null);

/// <summary>Discriminates persisted Entity, derived Occurrence, and unresolved rows.</summary>
public enum CalendarTodoQueryResultKind
{
    Entity,
    Occurrence,
    Unresolved
}

/// <summary>Compact Core item before MCP projection selection.</summary>
public sealed record CalendarTodoQueryItem(
    CalendarTodoQueryResultKind ResultKind,
    CalendarResourceSnapshot Snapshot,
    CalendarOccurrenceSnapshot? Occurrence,
    CalendarTodoCompletionClassification Completion,
    CalendarTemporalValue? Due,
    DateTimeOffset? EvaluatedDueUtc,
    CalendarTemporalValue? Start,
    DateTimeOffset? EvaluatedStartUtc,
    bool IsRecurring)
{
    public bool RequiresOccurrenceTarget => IsRecurring && ResultKind == CalendarTodoQueryResultKind.Entity;
}

/// <summary>Closed outcomes for compact To-do queries.</summary>
public enum CalendarTodoQueryCode
{
    Success,
    InvalidInput,
    UnsafeScope,
    NotFound,
    Ambiguous,
    OutsideScope,
    UnsupportedCapability,
    ConcurrencyUnavailable,
    LimitExhausted,
    PayloadTooLarge,
    UpstreamProtocolError,
    TemporalUnresolved,
    RecurrenceUnevaluable
}

/// <summary>Compact To-do query outcome with explicit excluded indeterminate count.</summary>
public sealed record CalendarTodoQueryResult(
    CalendarTodoQueryCode Code,
    IReadOnlyList<CalendarTodoQueryItem> Items,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics,
    IReadOnlyList<CalendarDescriptor> AuthorizedCandidates,
    int ExcludedIndeterminateCount = 0,
    CalendarEntityQueryExecutionLimits? Limits = null)
{
    public static CalendarTodoQueryResult Success(
        IReadOnlyList<CalendarTodoQueryItem> items,
        IReadOnlyList<CalendarResourceDiagnostic>? diagnostics = null,
        int excludedIndeterminateCount = 0) => new(
        CalendarTodoQueryCode.Success,
        items,
        diagnostics ?? [],
        [],
        excludedIndeterminateCount);

    public static CalendarTodoQueryResult Failure(
        CalendarTodoQueryCode code,
        IReadOnlyList<CalendarDescriptor>? authorizedCandidates = null,
        CalendarEntityQueryExecutionLimits? limits = null) => new(
        code,
        [],
        [],
        authorizedCandidates ?? [],
        0,
        limits);
}
