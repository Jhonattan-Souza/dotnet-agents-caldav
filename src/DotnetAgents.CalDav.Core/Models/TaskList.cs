namespace DotnetAgents.CalDav.Core.Models;

/// <summary>
/// Represents a CalDAV calendar collection that supports VTODO components.
/// </summary>
public sealed record TaskList
{
    /// <summary>The absolute href path of this calendar collection (e.g. <c>/calendars/user/tasks/</c>).</summary>
    public string Href { get; init; } = string.Empty;

    /// <summary>Human-readable display name (calendar displayName property).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Description of the calendar.</summary>
    public string? Description { get; init; }

    /// <summary>Color assigned to the calendar (hex format, e.g. <c>#FF0000</c>).</summary>
    public string? Color { get; init; }

    /// <summary>Whether this task list is the configured default list.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Calendar component types supported (e.g. <c>VTODO</c>, <c>VEVENT</c>).</summary>
    public IReadOnlyList<string> SupportedComponents { get; init; } = [];
}