using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using NodaTime;
using CalendarProperty = DotnetAgents.CalDav.Core.Models.CalendarProperty;
using IcalCalendar = Ical.Net.Calendar;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Resolves UTC and named-zone entity values without a host-zone fallback.</summary>
internal sealed class CalendarTemporalResolver
{
    private const int MaximumZoneTransitions = 10_000;
    private readonly IReadOnlyList<CalendarProperty> _properties;
    private readonly IcalCalendar? _typedCalendar;
    private readonly CancellationToken _cancellationToken;
    private readonly string? _evaluationTimeZone;

    public CalendarTemporalResolver(
        IReadOnlyList<CalendarProperty> properties,
        ReadOnlySpan<byte> authoritativeUtf8,
        CancellationToken cancellationToken = default,
        string? evaluationTimeZone = null)
    {
        _properties = properties;
        _cancellationToken = cancellationToken;
        _evaluationTimeZone = evaluationTimeZone;
        _typedCalendar = LoadTypedCalendar(authoritativeUtf8);
    }

    internal CalendarTemporalResolver(
        IReadOnlyList<CalendarProperty> properties,
        IcalCalendar? typedCalendar,
        CancellationToken cancellationToken = default,
        string? evaluationTimeZone = null)
    {
        _properties = properties;
        _cancellationToken = cancellationToken;
        _evaluationTimeZone = evaluationTimeZone;
        _typedCalendar = typedCalendar;
    }

    public ResolvedCalendarInstant Resolve(CalendarProperty? property)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (property is null)
            return new(null, false);
        if (property.ValueType == CalendarPropertyValueType.Date)
            return ResolveDate(property.RawEncodedValue);
        return ResolveToken(property.RawEncodedValue, GetTimeZoneId(property));
    }

    public bool CanResolve(CalendarProperty property)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return property.ValueType switch
        {
            CalendarPropertyValueType.Date => property.RawEncodedValue
                .Split(',', StringSplitOptions.None)
                .All(token => ResolveDate(token).Value is not null),
            CalendarPropertyValueType.DateTime => property.RawEncodedValue
                .Split(',', StringSplitOptions.None)
                .All(token => ResolveToken(token, GetTimeZoneId(property)).Value is not null),
            CalendarPropertyValueType.Period => property.RawEncodedValue
                .Split(',', StringSplitOptions.None)
                .All(token => CanResolvePeriod(token, GetTimeZoneId(property))),
            _ => true
        };
    }

    public ResolvedCalendarInstant ResolveToken(CalendarProperty property, string raw) =>
        ResolveToken(raw, GetTimeZoneId(property));

    public ResolvedCalendarInstant ResolveFollowingCivilDate(CalendarProperty property)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (property.ValueType != CalendarPropertyValueType.Date
            || !DateTime.TryParseExact(
                property.RawEncodedValue,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return new(null, true);
        }
        return ResolveInEvaluationZone(date.AddDays(1));
    }

    public ResolvedCalendarInstant ResolveFollowingCivilDate(CalDateTime value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return value.HasTime
            ? new(null, true)
            : ResolveInEvaluationZone(value.Value.Date.AddDays(1));
    }

    public ResolvedCalendarInstant Resolve(CalDateTime value, bool generated = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!value.HasTime)
            return ResolveInEvaluationZone(value.Value.Date);
        if (value.IsUtc)
            return new(new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)), false);
        return value.TzId is { Length: > 0 } timeZoneId
            ? ResolveLocal(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), timeZoneId, generated)
            : ResolveInEvaluationZone(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), generated);
    }

    public bool HasResourceLocalDefinition(string timeZoneId) => CountRawLocalDefinitions(timeZoneId) > 0;

    public CalendarTemporalValue? Project(DateTimeOffset instant, CalendarTemporalValue template)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (template.Kind == CalendarTemporalKind.UtcDateTime)
            return template with { Value = FormatUtc(instant) };
        var timeZoneId = template.Kind == CalendarTemporalKind.ZonedDateTime
            ? template.TimeZoneId
            : _evaluationTimeZone;
        if (timeZoneId is null)
            return null;
        var local = ProjectLocal(instant, timeZoneId);
        if (local is null)
            return null;
        if (template.Kind != CalendarTemporalKind.Date)
        {
            return template with
            {
                Value = local.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            };
        }
        return local.Value.TimeOfDay == TimeSpan.Zero
            ? template with { Value = local.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new CalendarTemporalValue(
                CalendarTemporalKind.ZonedDateTime,
                local.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                timeZoneId);
    }

    private static IcalCalendar? LoadTypedCalendar(ReadOnlySpan<byte> authoritativeUtf8)
    {
        try
        {
            var replay = CalendarContentDocument.Parse(authoritativeUtf8).ReplayForOccurrenceEvaluation();
            return IcalCalendar.Load(Encoding.UTF8.GetString(replay));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool CanResolvePeriod(string period, string? timeZoneId)
    {
        var parts = period.Split('/', StringSplitOptions.None);
        return parts.Length == 2
            && ResolveToken(parts[0], timeZoneId).Value is not null
            && (CalendarDurationArithmetic.LooksLikeDuration(parts[1])
                || ResolveToken(parts[1], timeZoneId).Value is not null);
    }

    private ResolvedCalendarInstant ResolveToken(string raw, string? timeZoneId)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (raw.EndsWith('Z'))
        {
            var parsed = DateTimeOffset.TryParseExact(
                raw,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant);
            return new(parsed ? instant : null, false);
        }
        if (!DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return new(null, true);

        return timeZoneId is null ? ResolveInEvaluationZone(local) : ResolveLocal(local, timeZoneId);
    }

    private ResolvedCalendarInstant ResolveDate(string raw)
    {
        var parsed = DateTime.TryParseExact(
            raw,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date);
        return parsed ? ResolveInEvaluationZone(date) : new(null, true);
    }

    private ResolvedCalendarInstant ResolveInEvaluationZone(DateTime local, bool generated = false) =>
        _evaluationTimeZone is null
            ? new(null, true)
            : ResolveFromIana(
                DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _evaluationTimeZone, generated);

    private ResolvedCalendarInstant ResolveLocal(DateTime local, string timeZoneId, bool generated = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var rawDefinitionCount = CountRawLocalDefinitions(timeZoneId);
        if (rawDefinitionCount > 1)
            return new(null, true);
        if (rawDefinitionCount == 0)
            return ResolveFromIana(local, timeZoneId, generated);
        var typedDefinitions = FindTypedLocalDefinitions(timeZoneId);
        return typedDefinitions.Count == 1
            ? ResolveFromLocalDefinition(local, typedDefinitions[0], generated, _cancellationToken)
            : new(null, true);
    }

    private int CountRawLocalDefinitions(string timeZoneId) => _properties.Count(property =>
        property.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase)
        && property.ComponentPath.Count == 2
        && property.ComponentPath[1].Name.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase)
        && property.RawEncodedValue.Equals(timeZoneId, StringComparison.Ordinal));

    private IReadOnlyList<VTimeZone> FindTypedLocalDefinitions(string timeZoneId) => _typedCalendar?.TimeZones
        .Where(zone => string.Equals(zone.TzId, timeZoneId, StringComparison.Ordinal))
        .ToArray() ?? [];

    private DateTime? ProjectLocal(DateTimeOffset instant, string timeZoneId)
    {
        var rawDefinitionCount = CountRawLocalDefinitions(timeZoneId);
        if (rawDefinitionCount > 1)
            return null;
        if (rawDefinitionCount == 0)
            return ProjectFromIana(instant, timeZoneId);
        var definitions = FindTypedLocalDefinitions(timeZoneId);
        return definitions.Count == 1
            ? ProjectFromLocalDefinition(instant, definitions[0], _cancellationToken)
            : null;
    }

    private static DateTime? ProjectFromIana(DateTimeOffset instant, string timeZoneId)
    {
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId);
        return zone is null
            ? null
            : Instant.FromDateTimeOffset(instant).InZone(zone).LocalDateTime.ToDateTimeUnspecified();
    }

    private static DateTime? ProjectFromLocalDefinition(
        DateTimeOffset instant,
        VTimeZone zone,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryGetConsistentTransitions(zone, instant.UtcDateTime, cancellationToken, out var transitions)
                || transitions.Length == 0)
                return null;
            var previous = transitions.LastOrDefault(transition =>
                GetTransitionInstant(transition) <= instant);
            var offset = previous?.OffsetTo ?? transitions[0].OffsetFrom;
            return DateTime.SpecifyKind(instant.UtcDateTime + offset, DateTimeKind.Unspecified);
        }
        catch (Exception exception) when (exception is EvaluationLimitExceededException
            or EvaluationOutOfRangeException
            or ZoneTransitionLimitException
            or FormatException
            or ArgumentException
            or OverflowException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTimeOffset GetTransitionInstant(ZoneTransition transition)
    {
        var utcTicks = checked(transition.LocalStart.Ticks - transition.OffsetFrom.Ticks);
        return new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc));
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static ResolvedCalendarInstant ResolveFromIana(
        DateTime local,
        string timeZoneId,
        bool generated = false)
    {
        try
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId);
            if (zone is null)
                return new(null, true);
            var mapping = zone.MapLocal(LocalDateTime.FromDateTime(local));
            return mapping.Count switch
            {
                0 when generated => new(null, false, true),
                0 => new(new DateTimeOffset(local, mapping.EarlyInterval.WallOffset.ToTimeSpan())
                    .ToUniversalTime(), false),
                1 => new(mapping.Single().ToDateTimeOffset().ToUniversalTime(), false),
                _ => new(mapping.First().ToDateTimeOffset().ToUniversalTime(), false)
            };
        }
        catch (ArgumentException)
        {
            return new(null, true);
        }
    }

    private static ResolvedCalendarInstant ResolveFromLocalDefinition(
        DateTime local,
        VTimeZone zone,
        bool generated,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetConsistentTransitions(zone, local, cancellationToken, out var transitions)
                || transitions.Length == 0)
                return new(null, true);
            var offset = ResolveLocalOffset(local, transitions, generated, out var skipped);
            if (skipped)
                return new(null, false, true);
            var utcTicks = checked(local.Ticks - offset.Ticks);
            return new(new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc)), false);
        }
        catch (Exception exception) when (exception is EvaluationLimitExceededException
            or EvaluationOutOfRangeException
            or ZoneTransitionLimitException
            or FormatException
            or ArgumentException
            or OverflowException
            or InvalidOperationException)
        {
            return new(null, true);
        }
    }

    private static IEnumerable<ZoneTransition> GetTransitions(
        VTimeZone zone,
        DateTime local,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var end = local.AddYears(1);
        var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumZoneTransitions };
        if (zone.TimeZoneInfos.Count == 0)
            throw new InvalidOperationException("The VTIMEZONE has no observances.");
        foreach (var observance in zone.TimeZoneInfos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observance.DtStart is null || observance.OffsetFrom is null || observance.OffsetTo is null)
                throw new InvalidOperationException("The VTIMEZONE contains an incomplete observance.");
            var from = (TimeSpan)observance.OffsetFrom;
            var to = (TimeSpan)observance.OffsetTo;
            foreach (var occurrence in observance.GetOccurrences(observance.DtStart, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transition = occurrence.Period.StartTime.Value;
                if (transition > end)
                    break;
                count++;
                if (count > MaximumZoneTransitions)
                    throw new ZoneTransitionLimitException();
                yield return new ZoneTransition(transition, from, to);
            }
        }
    }

    private static bool TryGetConsistentTransitions(
        VTimeZone zone,
        DateTime local,
        CancellationToken cancellationToken,
        out ZoneTransition[] transitions)
    {
        transitions = GetTransitions(zone, local, cancellationToken)
            .Distinct()
            .OrderBy(item => item.LocalStart)
            .ToArray();
        if (transitions.GroupBy(item => item.LocalStart).Any(group => group.Count() > 1))
            return false;
        for (var index = 1; index < transitions.Length; index++)
        {
            if (transitions[index - 1].OffsetTo != transitions[index].OffsetFrom)
                return false;
        }
        return true;
    }

    private static TimeSpan ResolveLocalOffset(
        DateTime local,
        IReadOnlyList<ZoneTransition> transitions,
        bool generated,
        out bool skipped)
    {
        skipped = false;
        foreach (var transition in transitions)
        {
            var change = transition.OffsetTo - transition.OffsetFrom;
            if (change > TimeSpan.Zero
                && local >= transition.LocalStart
                && local < transition.LocalStart + change)
            {
                skipped = generated;
                return transition.OffsetFrom;
            }
            if (change < TimeSpan.Zero
                && local >= transition.LocalStart + change
                && local < transition.LocalStart)
                return transition.OffsetFrom;
        }
        var previous = transitions.LastOrDefault(item => item.LocalStart <= local);
        return previous?.OffsetTo ?? transitions[0].OffsetFrom;
    }

    private static string? GetTimeZoneId(CalendarProperty property) => property.Parameters
        .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
        .SelectMany(parameter => parameter.Values)
        .SingleOrDefault();

    private sealed record ZoneTransition(DateTime LocalStart, TimeSpan OffsetFrom, TimeSpan OffsetTo);

    private sealed class ZoneTransitionLimitException : Exception
    {
    }
}

internal readonly record struct ResolvedCalendarInstant(
    DateTimeOffset? Value,
    bool Unresolved,
    bool Skipped = false);
