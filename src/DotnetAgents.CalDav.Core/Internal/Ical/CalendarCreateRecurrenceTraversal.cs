using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarCreateRecurrenceTraversal
{
    public static IEnumerable<CalendarTemporalValue?> EnumerateTemporalValues(
        IReadOnlyList<CalendarRecurrenceDateCreate>? recurrenceDates,
        IReadOnlyList<CalendarTemporalValue>? exceptionDates,
        IEnumerable<CalendarTemporalValue> overrideIdentities)
    {
        foreach (var recurrenceDate in recurrenceDates ?? [])
        {
            yield return recurrenceDate.Value;
            yield return recurrenceDate.Period?.Start;
            yield return recurrenceDate.Period?.End;
        }
        foreach (var value in exceptionDates ?? [])
            yield return value;
        foreach (var value in overrideIdentities)
            yield return value;
    }
}
