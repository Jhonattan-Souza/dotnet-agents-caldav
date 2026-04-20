namespace DotnetAgents.CalDav.Core.Models;

/// <summary>
/// Filter parameters for querying VTODO items from a calendar collection.
/// </summary>
public sealed record TaskQuery
{
    /// <summary>Filter tasks by status.</summary>
    public TaskStatus? Status { get; init; }

    /// <summary>If set, only return tasks due on or after this date.</summary>
    public DateTimeOffset? DueAfter { get; init; }

    /// <summary>If set, only return tasks due on or before this date.</summary>
    public DateTimeOffset? DueBefore { get; init; }

    /// <summary>Free-text filter on task summary / description.</summary>
    public string? TextSearch { get; init; }

    /// <summary>Filter by category / tag.</summary>
    public string? Category { get; init; }
}