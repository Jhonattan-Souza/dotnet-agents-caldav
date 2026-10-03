using System.Globalization;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed class CalendarRecurrenceUnevaluableException : Exception
{
    public CalendarRecurrenceUnevaluableException()
    {
    }

    public CalendarRecurrenceUnevaluableException(Exception innerException)
        : base("The recurrence set could not be evaluated safely.", innerException)
    {
    }

    public CalendarRecurrenceUnevaluableException(CalendarEntityViolation violation, Exception? innerException = null)
        : base(violation.Message, innerException) => Violation = violation;

    /// <summary>Gets the typed reason, when the evaluation failure is attributable to authored input.</summary>
    public CalendarEntityViolation? Violation { get; }
}

internal sealed record CalendarCreateRecurrenceAnalysis(bool IsUnbounded, DateTime LastLocalStart);

internal static class CalendarCreateRecurrenceAnalyzer
{
    public const int MaximumProfileOccurrences = 10_000;

    public static CalendarCreateRecurrenceAnalysis Analyze(
        string rule,
        CalendarTemporalValue masterStart)
    {
        try
        {
            if (TryReadCount(rule, out var count) && count < 1)
                throw new CalendarRecurrenceUnevaluableException(CalendarEntityViolations.RecurrenceCountInvalid);
            var pattern = ParseRule(rule);
            if (pattern.Count is > MaximumProfileOccurrences)
                throw new CalendarRecurrenceUnevaluableException(CalendarEntityViolations.RecurrenceOccurrenceLimit);
            var nominalStart = CreateStart(masterStart);
            var unbounded = pattern.Count is null && pattern.Until is null;
            var maximumStarts = unbounded ? 1 : MaximumProfileOccurrences + 1;
            var starts = new RecurrencePatternEvaluator(pattern)
                .Evaluate(
                    nominalStart,
                    nominalStart,
                    new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumProfileOccurrences })
                .Select(period => period.StartTime.Value)
                .Take(maximumStarts)
                .ToArray();
            EnsureProfileStarts(starts, nominalStart.Value);
            return new CalendarCreateRecurrenceAnalysis(unbounded, starts[^1]);
        }
        catch (CalendarRecurrenceUnevaluableException)
        {
            throw;
        }
        catch (EvaluationException exception)
        {
            throw new CalendarRecurrenceUnevaluableException(
                CalendarEntityViolations.RecurrenceEvaluationFailed,
                exception);
        }
    }

    private static RecurrenceRule ParseRule(string rule)
    {
        try
        {
            return new RecurrenceRule(rule);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw CalendarEntityViolations.RecurrenceRuleInvalid(exception);
        }
    }

    private static void EnsureProfileStarts(DateTime[] starts, DateTime nominalStart)
    {
        var violation = starts switch
        {
            [] => CalendarEntityViolations.RecurrenceNoOccurrences,
            _ when starts[0] != nominalStart => CalendarEntityViolations.RecurrenceStartMismatch,
            { Length: > MaximumProfileOccurrences } => CalendarEntityViolations.RecurrenceOccurrenceLimit,
            _ => null
        };
        if (violation is not null)
            throw new CalendarRecurrenceUnevaluableException(violation);
    }

    private static bool TryReadCount(string rule, out int count)
    {
        const string prefix = "COUNT=";
        foreach (var part in rule.Split(';'))
        {
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(
                    part.AsSpan(prefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out count);
            }
        }
        count = default;
        return false;
    }

    private static CalDateTime CreateStart(CalendarTemporalValue value)
    {
        var raw = value.Kind == CalendarTemporalKind.UtcDateTime ? value.Value[..^1] : value.Value;
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var parsed = DateTime.ParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return value.Kind switch
        {
            CalendarTemporalKind.Date => new CalDateTime(parsed.Year, parsed.Month, parsed.Day),
            CalendarTemporalKind.UtcDateTime => new CalDateTime(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)),
            CalendarTemporalKind.ZonedDateTime => new CalDateTime(parsed, value.TimeZoneId),
            _ => new CalDateTime(parsed)
        };
    }
}
