using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Names the bounded-window rule a query window breaks, at the caller's top-level window members.</summary>
internal static class CalendarQueryWindows
{
    internal static readonly TimeSpan MaximumSpan = TimeSpan.FromDays(366);

    internal static CalendarRequestViolation? Violation(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string fromMember = "from",
        string toMember = "to")
    {
        if (from is null || to is null)
        {
            return from is null && to is null
                ? null
                : new("/" + (from is null ? fromMember : toMember), "window_endpoint_missing",
                    $"Send both {fromMember} and {toMember}; a window needs both endpoints.");
        }
        if (to <= from)
            return new("/" + toMember, "window_not_increasing", $"{toMember} must be later than {fromMember}.");
        return to - from > MaximumSpan
            ? new("/" + toMember, "window_too_long", $"The {fromMember} to {toMember} window can span at most 366 days.")
            : null;
    }

    /// <summary>Returns the window failure when one explains the refusal, or the caller's generic failure.</summary>
    internal static QueryFailure InvalidInput(QueryFailure fallback, params CalendarRequestViolation?[] candidates) =>
        candidates.FirstOrDefault(candidate => candidate is not null) is { } violation
            ? fallback with { Message = violation.Message, Violations = [violation] }
            : fallback;
}
