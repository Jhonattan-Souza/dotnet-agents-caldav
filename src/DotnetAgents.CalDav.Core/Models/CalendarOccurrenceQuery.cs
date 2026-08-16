namespace DotnetAgents.CalDav.Core.Models;

/// <summary>A bounded query for derived Event and To-do Occurrences.</summary>
public sealed record CalendarOccurrenceQuery(
    CalendarEntityScope Scope,
    DateTimeOffset From,
    DateTimeOffset To,
    string? EvaluationTimeZone = null);

/// <summary>Preserved temporal family of a Calendar value.</summary>
public enum CalendarTemporalKind
{
    Date,
    FloatingDateTime,
    UtcDateTime,
    ZonedDateTime
}

/// <summary>A date or date-time that retains its original temporal family.</summary>
public sealed record CalendarTemporalValue(
    CalendarTemporalKind Kind,
    string Value,
    string? TimeZoneId = null)
{
    /// <summary>Returns the canonical ordinal key used for recurrence identity ordering and continuation.</summary>
    public string GetCanonicalSortKey() => $"{Kind:D}|{TimeZoneId}|{Value}";
}

/// <summary>Original and effective timing for one derived Occurrence.</summary>
public sealed record CalendarOccurrenceTiming(
    CalendarTemporalValue SourceStart,
    CalendarTemporalValue EffectiveStart,
    CalendarTemporalValue? SourceEnd = null,
    CalendarTemporalValue? EffectiveEnd = null,
    string? SourceDuration = null,
    string? EffectiveDuration = null,
    CalendarTemporalValue? EvaluatedStartUtc = null,
    CalendarTemporalValue? EvaluatedEndUtc = null,
    string? EvaluationTimeZone = null);

/// <summary>One immutable derived Occurrence over an authoritative resource snapshot.</summary>
public sealed record CalendarOccurrenceSnapshot(
    CalendarResourceSnapshot Snapshot,
    CalendarTemporalValue RecurrenceIdentity,
    CalendarOccurrenceTiming Timing);

/// <summary>Truthful numeric observation when Occurrence evaluation exhausts a frozen budget.</summary>
public sealed record CalendarOccurrenceQueryExecutionLimits(
    int? ResourcesInspected = null,
    int? OccurrenceCount = null,
    int? ByteCount = null);

/// <summary>Closed service outcomes for bounded Occurrence queries.</summary>
public enum CalendarOccurrenceQueryCode
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

/// <summary>Typed bounded Occurrence query outcome.</summary>
public sealed record CalendarOccurrenceQueryResult(
    CalendarOccurrenceQueryCode Code,
    IReadOnlyList<CalendarOccurrenceSnapshot> Items,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics,
    IReadOnlyList<CalendarDescriptor> AuthorizedCandidates,
    CalendarOccurrenceQueryExecutionLimits? Limits = null)
{
    public static CalendarOccurrenceQueryResult Success(
        IReadOnlyList<CalendarOccurrenceSnapshot> items,
        IReadOnlyList<CalendarResourceDiagnostic>? diagnostics = null) =>
        new(CalendarOccurrenceQueryCode.Success, items, diagnostics ?? [], []);

    public static CalendarOccurrenceQueryResult Failure(
        CalendarOccurrenceQueryCode code,
        IReadOnlyList<CalendarDescriptor>? authorizedCandidates = null,
        CalendarOccurrenceQueryExecutionLimits? limits = null) =>
        new(code, [], [], authorizedCandidates ?? [], limits);
}
