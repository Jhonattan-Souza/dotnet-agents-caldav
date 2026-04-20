namespace DotnetAgents.CalDav.Core.Models;

/// <summary>
/// Maps to RFC 5545 VTODO STATUS values.
/// </summary>
public enum TaskStatus
{
    /// <summary>Task needs action (STATUS:NEEDS-ACTION).</summary>
    NeedsAction = 0,

    /// <summary>Task is in progress (STATUS:IN-PROCESS).</summary>
    InProcess = 1,

    /// <summary>Task is completed (STATUS:COMPLETED).</summary>
    Completed = 2,

    /// <summary>Task was cancelled (STATUS:CANCELLED).</summary>
    Cancelled = 3
}