namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Effective read-side classification of a To-do's completion lifecycle.</summary>
public enum CalendarTodoCompletionState
{
    Open,
    Completed,
    Cancelled,
    Indeterminate
}
