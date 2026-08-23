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
