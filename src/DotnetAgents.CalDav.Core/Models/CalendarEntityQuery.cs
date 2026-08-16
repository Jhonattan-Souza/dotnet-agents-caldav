namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Explicit Calendar Scope mode for a semantic query.</summary>
public enum CalendarEntityScopeMode
{
    Default,
    Selected,
    All
}

/// <summary>Exact Calendar selector by display name or canonical href.</summary>
public sealed record CalendarReference(string? Name = null, string? Href = null);

/// <summary>Explicit Calendar Scope for a persisted Calendar Entity query.</summary>
public sealed record CalendarEntityScope(CalendarEntityScopeMode Mode, CalendarReference? Calendar = null)
{
    public static CalendarEntityScope Default { get; } = new(CalendarEntityScopeMode.Default);

    public static CalendarEntityScope All { get; } = new(CalendarEntityScopeMode.All);

    public static CalendarEntityScope Selected(CalendarReference calendar) =>
        new(CalendarEntityScopeMode.Selected, calendar);
}

/// <summary>Bounded semantic query for persisted Calendar Object Resources.</summary>
public sealed record CalendarEntityQuery(
    CalendarEntityScope Scope,
    IReadOnlyList<CalendarEntityKind> EntityKinds,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>Typed Calendar Entity query outcome.</summary>
public sealed record CalendarEntityQueryResult(
    CalendarEntityQueryCode Code,
    IReadOnlyList<CalendarResourceSnapshot> Items,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics,
    IReadOnlyList<CalendarDescriptor> AuthorizedCandidates,
    CalendarEntityQueryExecutionLimits? Limits = null)
{
    public static CalendarEntityQueryResult Success(
        IReadOnlyList<CalendarResourceSnapshot> items,
        IReadOnlyList<CalendarResourceDiagnostic>? diagnostics = null) =>
        new(CalendarEntityQueryCode.Success, items, diagnostics ?? [], []);

    public static CalendarEntityQueryResult Failure(
        CalendarEntityQueryCode code,
        IReadOnlyList<CalendarDescriptor>? authorizedCandidates = null,
        CalendarEntityQueryExecutionLimits? limits = null) =>
        new(code, [], [], authorizedCandidates ?? [], limits);
}

/// <summary>Truthful numeric observation when a Calendar Entity query exhausts a frozen budget.</summary>
public sealed record CalendarEntityQueryExecutionLimits(
    int? ResourcesInspected = null,
    int? OccurrenceCount = null,
    int? ByteCount = null);

/// <summary>Closed service outcomes for Calendar Entity queries.</summary>
public enum CalendarEntityQueryCode
{
    Success,
    InvalidInput,
    UnsafeScope,
    NotFound,
    Ambiguous,
    OutsideScope,
    EntityKindMismatch,
    UnsupportedCapability,
    ConcurrencyUnavailable,
    LimitExhausted,
    PayloadTooLarge,
    UpstreamProtocolError,
    TemporalUnresolved,
    RecurrenceUnevaluable
}
