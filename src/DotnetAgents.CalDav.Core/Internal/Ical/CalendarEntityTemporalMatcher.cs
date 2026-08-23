using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using CalendarProperty = DotnetAgents.CalDav.Core.Models.CalendarProperty;
using IcalCalendar = Ical.Net.Calendar;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal enum CalendarEntityTemporalMatch
{
    Match,
    NoMatch,
    Unresolved,
    Unevaluable,
    LimitExhausted
}

internal readonly record struct CalendarEntityTemporalResult(
    CalendarEntityTemporalMatch Match,
    int OccurrenceCount = 0);

/// <summary>Applies final instant-window predicates to authoritative snapshot properties.</summary>
internal static class CalendarEntityTemporalMatcher
{
    private const int MaximumEntityOccurrences = 2000;
    private const int MaximumUnmatchedIncrements = 10_000;
    private static readonly string[] TemporalPropertyNames =
        ["DTSTART", "DTEND", "DUE", "RDATE", "EXDATE", "RECURRENCE-ID"];

    public static CalendarEntityTemporalResult Matches(
        CalendarResourceSnapshot snapshot,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? evaluationTimeZone = null,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            return MatchOpaque(snapshot);
        try
        {
            return Matches(
                snapshot,
                CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span),
                null,
                from,
                to,
                evaluationTimeZone,
                cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return new(CalendarEntityTemporalMatch.Unevaluable);
        }
    }

    internal static CalendarEntityTemporalResult Matches(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IcalCalendar? typedCalendar,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? evaluationTimeZone = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (from is null || to is null)
            return new(CalendarEntityTemporalMatch.Match);
        if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            return MatchOpaque(snapshot);

        var componentName = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event ? "VEVENT" : "VTODO";
        var entityProperties = snapshot.CalendarProperties.Where(property => IsEntityProperty(property, componentName)).ToArray();
        var calendar = ResolveTypedCalendar(typedCalendar, document);
        var resolver = new CalendarTemporalResolver(
            snapshot.CalendarProperties,
            calendar,
            cancellationToken,
            evaluationTimeZone);
        if (HasUnresolvedTemporalValue(entityProperties, resolver, cancellationToken))
            return new(CalendarEntityTemporalMatch.Unresolved);
        var masterProperties = entityProperties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var recurrence = ClassifyRecurrence(entityProperties, masterProperties);
        if (recurrence == RecurrenceDisposition.Unevaluable)
            return new(CalendarEntityTemporalMatch.Unevaluable);
        if (recurrence == RecurrenceDisposition.Recurring)
            return MatchRecurring(
                snapshot,
                calendar,
                entityProperties,
                resolver,
                from.Value,
                to.Value,
                cancellationToken);
        var match = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            ? MatchEvent(masterProperties, resolver, from.Value, to.Value)
            : MatchTodo(masterProperties, resolver, from.Value, to.Value);
        return new(match, match == CalendarEntityTemporalMatch.NoMatch ? 0 : 1);
    }

    private static CalendarEntityTemporalResult MatchOpaque(CalendarResourceSnapshot snapshot)
    {
        _ = snapshot;
        return new(CalendarEntityTemporalMatch.Unresolved);
    }

    private static IcalCalendar? ResolveTypedCalendar(
        IcalCalendar? typedCalendar,
        CalendarContentDocument document) => typedCalendar ?? CalendarResourceProjector.LoadTypedCalendar(document);

    private static CalendarEntityTemporalResult MatchRecurring(
        CalendarResourceSnapshot snapshot,
        IcalCalendar? typedCalendar,
        IReadOnlyList<CalendarProperty> entityProperties,
        CalendarTemporalResolver resolver,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            if (typedCalendar is null)
                return new(CalendarEntityTemporalMatch.Unevaluable);
            var calendar = typedCalendar;
            cancellationToken.ThrowIfCancellationRequested();
            var searchStart = SubtractSafely(from, GetMaximumLookback(entityProperties, resolver, cancellationToken));
            var options = new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumUnmatchedIncrements };
            var occurrences = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
                ? calendar.GetOccurrences<CalendarEvent>(new CalDateTime(searchStart.UtcDateTime), options)
                : calendar.GetOccurrences<Todo>(new CalDateTime(searchStart.UtcDateTime), options);
            return EvaluateOccurrences(occurrences, resolver, from, to, cancellationToken);
        }
        catch (EvaluationLimitExceededException)
        {
            return new(CalendarEntityTemporalMatch.LimitExhausted);
        }
        catch (EvaluationOutOfRangeException)
        {
            return new(CalendarEntityTemporalMatch.Unevaluable);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return new(CalendarEntityTemporalMatch.Unevaluable);
        }
    }

    private static RecurrenceDisposition ClassifyRecurrence(
        IReadOnlyList<CalendarProperty> entityProperties,
        IReadOnlyList<CalendarProperty> masterProperties)
    {
        if (entityProperties.Any(HasUnsupportedRangeParameter))
            return RecurrenceDisposition.Unevaluable;
        if (entityProperties.Any(property => property.ComponentPath[1].Occurrence > 0
                && property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)))
            return RecurrenceDisposition.Unevaluable;
        var recurrenceRuleCount = entityProperties.Count(property =>
            property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase));
        if (recurrenceRuleCount > 1)
            return RecurrenceDisposition.Unevaluable;
        return recurrenceRuleCount == 1
            || masterProperties.Any(property => property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
            || entityProperties.Any(property => property.ComponentPath[1].Occurrence > 0)
            ? RecurrenceDisposition.Recurring
            : RecurrenceDisposition.None;
    }

    private static bool HasUnsupportedRangeParameter(CalendarProperty property) =>
        property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase)
        && property.Parameters.Any(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase));

    private static CalendarEntityTemporalResult EvaluateOccurrences(
        IEnumerable<Occurrence> occurrences,
        CalendarTemporalResolver resolver,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var matched = false;
        foreach (var occurrence in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveOccurrence(occurrence, resolver, out var start, out var end, out var failure))
                return new(failure, count);
            if (start >= to)
                break;
            count++;
            if (count > MaximumEntityOccurrences)
                return new(CalendarEntityTemporalMatch.LimitExhausted, count);
            matched |= Overlaps(start, end, from, to) == CalendarEntityTemporalMatch.Match;
        }
        return new(matched ? CalendarEntityTemporalMatch.Match : CalendarEntityTemporalMatch.NoMatch, count);
    }

    private static bool TryResolveOccurrence(
        Occurrence occurrence,
        CalendarTemporalResolver resolver,
        out DateTimeOffset start,
        out DateTimeOffset end,
        out CalendarEntityTemporalMatch failure)
    {
        start = default;
        end = default;
        failure = CalendarEntityTemporalMatch.Unevaluable;
        var resolvedStart = resolver.Resolve(occurrence.Period.StartTime);
        if (resolvedStart.Value is null)
        {
            failure = ToFailure(resolvedStart);
            return false;
        }
        start = resolvedStart.Value.Value;
        var effectiveEnd = occurrence.Period.EffectiveEndTime;
        if (effectiveEnd is null)
        {
            if (occurrence.Period.StartTime.HasTime)
            {
                end = start;
                return true;
            }
            var followingDate = resolver.ResolveFollowingCivilDate(occurrence.Period.StartTime);
            if (followingDate.Value is null)
            {
                failure = ToFailure(followingDate);
                return false;
            }
            end = followingDate.Value.Value;
            return true;
        }
        var resolvedEnd = resolver.Resolve(effectiveEnd);
        if (resolvedEnd.Value is null)
        {
            failure = ToFailure(resolvedEnd);
            return false;
        }
        end = resolvedEnd.Value.Value;
        return true;
    }

    private static TimeSpan GetMaximumLookback(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maximum = properties
            .GroupBy(property => property.ComponentPath[1].Occurrence)
            .Select(group => GetComponentSpan(group.ToArray(), resolver))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        foreach (var property in properties.Where(property =>
                     property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                     && property.ValueType == CalendarPropertyValueType.Period))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var period in property.RawEncodedValue.Split(',', StringSplitOptions.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                maximum = Max(maximum, GetPeriodSpan(property, period, resolver));
            }
        }
        return maximum;
    }

    private static TimeSpan GetComponentSpan(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver)
    {
        var startProperty = GetProperty(properties, "DTSTART");
        var start = resolver.Resolve(startProperty).Value;
        if (start is null)
            return TimeSpan.Zero;
        var end = resolver.Resolve(GetProperty(properties, "DTEND") ?? GetProperty(properties, "DUE")).Value;
        if (end > start)
            return end.Value - start.Value;
        var duration = GetDuration(properties);
        if (duration > TimeSpan.Zero)
            return duration.Value;
        var followingDate = startProperty is { ValueType: CalendarPropertyValueType.Date }
            ? resolver.ResolveFollowingCivilDate(startProperty).Value
            : null;
        return followingDate > start ? followingDate.Value - start.Value : TimeSpan.Zero;
    }

    private static TimeSpan GetPeriodSpan(
        CalendarProperty property,
        string period,
        CalendarTemporalResolver resolver)
    {
        var parts = period.Split('/', StringSplitOptions.None);
        if (parts.Length != 2)
            return TimeSpan.Zero;
        var start = resolver.ResolveToken(property, parts[0]).Value;
        if (start is null)
            return TimeSpan.Zero;
        if (parts[1].StartsWith('P') || parts[1].StartsWith("-P", StringComparison.Ordinal))
        {
            try
            {
                var duration = XmlConvert.ToTimeSpan(parts[1]);
                return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            }
            catch (FormatException)
            {
                return TimeSpan.Zero;
            }
        }
        var end = resolver.ResolveToken(property, parts[1]).Value;
        return end > start ? end.Value - start.Value : TimeSpan.Zero;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static DateTimeOffset SubtractSafely(DateTimeOffset instant, TimeSpan lookback) =>
        instant - DateTimeOffset.MinValue < lookback ? DateTimeOffset.MinValue : instant - lookback;

    private static bool HasUnresolvedTemporalValue(
        IEnumerable<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CancellationToken cancellationToken)
    {
        foreach (var property in properties.Where(property =>
                     TemporalPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolver.CanResolve(property))
                return true;
        }
        return false;
    }

    private static CalendarEntityTemporalMatch MatchEvent(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var start = resolver.Resolve(GetProperty(properties, "DTSTART"));
        if (start.Value is null)
            return ToFailure(start);

        var end = GetProperty(properties, "DTEND");
        var resolvedEnd = end is null ? default : resolver.Resolve(end);
        if (end is not null && resolvedEnd.Value is null)
            return ToFailure(resolvedEnd);
        var endInstant = resolvedEnd.Value
            ?? ApplyDuration(properties, start.Value.Value)
            ?? (GetProperty(properties, "DTSTART") is { ValueType: CalendarPropertyValueType.Date } dateStart
                ? resolver.ResolveFollowingCivilDate(dateStart).Value
                : null)
            ?? start.Value.Value;
        return Overlaps(start.Value.Value, endInstant, from, to);
    }

    private static CalendarEntityTemporalMatch MatchTodo(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var start = GetProperty(properties, "DTSTART");
        var due = GetProperty(properties, "DUE");
        if (start is null)
            return MatchTodoWithoutStart(due, resolver, from, to);

        var resolvedStart = resolver.Resolve(start);
        if (resolvedStart.Value is null)
            return ToFailure(resolvedStart);
        if (due is null)
        {
            var end = ApplyDuration(properties, resolvedStart.Value.Value) ?? resolvedStart.Value.Value;
            return Overlaps(resolvedStart.Value.Value, end, from, to);
        }

        var resolvedDue = resolver.Resolve(due);
        return resolvedDue.Value is null
            ? ToFailure(resolvedDue)
            : Overlaps(resolvedStart.Value.Value, resolvedDue.Value.Value, from, to);
    }

    private static CalendarEntityTemporalMatch MatchTodoWithoutStart(
        CalendarProperty? due,
        CalendarTemporalResolver resolver,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        if (due is null)
            return CalendarEntityTemporalMatch.NoMatch;
        var resolved = resolver.Resolve(due);
        return resolved.Value is null
            ? ToFailure(resolved)
            : Overlaps(resolved.Value.Value, resolved.Value.Value, from, to);
    }

    private static DateTimeOffset? ApplyDuration(
        IReadOnlyList<CalendarProperty> properties,
        DateTimeOffset start) => GetDuration(properties) is { } duration ? start + duration : null;

    private static TimeSpan? GetDuration(IReadOnlyList<CalendarProperty> properties)
    {
        var duration = GetProperty(properties, "DURATION");
        if (duration is null)
            return null;

        try
        {
            return XmlConvert.ToTimeSpan(duration.RawEncodedValue);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static CalendarEntityTemporalMatch Overlaps(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset from,
        DateTimeOffset to) => end > start
            ? start < to && end > from ? CalendarEntityTemporalMatch.Match : CalendarEntityTemporalMatch.NoMatch
            : start >= from && start < to ? CalendarEntityTemporalMatch.Match : CalendarEntityTemporalMatch.NoMatch;

    private static CalendarEntityTemporalMatch ToFailure(ResolvedCalendarInstant instant) => instant.Unresolved
        ? CalendarEntityTemporalMatch.Unresolved
        : CalendarEntityTemporalMatch.Unevaluable;

    private static CalendarProperty? GetProperty(IEnumerable<CalendarProperty> properties, string name) =>
        properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsEntityProperty(CalendarProperty property, string componentName) =>
        property.ComponentPath.Count == 2
        && property.ComponentPath[1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase);

    private enum RecurrenceDisposition
    {
        None,
        Recurring,
        Unevaluable
    }
}
