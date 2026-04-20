namespace DotnetAgents.CalDav.Core.Models;

/// <summary>
/// Represents a VTODO (task) in a CalDAV calendar collection.
/// Immutable domain model — use <c>with</c> expressions to create modified copies.
/// </summary>
public sealed record TaskItem
{
    /// <summary>Unique identifier (UID in iCalendar). Required for updates; generated on creation if omitted.</summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>Brief summary / title of the task.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Detailed description (may be null).</summary>
    public string? Description { get; init; }

    /// <summary>When the task is due (DUE property).</summary>
    public DateTimeOffset? Due { get; init; }

    /// <summary>When the task starts (DTSTART property).</summary>
    public DateTimeOffset? Start { get; init; }

    /// <summary>When the task was completed (COMPLETED property).</summary>
    public DateTimeOffset? Completed { get; init; }

    /// <summary>Task priority, mapping to RFC 5545 PRIORITY 0–9.</summary>
    public TaskPriority Priority { get; init; } = TaskPriority.None;

    /// <summary>Task status.</summary>
    public TaskStatus Status { get; init; } = TaskStatus.NeedsAction;

    /// <summary>Categories / tags (CATEGORIES property).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>RRULE for recurring tasks, expressed as an iCalendar recurrence string (e.g. "FREQ=DAILY;COUNT=5").</summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>
    /// Opaque ETag returned by the CalDAV server.
    /// Used for optimistic concurrency on updates (<c>If-Match</c> header).
    /// Populated by read operations; sent back on updates.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>The absolute href path of this task resource on the CalDAV server (e.g. <c>/calendars/user/tasks/abc.ics</c>).</summary>
    public string Href { get; init; } = string.Empty;
}