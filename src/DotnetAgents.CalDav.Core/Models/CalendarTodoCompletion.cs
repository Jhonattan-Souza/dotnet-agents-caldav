namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Effective read-side classification of a To-do's completion lifecycle.</summary>
public enum CalendarTodoCompletionState
{
    Open,
    Completed,
    Cancelled,
    Indeterminate
}

/// <summary>Normalized To-do completion evidence retained for compact semantic reads.</summary>
public sealed record CalendarTodoCompletionClassification(
    CalendarTodoCompletionState State,
    string? Status,
    CalendarTemporalValue? CompletedAt,
    int? PercentComplete,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics)
{
    public bool HasTerminalEvidence => State is CalendarTodoCompletionState.Completed or CalendarTodoCompletionState.Cancelled;
}
