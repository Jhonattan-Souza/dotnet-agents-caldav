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

    public CalendarTemporalResolver(
        IReadOnlyList<CalendarProperty> properties,
        ReadOnlySpan<byte> authoritativeUtf8,
        CancellationToken cancellationToken = default)
    {
        _properties = properties;
        _cancellationToken = cancellationToken;
        _typedCalendar = LoadTypedCalendar(authoritativeUtf8);
    }

    public ResolvedCalendarInstant Resolve(CalendarProperty? property)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (property is null)
            return new(null, false);
        if (property.ValueType == CalendarPropertyValueType.Date)
            return new(null, true);
        return ResolveToken(property.RawEncodedValue, GetTimeZoneId(property));
    }

    public bool CanResolve(CalendarProperty property)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return property.ValueType switch
        {
            CalendarPropertyValueType.Date => false,
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

    public ResolvedCalendarInstant Resolve(CalDateTime value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!value.HasTime)
            return new(null, true);
        if (value.IsUtc)
            return new(new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)), false);
        return value.TzId is { Length: > 0 } timeZoneId
            ? ResolveLocal(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), timeZoneId)
            : new(null, true);
    }

    private static IcalCalendar? LoadTypedCalendar(ReadOnlySpan<byte> authoritativeUtf8)
    {
        try
        {
            var replay = CalendarContentDocument.Parse(authoritativeUtf8).ReplayForTypedValidation();
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
            && (parts[1].StartsWith('P')
                || parts[1].StartsWith("-P", StringComparison.Ordinal)
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
        if (timeZoneId is null
            || !DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return new(null, true);

        return ResolveLocal(local, timeZoneId);
    }

    private ResolvedCalendarInstant ResolveLocal(DateTime local, string timeZoneId)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var rawDefinitionCount = CountRawLocalDefinitions(timeZoneId);
        if (rawDefinitionCount > 1)
            return new(null, true);
        if (rawDefinitionCount == 0)
            return ResolveFromIana(local, timeZoneId);
        var typedDefinitions = FindTypedLocalDefinitions(timeZoneId);
        return typedDefinitions.Count == 1
            ? ResolveFromLocalDefinition(local, typedDefinitions[0], _cancellationToken)
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

    private static ResolvedCalendarInstant ResolveFromIana(DateTime local, string timeZoneId)
    {
        try
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId);
            if (zone is null)
                return new(null, true);
            var mapping = zone.MapLocal(LocalDateTime.FromDateTime(local));
            return mapping.Count == 1
                ? new(mapping.Single().ToDateTimeOffset().ToUniversalTime(), false)
                : new(null, true);
        }
        catch (ArgumentException)
        {
            return new(null, true);
        }
    }

    private static ResolvedCalendarInstant ResolveFromLocalDefinition(
        DateTime local,
        VTimeZone zone,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetConsistentTransitions(zone, local, cancellationToken, out var transitions)
                || transitions.Length == 0
                || IsAmbiguousOrInvalid(local, transitions))
                return new(null, true);
            var previous = transitions.LastOrDefault(item => item.LocalStart <= local);
            var offset = previous?.OffsetTo ?? transitions[0].OffsetFrom;
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

    private static bool IsAmbiguousOrInvalid(DateTime local, IEnumerable<ZoneTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            var change = transition.OffsetTo - transition.OffsetFrom;
            if (change > TimeSpan.Zero
                && local >= transition.LocalStart
                && local < transition.LocalStart + change)
                return true;
            if (change < TimeSpan.Zero
                && local >= transition.LocalStart + change
                && local < transition.LocalStart)
                return true;
        }
        return false;
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

internal readonly record struct ResolvedCalendarInstant(DateTimeOffset? Value, bool Unresolved);
