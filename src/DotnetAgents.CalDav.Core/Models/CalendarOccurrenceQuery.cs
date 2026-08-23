namespace DotnetAgents.CalDav.Core.Models;

/// <summary>A bounded query for derived Event and To-do Occurrences.</summary>
public sealed record CalendarOccurrenceQuery(
    CalendarEntityScope Scope,
    DateTimeOffset From,
    DateTimeOffset To,
    string? EvaluationTimeZone = null,
    bool IncludeCancelledOccurrences = false);

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
