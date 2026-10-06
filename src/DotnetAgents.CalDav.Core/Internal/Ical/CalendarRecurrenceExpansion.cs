using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Expands RRULEs through Ical.Net while applying a YEARLY BYMONTH rule's UNTIL itself. Since 5.1.3
/// (ical-org/ical.net PR #884), such a rule stops a year early when its BYDAY lands before DTSTART's day of month,
/// silently losing the last occurrence. Evaluation therefore runs with UNTIL one year later and reapplies the
/// inclusive bound with Ical.Net's own comparison; every other rule shape, and its unmatched-increment budget,
/// is untouched. Remove once a released Ical.Net fixes it; the v6 main branch already does.
/// </summary>
internal static class CalendarRecurrenceExpansion
{
    public static IEnumerable<Period> EvaluateRule(
        RecurrenceRule rule,
        CalDateTime referenceDate,
        CalDateTime? periodStart,
        EvaluationOptions options)
    {
        if (rule.Until is not { } until || Widen(rule, until) is not { } widened)
            return new RecurrencePatternEvaluator(rule).Evaluate(referenceDate, periodStart, options);
        return new RecurrencePatternEvaluator(widened)
            .Evaluate(referenceDate, periodStart, options)
            .TakeWhile(period => period.StartTime <= until);
    }

    /// <summary>
    /// Returns a component's occurrences with its single bounded RRULE widened on a copy. RDATE starts past UNTIL
    /// stay, because UNTIL bounds only the rule.
    /// </summary>
    public static IEnumerable<Occurrence> GetOccurrences<T>(
        T component,
        CalDateTime? periodStart,
        EvaluationOptions options)
        where T : CalendarComponent, IRecurrable
    {
        if (component.Properties.Count(property => property.Name == "RRULE") != 1
            || component.RecurrenceRule is not { Until: { } until } rule
            || Widen(rule, until) is not { } widened
            || component.Copy<T>() is not { } copy)
        {
            return component.GetOccurrences(periodStart, options);
        }
        copy.RecurrenceRule = widened;
        var recurrenceDates = component.RecurrenceDates.GetAllPeriods()
            .Select(period => period.StartTime)
            .ToHashSet();
        return copy.GetOccurrences(periodStart, options).Where(occurrence =>
            occurrence.Period.StartTime <= until || recurrenceDates.Contains(occurrence.Period.StartTime));
    }

    private static RecurrenceRule? Widen(RecurrenceRule rule, CalDateTime until)
    {
        if (rule.Frequency != FrequencyType.Yearly || rule.ByMonth.Count == 0)
            return null;
        var widened = new RecurrenceRule();
        widened.CopyFrom(rule);
        try
        {
            widened.Until = until.AddYears(rule.Interval);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        return widened;
    }
}
