using DotnetAgents.CalDav.Core.Internal.Ical;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Validates public RFC 5545 duration inputs without exposing parser internals.</summary>
public static class CalendarDurationValidator
{
    public static bool IsStrictlyPositive(string value) =>
        CalendarDurationArithmetic.TryParse(value, out var parsed) && parsed.IsStrictlyPositive;
}
