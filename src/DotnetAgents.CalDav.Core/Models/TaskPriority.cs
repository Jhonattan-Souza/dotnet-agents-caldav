namespace DotnetAgents.CalDav.Core.Models;

/// <summary>
/// Maps to RFC 5545 PRIORITY integer values (0–9).
/// <para>
/// Per RFC 5545: 0 = undefined/no priority,
/// 1–4 = high priority,
/// 5 = medium priority,
/// 6–9 = low priority.
/// </para>
/// </summary>
public enum TaskPriority
{
    /// <summary>No priority specified (PRIORITY:0).</summary>
    None = 0,

    /// <summary>High priority (maps to RFC 5545 PRIORITY 1–4, stored as 1).</summary>
    High = 1,

    /// <summary>Medium priority (PRIORITY:5).</summary>
    Medium = 5,

    /// <summary>Low priority (maps to RFC 5545 PRIORITY 6–9, stored as 9).</summary>
    Low = 9
}