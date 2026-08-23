using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal enum CalendarTodoEvaluationKind
{
    Entity,
    Occurrence,
    Unresolved
}

internal sealed record EvaluatedTodoCompletion(
    CalendarTodoCompletionState State,
    string? Status,
    CalendarTemporalValue? CompletedAt,
    int? PercentComplete,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics)
{
    internal bool HasTerminalEvidence => State is CalendarTodoCompletionState.Completed
        or CalendarTodoCompletionState.Cancelled;
}

internal sealed record EvaluatedOccurrenceTiming(
    CalendarTemporalValue SourceStart,
    CalendarTemporalValue EffectiveStart,
    CalendarTemporalValue? SourceEnd = null,
    CalendarTemporalValue? EffectiveEnd = null,
    string? SourceDuration = null,
    string? EffectiveDuration = null,
    CalendarTemporalValue? EvaluatedStartUtc = null,
    CalendarTemporalValue? EvaluatedEndUtc = null,
    string? EvaluationTimeZone = null);

internal sealed record EvaluatedOccurrence(
    CalendarResourceSnapshot Snapshot,
    CalendarTemporalValue RecurrenceIdentity,
    EvaluatedOccurrenceTiming Timing);
