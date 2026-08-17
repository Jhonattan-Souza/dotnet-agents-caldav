using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using CalendarProperty = DotnetAgents.CalDav.Core.Models.CalendarProperty;
using IcalCalendar = Ical.Net.Calendar;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarOccurrenceEvaluation(
    CalendarOccurrenceQueryCode Code,
    IReadOnlyList<CalendarOccurrenceSnapshot> Items,
    int ObservedOccurrenceCount,
    IReadOnlySet<string>? ObservedIdentities = null);

/// <summary>Expands one authoritative resource without using a server-expanded representation.</summary>
internal static class CalendarOccurrenceEvaluator
{
    private const int MaximumEntityOccurrences = 2000;
    private const int MaximumUnmatchedIncrements = 10_000;

    public static CalendarOccurrenceEvaluation Evaluate(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = GetEntityProperties(snapshot);
            var masterProperties = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
            if (HasUnevaluableRecurrenceShape(snapshot, properties, masterProperties))
                return Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);

            var resolver = new CalendarTemporalResolver(
                snapshot.CalendarProperties,
                snapshot.AuthoritativeUtf8.Span,
                cancellationToken,
                query.EvaluationTimeZone);
            var periodValidation = ValidatePeriodStructure(properties, resolver);
            if (periodValidation is not null)
                return Failure(periodValidation.Value);
            if (masterProperties.Where(RequiresEagerResolution).Any(property => !resolver.CanResolve(property)))
                return Failure(CalendarOccurrenceQueryCode.TemporalUnresolved);
            var overrides = CreateOverrides(properties, snapshot.Projection.Kind);

            var replay = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span).ReplayForOccurrenceEvaluation();
            var calendar = IcalCalendar.Load(Encoding.UTF8.GetString(replay));
            if (calendar is null)
                return Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
            var evaluated = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
                ? EvaluateEvent(
                    snapshot,
                    query,
                    resolver,
                    calendar.Events.Single(item => item.RecurrenceIdentifier is null),
                    properties,
                    overrides,
                    cancellationToken)
                : EvaluateTodo(
                    snapshot,
                    query,
                    resolver,
                    calendar.Todos.Single(item => item.RecurrenceIdentifier is null),
                    properties,
                    overrides,
                    cancellationToken);
            if (evaluated.Code != CalendarOccurrenceQueryCode.Success)
                return evaluated;
            var periodDates = EvaluatePeriodDates(snapshot, query, resolver, properties, overrides, cancellationToken);
            if (periodDates.Code != CalendarOccurrenceQueryCode.Success)
                return periodDates;
            var detached = EvaluateDetachedOverrides(
                snapshot, query, resolver, properties, overrides, cancellationToken);
            if (detached.Code != CalendarOccurrenceQueryCode.Success)
                return detached;
            var merged = MergeOccurrences(evaluated, periodDates);
            return merged.Code == CalendarOccurrenceQueryCode.Success
                ? MergeOccurrences(merged, detached, replaceExisting: false)
                : merged;
        }
        catch (EvaluationLimitExceededException)
        {
            return Failure(CalendarOccurrenceQueryCode.LimitExhausted);
        }
        catch (Exception exception) when (exception is EvaluationOutOfRangeException
            or FormatException
            or ArgumentException
            or OverflowException
            or InvalidOperationException)
        {
            return Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        }
    }

    public static string GetIdentitySortKey(CalendarTemporalValue value) =>
        value.GetCanonicalSortKey();

    public static bool HasInvalidComponentStructure(CalendarResourceSnapshot snapshot) =>
        snapshot.CalendarProperties.Any(IsInvalidObservanceRecurrenceDate)
        || snapshot.CalendarProperties
            .Where(property => property.ComponentPath.Count == 2
                && property.ComponentPath[1].Name is "VEVENT" or "VTODO")
            .GroupBy(property => (
                property.ComponentPath[1].Name,
                property.ComponentPath[1].Occurrence))
            .Any(HasInvalidComponentDuration);

    internal static bool HasUnevaluableRecurrenceStructure(CalendarResourceSnapshot snapshot)
    {
        var properties = GetEntityProperties(snapshot);
        var masterProperties = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        return HasUnevaluableRecurrenceShape(snapshot, properties, masterProperties);
    }

    private static bool IsInvalidObservanceRecurrenceDate(CalendarProperty property) =>
        property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
        && property.ValueType == CalendarPropertyValueType.Period
        && property.ComponentPath.Count == 3
        && property.ComponentPath[2].Name is "STANDARD" or "DAYLIGHT";

    private static bool HasInvalidComponentDuration(IEnumerable<CalendarProperty> component)
    {
        var properties = component.ToArray();
        var durationProperty = GetProperty(properties, "DURATION");
        if (durationProperty is null)
            return false;
        var startProperty = GetProperty(properties, "DTSTART");
        var endPropertyName = properties[0].ComponentPath[1].Name == "VEVENT" ? "DTEND" : "DUE";
        return startProperty is null
            || GetProperty(properties, endPropertyName) is not null
            || !CalendarDurationArithmetic.TryParse(durationProperty.RawEncodedValue, out var duration)
            || !duration.IsStrictlyPositive
            || startProperty.ValueType == CalendarPropertyValueType.Date
                && durationProperty.RawEncodedValue.Contains('T', StringComparison.Ordinal);
    }

    private static bool HasUnevaluableRecurrenceShape(
        CalendarResourceSnapshot snapshot,
        IReadOnlyList<CalendarProperty> properties,
        IReadOnlyList<CalendarProperty> masterProperties) =>
        HasInvalidComponentStructure(snapshot)
        || properties.Count(property => property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)) > 1
        || properties.Any(HasUnsupportedRange)
        || snapshot.Projection.Kind == CalendarResourceProjectionKind.Todo
            && GetProperty(masterProperties, "RRULE") is not null
            && GetProperty(masterProperties, "DTSTART") is null;

    private static CalendarOccurrenceQueryCode? ValidatePeriodStructure(
        IEnumerable<CalendarProperty> properties,
        CalendarTemporalResolver resolver)
    {
        foreach (var property in properties.Where(property =>
                     property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                     && property.ValueType == CalendarPropertyValueType.Period))
        {
            foreach (var period in property.RawEncodedValue.Split(',', StringSplitOptions.None))
            {
                var code = ValidatePeriod(property, period, resolver);
                if (code is not null)
                    return code;
            }
        }
        return null;
    }

    private static CalendarOccurrenceQueryCode? ValidatePeriod(
        CalendarProperty property,
        string period,
        CalendarTemporalResolver resolver)
    {
        var parts = period.Split('/', StringSplitOptions.None);
        if (parts.Length != 2 || !IsPeriodDateTime(parts[0]))
            return CalendarOccurrenceQueryCode.RecurrenceUnevaluable;
        return CalendarDurationArithmetic.LooksLikeDuration(parts[1])
            ? ValidatePeriodDuration(property, parts, resolver)
            : ValidateExplicitPeriod(property, parts, resolver);
    }

    private static CalendarOccurrenceQueryCode? ValidatePeriodDuration(
        CalendarProperty property,
        IReadOnlyList<string> parts,
        CalendarTemporalResolver resolver)
    {
        if (!CalendarDurationArithmetic.TryParse(parts[1], out var duration) || !duration.IsStrictlyPositive)
            return CalendarOccurrenceQueryCode.RecurrenceUnevaluable;
        var start = resolver.ResolveToken(property, parts[0]);
        if (start.Value is null)
            return ToFailure(start);
        return null;
    }

    private static CalendarOccurrenceQueryCode? ValidateExplicitPeriod(
        CalendarProperty property,
        IReadOnlyList<string> parts,
        CalendarTemporalResolver resolver)
    {
        if (!IsPeriodDateTime(parts[1]))
            return CalendarOccurrenceQueryCode.RecurrenceUnevaluable;
        var start = resolver.ResolveToken(property, parts[0]);
        if (start.Value is null)
            return ToFailure(start);
        var end = resolver.ResolveToken(property, parts[1]);
        if (end.Value is null)
            return ToFailure(end);
        return end.Value <= start.Value ? CalendarOccurrenceQueryCode.RecurrenceUnevaluable : null;
    }

    private static bool IsPeriodDateTime(string raw) => raw.EndsWith('Z')
        ? DateTimeOffset.TryParseExact(
            raw,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _)
        : DateTime.TryParseExact(
            raw,
            "yyyyMMdd'T'HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static CalendarOccurrenceEvaluation MergeOccurrences(
        CalendarOccurrenceEvaluation generated,
        CalendarOccurrenceEvaluation periodDates,
        bool replaceExisting = true)
    {
        var identities = generated.ObservedIdentities is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(generated.ObservedIdentities, StringComparer.Ordinal);
        if (periodDates.ObservedIdentities is not null)
            identities.UnionWith(periodDates.ObservedIdentities);
        if (identities.Count > MaximumEntityOccurrences)
            return Failure(CalendarOccurrenceQueryCode.LimitExhausted, identities.Count);
        var items = generated.Items.ToDictionary(
            item => GetIdentitySortKey(item.RecurrenceIdentity),
            StringComparer.Ordinal);
        foreach (var item in periodDates.Items)
        {
            var key = GetIdentitySortKey(item.RecurrenceIdentity);
            if (replaceExisting || !items.ContainsKey(key))
                items[key] = item;
        }
        return new CalendarOccurrenceEvaluation(
            CalendarOccurrenceQueryCode.Success,
            items.Values.ToArray(),
            identities.Count,
            identities);
    }

    private static CalendarOccurrenceEvaluation EvaluatePeriodDates(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        IReadOnlyList<CalendarProperty> properties,
        OverridePlan overrides,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, CalendarOccurrenceSnapshot>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var excluded = GetExcludedIdentities(properties);
        foreach (var property in properties.Where(property => property.ComponentPath[1].Occurrence == 0
                     && property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                     && property.ValueType == CalendarPropertyValueType.Period))
        {
            foreach (var period in property.RawEncodedValue.Split(',', StringSplitOptions.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = GetPeriodIdentity(property, period);
                if (identity is not null && identities.Add(identity) && identities.Count > MaximumEntityOccurrences)
                    return Failure(CalendarOccurrenceQueryCode.LimitExhausted, identities.Count);
                var evaluated = EvaluatePeriodDate(snapshot, query, resolver, property, period, excluded, overrides);
                if (evaluated.Code is { } code)
                    return Failure(code, identities.Count);
                if (evaluated.Item is not null)
                    items[GetIdentitySortKey(evaluated.Item.RecurrenceIdentity)] = evaluated.Item;
            }
        }
        return new CalendarOccurrenceEvaluation(
            CalendarOccurrenceQueryCode.Success, items.Values.ToArray(), identities.Count, identities);
    }

    private static string? GetPeriodIdentity(CalendarProperty property, string period)
    {
        var separator = period.IndexOf('/');
        return separator < 0
            ? null
            : GetIdentitySortKey(ToTemporalValue(property, period[..separator]));
    }

    private static CalendarOccurrenceEvaluation EvaluateDetachedOverrides(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        IReadOnlyList<CalendarProperty> properties,
        OverridePlan overrides,
        CancellationToken cancellationToken)
    {
        if (overrides.All.Count == 0)
            return new CalendarOccurrenceEvaluation(CalendarOccurrenceQueryCode.Success, [], 0, new HashSet<string>());
        var items = new Dictionary<string, CalendarOccurrenceSnapshot>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var excluded = GetExcludedIdentities(properties);
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var sourceDuration = GetMasterDurationValue(properties);
        var sourceExactDuration = GetMasterExactDuration(master, resolver);
        var nominalDuration = GetNominalMasterDuration(master);
        foreach (var definition in overrides.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceStart = ToTemporalValue(definition.Identity);
            var identityKey = GetIdentitySortKey(sourceStart);
            if (ExceedsEntityLimit(identities, identityKey))
                return Failure(CalendarOccurrenceQueryCode.LimitExhausted, identities.Count);
            if (ShouldSkipDetachedOverride(definition, overrides, excluded, identityKey))
                continue;
            var sourceEnd = ResolveDetachedSourceEnd(
                sourceStart, sourceDuration, sourceExactDuration, nominalDuration, resolver);
            if (CannotUseDetachedSourceEnd(sourceEnd, sourceDuration, sourceExactDuration))
                return Failure(ToFailure(sourceEnd.Instant), identities.Count);
            var evaluated = definition.IsRange
                ? EvaluateRangeOverride(
                    snapshot, query, resolver, definition, sourceStart, sourceEnd.Value, sourceDuration)
                : EvaluateOverride(
                    snapshot, query, resolver, definition, sourceStart, sourceEnd.Value, sourceDuration);
            if (evaluated.Code is { } code)
                return Failure(code, identities.Count);
            if (evaluated.Item is not null)
                items[identityKey] = evaluated.Item;
        }
        return new CalendarOccurrenceEvaluation(
            CalendarOccurrenceQueryCode.Success, items.Values.ToArray(), identities.Count, identities);
    }

    private static bool ShouldSkipDetachedOverride(
        OverrideDefinition definition,
        OverridePlan overrides,
        IReadOnlySet<string> excluded,
        string identityKey) => definition.IsRange && overrides.Individuals.ContainsKey(identityKey)
        || excluded.Contains(identityKey)
        || definition.Cancelled;

    private static CalendarDurationResolution ResolveDetachedSourceEnd(
        CalendarTemporalValue sourceStart,
        string? sourceDuration,
        TimeSpan? sourceExactDuration,
        TimeSpan nominalDuration,
        CalendarTemporalResolver resolver)
    {
        if (sourceExactDuration is not null)
        {
            if (sourceStart.Kind == CalendarTemporalKind.Date)
            {
                var value = AddTemporalOffset(sourceStart, nominalDuration);
                return new(value, resolver.Resolve(ToCalDateTime(value)));
            }
            return ResolveAccurateDuration(sourceStart, sourceExactDuration.Value, resolver);
        }
        if (sourceDuration is null)
        {
            var value = nominalDuration == TimeSpan.Zero
                ? null
                : AddTemporalOffset(sourceStart, nominalDuration);
            return new(value, new ResolvedCalendarInstant(null, false));
        }
        var start = resolver.Resolve(ToCalDateTime(sourceStart));
        return start.Value is null
            ? new CalendarDurationResolution(null, start)
            : CalendarDurationArithmetic.Resolve(sourceStart, start.Value.Value, sourceDuration, resolver);
    }

    private static CalendarDurationResolution ResolveAccurateDuration(
        CalendarTemporalValue start,
        TimeSpan duration,
        CalendarTemporalResolver resolver)
    {
        var resolvedStart = resolver.Resolve(ToCalDateTime(start));
        return resolvedStart.Value is null
            ? new CalendarDurationResolution(null, resolvedStart)
            : CalendarDurationArithmetic.ResolveAccurate(start, resolvedStart.Value.Value, duration, resolver);
    }

    private static bool CannotUseDetachedSourceEnd(
        CalendarDurationResolution sourceEnd,
        string? sourceDuration,
        TimeSpan? sourceExactDuration) => sourceEnd.Instant.Unresolved
        || sourceEnd.Instant.Value is null && (sourceDuration is not null || sourceExactDuration is not null);

    private static bool ExceedsEntityLimit(ISet<string> identities, string identity)
    {
        identities.Add(identity);
        return identities.Count > MaximumEntityOccurrences;
    }

    private static PeriodEvaluation EvaluatePeriodDate(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        CalendarProperty property,
        string period,
        IReadOnlySet<string> excluded,
        OverridePlan overrides)
    {
        var parts = period.Split('/', StringSplitOptions.None);
        if (parts.Length != 2)
            return PeriodEvaluation.Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        var start = resolver.ResolveToken(property, parts[0]);
        if (start.Value is null)
            return PeriodEvaluation.Failure(ToFailure(start));
        var sourceStart = ToTemporalValue(property, parts[0]);
        var resolvedEnd = ResolvePeriodEnd(property, parts[1], sourceStart, start.Value.Value, resolver);
        if (resolvedEnd.Instant.Value is null)
            return PeriodEvaluation.Failure(ToFailure(resolvedEnd.Instant));
        if (excluded.Contains(GetIdentitySortKey(sourceStart)))
            return PeriodEvaluation.NoMatch;
        var sourceDuration = CalendarDurationArithmetic.LooksLikeDuration(parts[1]) ? parts[1] : null;
        var identityKey = GetIdentitySortKey(sourceStart);
        var overridden = EvaluateAppliedOverride(
            snapshot, query, resolver, overrides, identityKey, sourceStart, resolvedEnd.Value, sourceDuration);
        if (overridden is not null)
            return overridden;
        if (!Overlaps(start.Value.Value, resolvedEnd.Instant.Value.Value, query.From, query.To))
            return PeriodEvaluation.NoMatch;
        return PeriodEvaluation.Match(new CalendarOccurrenceSnapshot(
            snapshot,
            sourceStart,
            new CalendarOccurrenceTiming(
                sourceStart,
                sourceStart,
                resolvedEnd.Value,
                resolvedEnd.Value,
                SourceDuration: sourceDuration,
                EffectiveDuration: sourceDuration,
                EvaluatedStartUtc: ToUtcValue(start.Value.Value),
                EvaluatedEndUtc: ToUtcValue(resolvedEnd.Instant.Value.Value),
                EvaluationTimeZone: query.EvaluationTimeZone)));
    }

    private static CalendarDurationResolution ResolvePeriodEnd(
        CalendarProperty property,
        string rawEnd,
        CalendarTemporalValue sourceStart,
        DateTimeOffset start,
        CalendarTemporalResolver resolver)
    {
        if (CalendarDurationArithmetic.LooksLikeDuration(rawEnd))
            return CalendarDurationArithmetic.Resolve(sourceStart, start, rawEnd, resolver);
        return new(ToTemporalValue(property, rawEnd), resolver.ResolveToken(property, rawEnd));
    }

    private static IReadOnlySet<string> GetExcludedIdentities(IReadOnlyList<CalendarProperty> properties) => properties
        .Where(property => property.ComponentPath[1].Occurrence == 0
            && property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
        .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
            .Select(value => GetIdentitySortKey(ToTemporalValue(property, value))))
        .ToHashSet(StringComparer.Ordinal);

    private static CalendarOccurrenceEvaluation EvaluateEvent(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        CalendarEvent master,
        IReadOnlyList<CalendarProperty> properties,
        OverridePlan overrides,
        CancellationToken cancellationToken) => IsRecurring(properties)
            ? EvaluatePeriods(
                snapshot,
                query,
                resolver,
                GetRecurringOccurrences(master, properties, resolver, query),
                overrides,
                GetEvaluationBounds(properties, resolver, query).StopAt,
                false,
                GetMasterDurationValue(properties),
                GetMasterExactDuration(properties, resolver),
                cancellationToken)
            : EvaluateNonRecurringEvent(snapshot, query, resolver, properties);

    private static CalendarOccurrenceEvaluation EvaluateNonRecurringEvent(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        IReadOnlyList<CalendarProperty> properties)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var startProperty = GetProperty(master, "DTSTART")!;
        var start = resolver.Resolve(startProperty);
        if (start.Value is null)
            return Failure(ToFailure(start));
        var sourceStart = ToTemporalValue(startProperty);
        var endProperty = GetProperty(master, "DTEND");
        var durationProperty = GetProperty(master, "DURATION");
        var sourceEnd = ResolveNonRecurringEventEnd(
            startProperty, endProperty, durationProperty, resolver, start.Value.Value);
        if (sourceEnd.Instant is null)
            return Failure(sourceEnd.Code);
        if (!Overlaps(start.Value.Value, sourceEnd.Instant.Value, query.From, query.To))
            return new CalendarOccurrenceEvaluation(
                CalendarOccurrenceQueryCode.Success, [], 1, new HashSet<string> { GetIdentitySortKey(sourceStart) });
        var endValue = sourceEnd.Value;
        return new CalendarOccurrenceEvaluation(
            CalendarOccurrenceQueryCode.Success,
            [new CalendarOccurrenceSnapshot(
                snapshot,
                sourceStart,
                new CalendarOccurrenceTiming(
                    sourceStart,
                    sourceStart,
                    endValue,
                    endValue,
                    SourceDuration: durationProperty?.RawEncodedValue,
                    EffectiveDuration: durationProperty?.RawEncodedValue,
                    EvaluatedStartUtc: ToUtcValue(start.Value.Value),
                    EvaluatedEndUtc: ToUtcValue(sourceEnd.Instant.Value),
                    EvaluationTimeZone: query.EvaluationTimeZone))],
            1,
            new HashSet<string> { GetIdentitySortKey(sourceStart) });
    }

    private static OverrideEndResolution ResolveNonRecurringEventEnd(
        CalendarProperty startProperty,
        CalendarProperty? endProperty,
        CalendarProperty? durationProperty,
        CalendarTemporalResolver resolver,
        DateTimeOffset start)
    {
        if (endProperty is not null)
        {
            var end = resolver.Resolve(endProperty);
            return end.Value is null
                ? new OverrideEndResolution(null, null, ToFailure(end))
                : new OverrideEndResolution(ToTemporalValue(endProperty), end.Value, CalendarOccurrenceQueryCode.Success);
        }
        if (durationProperty is not null)
            return FromDuration(CalendarDurationArithmetic.Resolve(
                ToTemporalValue(startProperty), start, durationProperty.RawEncodedValue, resolver));
        if (startProperty.ValueType != CalendarPropertyValueType.Date)
            return new OverrideEndResolution(null, start, CalendarOccurrenceQueryCode.Success);
        return FromDuration(CalendarDurationArithmetic.Resolve(
            ToTemporalValue(startProperty), start, "P1D", resolver));
    }

    private static CalendarOccurrenceEvaluation EvaluateTodo(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        Todo master,
        IReadOnlyList<CalendarProperty> properties,
        OverridePlan overrides,
        CancellationToken cancellationToken) => EvaluatePeriods(
            snapshot,
            query,
            resolver,
            IsRecurring(properties)
                ? GetRecurringOccurrences(master, properties, resolver, query)
                : CreateNonRecurringTodoOccurrence(properties),
            overrides,
            GetEvaluationBounds(properties, resolver, query).StopAt,
            !IsRecurring(properties) && IsPointTodo(properties),
            GetMasterDurationValue(properties),
            GetMasterExactDuration(properties, resolver),
            cancellationToken);

    private static string? GetMasterDurationValue(IReadOnlyList<CalendarProperty> properties) =>
        GetProperty(properties.Where(property => property.ComponentPath[1].Occurrence == 0), "DURATION")?.RawEncodedValue;

    private static TimeSpan? GetMasterExactDuration(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        if (GetProperty(master, "DURATION") is not null)
            return null;
        var start = resolver.Resolve(GetProperty(master, "DTSTART")).Value;
        var end = resolver.Resolve(GetProperty(master, "DTEND") ?? GetProperty(master, "DUE")).Value;
        return start is not null && end > start ? end.Value - start.Value : null;
    }

    private static bool IsPointTodo(IReadOnlyList<CalendarProperty> properties)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        return GetProperty(master, "DURATION") is null
            && (GetProperty(master, "DTSTART") is null || GetProperty(master, "DUE") is null);
    }

    private static IEnumerable<Occurrence> CreateNonRecurringTodoOccurrence(
        IReadOnlyList<CalendarProperty> properties)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var start = GetProperty(master, "DTSTART");
        var due = GetProperty(master, "DUE");
        var identity = start ?? due;
        if (identity is null)
            return [];

        var typedStart = ToCalDateTime(identity);
        var end = due is not null && start is not null
            ? ToCalDateTime(due)
            : AddDuration(typedStart, GetProperty(master, "DURATION"));
        return [new Occurrence(null!, new Period(typedStart, end))];
    }

    private static bool IsRecurring(IReadOnlyList<CalendarProperty> properties) => properties.Any(property =>
        property.Name is "RRULE" or "RDATE" or "EXDATE" or "RECURRENCE-ID");

    private static IEnumerable<Occurrence> GetRecurringOccurrences(
        RecurringComponent master,
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CalendarOccurrenceQuery query)
    {
        var searchStart = GetSearchStart(properties, resolver, query);
        var masterProperties = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var identity = GetProperty(masterProperties, "DTSTART") ?? GetProperty(masterProperties, "DUE");
        var timeZoneId = identity?.Parameters
            .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault();
        var usesEvaluationZone = timeZoneId is null
            && identity is not null
            && (identity.ValueType == CalendarPropertyValueType.Date
                || !identity.RawEncodedValue.EndsWith('Z'))
            && query.EvaluationTimeZone is not null;
        return timeZoneId is not null || usesEvaluationZone
            ? CreateLocalOccurrences(properties, resolver, query, timeZoneId)
            : master.GetOccurrences(
                searchStart,
                new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumUnmatchedIncrements });
    }

    private static IEnumerable<Occurrence> CreateLocalOccurrences(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CalendarOccurrenceQuery query,
        string? timeZoneId)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var identity = GetProperty(master, "DTSTART") ?? GetProperty(master, "DUE")
            ?? throw new InvalidOperationException("A recurring component has no temporal identity.");
        var start = ToCalDateTime(identity);
        var nominalStart = new CalDateTime(start.Value);
        var ruleProperty = GetProperty(master, "RRULE");
        var ruleStarts = ruleProperty is null
            ? [nominalStart]
            : CreateLocalRuleStarts(
                ruleProperty.RawEncodedValue,
                nominalStart,
                timeZoneId,
                resolver,
                GetEvaluationBounds(properties, resolver, query).SearchFrom);
        var recurrenceDates = master.Where(property => property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                && property.ValueType != CalendarPropertyValueType.Period)
            .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
                .Select(value => new CalDateTime(ToCalDateTime(property, value).Value)))
            .OrderBy(value => value.Value)
            .ToArray();
        var excluded = GetExcludedIdentities(properties);
        var duration = GetNominalMasterDuration(master);
        return MergeNominalStarts(ruleStarts, recurrenceDates)
            .Select(value => CreateOccurrenceStart(value.Value, identity, timeZoneId))
            .Where(value => !excluded.Contains(GetIdentitySortKey(ToTemporalValue(value))))
            .Select(value => new Occurrence(null!, new Period(
                value,
                CreateOccurrenceStart(value.Value.Add(duration), identity, timeZoneId))));
    }

    private static CalDateTime CreateOccurrenceStart(
        DateTime value,
        CalendarProperty identity,
        string? timeZoneId) => identity.ValueType == CalendarPropertyValueType.Date
            ? new CalDateTime(value.Year, value.Month, value.Day)
            : CreateLocalDateTime(value, timeZoneId);

    private static IEnumerable<CalDateTime> CreateLocalRuleStarts(
        string rawRule,
        CalDateTime nominalStart,
        string? timeZoneId,
        CalendarTemporalResolver resolver,
        DateTimeOffset searchFrom)
    {
        var pattern = new RecurrenceRule(rawRule);
        var count = pattern.Count;
        var evaluationPattern = count is null
            ? pattern
            : new RecurrenceRule(string.Join(
                ';',
                rawRule.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !part.StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase))));
        var periodStart = count is null
            ? new CalDateTime(Max(nominalStart.Value, SubtractSafely(searchFrom, TimeSpan.FromDays(2)).UtcDateTime))
            : nominalStart;
        var starts = new RecurrencePatternEvaluator(evaluationPattern)
                .Evaluate(
                    nominalStart,
                    periodStart,
                    new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumUnmatchedIncrements })
                .Select(period => period.StartTime);
        return TakeValidLocalStarts(starts, nominalStart, timeZoneId, resolver, count);
    }

    private static IEnumerable<CalDateTime> TakeValidLocalStarts(
        IEnumerable<CalDateTime> starts,
        CalDateTime nominalStart,
        string? timeZoneId,
        CalendarTemporalResolver resolver,
        int? count)
    {
        var accepted = 0;
        foreach (var start in starts)
        {
            var local = CreateLocalDateTime(start.Value, timeZoneId);
            var generated = start.Value != nominalStart.Value;
            if (resolver.Resolve(local, generated).Skipped)
                continue;
            yield return start;
            accepted++;
            if (count is not null && accepted >= count)
                yield break;
        }
    }

    private static CalDateTime CreateLocalDateTime(DateTime value, string? timeZoneId) =>
        timeZoneId is null ? new CalDateTime(value) : new CalDateTime(value, timeZoneId);

    private static IEnumerable<CalDateTime> MergeNominalStarts(
        IEnumerable<CalDateTime> ruleStarts,
        IReadOnlyList<CalDateTime> recurrenceDates)
    {
        using var rules = ruleStarts.GetEnumerator();
        var hasRule = rules.MoveNext();
        var dateIndex = 0;
        CalDateTime? previous = null;
        while (hasRule || dateIndex < recurrenceDates.Count)
        {
            var next = !hasRule || dateIndex < recurrenceDates.Count
                    && recurrenceDates[dateIndex].Value < rules.Current.Value
                ? recurrenceDates[dateIndex++]
                : rules.Current;
            if (hasRule && next == rules.Current)
                hasRule = rules.MoveNext();
            if (previous is null || next.Value != previous.Value)
                yield return next;
            previous = next;
        }
    }

    private static TimeSpan GetNominalMasterDuration(IReadOnlyList<CalendarProperty> properties)
    {
        var duration = GetProperty(properties, "DURATION");
        if (duration is not null)
            return CalendarDurationArithmetic.TryParse(duration.RawEncodedValue, out var parsed)
                && parsed.IsStrictlyPositive
                ? parsed.LocalClockDuration
                : throw new FormatException("The DURATION value is invalid.");
        var start = GetProperty(properties, "DTSTART") ?? GetProperty(properties, "DUE")!;
        var end = GetProperty(properties, "DTEND") ?? GetProperty(properties, "DUE");
        if (end is not null && end != start)
            return GetNominalDifference(ToTemporalValue(start), ToTemporalValue(end));
        return start.ValueType == CalendarPropertyValueType.Date ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private static CalDateTime GetSearchStart(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CalendarOccurrenceQuery query)
    {
        var master = properties.Where(property => property.ComponentPath[1].Occurrence == 0).ToArray();
        var identity = GetProperty(master, "DTSTART") ?? GetProperty(master, "DUE")
            ?? throw new InvalidOperationException("A recurring component has no temporal identity.");
        var masterStart = ToCalDateTime(identity);
        return masterStart.TzId is { Length: > 0 } timeZoneId && resolver.HasResourceLocalDefinition(timeZoneId)
            ? masterStart
            : new CalDateTime(GetEvaluationBounds(properties, resolver, query).SearchFrom.UtcDateTime);
    }

    private static CalDateTime AddDuration(CalDateTime start, CalendarProperty? property)
    {
        if (property is null)
            return start;
        if (!CalendarDurationArithmetic.TryParse(property.RawEncodedValue, out var duration)
            || !duration.IsStrictlyPositive)
        {
            throw new FormatException("The DURATION value is invalid.");
        }
        if (start.HasTime || duration.Accurate != TimeSpan.Zero)
            return start.Add(Duration.FromTimeSpanExact(duration.LocalClockDuration));
        var end = start.Value.AddDays(duration.NominalDays);
        return new CalDateTime(end.Year, end.Month, end.Day);
    }

    private static CalDateTime ToCalDateTime(CalendarProperty property)
        => ToCalDateTime(property, property.RawEncodedValue);

    private static CalDateTime ToCalDateTime(CalendarProperty property, string rawValue)
    {
        var timeZoneId = property.Parameters
            .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault();
        if (property.ValueType == CalendarPropertyValueType.Date)
        {
            var date = DateTime.ParseExact(rawValue, "yyyyMMdd", CultureInfo.InvariantCulture);
            return new CalDateTime(date.Year, date.Month, date.Day);
        }
        if (rawValue.EndsWith('Z'))
        {
            var utc = DateTime.ParseExact(
                rawValue,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return new CalDateTime(utc);
        }
        var local = DateTime.ParseExact(rawValue, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        return timeZoneId is null ? new CalDateTime(local) : new CalDateTime(local, timeZoneId);
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

    private static CalendarOccurrenceEvaluation EvaluatePeriods(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        IEnumerable<Occurrence> periods,
        OverridePlan overrides,
        DateTimeOffset stopAt,
        bool pointEndIsImplicit,
        string? sourceDuration,
        TimeSpan? sourceExactDuration,
        CancellationToken cancellationToken)
    {
        var items = new List<CalendarOccurrenceSnapshot>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in periods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = resolver.Resolve(occurrence.Period.StartTime);
            if (start.Value is null)
                return Failure(ToFailure(start), identities.Count);
            if (start.Value >= stopAt)
                break;
            identities.Add(GetIdentitySortKey(ToTemporalValue(occurrence.Period.StartTime)));
            if (identities.Count > MaximumEntityOccurrences)
                return Failure(CalendarOccurrenceQueryCode.LimitExhausted, identities.Count);
            var evaluated = EvaluatePeriod(
                snapshot,
                query,
                resolver,
                occurrence,
                overrides,
                start.Value.Value,
                pointEndIsImplicit,
                sourceDuration,
                sourceExactDuration);
            if (evaluated.Code is { } code)
                return Failure(code, identities.Count);
            if (evaluated.Item is not null)
                items.Add(evaluated.Item);
        }
        return new CalendarOccurrenceEvaluation(
            CalendarOccurrenceQueryCode.Success, items, identities.Count, identities);
    }

    private static PeriodEvaluation EvaluatePeriod(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        Occurrence occurrence,
        OverridePlan overrides,
        DateTimeOffset resolvedStart,
        bool pointEndIsImplicit,
        string? sourceDuration,
        TimeSpan? sourceExactDuration)
    {
        var sourceStart = ToTemporalValue(occurrence.Period.StartTime);
        var end = ResolveOccurrenceEnd(
            occurrence,
            pointEndIsImplicit,
            sourceDuration,
            sourceExactDuration,
            sourceStart,
            resolvedStart,
            resolver);
        if (end.Instant.Value is null)
            return PeriodEvaluation.Failure(ToFailure(end.Instant));
        var sourceEnd = sourceExactDuration is not null
            && sourceStart.Kind == CalendarTemporalKind.Date
            && occurrence.Period.EffectiveEndTime is not null
                ? ToTemporalValue(occurrence.Period.EffectiveEndTime)
                : end.Value;
        var identityKey = GetIdentitySortKey(sourceStart);
        var overridden = EvaluateAppliedOverride(
            snapshot, query, resolver, overrides, identityKey, sourceStart, sourceEnd, sourceDuration);
        if (overridden is not null)
            return overridden;
        if (!Overlaps(resolvedStart, end.Instant.Value.Value, query.From, query.To))
            return PeriodEvaluation.NoMatch;

        var evaluatedStart = ToUtcValue(resolvedStart);
        var evaluatedEnd = end.Value is null ? null : ToUtcValue(end.Instant.Value.Value);
        return PeriodEvaluation.Match(new CalendarOccurrenceSnapshot(
            snapshot,
            sourceStart,
            new CalendarOccurrenceTiming(
                sourceStart,
                sourceStart,
                sourceEnd,
                end.Value,
                SourceDuration: sourceDuration,
                EffectiveDuration: sourceDuration,
                EvaluatedStartUtc: evaluatedStart,
                EvaluatedEndUtc: evaluatedEnd,
                EvaluationTimeZone: query.EvaluationTimeZone)));
    }

    private static CalendarDurationResolution ResolveOccurrenceEnd(
        Occurrence occurrence,
        bool pointEndIsImplicit,
        string? sourceDuration,
        TimeSpan? sourceExactDuration,
        CalendarTemporalValue sourceStart,
        DateTimeOffset start,
        CalendarTemporalResolver resolver)
    {
        if (sourceDuration is not null)
            return CalendarDurationArithmetic.Resolve(sourceStart, start, sourceDuration, resolver);
        if (sourceExactDuration is not null)
            return CalendarDurationArithmetic.ResolveAccurate(
                sourceStart, start, sourceExactDuration.Value, resolver);
        var endTime = pointEndIsImplicit ? null : occurrence.Period.EffectiveEndTime;
        return endTime is null
            ? new CalendarDurationResolution(null, new ResolvedCalendarInstant(start, false))
            : new CalendarDurationResolution(ToTemporalValue(endTime), resolver.Resolve(endTime));
    }

    private static PeriodEvaluation? EvaluateAppliedOverride(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        OverridePlan overrides,
        string identityKey,
        CalendarTemporalValue sourceStart,
        CalendarTemporalValue? sourceEnd,
        string? sourceDuration = null)
    {
        if (overrides.Individuals.TryGetValue(identityKey, out var definition))
            return EvaluateOverride(snapshot, query, resolver, definition, sourceStart, sourceEnd, sourceDuration);
        var range = FindRange(overrides, identityKey);
        return range is null
            ? null
            : EvaluateRangeOverride(snapshot, query, resolver, range, sourceStart, sourceEnd, sourceDuration);
    }

    private static PeriodEvaluation EvaluateOverride(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        OverrideDefinition definition,
        CalendarTemporalValue sourceStart,
        CalendarTemporalValue? sourceEnd,
        string? sourceDuration = null)
    {
        if (definition.Cancelled)
            return PeriodEvaluation.NoMatch;
        if (!resolver.CanResolve(definition.Identity))
            return PeriodEvaluation.Failure(CalendarOccurrenceQueryCode.TemporalUnresolved);
        if (definition.Start is null)
            return PeriodEvaluation.Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        var resolvedStart = resolver.Resolve(definition.Start);
        if (resolvedStart.Value is null)
            return PeriodEvaluation.Failure(ToFailure(resolvedStart));
        var effectiveStart = ToTemporalValue(definition.Start);
        var resolvedEnd = ResolveOverrideEnd(definition, effectiveStart, resolver, resolvedStart.Value.Value);
        if (resolvedEnd.Instant is null)
            return PeriodEvaluation.Failure(resolvedEnd.Code);
        if (!Overlaps(resolvedStart.Value.Value, resolvedEnd.Instant.Value, query.From, query.To))
            return PeriodEvaluation.NoMatch;

        var effectiveEnd = resolvedEnd.Value;
        return PeriodEvaluation.Match(new CalendarOccurrenceSnapshot(
            snapshot,
            sourceStart,
            new CalendarOccurrenceTiming(
                sourceStart,
                effectiveStart,
                sourceEnd,
                effectiveEnd,
                SourceDuration: sourceDuration,
                EffectiveDuration: definition.Duration?.RawEncodedValue,
                EvaluatedStartUtc: ToUtcValue(resolvedStart.Value.Value),
                EvaluatedEndUtc: effectiveEnd is null ? null : ToUtcValue(resolvedEnd.Instant.Value),
                EvaluationTimeZone: query.EvaluationTimeZone)));
    }

    private static PeriodEvaluation EvaluateRangeOverride(
        CalendarResourceSnapshot snapshot,
        CalendarOccurrenceQuery query,
        CalendarTemporalResolver resolver,
        OverrideDefinition definition,
        CalendarTemporalValue sourceStart,
        CalendarTemporalValue? sourceEnd,
        string? sourceDuration = null)
    {
        if (definition.Cancelled)
            return PeriodEvaluation.NoMatch;
        if (!resolver.CanResolve(definition.Identity))
            return PeriodEvaluation.Failure(CalendarOccurrenceQueryCode.TemporalUnresolved);
        if (definition.Start is null)
            return PeriodEvaluation.Failure(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        var anchorIdentity = ToTemporalValue(definition.Identity);
        var anchorStart = ToTemporalValue(definition.Start);
        var effectiveStart = AddTemporalOffset(sourceStart, GetNominalDifference(anchorIdentity, anchorStart));
        var resolvedStart = resolver.Resolve(ToCalDateTime(effectiveStart));
        if (resolvedStart.Value is null)
            return PeriodEvaluation.Failure(ToFailure(resolvedStart));
        var resolvedEnd = ResolveRangeEnd(definition, effectiveStart, resolvedStart.Value.Value, resolver);
        if (resolvedEnd.Instant.Value is null)
            return PeriodEvaluation.Failure(ToFailure(resolvedEnd.Instant));
        if (!Overlaps(resolvedStart.Value.Value, resolvedEnd.Instant.Value.Value, query.From, query.To))
            return PeriodEvaluation.NoMatch;
        return PeriodEvaluation.Match(new CalendarOccurrenceSnapshot(
            snapshot,
            sourceStart,
            new CalendarOccurrenceTiming(
                sourceStart,
                effectiveStart,
                sourceEnd,
                resolvedEnd.Value,
                SourceDuration: sourceDuration,
                EffectiveDuration: definition.Duration?.RawEncodedValue,
                EvaluatedStartUtc: ToUtcValue(resolvedStart.Value.Value),
                EvaluatedEndUtc: resolvedEnd.Value is null ? null : ToUtcValue(resolvedEnd.Instant.Value.Value),
                EvaluationTimeZone: query.EvaluationTimeZone)));
    }

    private static OverrideDefinition? FindRange(OverridePlan overrides, string identityKey) =>
        overrides.Ranges.LastOrDefault(candidate =>
            string.CompareOrdinal(GetIdentitySortKey(ToTemporalValue(candidate.Identity)), identityKey) <= 0);

    private static CalendarDurationResolution ResolveRangeEnd(
        OverrideDefinition definition,
        CalendarTemporalValue effectiveStart,
        DateTimeOffset resolvedStart,
        CalendarTemporalResolver resolver)
    {
        if (definition.Duration is not null)
            return CalendarDurationArithmetic.Resolve(
                effectiveStart, resolvedStart, definition.Duration.RawEncodedValue, resolver);
        if (definition.End is not null)
        {
            var anchorStart = resolver.Resolve(definition.Start);
            var anchorEnd = resolver.Resolve(definition.End);
            if (anchorStart.Value is null || anchorEnd.Value is null)
                return new(null, anchorStart.Value is null ? anchorStart : anchorEnd);
            return CalendarDurationArithmetic.ResolveAccurate(
                effectiveStart,
                resolvedStart,
                anchorEnd.Value.Value - anchorStart.Value.Value,
                resolver);
        }
        return definition.DateEventDefaultsToOneDay
            ? CalendarDurationArithmetic.Resolve(effectiveStart, resolvedStart, "P1D", resolver)
            : new CalendarDurationResolution(null, new ResolvedCalendarInstant(resolvedStart, false));
    }

    private static OverrideEndResolution ResolveOverrideEnd(
        OverrideDefinition definition,
        CalendarTemporalValue effectiveStart,
        CalendarTemporalResolver resolver,
        DateTimeOffset start)
    {
        if (definition.End is not null)
        {
            var end = resolver.Resolve(definition.End);
            return end.Value is null
                ? new OverrideEndResolution(null, null, ToFailure(end))
                : new OverrideEndResolution(ToTemporalValue(definition.End), end.Value, CalendarOccurrenceQueryCode.Success);
        }
        if (definition.Duration is null)
            return definition.DateEventDefaultsToOneDay
                ? FromDuration(CalendarDurationArithmetic.Resolve(effectiveStart, start, "P1D", resolver))
                : new OverrideEndResolution(null, start, CalendarOccurrenceQueryCode.Success);
        return FromDuration(CalendarDurationArithmetic.Resolve(
            effectiveStart, start, definition.Duration.RawEncodedValue, resolver));
    }

    private static OverrideEndResolution FromDuration(CalendarDurationResolution duration) =>
        duration.Instant.Value is null
            ? new OverrideEndResolution(null, null, ToFailure(duration.Instant))
            : new OverrideEndResolution(
                duration.Value, duration.Instant.Value, CalendarOccurrenceQueryCode.Success);

    private static CalendarTemporalValue AddTemporalOffset(CalendarTemporalValue value, TimeSpan offset)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var parsed = DateTime.ParseExact(value.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture).Add(offset);
        var suffix = value.Kind == CalendarTemporalKind.UtcDateTime ? "Z" : string.Empty;
        return value with { Value = parsed.ToString(format, CultureInfo.InvariantCulture) + suffix };
    }

    private static TimeSpan GetNominalDifference(CalendarTemporalValue identity, CalendarTemporalValue movedStart)
    {
        var format = identity.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var original = DateTime.ParseExact(identity.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture);
        var moved = DateTime.ParseExact(movedStart.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture);
        return moved - original;
    }

    private static OverridePlan CreateOverrides(
        IReadOnlyList<CalendarProperty> properties,
        CalendarResourceProjectionKind kind)
    {
        var definitions = properties.Where(property => property.ComponentPath[1].Occurrence > 0)
            .GroupBy(property => property.ComponentPath[1].Occurrence)
            .Select(group => CreateOverride(group.ToArray(), kind))
            .Where(definition => definition is not null)
            .Cast<OverrideDefinition>()
            .ToArray();
        var individuals = definitions.Where(definition => !definition.IsRange).ToDictionary(
            definition => GetIdentitySortKey(ToTemporalValue(definition.Identity)),
            StringComparer.Ordinal);
        var ranges = definitions.Where(definition => definition.IsRange)
            .OrderBy(definition => GetIdentitySortKey(ToTemporalValue(definition.Identity)), StringComparer.Ordinal)
            .ToArray();
        return new OverridePlan(individuals, ranges, definitions);
    }

    private static OverrideDefinition? CreateOverride(
        IReadOnlyList<CalendarProperty> properties,
        CalendarResourceProjectionKind kind)
    {
        var identity = GetProperty(properties, "RECURRENCE-ID");
        var explicitStart = GetProperty(properties, "DTSTART");
        var due = GetProperty(properties, "DUE");
        var start = GetOverrideStart(kind, explicitStart, due);
        var cancelled = IsCancelled(properties);
        if (identity is null || start is null && !cancelled)
            return null;
        var range = identity.Parameters
            .Where(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault();
        return new OverrideDefinition(
            identity,
            start,
            GetProperty(properties, "DTEND") ?? (explicitStart is null ? null : due),
            GetProperty(properties, "DURATION"),
            cancelled,
            range is not null,
            IsDefaultDateEvent(kind, start));
    }

    private static CalendarProperty? GetOverrideStart(
        CalendarResourceProjectionKind kind,
        CalendarProperty? explicitStart,
        CalendarProperty? due) => explicitStart
            ?? (kind == CalendarResourceProjectionKind.Todo ? due : null);

    private static bool IsCancelled(IEnumerable<CalendarProperty> properties) =>
        GetProperty(properties, "STATUS")?.RawEncodedValue.Equals(
            "CANCELLED", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDefaultDateEvent(
        CalendarResourceProjectionKind kind,
        CalendarProperty? start) => kind == CalendarResourceProjectionKind.Event
            && start?.ValueType == CalendarPropertyValueType.Date;

    private static EvaluationBounds GetEvaluationBounds(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver,
        CalendarOccurrenceQuery query)
    {
        var maximumShift = properties.Where(property => property.ComponentPath[1].Occurrence > 0)
            .GroupBy(property => property.ComponentPath[1].Occurrence)
            .Select(group => GetOverrideShift(group.ToArray(), resolver))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        var maximumDuration = properties.GroupBy(property => property.ComponentPath[1].Occurrence)
            .Select(group => GetDuration(group.ToArray(), resolver))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        maximumDuration = Max(maximumDuration, GetMaximumPeriodDuration(properties, resolver));
        return new EvaluationBounds(
            SubtractSafely(query.From, maximumShift + maximumDuration),
            AddSafely(query.To, maximumShift));
    }

    private static TimeSpan GetOverrideShift(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver)
    {
        var identity = resolver.Resolve(GetProperty(properties, "RECURRENCE-ID")).Value;
        var start = resolver.Resolve(
            GetProperty(properties, "DTSTART") ?? GetProperty(properties, "DUE")).Value;
        return identity is null || start is null ? TimeSpan.Zero : (start.Value - identity.Value).Duration();
    }

    private static TimeSpan GetDuration(
        IReadOnlyList<CalendarProperty> properties,
        CalendarTemporalResolver resolver)
    {
        var startProperty = GetProperty(properties, "DTSTART");
        var start = resolver.Resolve(startProperty).Value;
        var end = resolver.Resolve(GetProperty(properties, "DTEND") ?? GetProperty(properties, "DUE")).Value;
        if (start is not null && end > start)
            return end.Value - start.Value;
        var duration = GetProperty(properties, "DURATION");
        var rawDuration = duration?.RawEncodedValue ?? GetDefaultEventDuration(properties, startProperty);
        if (start is null || startProperty is null || rawDuration is null)
            return TimeSpan.Zero;
        var resolved = CalendarDurationArithmetic.Resolve(
            ToTemporalValue(startProperty), start.Value, rawDuration, resolver).Instant.Value;
        return resolved is null ? TimeSpan.Zero : (resolved.Value - start.Value).Duration();
    }

    private static string? GetDefaultEventDuration(
        IReadOnlyList<CalendarProperty> properties,
        CalendarProperty? start) => start?.ValueType == CalendarPropertyValueType.Date
        && properties.Any(property => property.ComponentPath[1].Name.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            ? "P1D"
            : null;

    private static TimeSpan GetMaximumPeriodDuration(
        IEnumerable<CalendarProperty> properties,
        CalendarTemporalResolver resolver) => properties
        .Where(property => property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
            && property.ValueType == CalendarPropertyValueType.Period)
        .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
            .Select(period => GetPeriodDuration(property, period, resolver)))
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();

    private static TimeSpan GetPeriodDuration(
        CalendarProperty property,
        string period,
        CalendarTemporalResolver resolver)
    {
        var parts = period.Split('/', StringSplitOptions.None);
        if (parts.Length != 2)
            return TimeSpan.Zero;
        if (CalendarDurationArithmetic.LooksLikeDuration(parts[1]))
        {
            var startValue = resolver.ResolveToken(property, parts[0]).Value;
            if (startValue is null)
                return TimeSpan.Zero;
            var resolved = CalendarDurationArithmetic.Resolve(
                ToTemporalValue(property, parts[0]), startValue.Value, parts[1], resolver).Instant.Value;
            return resolved is null ? TimeSpan.Zero : (resolved.Value - startValue.Value).Duration();
        }
        var start = resolver.ResolveToken(property, parts[0]).Value;
        var end = resolver.ResolveToken(property, parts[1]).Value;
        return start is not null && end > start ? end.Value - start.Value : TimeSpan.Zero;
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;

    private static DateTimeOffset SubtractSafely(DateTimeOffset value, TimeSpan offset) =>
        value - DateTimeOffset.MinValue < offset ? DateTimeOffset.MinValue : value - offset;

    private static DateTimeOffset AddSafely(DateTimeOffset value, TimeSpan offset) =>
        DateTimeOffset.MaxValue - value < offset ? DateTimeOffset.MaxValue : value + offset;

    private static CalendarOccurrenceQueryCode ToFailure(ResolvedCalendarInstant instant) => instant.Unresolved
        ? CalendarOccurrenceQueryCode.TemporalUnresolved
        : CalendarOccurrenceQueryCode.RecurrenceUnevaluable;

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to) =>
        end > start ? start < to && end > from : start >= from && start < to;

    private static CalendarTemporalValue ToTemporalValue(CalDateTime value)
    {
        if (!value.HasTime)
            return new(CalendarTemporalKind.Date, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var localValue = value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (value.IsUtc)
            return new(CalendarTemporalKind.UtcDateTime, localValue + "Z");
        return value.TzId is { Length: > 0 } timeZoneId
            ? new CalendarTemporalValue(CalendarTemporalKind.ZonedDateTime, localValue, timeZoneId)
            : new CalendarTemporalValue(CalendarTemporalKind.FloatingDateTime, localValue);
    }

    private static CalendarTemporalValue ToTemporalValue(CalendarProperty property) =>
        ToTemporalValue(ToCalDateTime(property));

    private static CalendarTemporalValue ToTemporalValue(CalendarProperty property, string rawValue) =>
        ToTemporalValue(ToCalDateTime(property, rawValue));

    private static CalendarTemporalValue ToUtcValue(DateTimeOffset value) => new(
        CalendarTemporalKind.UtcDateTime,
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static CalendarProperty[] GetEntityProperties(CalendarResourceSnapshot snapshot)
    {
        var componentName = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event ? "VEVENT" : "VTODO";
        return snapshot.CalendarProperties.Where(property => property.ComponentPath.Count == 2
            && property.ComponentPath[1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool IsTemporalProperty(CalendarProperty property) => property.Name is
        "DTSTART" or "DTEND" or "DUE" or "RDATE" or "EXDATE" or "RECURRENCE-ID";

    private static bool RequiresEagerResolution(CalendarProperty property) =>
        IsTemporalProperty(property)
        && !property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase)
        && property.ValueType != CalendarPropertyValueType.Period;

    private static bool HasUnsupportedRange(CalendarProperty property) =>
        property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase)
        && property.Parameters.Where(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .Any(value => !value.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase));

    private static CalendarProperty? GetProperty(IEnumerable<CalendarProperty> properties, string name) =>
        properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static CalendarOccurrenceEvaluation Failure(
        CalendarOccurrenceQueryCode code,
        int observed = 0) => new(code, [], observed);

    private sealed record PeriodEvaluation(
        CalendarOccurrenceQueryCode? Code,
        CalendarOccurrenceSnapshot? Item)
    {
        public static PeriodEvaluation NoMatch { get; } = new(null, null);
        public static PeriodEvaluation Failure(CalendarOccurrenceQueryCode code) => new(code, null);
        public static PeriodEvaluation Match(CalendarOccurrenceSnapshot item) => new(null, item);
    }

    private sealed record OverrideDefinition(
        CalendarProperty Identity,
        CalendarProperty? Start,
        CalendarProperty? End,
        CalendarProperty? Duration,
        bool Cancelled,
        bool IsRange,
        bool DateEventDefaultsToOneDay);

    private sealed record OverridePlan(
        IReadOnlyDictionary<string, OverrideDefinition> Individuals,
        IReadOnlyList<OverrideDefinition> Ranges,
        IReadOnlyList<OverrideDefinition> All);

    private readonly record struct OverrideEndResolution(
        CalendarTemporalValue? Value,
        DateTimeOffset? Instant,
        CalendarOccurrenceQueryCode Code);

    private readonly record struct EvaluationBounds(DateTimeOffset SearchFrom, DateTimeOffset StopAt);
}
