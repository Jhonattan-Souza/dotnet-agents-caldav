using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed record CalendarTodoEvaluationResult(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    long ProjectedBytes,
    int ExcludedIndeterminateCount,
    QueryFailure? Error)
{
    internal static CalendarTodoEvaluationResult Success(
        ImmutableArray<StoredCalendarEntityQueryItem> items,
        long projectedBytes,
        int excludedIndeterminateCount) => new(items, projectedBytes, excludedIndeterminateCount, null);

    internal static CalendarTodoEvaluationResult Failure(QueryFailure error) => new([], 0, 0, error);
}

internal static class CalendarTodoSnapshotEvaluator
{
    private static readonly CalendarTodoCompletionState[] DefaultStates = [CalendarTodoCompletionState.Open];

    internal static CalendarTodoEvaluationResult Evaluate(
        IReadOnlyList<AcquiredCalendarResource> resources,
        CalendarTodoQuery query,
        IReadOnlyList<CalendarTodoProjectionField> projection,
        CancellationToken cancellationToken)
    {
        var rows = new List<CalendarTodoEvaluatedRow>();
        var observedOccurrences = 0;
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalendarQueryTelemetry.Add(CalendarQueryCounter.Evaluation);
            var evaluated = EvaluateResource(resource, query, cancellationToken);
            observedOccurrences += evaluated.ObservedOccurrences;
            if (evaluated.Error is not null)
                return CalendarTodoEvaluationResult.Failure(evaluated.Error);
            if (observedOccurrences > CalendarQueryPolicy.MaximumOccurrences)
            {
                return CalendarTodoEvaluationResult.Failure(CalendarQueryFailures.Limit(
                    "The To-do query exhausted its occurrence budget.",
                    new QueryExecutionLimits(OccurrenceCount: observedOccurrences)));
            }
            rows.AddRange(evaluated.Rows);
        }

        return FilterOrderAndProject(rows, query, projection, cancellationToken);
    }

    private static CalendarTodoResourceEvaluation EvaluateResource(
        AcquiredCalendarResource resource,
        CalendarTodoQuery query,
        CancellationToken cancellationToken)
    {
        var snapshot = resource.Snapshot;
        if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            return CalendarTodoResourceEvaluation.Success([CreateOpaqueRow(snapshot)], 0);
        if (snapshot.Projection.Kind != CalendarResourceProjectionKind.Todo)
            return CalendarTodoResourceEvaluation.Success([], 0);
        try
        {
            var document = resource.Document
                ?? throw new InvalidOperationException("A typed query resource must retain its parsed document.");
            var master = document.GetMasterComponent(CalendarEntityKind.Todo);
            var recurring = IsRecurring(document);
            if (query.From is null)
            {
                var timing = ReadTiming(
                    snapshot,
                    document,
                    master,
                    resource.TypedCalendar,
                    query.EvaluationTimeZone,
                    cancellationToken);
                return CalendarTodoResourceEvaluation.Success(
                    [CreateEntityRow(snapshot, document, master, recurring, timing)],
                    0);
            }
            return recurring
                ? EvaluateRecurring(resource, document, master, query, cancellationToken)
                : EvaluateNonRecurring(resource, document, master, query, cancellationToken);
        }
        catch (CalendarTodoTemporalUnresolvedException)
        {
            return CalendarTodoResourceEvaluation.Failure(CalendarQueryFailures.TemporalUnresolved());
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return CalendarTodoResourceEvaluation.Failure(CalendarQueryFailures.RecurrenceUnevaluable());
        }
    }

    private static CalendarTodoResourceEvaluation EvaluateNonRecurring(
        AcquiredCalendarResource resource,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarTodoQuery query,
        CancellationToken cancellationToken)
    {
        var snapshot = resource.Snapshot;
        var hasTemporalBoundary = Owned(document, master).Any(property => property.Name is "DTSTART" or "DUE");
        var timing = ReadTiming(
            snapshot,
            document,
            master,
            resource.TypedCalendar,
            query.EvaluationTimeZone,
            cancellationToken);
        if (hasTemporalBoundary && !Overlaps(timing, query.From!.Value, query.To!.Value))
        {
            return CalendarTodoResourceEvaluation.Success([], 0);
        }
        return CalendarTodoResourceEvaluation.Success(
            [CreateEntityRow(snapshot, document, master, false, timing)],
            hasTemporalBoundary ? 1 : 0);
    }

    private static CalendarTodoResourceEvaluation EvaluateRecurring(
        AcquiredCalendarResource resource,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarTodoQuery query,
        CancellationToken cancellationToken)
    {
        var snapshot = resource.Snapshot;
        var occurrenceQuery = new CalendarOccurrenceQuery(
            query.Scope,
            query.From!.Value,
            query.To!.Value,
            query.EvaluationTimeZone,
            IncludeCancelledOccurrences: true);
        var evaluated = CalendarOccurrenceEvaluator.Evaluate(
            snapshot,
            occurrenceQuery,
            document,
            resource.TypedCalendar,
            cancellationToken);
        if (evaluated.Code != CalendarOccurrenceEvaluationCode.Success)
            return CalendarTodoResourceEvaluation.Failure(EvaluationFailure(evaluated));
        var rows = evaluated.Items.Select(occurrence => CreateOccurrenceRow(
                occurrence,
                document,
                master))
            .ToArray();
        return CalendarTodoResourceEvaluation.Success(rows, evaluated.ObservedOccurrenceCount);
    }

    private static CalendarTodoEvaluatedRow CreateEntityRow(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        bool recurring,
        TodoTiming timing)
    {
        var completion = CalendarTodoCompletionClassifier.Classify(document, master.Path);
        return new CalendarTodoEvaluatedRow(
            CalendarTodoEvaluationKind.Entity,
            snapshot,
            null,
            completion,
            timing.Due,
            timing.EvaluatedDueUtc,
            timing.Start,
            timing.EvaluatedStartUtc,
            recurring,
            CalendarResourceSemanticProjectionMapper.TodoForComponent(document, master, master),
            CalendarResourceSemanticProjectionMapper.TodoCompletedAt(document, master));
    }

    private static CalendarTodoEvaluatedRow CreateOccurrenceRow(
        EvaluatedOccurrence occurrence,
        CalendarContentDocument document,
        CalendarContentComponent master)
    {
        var component = CalendarTodoComponentSelector.Select(document, occurrence.RecurrenceIdentity);
        var timing = ReadOccurrenceTiming(document, component, master, occurrence);
        var completion = CalendarTodoCompletionClassifier.Classify(document, component.Path);
        return new CalendarTodoEvaluatedRow(
            CalendarTodoEvaluationKind.Occurrence,
            occurrence.Snapshot,
            occurrence,
            completion,
            timing.Due,
            timing.EvaluatedDueUtc,
            timing.Start,
            timing.EvaluatedStartUtc,
            true,
            CalendarResourceSemanticProjectionMapper.TodoForComponent(document, component, master),
            CalendarResourceSemanticProjectionMapper.TodoCompletedAt(document, component));
    }

    private static TodoOccurrenceTiming ReadOccurrenceTiming(
        CalendarContentDocument document,
        CalendarContentComponent component,
        CalendarContentComponent master,
        EvaluatedOccurrence occurrence)
    {
        var properties = Owned(document, component).ToArray();
        if (!properties.Any(property => property.Name is "DTSTART" or "DUE" or "DURATION"))
            properties = Owned(document, master).ToArray();
        var hasStart = properties.Any(property => property.Name == "DTSTART");
        var hasDue = properties.Any(property => property.Name == "DUE");
        var hasDuration = properties.Any(property => property.Name == "DURATION");
        if (!hasStart && hasDue)
        {
            return new TodoOccurrenceTiming(
                occurrence.Timing.EffectiveStart,
                ParseUtc(occurrence.Timing.EvaluatedStartUtc),
                null,
                null);
        }
        return new TodoOccurrenceTiming(
            hasDue || hasDuration ? occurrence.Timing.EffectiveEnd : null,
            hasDue || hasDuration ? ParseUtc(occurrence.Timing.EvaluatedEndUtc) : null,
            hasStart ? occurrence.Timing.EffectiveStart : null,
            hasStart ? ParseUtc(occurrence.Timing.EvaluatedStartUtc) : null);
    }

    private static CalendarTodoEvaluatedRow CreateOpaqueRow(CalendarResourceSnapshot snapshot) => new(
        CalendarTodoEvaluationKind.Unresolved,
        snapshot,
        null,
        new EvaluatedTodoCompletion(CalendarTodoCompletionState.Indeterminate, null, null, null, []),
        null,
        null,
        null,
        null,
        false,
        JsonSerializer.SerializeToElement(new { }),
        null);

    private static CalendarTodoEvaluationResult FilterOrderAndProject(
        IReadOnlyList<CalendarTodoEvaluatedRow> rows,
        CalendarTodoQuery query,
        IReadOnlyList<CalendarTodoProjectionField> projection,
        CancellationToken cancellationToken)
    {
        var excludedIndeterminate = 0;
        var states = query.CompletionStates ?? DefaultStates;
        var filtered = new List<CalendarTodoEvaluatedRow>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ShouldInclude(row, query, states, ref excludedIndeterminate))
                continue;
            filtered.Add(row);
        }
        var ordered = filtered
            .OrderBy(row => row.EvaluatedDueUtc is null)
            .ThenBy(row => row.EvaluatedDueUtc)
            .ThenBy(row => row.EvaluatedStartUtc is null)
            .ThenBy(row => row.EvaluatedStartUtc)
            .ThenBy(row => row.Snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(row => row.Snapshot.Projection.EntityUid, StringComparer.Ordinal)
            .ThenBy(row => row.Snapshot.ResourceHref, StringComparer.Ordinal)
            .ThenBy(row => row.Occurrence?.RecurrenceIdentity.GetCanonicalSortKey() ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        var projected = ImmutableArray.CreateBuilder<StoredCalendarEntityQueryItem>(ordered.Length);
        long projectedBytes = 0;
        foreach (var row in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Project(row, projection);
            projected.Add(item);
            projectedBytes += item.JsonByteCount;
            var failure = CalendarQuerySnapshotPolicy.Validate(projected.Count, projectedBytes);
            if (failure is not null)
                return CalendarTodoEvaluationResult.Failure(failure);
        }
        return CalendarTodoEvaluationResult.Success(
            projected.MoveToImmutable(),
            projectedBytes,
            excludedIndeterminate);
    }

    private static StoredCalendarEntityQueryItem Project(
        CalendarTodoEvaluatedRow row,
        IReadOnlyList<CalendarTodoProjectionField> projection)
    {
        CalendarQueryTelemetry.Add(CalendarQueryCounter.Serialization);
        return CalendarTodoQueryProjector.Project(row, projection);
    }

    private static bool ShouldInclude(
        CalendarTodoEvaluatedRow row,
        CalendarTodoQuery query,
        IReadOnlyList<CalendarTodoCompletionState> states,
        ref int excludedIndeterminate)
    {
        if (!states.Contains(row.Completion.State))
        {
            if (row.Completion.State == CalendarTodoCompletionState.Indeterminate)
                excludedIndeterminate++;
            return false;
        }
        return query.DueFrom is null
            || row.EvaluatedDueUtc is { } due && due >= query.DueFrom && due < query.DueTo;
    }

    private static TodoTiming ReadTiming(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent component,
        global::Ical.Net.Calendar? typedCalendar,
        string? evaluationTimeZone,
        CancellationToken cancellationToken)
    {
        var properties = Owned(document, component).ToArray();
        var dueProperty = Single(properties, "DUE");
        var startProperty = Single(properties, "DTSTART");
        var durationProperty = Single(properties, "DURATION");
        var due = ParseTemporal(dueProperty);
        var start = ParseTemporal(startProperty);
        var resolver = new CalendarTemporalResolver(
            snapshot.CalendarProperties,
            typedCalendar,
            cancellationToken,
            evaluationTimeZone);
        var dueUtc = Resolve(dueProperty, resolver);
        var startUtc = Resolve(startProperty, resolver);
        RequireResolved(dueProperty, dueUtc);
        RequireResolved(startProperty, startUtc);
        TimeSpan? duration = durationProperty is null
            ? null
            : XmlConvert.ToTimeSpan(durationProperty.RawEncodedValue);
        var intervalStart = startUtc ?? dueUtc;
        var intervalEnd = ResolveIntervalEnd(dueUtc, startUtc, duration);
        return new TodoTiming(due, dueUtc, start, startUtc, intervalStart, intervalEnd);
    }

    private static CalendarTemporalValue? ParseTemporal(CalendarContentProperty? property) => property is null
        ? null
        : CalendarPatchValueSerializer.ParseTemporal(property);

    private static void RequireResolved(CalendarContentProperty? property, DateTimeOffset? instant)
    {
        if (property is not null && instant is null)
            throw new CalendarTodoTemporalUnresolvedException();
    }

    private static DateTimeOffset? ResolveIntervalEnd(
        DateTimeOffset? due,
        DateTimeOffset? start,
        TimeSpan? duration) => due ?? (start.HasValue && duration.HasValue ? start + duration : start);

    private static bool Overlaps(TodoTiming timing, DateTimeOffset from, DateTimeOffset to)
    {
        var start = timing.IntervalStartUtc
            ?? throw new CalendarTodoTemporalUnresolvedException();
        var end = timing.IntervalEndUtc ?? start;
        return end > start ? start < to && end > from : start >= from && start < to;
    }

    private static IEnumerable<CalendarContentProperty> Owned(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties.Where(property =>
        property.ComponentPath.SequenceEqual(component.Path));

    private static bool IsRecurring(CalendarContentDocument document) => document.Properties.Any(property =>
        property.ComponentPath.Count == 2
        && property.ComponentPath[1].Name.Equals("VTODO", StringComparison.OrdinalIgnoreCase)
        && property.Name is "RRULE" or "RDATE" or "EXDATE" or "RECURRENCE-ID");

    private static CalendarContentProperty? Single(
        IReadOnlyList<CalendarContentProperty> properties,
        string name)
    {
        var matches = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length <= 1
            ? matches.SingleOrDefault()
            : throw new FormatException($"The To-do {name} property is ambiguous.");
    }

    private static DateTimeOffset? Resolve(
        CalendarContentProperty? property,
        CalendarTemporalResolver resolver) => property is null
        ? null
        : resolver.Resolve(ToPublicProperty(property)).Value;

    private static CalendarProperty ToPublicProperty(CalendarContentProperty property) => new(
        property.ComponentPath,
        property.Name,
        property.Parameters,
        property.ValueType,
        property.RawEncodedValue,
        property.OriginalSlice);

    private static DateTimeOffset? ParseUtc(CalendarTemporalValue? value) => value is null
        ? null
        : DateTimeOffset.TryParse(value.Value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static QueryFailure EvaluationFailure(CalendarOccurrenceEvaluation evaluation) => evaluation.Code switch
    {
        CalendarOccurrenceEvaluationCode.TemporalUnresolved => CalendarQueryFailures.TemporalUnresolved(),
        CalendarOccurrenceEvaluationCode.LimitExhausted => CalendarQueryFailures.Limit(
            "The To-do query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: evaluation.ObservedOccurrenceCount)),
        _ => CalendarQueryFailures.RecurrenceUnevaluable()
    };

    private sealed record CalendarTodoResourceEvaluation(
        IReadOnlyList<CalendarTodoEvaluatedRow> Rows,
        int ObservedOccurrences,
        QueryFailure? Error)
    {
        internal static CalendarTodoResourceEvaluation Success(
            IReadOnlyList<CalendarTodoEvaluatedRow> rows,
            int observedOccurrences) => new(rows, observedOccurrences, null);

        internal static CalendarTodoResourceEvaluation Failure(QueryFailure error) => new([], 0, error);
    }

    private sealed record TodoTiming(
        CalendarTemporalValue? Due,
        DateTimeOffset? EvaluatedDueUtc,
        CalendarTemporalValue? Start,
        DateTimeOffset? EvaluatedStartUtc,
        DateTimeOffset? IntervalStartUtc,
        DateTimeOffset? IntervalEndUtc);

    private sealed record TodoOccurrenceTiming(
        CalendarTemporalValue? Due,
        DateTimeOffset? EvaluatedDueUtc,
        CalendarTemporalValue? Start,
        DateTimeOffset? EvaluatedStartUtc);

    private sealed class CalendarTodoTemporalUnresolvedException : Exception;
}

internal sealed record CalendarTodoEvaluatedRow(
    CalendarTodoEvaluationKind ResultKind,
    CalendarResourceSnapshot Snapshot,
    EvaluatedOccurrence? Occurrence,
    EvaluatedTodoCompletion Completion,
    CalendarTemporalValue? Due,
    DateTimeOffset? EvaluatedDueUtc,
    CalendarTemporalValue? Start,
    DateTimeOffset? EvaluatedStartUtc,
    bool IsRecurring,
    JsonElement Fields,
    JsonElement? CompletedAt)
{
    internal bool RequiresOccurrenceTarget => IsRecurring && ResultKind == CalendarTodoEvaluationKind.Entity;
}

internal static class CalendarTodoQueryProjector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static StoredCalendarEntityQueryItem Project(
        CalendarTodoEvaluatedRow row,
        IReadOnlyList<CalendarTodoProjectionField> projection)
    {
        var selected = projection.ToHashSet();
        var wire = new TodoWire(
            ResultKind(row.ResultKind),
            row.Snapshot.Projection.EntityUid,
            CompletionState(row.Completion.State),
            StringField(row.Fields, selected, CalendarTodoProjectionField.Summary, "summary"),
            JsonField(row.Fields, selected, CalendarTodoProjectionField.Status, "status"),
            selected.Contains(CalendarTodoProjectionField.CompletedAt) ? row.CompletedAt : null,
            IntegerField(row.Fields, selected, CalendarTodoProjectionField.PercentComplete, "percentComplete"),
            TemporalField(row.Fields, selected, CalendarTodoProjectionField.Due, "due", row.Due, row.EvaluatedDueUtc),
            IntegerField(row.Fields, selected, CalendarTodoProjectionField.Priority, "priority"),
            CategoriesField(row.Fields, selected),
            TemporalField(row.Fields, selected, CalendarTodoProjectionField.Start, "start", row.Start, row.EvaluatedStartUtc),
            StringField(row.Fields, selected, CalendarTodoProjectionField.Description, "description"),
            JsonField(row.Fields, selected, CalendarTodoProjectionField.Recurrence, "recurrenceSet"),
            Target(row),
            row.Snapshot.Diagnostics.Concat(row.Completion.Diagnostics)
                .Select(CalendarEntityQueryProjector.Diagnostic)
                .ToArray());
        return new StoredCalendarEntityQueryItem(JsonSerializer.SerializeToUtf8Bytes(wire, SerializerOptions));
    }

    private static CompletionTargetWire Target(CalendarTodoEvaluatedRow row)
    {
        var entityRevision = row.Snapshot.SemanticMutationAvailable
            ? new EntityRevisionWire(
                row.Snapshot.ResourceHref,
                row.Snapshot.Projection.EntityUid!,
                "todo",
                row.Snapshot.EntityTag)
            : null;
        if (row.ResultKind == CalendarTodoEvaluationKind.Unresolved)
        {
            return new CompletionTargetWire(
                "unavailable",
                null,
                null,
                new ResourceRevisionWire(row.Snapshot.ResourceHref, row.Snapshot.EntityTag));
        }
        if (row.RequiresOccurrenceTarget)
            return new CompletionTargetWire("occurrence_required", entityRevision, null, null);
        return new CompletionTargetWire(
            "direct",
            entityRevision,
            row.Occurrence is null ? null : Temporal(row.Occurrence.RecurrenceIdentity),
            null);
    }

    private static JsonElement? TemporalField(
        JsonElement fields,
        IReadOnlySet<CalendarTodoProjectionField> projection,
        CalendarTodoProjectionField field,
        string name,
        CalendarTemporalValue? source,
        DateTimeOffset? evaluatedUtc)
    {
        if (!projection.Contains(field))
            return null;
        var hasOriginal = fields.TryGetProperty(name, out var original);
        if (source is null)
            return hasOriginal ? original : null;
        var effectiveSource = JsonSerializer.SerializeToElement(Temporal(source), SerializerOptions);
        if (evaluatedUtc is null)
            return effectiveSource;
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["source"] = JsonNode.Parse(effectiveSource.GetRawText()),
            ["evaluatedUtc"] = new JsonObject
            {
                ["kind"] = "utcDateTime",
                ["value"] = evaluatedUtc.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            }
        });
    }

    private static string? StringField(
        JsonElement fields,
        IReadOnlySet<CalendarTodoProjectionField> projection,
        CalendarTodoProjectionField field,
        string name) => projection.Contains(field)
        && fields.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntegerField(
        JsonElement fields,
        IReadOnlySet<CalendarTodoProjectionField> projection,
        CalendarTodoProjectionField field,
        string name) => projection.Contains(field)
        && fields.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var integer)
            ? integer
            : null;

    private static JsonElement? JsonField(
        JsonElement fields,
        IReadOnlySet<CalendarTodoProjectionField> projection,
        CalendarTodoProjectionField field,
        string name) => projection.Contains(field) && fields.TryGetProperty(name, out var value)
        ? value
        : null;

    private static IReadOnlyList<string>? CategoriesField(
        JsonElement fields,
        IReadOnlySet<CalendarTodoProjectionField> projection)
    {
        if (!projection.Contains(CalendarTodoProjectionField.Categories))
            return null;
        return fields.TryGetProperty("categories", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : [];
    }

    private static TemporalWire Temporal(CalendarTemporalValue value) => new(
        value.Kind switch
        {
            CalendarTemporalKind.Date => "date",
            CalendarTemporalKind.FloatingDateTime => "floatingDateTime",
            CalendarTemporalKind.UtcDateTime => "utcDateTime",
            CalendarTemporalKind.ZonedDateTime => "zonedDateTime",
            _ => "unknown"
        },
        value.Value,
        value.TimeZoneId);

    private static string ResultKind(CalendarTodoEvaluationKind kind) => kind switch
    {
        CalendarTodoEvaluationKind.Entity => "entity",
        CalendarTodoEvaluationKind.Occurrence => "occurrence",
        _ => "unresolved"
    };

    private static string CompletionState(CalendarTodoCompletionState state) => state switch
    {
        CalendarTodoCompletionState.Open => "open",
        CalendarTodoCompletionState.Completed => "completed",
        CalendarTodoCompletionState.Cancelled => "cancelled",
        _ => "indeterminate"
    };

    private sealed record TodoWire(
        [property: JsonPropertyName("resultKind")] string ResultKind,
        [property: JsonPropertyName("uid")] string? Uid,
        [property: JsonPropertyName("completionState")] string CompletionState,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("status")] JsonElement? Status,
        [property: JsonPropertyName("completedAt")] JsonElement? CompletedAt,
        [property: JsonPropertyName("percentComplete")] int? PercentComplete,
        [property: JsonPropertyName("due")] JsonElement? Due,
        [property: JsonPropertyName("priority")] int? Priority,
        [property: JsonPropertyName("categories")] IReadOnlyList<string>? Categories,
        [property: JsonPropertyName("start")] JsonElement? Start,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("recurrence")] JsonElement? Recurrence,
        [property: JsonPropertyName("completionTarget")] CompletionTargetWire CompletionTarget,
        [property: JsonPropertyName("diagnostics")] IReadOnlyList<QueryDiagnostic> Diagnostics);

    private sealed record CompletionTargetWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("entityRevision")] EntityRevisionWire? EntityRevision,
        [property: JsonPropertyName("recurrenceIdentity")] TemporalWire? RecurrenceIdentity,
        [property: JsonPropertyName("resourceRevision")] ResourceRevisionWire? ResourceRevision);

    private sealed record EntityRevisionWire(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("entityUid")] string EntityUid,
        [property: JsonPropertyName("entityKind")] string EntityKind,
        [property: JsonPropertyName("entityTag")] string EntityTag);

    private sealed record ResourceRevisionWire(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("entityTag")] string EntityTag);

    private sealed record TemporalWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("timeZoneId")] string? TimeZoneId);
}
