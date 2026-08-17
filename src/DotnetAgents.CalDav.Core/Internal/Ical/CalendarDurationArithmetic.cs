using System.Globalization;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net.DataTypes;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Applies RFC 5545 nominal day/week duration parts before accurate time parts.</summary>
internal static class CalendarDurationArithmetic
{
    public static CalendarDurationResolution Resolve(
        CalendarTemporalValue start,
        DateTimeOffset resolvedStart,
        string rawDuration,
        CalendarTemporalResolver resolver)
    {
        try
        {
            return ResolveCore(start, resolvedStart, rawDuration, resolver);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return CalendarDurationResolution.Invalid;
        }
    }

    internal static CalendarTemporalValue? ResolveCreateEnd(
        CalendarTemporalValue start,
        string rawDuration)
    {
        var resolver = new CalendarTemporalResolver([], ReadOnlySpan<byte>.Empty);
        var resolvedStart = resolver.Resolve(ToCalDateTime(start));
        return resolvedStart.Value is null
            ? null
            : Resolve(start, resolvedStart.Value.Value, rawDuration, resolver).Value;
    }

    internal static CalendarTemporalValue? ShiftCreateExplicitEnd(
        CalendarTemporalValue oldStart,
        CalendarTemporalValue oldEnd,
        CalendarTemporalValue newStart) => ShiftExplicitEnd(
            oldStart,
            oldEnd,
            newStart,
            new CalendarTemporalResolver([], ReadOnlySpan<byte>.Empty));

    private static CalendarDurationResolution ResolveCore(
        CalendarTemporalValue start,
        DateTimeOffset resolvedStart,
        string rawDuration,
        CalendarTemporalResolver resolver)
    {
        if (!TryParse(rawDuration, out var duration) || !duration.IsStrictlyPositive)
            return CalendarDurationResolution.Invalid;
        var nominalEnd = AddLocalDays(start, duration.NominalDays);
        var resolvedNominalEnd = duration.NominalDays == 0
            ? new ResolvedCalendarInstant(resolvedStart, false)
            : resolver.Resolve(ToCalDateTime(nominalEnd));
        if (resolvedNominalEnd.Value is null)
            return new(null, resolvedNominalEnd);
        if (duration.Accurate == TimeSpan.Zero)
            return new(nominalEnd, resolvedNominalEnd);

        var instant = resolvedNominalEnd.Value.Value + duration.Accurate;
        var projected = resolver.Project(instant, nominalEnd);
        return projected is null
            ? new(null, new ResolvedCalendarInstant(null, true))
            : new(projected, new ResolvedCalendarInstant(instant, false));
    }

    public static CalendarDurationResolution ResolveAccurate(
        CalendarTemporalValue start,
        DateTimeOffset resolvedStart,
        TimeSpan duration,
        CalendarTemporalResolver resolver)
    {
        try
        {
            return ResolveAccurateCore(start, resolvedStart, duration, resolver);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return CalendarDurationResolution.Invalid;
        }
    }

    public static CalendarTemporalValue? ShiftExplicitEnd(
        CalendarTemporalValue oldStart,
        CalendarTemporalValue oldEnd,
        CalendarTemporalValue newStart,
        CalendarTemporalResolver resolver) => ShiftExplicitEndResolution(
            oldStart,
            oldEnd,
            newStart,
            resolver).Value;

    public static CalendarDurationResolution ShiftExplicitEndResolution(
        CalendarTemporalValue oldStart,
        CalendarTemporalValue oldEnd,
        CalendarTemporalValue newStart,
        CalendarTemporalResolver resolver)
    {
        if (oldStart.Kind is CalendarTemporalKind.Date or CalendarTemporalKind.FloatingDateTime)
        {
            var shifted = ShiftByLocalClock(oldStart, oldEnd, newStart);
            return shifted is null
                ? CalendarDurationResolution.Invalid
                : new(shifted, new ResolvedCalendarInstant(null, false));
        }
        var resolvedOldStart = resolver.Resolve(ToCalDateTime(oldStart));
        var resolvedOldEnd = resolver.Resolve(ToCalDateTime(oldEnd));
        var resolvedNewStart = resolver.Resolve(ToCalDateTime(newStart), generated: true);
        if (resolvedOldStart.Value is null || resolvedOldEnd.Value is null || resolvedNewStart.Value is null)
        {
            var unresolved = resolvedOldStart.Unresolved
                || resolvedOldEnd.Unresolved
                || resolvedNewStart.Unresolved;
            var skipped = resolvedOldStart.Skipped
                || resolvedOldEnd.Skipped
                || resolvedNewStart.Skipped;
            return new(null, new ResolvedCalendarInstant(null, unresolved, skipped));
        }
        var duration = resolvedOldEnd.Value.Value - resolvedOldStart.Value.Value;
        return ResolveAccurate(newStart, resolvedNewStart.Value.Value, duration, resolver);
    }

    private static CalendarTemporalValue? ShiftByLocalClock(
        CalendarTemporalValue oldStart,
        CalendarTemporalValue oldEnd,
        CalendarTemporalValue newStart)
    {
        var oldStartValue = ParseLocal(oldStart);
        var duration = ParseLocal(oldEnd) - oldStartValue;
        if (duration <= TimeSpan.Zero)
            return null;
        var shifted = ParseLocal(newStart) + duration;
        var format = newStart.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var value = shifted.ToString(format, CultureInfo.InvariantCulture);
        return newStart with
        {
            Value = newStart.Kind == CalendarTemporalKind.UtcDateTime ? value + "Z" : value
        };
    }

    public static CalendarTemporalValue AddNominalDay(CalendarTemporalValue start)
    {
        var shifted = ParseLocal(start).AddDays(1);
        var format = start.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var value = shifted.ToString(format, CultureInfo.InvariantCulture);
        return start with { Value = start.Kind == CalendarTemporalKind.UtcDateTime ? value + "Z" : value };
    }

    private static DateTime ParseLocal(CalendarTemporalValue value)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        return DateTime.ParseExact(value.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture);
    }

    private static CalendarDurationResolution ResolveAccurateCore(
        CalendarTemporalValue start,
        DateTimeOffset resolvedStart,
        TimeSpan duration,
        CalendarTemporalResolver resolver)
    {
        if (duration <= TimeSpan.Zero)
            return CalendarDurationResolution.Invalid;
        var instant = resolvedStart + duration;
        var projected = resolver.Project(instant, start);
        return projected is null
            ? new(null, new ResolvedCalendarInstant(null, true))
            : new(projected, new ResolvedCalendarInstant(instant, false));
    }

    internal static bool TryParse(string raw, out CalendarDurationParts duration)
    {
        duration = default;
        try
        {
            var index = 0;
            var sign = ReadSign(raw, ref index);
            if (index >= raw.Length || raw[index++] != 'P' || index >= raw.Length)
                return false;
            var parsed = raw[index] == 'T'
                ? TryParseTime(raw, index + 1, sign, 0, out duration)
                : TryParseDate(raw, index, sign, out duration);
            if (parsed)
                _ = duration.LocalClockDuration;
            return parsed;
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentOutOfRangeException
            or OverflowException)
        {
            return false;
        }
    }

    internal static bool LooksLikeDuration(string raw) => raw.StartsWith('P')
        || raw.StartsWith("+P", StringComparison.Ordinal)
        || raw.StartsWith("-P", StringComparison.Ordinal);

    private static bool TryParseDate(
        string raw,
        int index,
        int sign,
        out CalendarDurationParts duration)
    {
        duration = default;
        if (!TryReadNumber(raw, ref index, out var amount) || index >= raw.Length)
            return false;
        if (raw[index] == 'W')
            return index + 1 == raw.Length
                && TryCreate(sign, amount, 7, TimeSpan.Zero, out duration);
        if (raw[index++] != 'D' || !TryCreate(sign, amount, 1, TimeSpan.Zero, out duration))
            return false;
        return index == raw.Length
            || raw[index] == 'T' && TryParseTime(raw, index + 1, sign, duration.NominalDays, out duration);
    }

    private static int ReadSign(string raw, ref int index)
    {
        if (raw.Length == 0 || raw[0] is not ('+' or '-'))
            return 1;
        index = 1;
        return raw[0] == '-' ? -1 : 1;
    }

    private static bool TryParseTime(
        string raw,
        int index,
        int sign,
        int nominalDays,
        out CalendarDurationParts duration)
    {
        duration = default;
        if (!TryReadNumber(raw, ref index, out var first) || index >= raw.Length)
            return false;
        var designator = raw[index++];
        return designator switch
        {
            'H' => TryParseAfterHours(raw, index, sign, nominalDays, first, out duration),
            'M' => TryParseAfterMinutes(raw, index, sign, nominalDays, first, out duration),
            'S' => index == raw.Length
                && TryCreateAccurate(sign, nominalDays, 0, 0, first, out duration),
            _ => false
        };
    }

    private static bool TryParseAfterHours(
        string raw,
        int index,
        int sign,
        int nominalDays,
        long hours,
        out CalendarDurationParts duration)
    {
        duration = default;
        long minutes = 0;
        long seconds = 0;
        return (index == raw.Length || TryReadComponent(raw, ref index, 'M', out minutes))
            && (index == raw.Length || TryReadComponent(raw, ref index, 'S', out seconds))
            && index == raw.Length
            && TryCreateAccurate(sign, nominalDays, hours, minutes, seconds, out duration);
    }

    private static bool TryParseAfterMinutes(
        string raw,
        int index,
        int sign,
        int nominalDays,
        long minutes,
        out CalendarDurationParts duration)
    {
        duration = default;
        long seconds = 0;
        return (index == raw.Length || TryReadComponent(raw, ref index, 'S', out seconds))
            && index == raw.Length
            && TryCreateAccurate(sign, nominalDays, 0, minutes, seconds, out duration);
    }

    private static bool TryReadComponent(string raw, ref int index, char designator, out long value)
    {
        value = 0;
        return TryReadNumber(raw, ref index, out value)
            && index < raw.Length
            && raw[index++] == designator;
    }

    private static bool TryReadNumber(string raw, ref int index, out long value)
    {
        value = 0;
        var start = index;
        while (index < raw.Length && raw[index] is >= '0' and <= '9')
            index++;
        return index > start
            && long.TryParse(raw.AsSpan(start, index - start), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryCreate(
        int sign,
        long amount,
        int multiplier,
        TimeSpan accurate,
        out CalendarDurationParts duration)
    {
        var nominalDays = checked((int)checked(sign * amount * multiplier));
        duration = new CalendarDurationParts(nominalDays, accurate);
        return true;
    }

    private static bool TryCreateAccurate(
        int sign,
        int nominalDays,
        long hours,
        long minutes,
        long seconds,
        out CalendarDurationParts duration)
    {
        var ticks = checked(hours * TimeSpan.TicksPerHour
            + minutes * TimeSpan.TicksPerMinute
            + seconds * TimeSpan.TicksPerSecond);
        duration = new CalendarDurationParts(nominalDays, TimeSpan.FromTicks(checked(sign * ticks)));
        return true;
    }

    private static CalendarTemporalValue AddLocalDays(CalendarTemporalValue value, int days)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var parsed = DateTime.ParseExact(value.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture).AddDays(days);
        var suffix = value.Kind == CalendarTemporalKind.UtcDateTime ? "Z" : string.Empty;
        return value with { Value = parsed.ToString(format, CultureInfo.InvariantCulture) + suffix };
    }

    private static CalDateTime ToCalDateTime(CalendarTemporalValue value)
    {
        if (value.Kind == CalendarTemporalKind.Date)
        {
            var date = DateTime.ParseExact(value.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new CalDateTime(date.Year, date.Month, date.Day);
        }
        var local = DateTime.ParseExact(
            value.Value.TrimEnd('Z'),
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture);
        return value.Kind switch
        {
            CalendarTemporalKind.UtcDateTime => new CalDateTime(DateTime.SpecifyKind(local, DateTimeKind.Utc)),
            CalendarTemporalKind.ZonedDateTime => new CalDateTime(local, value.TimeZoneId),
            _ => new CalDateTime(local)
        };
    }

}

internal readonly record struct CalendarDurationParts(int NominalDays, TimeSpan Accurate)
{
    public TimeSpan LocalClockDuration => TimeSpan.FromDays(NominalDays) + Accurate;

    public bool IsStrictlyPositive => NominalDays > 0 || NominalDays == 0 && Accurate > TimeSpan.Zero;
}

internal readonly record struct CalendarDurationResolution(
    CalendarTemporalValue? Value,
    ResolvedCalendarInstant Instant)
{
    public static CalendarDurationResolution Invalid { get; } = new(null, new ResolvedCalendarInstant(null, false));
}
