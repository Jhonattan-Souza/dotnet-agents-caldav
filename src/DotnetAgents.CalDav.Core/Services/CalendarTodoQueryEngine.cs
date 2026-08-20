using System.Collections.Frozen;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using NodaTime;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Builds compact To-do results from the existing bounded entity and occurrence seams.</summary>
internal sealed class CalendarTodoQueryEngine
{
    private static readonly IReadOnlyList<CalendarTodoCompletionState> DefaultStates = [CalendarTodoCompletionState.Open];
    private static readonly FrozenDictionary<CalendarEntityQueryCode, CalendarTodoQueryCode> EntityQueryCodeMap =
        new Dictionary<CalendarEntityQueryCode, CalendarTodoQueryCode>
        {
            [CalendarEntityQueryCode.InvalidInput] = CalendarTodoQueryCode.InvalidInput,
            [CalendarEntityQueryCode.UnsafeScope] = CalendarTodoQueryCode.UnsafeScope,
            [CalendarEntityQueryCode.NotFound] = CalendarTodoQueryCode.NotFound,
            [CalendarEntityQueryCode.Ambiguous] = CalendarTodoQueryCode.Ambiguous,
            [CalendarEntityQueryCode.OutsideScope] = CalendarTodoQueryCode.OutsideScope,
            [CalendarEntityQueryCode.UnsupportedCapability] = CalendarTodoQueryCode.UnsupportedCapability,
            [CalendarEntityQueryCode.ConcurrencyUnavailable] = CalendarTodoQueryCode.ConcurrencyUnavailable,
            [CalendarEntityQueryCode.LimitExhausted] = CalendarTodoQueryCode.LimitExhausted,
            [CalendarEntityQueryCode.PayloadTooLarge] = CalendarTodoQueryCode.PayloadTooLarge,
            [CalendarEntityQueryCode.TemporalUnresolved] = CalendarTodoQueryCode.TemporalUnresolved,
            [CalendarEntityQueryCode.RecurrenceUnevaluable] = CalendarTodoQueryCode.RecurrenceUnevaluable
        }.ToFrozenDictionary();
    private static readonly FrozenDictionary<CalendarOccurrenceQueryCode, CalendarTodoQueryCode> OccurrenceQueryCodeMap =
        new Dictionary<CalendarOccurrenceQueryCode, CalendarTodoQueryCode>
        {
            [CalendarOccurrenceQueryCode.InvalidInput] = CalendarTodoQueryCode.InvalidInput,
            [CalendarOccurrenceQueryCode.UnsafeScope] = CalendarTodoQueryCode.UnsafeScope,
            [CalendarOccurrenceQueryCode.NotFound] = CalendarTodoQueryCode.NotFound,
            [CalendarOccurrenceQueryCode.Ambiguous] = CalendarTodoQueryCode.Ambiguous,
            [CalendarOccurrenceQueryCode.OutsideScope] = CalendarTodoQueryCode.OutsideScope,
            [CalendarOccurrenceQueryCode.UnsupportedCapability] = CalendarTodoQueryCode.UnsupportedCapability,
            [CalendarOccurrenceQueryCode.ConcurrencyUnavailable] = CalendarTodoQueryCode.ConcurrencyUnavailable,
            [CalendarOccurrenceQueryCode.LimitExhausted] = CalendarTodoQueryCode.LimitExhausted,
            [CalendarOccurrenceQueryCode.PayloadTooLarge] = CalendarTodoQueryCode.PayloadTooLarge,
            [CalendarOccurrenceQueryCode.TemporalUnresolved] = CalendarTodoQueryCode.TemporalUnresolved,
            [CalendarOccurrenceQueryCode.RecurrenceUnevaluable] = CalendarTodoQueryCode.RecurrenceUnevaluable
        }.ToFrozenDictionary();
    private readonly CalendarEntityQueryEngine _entityQueryEngine;
    private readonly CalendarOccurrenceQueryEngine _occurrenceQueryEngine;

    public CalendarTodoQueryEngine(
        ICalendarClient calendarClient,
        CalDavOptions options,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
            resolveDefault)
    {
        _entityQueryEngine = new CalendarEntityQueryEngine(calendarClient, options, applyScope, resolveDefault);
        _occurrenceQueryEngine = new CalendarOccurrenceQueryEngine(calendarClient, options, applyScope, resolveDefault);
    }

    public async Task<CalendarTodoQueryResult> QueryAsync(
        CalendarTodoQuery query,
        CancellationToken cancellationToken)
    {
        if (!IsValid(query))
            return CalendarTodoQueryResult.Failure(CalendarTodoQueryCode.InvalidInput);

        var entities = await _entityQueryEngine.QueryAsync(
            new CalendarEntityQuery(query.Scope, [CalendarEntityKind.Todo]),
            cancellationToken);
        if (entities.Code != CalendarEntityQueryCode.Success)
            return MapEntityFailure(entities);

        var diagnostics = entities.Diagnostics.ToList();
        var rows = new List<CalendarTodoQueryItem>();
        if (query.From is null)
        {
            foreach (var snapshot in entities.Items)
                AddEntityRow(rows, snapshot, query, diagnostics, cancellationToken);
        }
        else
        {
            AddUndatedEntityRows(rows, entities.Items, query, diagnostics, cancellationToken);
            var occurrences = await _occurrenceQueryEngine.QueryAsync(
                new CalendarOccurrenceQuery(
                    query.Scope,
                    query.From.Value,
                    query.To!.Value,
                    query.EvaluationTimeZone,
                    IncludeCancelledOccurrences: (query.CompletionStates ?? DefaultStates).Contains(CalendarTodoCompletionState.Cancelled)),
                cancellationToken);
            if (occurrences.Code != CalendarOccurrenceQueryCode.Success)
                return MapOccurrenceFailure(occurrences);
            foreach (var occurrence in occurrences.Items.Where(item => item.Snapshot.Projection.Kind == CalendarResourceProjectionKind.Todo))
                AddOccurrenceRow(rows, occurrence, query, diagnostics, cancellationToken);
        }

        var excludedIndeterminate = 0;
        var states = query.CompletionStates ?? [CalendarTodoCompletionState.Open];
        var filtered = rows.Where(row => MatchesState(row.Completion.State, states, ref excludedIndeterminate))
            .Where(row => MatchesDue(row, query))
            .OrderBy(row => row.EvaluatedDueUtc is null)
            .ThenBy(row => row.EvaluatedDueUtc)
            .ThenBy(row => row.EvaluatedStartUtc is null)
            .ThenBy(row => row.EvaluatedStartUtc)
            .ThenBy(row => row.Snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(row => row.Snapshot.Projection.EntityUid, StringComparer.Ordinal)
            .ThenBy(row => row.Snapshot.ResourceHref, StringComparer.Ordinal)
            .ThenBy(row => row.Occurrence is null ? string.Empty : row.Occurrence.RecurrenceIdentity.GetCanonicalSortKey(), StringComparer.Ordinal)
            .ToArray();

        return CalendarTodoQueryResult.Success(filtered, diagnostics, excludedIndeterminate);
    }

    private static void AddEntityRow(
        ICollection<CalendarTodoQueryItem> rows,
        CalendarResourceSnapshot snapshot,
        CalendarTodoQuery query,
        ICollection<CalendarResourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var timing = ReadTiming(snapshot, query.EvaluationTimeZone, cancellationToken);
        if (timing.Code is not null)
        {
            diagnostics.Add(new CalendarResourceDiagnostic(timing.Code, timing.Message!, CalendarResourceDiagnosticSeverity.Warning));
            return;
        }
        var completion = CalendarTodoCompletionClassifier.Classify(snapshot);
        rows.Add(new(
            snapshot.Projection.Kind == CalendarResourceProjectionKind.Todo
                ? CalendarTodoQueryResultKind.Entity
                : CalendarTodoQueryResultKind.Unresolved,
            snapshot,
            null,
            completion,
            timing.Due,
            timing.EvaluatedDueUtc,
            timing.Start,
            timing.EvaluatedStartUtc,
            timing.IsRecurring));
    }

    private static void AddUndatedEntityRows(
        ICollection<CalendarTodoQueryItem> rows,
        IEnumerable<CalendarResourceSnapshot> snapshots,
        CalendarTodoQuery query,
        ICollection<CalendarResourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            var timing = ReadTiming(snapshot, query.EvaluationTimeZone, cancellationToken);
            if (timing.HasTemporalValue)
                continue;
            AddEntityRow(rows, snapshot, query, diagnostics, cancellationToken);
        }
    }

    private static void AddOccurrenceRow(
        ICollection<CalendarTodoQueryItem> rows,
        CalendarOccurrenceSnapshot occurrence,
        CalendarTodoQuery query,
        ICollection<CalendarResourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var timing = ReadTiming(occurrence.Snapshot, query.EvaluationTimeZone, cancellationToken);
        if (timing.Code is not null)
        {
            diagnostics.Add(new CalendarResourceDiagnostic(timing.Code, timing.Message!, CalendarResourceDiagnosticSeverity.Warning));
            return;
        }
        var completion = CalendarTodoCompletionClassifier.Classify(
            occurrence.Snapshot,
            occurrence.RecurrenceIdentity);
        rows.Add(new(
            CalendarTodoQueryResultKind.Occurrence,
            occurrence.Snapshot,
            occurrence,
            completion,
            HasEffectiveDue(occurrence, occurrence.Snapshot)
                ? occurrence.Timing.EffectiveEnd ?? timing.Due
                : null,
            HasEffectiveDue(occurrence, occurrence.Snapshot)
                ? ParseUtc(occurrence.Timing.EvaluatedEndUtc) ?? timing.EvaluatedDueUtc
                : null,
            occurrence.Timing.EffectiveStart,
            ParseUtc(occurrence.Timing.EvaluatedStartUtc),
            timing.IsRecurring));
    }


    private static bool MatchesState(
        CalendarTodoCompletionState state,
        IReadOnlyList<CalendarTodoCompletionState> states,
        ref int excludedIndeterminate)
    {
        var matches = states.Contains(state);
        if (!matches && state == CalendarTodoCompletionState.Indeterminate)
            excludedIndeterminate++;
        return matches;
    }

    private static bool MatchesDue(CalendarTodoQueryItem row, CalendarTodoQuery query)
    {
        if (query.DueFrom is null)
            return true;
        return row.EvaluatedDueUtc is { } due
            && due >= query.DueFrom.Value
            && due < query.DueTo!.Value;
    }

    private static bool HasEffectiveDue(
        CalendarOccurrenceSnapshot occurrence,
        CalendarResourceSnapshot snapshot)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var master = document.GetMasterComponent(CalendarEntityKind.Todo);
            var effective = CalendarTodoComponentSelector.Select(document, occurrence.RecurrenceIdentity);
            return HasDueOrDuration(document, master) || HasDueOrDuration(document, effective);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasDueOrDuration(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties.Any(property =>
            property.ComponentPath.SequenceEqual(component.Path)
                && (property.Name.Equals("DUE", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("DURATION", StringComparison.OrdinalIgnoreCase)));

    private static TemporalRead ReadTiming(
        CalendarResourceSnapshot snapshot,
        string? evaluationTimeZone,
        CancellationToken cancellationToken)
    {
        if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            return TemporalRead.Empty;
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var master = document.GetMasterComponent(CalendarEntityKind.Todo);
            var properties = document.Properties
                .Where(property => property.ComponentPath.SequenceEqual(master.Path))
                .ToArray();
            var dueProperty = properties.SingleOrDefault(property => property.Name.Equals("DUE", StringComparison.OrdinalIgnoreCase));
            var startProperty = properties.SingleOrDefault(property => property.Name.Equals("DTSTART", StringComparison.OrdinalIgnoreCase));
            var due = dueProperty is null ? null : CalendarPatchValueSerializer.ParseTemporal(dueProperty);
            var start = startProperty is null ? null : CalendarPatchValueSerializer.ParseTemporal(startProperty);
            var resolver = new CalendarTemporalResolver(snapshot.CalendarProperties, snapshot.AuthoritativeUtf8.Span, cancellationToken, evaluationTimeZone);
            var dueUtc = Resolve(dueProperty, resolver);
            var startUtc = Resolve(startProperty, resolver);
            if (HasUnresolvedValue(dueProperty, dueUtc) || HasUnresolvedValue(startProperty, startUtc))
                return new("temporal_unresolved", "Temporal evaluation requires an explicit evaluation time zone.", null, null, null, null, false, true);
            return new(null, null, due, dueUtc, start, startUtc, IsRecurring(document, master, properties));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return new("temporal_unresolved", "Temporal evaluation could not be resolved.", null, null, null, null, false, true);
        }
    }

    private static DateTimeOffset? Resolve(CalendarContentProperty? property, CalendarTemporalResolver resolver) =>
        property is null ? null : resolver.Resolve(ToPublicProperty(property)).Value;

    private static bool HasUnresolvedValue(CalendarContentProperty? property, DateTimeOffset? resolved) =>
        property is not null && resolved is null;

    private static bool IsRecurring(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<CalendarContentProperty> properties) =>
        properties.Any(property => property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
        || document.Components.Any(component => component.Path.Count == 2
            && component.Path[^1].Name.Equals("VTODO", StringComparison.OrdinalIgnoreCase)
            && !component.Path.SequenceEqual(master.Path));

    private static CalendarProperty ToPublicProperty(CalendarContentProperty property) => new(
        property.ComponentPath,
        property.Name,
        property.Parameters,
        property.ValueType,
        property.RawEncodedValue,
        property.OriginalSlice);

    private static DateTimeOffset? ParseUtc(CalendarTemporalValue? value) => value is null
        ? null
        : DateTimeOffset.TryParse(value.Value, out var parsed) ? parsed.ToUniversalTime() : null;

    private static bool IsValid(CalendarTodoQuery query) =>
        query.Scope.Mode is CalendarEntityScopeMode.Selected or CalendarEntityScopeMode.All
        && HasWindow(query.From, query.To)
        && HasWindow(query.DueFrom, query.DueTo)
        && (query.EvaluationTimeZone is null
            || DateTimeZoneProviders.Tzdb.GetZoneOrNull(query.EvaluationTimeZone) is not null)
        && (query.CompletionStates is null || query.CompletionStates.Count > 0
            && query.CompletionStates.Distinct().Count() == query.CompletionStates.Count
            && query.CompletionStates.All(state => Enum.IsDefined(state)));

    private static bool HasWindow(DateTimeOffset? from, DateTimeOffset? to) =>
        from is null && to is null
        || from is not null && to is not null
            && from.Value.Offset == TimeSpan.Zero
            && to.Value.Offset == TimeSpan.Zero
            && to > from
            && to - from <= TimeSpan.FromDays(366);

    private static CalendarTodoQueryResult MapEntityFailure(CalendarEntityQueryResult result) =>
        CalendarTodoQueryResult.Failure(Map(result.Code), result.AuthorizedCandidates, result.Limits);

    private static CalendarTodoQueryResult MapOccurrenceFailure(CalendarOccurrenceQueryResult result) =>
        CalendarTodoQueryResult.Failure(Map(result.Code), result.AuthorizedCandidates, result.Limits is null
            ? null
            : new CalendarEntityQueryExecutionLimits(result.Limits.ResourcesInspected, result.Limits.OccurrenceCount, result.Limits.ByteCount));

    private static CalendarTodoQueryCode Map(CalendarEntityQueryCode code) =>
        EntityQueryCodeMap.TryGetValue(code, out var mapped)
            ? mapped
            : CalendarTodoQueryCode.UpstreamProtocolError;

    private static CalendarTodoQueryCode Map(CalendarOccurrenceQueryCode code) =>
        OccurrenceQueryCodeMap.TryGetValue(code, out var mapped)
            ? mapped
            : CalendarTodoQueryCode.UpstreamProtocolError;

    private sealed record TemporalRead(
        string? Code,
        string? Message,
        CalendarTemporalValue? Due = null,
        DateTimeOffset? EvaluatedDueUtc = null,
        CalendarTemporalValue? Start = null,
        DateTimeOffset? EvaluatedStartUtc = null,
        bool IsRecurring = false,
        bool TemporalValuePresent = false)
    {
        public bool HasTemporalValue => TemporalValuePresent || Due is not null || Start is not null;

        public static TemporalRead Empty { get; } = new(null, null);
    }
}
