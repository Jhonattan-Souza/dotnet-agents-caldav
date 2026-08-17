using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using NodaTime;
using NodaTime.TimeZones;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarCreateTimeZoneSerializer
{
    private static readonly Instant MaximumSupportedInstant = Instant.FromUtc(9999, 12, 31, 23, 59);

    public static void AppendForEvent(StringBuilder destination, CalendarEventCreateFields fields)
    {
        var recurrence = Analyze(fields.RecurrenceSet?.Rule, fields.Start);
        Append(destination, CollectEvent(fields, recurrence), fields.Start, recurrence);
    }

    public static void AppendForTodo(StringBuilder destination, CalendarTodoCreateFields fields)
    {
        var recurrence = Analyze(fields.RecurrenceSet?.Rule, fields.Start);
        Append(destination, CollectTodo(fields, recurrence), fields.Start, recurrence);
    }

    private static void Append(
        StringBuilder destination,
        IEnumerable<CalendarTemporalValue?> values,
        CalendarTemporalValue? masterStart,
        CalendarCreateRecurrenceAnalysis? recurrence)
    {
        var zoneValues = values
            .Where(value => value?.Kind == CalendarTemporalKind.ZonedDateTime)
            .Select(value => value!)
            .GroupBy(value => value.TimeZoneId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var zone in zoneValues)
            AppendZone(destination, zone, masterStart, recurrence);
    }

    private static CalendarCreateRecurrenceAnalysis? Analyze(
        string? rule,
        CalendarTemporalValue? masterStart) => rule is null || masterStart is null
            ? null
            : CalendarCreateRecurrenceAnalyzer.Analyze(rule, masterStart);

    private static void AppendZone(
        StringBuilder destination,
        IGrouping<string, CalendarTemporalValue> zone,
        CalendarTemporalValue? masterStart,
        CalendarCreateRecurrenceAnalysis? recurrence)
    {
        var earliest = zone.Min(value => ParseLocal(value.Value));
        var latest = zone.Max(value => ParseLocal(value.Value));
        var isMasterZone = masterStart?.Kind == CalendarTemporalKind.ZonedDateTime
            && string.Equals(masterStart.TimeZoneId, zone.Key, StringComparison.Ordinal);
        destination.Append(SerializeZone(zone.Key, earliest, latest, isMasterZone && recurrence?.IsUnbounded == true));
    }

    private static string SerializeZone(
        string timeZoneId,
        DateTime earliest,
        DateTime latest,
        bool unbounded)
    {
        var zone = DateTimeZoneProviders.Tzdb[timeZoneId];
        var earliestLocal = LocalDateTime.FromDateTime(earliest);
        var startInstant = zone.AtStrictly(earliestLocal).ToInstant();
        var endInstant = unbounded
            ? MaximumSupportedInstant
            : zone.AtStrictly(LocalDateTime.FromDateTime(latest)).ToInstant();
        if (endInstant <= startInstant)
            endInstant = startInstant + Duration.FromSeconds(1);

        var intervals = zone.GetZoneIntervals(startInstant, endInstant).ToArray();
        var observances = new List<ZoneObservance>
        {
            ZoneObservance.Baseline(earliestLocal, intervals[0])
        };
        for (var index = 1; index < intervals.Length; index++)
            observances.Add(ZoneObservance.Transition(intervals[index - 1], intervals[index]));

        var content = new StringBuilder()
            .Append("BEGIN:VTIMEZONE\r\nTZID:").Append(EscapeText(timeZoneId)).Append("\r\n");
        foreach (var group in observances
                     .GroupBy(observance => observance.Signature)
                     .Select(group => new { group.Key, Values = group.OrderBy(value => value.LocalStart).ToArray() })
                     .OrderBy(group => group.Values[0].LocalStart))
        {
            content.Append("BEGIN:").Append(group.Key.ComponentName).Append("\r\n")
                .Append("DTSTART:").Append(FormatLocal(group.Values[0].LocalStart)).Append("\r\n");
            foreach (var observance in group.Values.Skip(1))
                content.Append("RDATE:").Append(FormatLocal(observance.LocalStart)).Append("\r\n");
            content.Append("TZOFFSETFROM:").Append(FormatOffset(group.Key.OffsetFrom)).Append("\r\n")
                .Append("TZOFFSETTO:").Append(FormatOffset(group.Key.OffsetTo)).Append("\r\n")
                .Append("TZNAME:").Append(EscapeText(group.Key.Name)).Append("\r\n")
                .Append("END:").Append(group.Key.ComponentName).Append("\r\n");
        }
        return content.Append("END:VTIMEZONE\r\n").ToString();
    }

    private static string FormatLocal(LocalDateTime value) => value.ToString(
        "yyyyMMdd'T'HHmmss",
        CultureInfo.InvariantCulture);

    private static string FormatOffset(Offset value)
    {
        var seconds = value.Seconds;
        var sign = seconds < 0 ? '-' : '+';
        seconds = Math.Abs(seconds);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var remainder = seconds % 60;
        return remainder == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:00}{minutes:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{hours:00}{minutes:00}{remainder:00}");
    }

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal);

    private static DateTime ParseLocal(string value) => DateTime.ParseExact(
        value,
        "yyyy-MM-dd'T'HH:mm:ss",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None);

    private static IEnumerable<CalendarTemporalValue?> CollectEvent(
        CalendarEventCreateFields fields,
        CalendarCreateRecurrenceAnalysis? recurrence = null)
    {
        yield return fields.Start;
        yield return fields.End;
        yield return ResolveDurationEnd(fields.Start, fields.Duration);
        yield return ResolveLastStart(fields.Start, recurrence);
        yield return ResolveLastExplicitEnd(fields.Start, fields.End, recurrence);
        yield return ResolveLastDurationEnd(fields.Start, fields.Duration, recurrence);
        foreach (var value in CollectEventRecurrence(fields.RecurrenceSet))
            yield return value;
    }

    private static IEnumerable<CalendarTemporalValue?> CollectEventRecurrence(
        CalendarEventRecurrenceSetCreate? recurrence)
    {
        if (recurrence is null)
            yield break;
        foreach (var value in CalendarCreateRecurrenceTraversal.EnumerateTemporalValues(
                     recurrence.RecurrenceDates,
                     recurrence.ExceptionDates,
                     recurrence.Overrides?.Select(item => item.RecurrenceIdentity) ?? []))
            yield return value;
        foreach (var recurrenceOverride in recurrence.Overrides ?? [])
        {
            foreach (var value in CollectEvent(recurrenceOverride.Fields))
                yield return value;
        }
    }

    private static IEnumerable<CalendarTemporalValue?> CollectTodo(
        CalendarTodoCreateFields fields,
        CalendarCreateRecurrenceAnalysis? recurrence = null)
    {
        yield return fields.Start;
        yield return fields.Due;
        yield return ResolveDurationEnd(fields.Start, fields.Duration);
        yield return ResolveLastStart(fields.Start, recurrence);
        yield return ResolveLastExplicitEnd(fields.Start, fields.Due, recurrence);
        yield return ResolveLastDurationEnd(fields.Start, fields.Duration, recurrence);
        foreach (var value in CollectTodoRecurrence(fields.RecurrenceSet))
            yield return value;
    }

    private static IEnumerable<CalendarTemporalValue?> CollectTodoRecurrence(
        CalendarTodoRecurrenceSetCreate? recurrence)
    {
        if (recurrence is null)
            yield break;
        foreach (var value in CalendarCreateRecurrenceTraversal.EnumerateTemporalValues(
                     recurrence.RecurrenceDates,
                     recurrence.ExceptionDates,
                     recurrence.Overrides?.Select(item => item.RecurrenceIdentity) ?? []))
            yield return value;
        foreach (var recurrenceOverride in recurrence.Overrides ?? [])
        {
            foreach (var value in CollectTodo(recurrenceOverride.Fields))
                yield return value;
        }
    }

    private static CalendarTemporalValue? ResolveDurationEnd(
        CalendarTemporalValue? start,
        string? duration) => start?.Kind != CalendarTemporalKind.ZonedDateTime || duration is null
            ? null
            : CalendarDurationArithmetic.ResolveCreateEnd(start, duration);

    private static CalendarTemporalValue? ResolveLastStart(
        CalendarTemporalValue? start,
        CalendarCreateRecurrenceAnalysis? recurrence) =>
        start?.Kind != CalendarTemporalKind.ZonedDateTime || recurrence is null || recurrence.IsUnbounded
            ? null
            : start with
            {
                Value = recurrence.LastLocalStart.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture)
            };

    private static CalendarTemporalValue? ResolveLastDurationEnd(
        CalendarTemporalValue? start,
        string? duration,
        CalendarCreateRecurrenceAnalysis? recurrence)
    {
        var lastStart = ResolveLastStart(start, recurrence);
        if (lastStart is null || duration is null)
            return null;
        return CalendarDurationArithmetic.ResolveCreateEnd(lastStart, duration);
    }

    private static CalendarTemporalValue? ResolveLastExplicitEnd(
        CalendarTemporalValue? start,
        CalendarTemporalValue? end,
        CalendarCreateRecurrenceAnalysis? recurrence)
    {
        var lastStart = ResolveLastStart(start, recurrence);
        if (start is null || end is null || lastStart is null)
            return null;
        return CalendarDurationArithmetic.ShiftCreateExplicitEnd(start, end, lastStart);
    }

    private sealed record ZoneObservance(LocalDateTime LocalStart, ZoneSignature Signature)
    {
        public static ZoneObservance Baseline(LocalDateTime localStart, ZoneInterval interval) => new(
            localStart,
            new ZoneSignature(
                Component(interval),
                interval.Name,
                interval.WallOffset,
                interval.WallOffset));

        public static ZoneObservance Transition(ZoneInterval previous, ZoneInterval current) => new(
            current.Start.WithOffset(previous.WallOffset).LocalDateTime,
            new ZoneSignature(
                Component(current),
                current.Name,
                previous.WallOffset,
                current.WallOffset));

        private static string Component(ZoneInterval interval) =>
            interval.Savings == Offset.Zero ? "STANDARD" : "DAYLIGHT";
    }

    private sealed record ZoneSignature(
        string ComponentName,
        string Name,
        Offset OffsetFrom,
        Offset OffsetTo);
}
