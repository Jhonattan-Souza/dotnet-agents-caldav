using System.Globalization;
using NodaTime;
using NodaTime.TimeZones;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Finds the rule a tzdb zone follows from some onset on, as VTIMEZONE observances that never end: either one
/// fixed offset or a yearly STANDARD and DAYLIGHT pair. Every recurring rule matches each tzdb onset up to a
/// fixed horizon. A definition that ends in this rule stays correct after the values that produced it, so a
/// server that reuses it for other resources with the same TZID does not freeze the zone.
/// </summary>
internal static class CalendarTimeZoneTail
{
    private const int LastOnOrAfterDay = 25;
    private static readonly Instant Floor = Instant.FromUtc(1900, 1, 1, 0, 0);
    private static readonly Instant Horizon = Instant.FromUtc(2200, 1, 1, 0, 0);

    internal static ZoneTail Find(DateTimeZone zone)
    {
        var intervals = zone.GetZoneIntervals(Floor, Horizon).ToArray();
        if (intervals.Length > 1)
            return intervals[^1].HasEnd ? FindAlternating(intervals) : LastOffset(intervals);
        // A zone without onsets in the sampled range may still have settled on its offset before it.
        var only = intervals[0];
        return only.HasStart
            ? LastOffset([zone.GetZoneInterval(only.Start - Duration.Epsilon), only])
            : new ZoneTail(null, only, []);
    }

    // Also the fallback when no yearly rule fits before the horizon: the exact onsets then run up to it.
    private static ZoneTail LastOffset(ZoneInterval[] intervals) =>
        new(intervals[^1].Start, intervals[^1], [Observance(intervals[^2], intervals[^1], null)]);

    private static ZoneTail FindAlternating(ZoneInterval[] intervals)
    {
        var transitions = intervals.Skip(1).Select((current, index) => (Previous: intervals[index], Current: current))
            .ToArray();
        var standardOnsets = transitions.Where(pair => pair.Current.Savings == Offset.Zero).ToArray();
        var daylightOnsets = transitions.Where(pair => pair.Current.Savings != Offset.Zero).ToArray();
        if (standardOnsets.Length == 0 || daylightOnsets.Length == 0)
            return LastOffset(intervals);
        var standard = FindRun(standardOnsets);
        var daylight = FindRun(daylightOnsets);
        var start = Instant.Max(standard.Start, daylight.Start);
        var standardStart = FirstOnsetFrom(standardOnsets, standard, start);
        var daylightStart = FirstOnsetFrom(daylightOnsets, daylight, start);
        return standardStart is null || daylightStart is null
            ? LastOffset(intervals)
            : new ZoneTail(start, intervals[^1], [standardStart, daylightStart]);
    }

    private static ZoneTailObservance? FirstOnsetFrom(
        IEnumerable<(ZoneInterval Previous, ZoneInterval Current)> onsets,
        ZoneRun run,
        Instant start) => onsets
        .Where(pair => pair.Current.Start >= start)
        .Select(pair => Observance(pair.Previous, pair.Current, run.Rule))
        .FirstOrDefault();

    /// <summary>Walks back from the last onset of one kind while every onset fits one yearly rule.</summary>
    private static ZoneRun FindRun((ZoneInterval Previous, ZoneInterval Current)[] onsets)
    {
        var last = onsets[^1];
        var key = Key(last.Previous, last.Current);
        var days = DayRules.Of(LocalStart(last.Previous, last.Current).Date);
        var start = last.Current.Start;
        for (var index = onsets.Length - 2; index >= 0; index--)
        {
            var onset = onsets[index];
            var narrowed = days.Intersect(DayRules.Of(LocalStart(onset.Previous, onset.Current).Date));
            if (Key(onset.Previous, onset.Current) != key || narrowed.IsEmpty)
                break;
            days = narrowed;
            start = onset.Current.Start;
        }
        var localStart = LocalStart(last.Previous, last.Current);
        return new ZoneRun(
            start,
            $"FREQ=YEARLY;BYMONTH={localStart.Month};{days.Format(localStart.DayOfWeek)}");
    }

    private static OnsetKey Key(ZoneInterval previous, ZoneInterval current)
    {
        var local = LocalStart(previous, current);
        return new OnsetKey(local.Month, local.DayOfWeek, local.TimeOfDay, previous.WallOffset, current.WallOffset,
            current.Name);
    }

    private static ZoneTailObservance Observance(ZoneInterval previous, ZoneInterval current, string? rule) => new(
        current.Savings == Offset.Zero ? "STANDARD" : "DAYLIGHT",
        current.Name,
        previous.WallOffset,
        current.WallOffset,
        LocalStart(previous, current),
        rule);

    private static LocalDateTime LocalStart(ZoneInterval previous, ZoneInterval current) =>
        current.Start.WithOffset(previous.WallOffset).LocalDateTime;

    private sealed record OnsetKey(
        int Month,
        IsoDayOfWeek DayOfWeek,
        LocalTime Time,
        Offset From,
        Offset To,
        string Name);

    private sealed record ZoneRun(Instant Start, string Rule);

    /// <summary>The weekday positions one onset date satisfies: last in its month, or on or after day k.</summary>
    private sealed record DayRules(bool Last, IReadOnlySet<int> OnOrAfter)
    {
        internal bool IsEmpty => !Last && OnOrAfter.Count == 0;

        internal static DayRules Of(LocalDate date) => new(
            date.Day > date.Calendar.GetDaysInMonth(date.Year, date.Month) - 7,
            Enumerable.Range(Math.Max(1, date.Day - 6), Math.Min(date.Day, LastOnOrAfterDay) - Math.Max(1, date.Day - 6) + 1)
                .ToHashSet());

        internal DayRules Intersect(DayRules other) => new(
            Last && other.Last,
            OnOrAfter.Where(other.OnOrAfter.Contains).ToHashSet());

        internal string Format(IsoDayOfWeek dayOfWeek)
        {
            var weekday = dayOfWeek.ToString()[..2].ToUpperInvariant();
            if (OnOrAfter.FirstOrDefault(day => day % 7 == 1) is var nth and > 0)
                return string.Create(CultureInfo.InvariantCulture, $"BYDAY={(nth - 1) / 7 + 1}{weekday}");
            if (Last)
                return $"BYDAY=-1{weekday}";
            var first = OnOrAfter.Min();
            return $"BYDAY={weekday};BYMONTHDAY={string.Join(',', Enumerable.Range(first, 7))}";
        }
    }
}

/// <summary>
/// The never-ending end of a zone. <paramref name="Start"/> is null when the zone has had one offset throughout.
/// </summary>
internal sealed record ZoneTail(Instant? Start, ZoneInterval Interval, IReadOnlyList<ZoneTailObservance> Observances);

internal sealed record ZoneTailObservance(
    string ComponentName,
    string Name,
    Offset OffsetFrom,
    Offset OffsetTo,
    LocalDateTime LocalStart,
    string? Rule);
