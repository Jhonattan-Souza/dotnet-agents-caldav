using System.Xml;
using DotnetAgents.CalDav.Core.Models;
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
                document,
                calendar,
                from.Value,
                to.Value,
                evaluationTimeZone,
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
        CalendarContentDocument document,
        IcalCalendar? typedCalendar,
        DateTimeOffset from,
        DateTimeOffset to,
        string? evaluationTimeZone,
        CancellationToken cancellationToken)
    {
        var evaluated = CalendarOccurrenceEvaluator.Evaluate(
            snapshot,
            new CalendarOccurrenceQuery(
                CalendarEntityScope.All,
                from,
                to,
                evaluationTimeZone),
            document,
            typedCalendar,
            cancellationToken);
        return evaluated.Code switch
        {
            CalendarOccurrenceEvaluationCode.Success => new(
                evaluated.Items.Count > 0
                    ? CalendarEntityTemporalMatch.Match
                    : CalendarEntityTemporalMatch.NoMatch,
                evaluated.ObservedOccurrenceCount),
            CalendarOccurrenceEvaluationCode.TemporalUnresolved => new(
                CalendarEntityTemporalMatch.Unresolved,
                evaluated.ObservedOccurrenceCount),
            CalendarOccurrenceEvaluationCode.LimitExhausted => new(
                CalendarEntityTemporalMatch.LimitExhausted,
                evaluated.ObservedOccurrenceCount),
            _ => new(CalendarEntityTemporalMatch.Unevaluable, evaluated.ObservedOccurrenceCount)
        };
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
