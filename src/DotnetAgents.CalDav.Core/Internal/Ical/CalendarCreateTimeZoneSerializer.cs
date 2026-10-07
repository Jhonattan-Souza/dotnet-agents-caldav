using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using NodaTime;
using NodaTime.TimeZones;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarCreateTimeZoneSerializer
{
    // A Calendar collection time zone has no temporal values to bound it. Midday avoids a local
    // transition gap; the definition starts with the Unix era.
    private static readonly DateTime CollectionWindowStart = new(1970, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);

    public static void AppendForEvent(StringBuilder destination, CalendarEventCreateFields fields) =>
        Append(destination, CollectEvent(fields));

    public static void AppendForTodo(StringBuilder destination, CalendarTodoCreateFields fields) =>
        Append(destination, CollectTodo(fields));

    /// <summary>Serializes one tzdb zone as the VCALENDAR required by CALDAV:calendar-timezone.</summary>
    public static string SerializeCollectionTimeZone(string timeZoneId) => new StringBuilder()
        .Append("BEGIN:VCALENDAR\r\n")
        .Append("VERSION:2.0\r\n")
        .Append("PRODID:-//dotnet-agents-caldav//EN\r\n")
        .Append(SerializeZone(timeZoneId, CollectionWindowStart))
        .Append("END:VCALENDAR\r\n")
        .ToString();

    // Each definition starts at its zone's earliest value and ends in the zone's never-ending rule, so later
    // values, recurrences and durations need no further coverage.
    private static void Append(StringBuilder destination, IEnumerable<CalendarTemporalValue?> values)
    {
        var zoneValues = values
            .Where(value => value?.Kind == CalendarTemporalKind.ZonedDateTime)
            .Select(value => value!)
            .GroupBy(value => value.TimeZoneId!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var zone in zoneValues)
            destination.Append(SerializeZone(zone.Key, zone.Min(value => ParseLocal(value.Value))));
    }

    /// <summary>
    /// Serializes one IANA VTIMEZONE covering already-stored local values; resolved leniently
    /// because they may be derived values rather than strictly validated caller input.
    /// </summary>
    public static string SerializeForLocalValues(string timeZoneId, IReadOnlyCollection<DateTime> localValues)
    {
        var zone = DateTimeZoneProviders.Tzdb[timeZoneId];
        var earliest = LocalDateTime.FromDateTime(localValues.Min());
        return SerializeZone(timeZoneId, zone, earliest, zone.AtLeniently(earliest).ToInstant());
    }

    private static string SerializeZone(string timeZoneId, DateTime earliest)
    {
        var zone = DateTimeZoneProviders.Tzdb[timeZoneId];
        var earliestLocal = LocalDateTime.FromDateTime(earliest);
        return SerializeZone(timeZoneId, zone, earliestLocal, zone.AtStrictly(earliestLocal).ToInstant());
    }

    private static string SerializeZone(
        string timeZoneId,
        DateTimeZone zone,
        LocalDateTime earliestLocal,
        Instant startInstant)
    {
        var tail = CalendarTimeZoneTail.Find(zone);
        var observances = History(zone, tail, earliestLocal, startInstant);

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
        foreach (var observance in tail.Observances.OrderBy(observance => observance.LocalStart))
            AppendTailObservance(content, observance);
        return content.Append("END:VTIMEZONE\r\n").ToString();
    }

    /// <summary>Returns the exact onsets from the earliest value until the zone's never-ending rule begins.</summary>
    private static List<ZoneObservance> History(
        DateTimeZone zone,
        ZoneTail tail,
        LocalDateTime earliestLocal,
        Instant startInstant)
    {
        if (tail.Start is not { } tailStart)
            return [ZoneObservance.Baseline(earliestLocal, tail.Interval)];
        if (startInstant >= tailStart)
            return [];
        var intervals = zone.GetZoneIntervals(startInstant, tailStart).ToArray();
        var observances = new List<ZoneObservance> { ZoneObservance.Baseline(earliestLocal, intervals[0]) };
        for (var index = 1; index < intervals.Length; index++)
            observances.Add(ZoneObservance.Transition(intervals[index - 1], intervals[index]));
        return observances;
    }

    private static void AppendTailObservance(StringBuilder content, ZoneTailObservance observance)
    {
        content.Append("BEGIN:").Append(observance.ComponentName).Append("\r\n")
            .Append("DTSTART:").Append(FormatLocal(observance.LocalStart)).Append("\r\n");
        if (observance.Rule is not null)
            content.Append("RRULE:").Append(observance.Rule).Append("\r\n");
        content.Append("TZOFFSETFROM:").Append(FormatOffset(observance.OffsetFrom)).Append("\r\n")
            .Append("TZOFFSETTO:").Append(FormatOffset(observance.OffsetTo)).Append("\r\n")
            .Append("TZNAME:").Append(EscapeText(observance.Name)).Append("\r\n")
            .Append("END:").Append(observance.ComponentName).Append("\r\n");
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

    private static IEnumerable<CalendarTemporalValue?> CollectEvent(CalendarEventCreateFields fields)
    {
        yield return fields.Start;
        yield return fields.End;
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

    private static IEnumerable<CalendarTemporalValue?> CollectTodo(CalendarTodoCreateFields fields)
    {
        yield return fields.Start;
        yield return fields.Due;
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
