namespace DotnetAgents.CalDav.Core.Models;

/// <summary>One revision-bound mutation of an original Recurrence Identity.</summary>
public sealed record CalendarOccurrenceMutationRequest(
    CalendarResourceRevisionReference Snapshot,
    CalendarTemporalValue RecurrenceIdentity);

/// <summary>One revision-bound To-do Completion, optionally targeting one original Recurrence Identity.</summary>
public sealed record CalendarTodoCompletionRequest(
    CalendarResourceRevisionReference Snapshot,
    CalendarTemporalValue? RecurrenceIdentity = null);
