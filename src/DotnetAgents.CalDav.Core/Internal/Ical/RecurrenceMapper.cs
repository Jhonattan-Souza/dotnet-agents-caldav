using Ical.Net.DataTypes;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Maps between RFC 5545 RRULE strings and <see cref="RecurrencePattern"/> objects.
/// Provides bidirectional conversion for recurring task support.
/// </summary>
internal static class RecurrenceMapper
{
    /// <summary>Parses an RRULE string (e.g. "FREQ=DAILY;COUNT=5") into a <see cref="RecurrencePattern"/>.</summary>
    /// <returns>A parsed <see cref="RecurrencePattern"/>, or <c>null</c> if the string cannot be parsed.</returns>
    public static RecurrencePattern? FromString(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            return null;

        try
        {
            return new RecurrencePattern(rrule);
        }
        catch (Exception)
        {
            // Invalid RRULE values are treated as absent recurrence data.
            return null;
        }
    }

    /// <summary>Converts a <see cref="RecurrencePattern"/> back to an RRULE string.</summary>
    public static string? ToString(RecurrencePattern? pattern)
    {
        return pattern?.ToString();
    }
}
