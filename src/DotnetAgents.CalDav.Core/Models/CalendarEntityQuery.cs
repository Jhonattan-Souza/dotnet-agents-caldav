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
    DateTimeOffset? To = null,
    string? EvaluationTimeZone = null,
    CalendarTextFilter? TextFilter = null);

/// <summary>
/// Optional content filter for a semantic query. Every whitespace-separated term of <see cref="Text"/> must occur,
/// case-insensitively, in SUMMARY, DESCRIPTION, LOCATION, or one CATEGORIES value of the same Calendar Entity
/// component, and that component must carry every requested category.
/// </summary>
public sealed record CalendarTextFilter(
    string? Text = null,
    IReadOnlyList<string>? Categories = null);

/// <summary>Explicit context used to evaluate floating and date-only Temporal Values.</summary>
public sealed record TemporalEvaluationContext(
    string TimeZone,
    TemporalEvaluationContextSource Source);

/// <summary>Observable provenance of a Temporal Evaluation Context.</summary>
public enum TemporalEvaluationContextSource
{
    Caller,
    Configuration
}
