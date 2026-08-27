using System.Collections.Immutable;
using System.Net;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarQueryModule(
    CalendarEntityQueryStartExecutor startExecutor,
    CalendarEntityQueryContinueExecutor continueExecutor,
    CalendarOccurrenceQueryStartExecutor occurrenceStartExecutor,
    CalendarOccurrenceQueryContinueExecutor occurrenceContinueExecutor,
    CalendarTodoQueryStartExecutor todoStartExecutor,
    CalendarTodoQueryContinueExecutor todoContinueExecutor) : ICalendarQueryModule
{
    public async Task<QueryReply<CalendarEntityQueryItem>> QueryEntitiesAsync(
        CalendarEntityQueryRequest request,
        CancellationToken cancellationToken)
    {
        CalendarQueryTelemetry.Begin(request is CalendarEntityQueryRequest.Continue);
        return request switch
        {
            CalendarEntityQueryRequest.Start start => await startExecutor.ExecuteAsync(start, cancellationToken)
                .ConfigureAwait(false),
            CalendarEntityQueryRequest.Continue continuation => await continueExecutor.ExecuteAsync(
                    continuation,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new QueryReply<CalendarEntityQueryItem>.Failure(CalendarQueryFailures.InvalidInput())
        };
    }

    public async Task<QueryReply<CalendarOccurrenceQueryItem>> QueryOccurrencesAsync(
        CalendarOccurrenceQueryRequest request,
        CancellationToken cancellationToken)
    {
        CalendarQueryTelemetry.Begin(request is CalendarOccurrenceQueryRequest.Continue);
        return request switch
        {
            CalendarOccurrenceQueryRequest.Start start => await occurrenceStartExecutor.ExecuteAsync(
                    start,
                    cancellationToken)
                .ConfigureAwait(false),
            CalendarOccurrenceQueryRequest.Continue continuation => await occurrenceContinueExecutor.ExecuteAsync(
                    continuation,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new QueryReply<CalendarOccurrenceQueryItem>.Failure(CalendarQueryFailures.InvalidInput())
        };
    }

    public async Task<QueryReply<CalendarTodoQueryPageItem>> QueryTodosAsync(
        CalendarTodoQueryRequest request,
        CancellationToken cancellationToken)
    {
        CalendarQueryTelemetry.Begin(request is CalendarTodoQueryRequest.Continue);
        return request switch
        {
            CalendarTodoQueryRequest.Start start => await todoStartExecutor.ExecuteAsync(start, cancellationToken)
                .ConfigureAwait(false),
            CalendarTodoQueryRequest.Continue continuation => await todoContinueExecutor.ExecuteAsync(
                    continuation,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new QueryReply<CalendarTodoQueryPageItem>.Failure(CalendarQueryFailures.InvalidInput())
        };
    }
}

internal sealed class CalendarEntityQueryStartExecutor
{
    internal const int MaximumSnapshotItems = CalendarQuerySnapshotPolicy.MaximumItems;
    internal const long MaximumSnapshotBytes = CalendarQuerySnapshotPolicy.MaximumBytes;
    private readonly CalendarQueryPolicy _queryPolicy;
    private readonly CalendarQuerySnapshotPublication _snapshotPublication;
    private readonly CalendarEntityQueryPageCodec _pageCodec;
    private readonly CalendarQueryAcquisitionExecutor _acquisitionExecutor;
    private readonly CalendarTemporalContextResolver _temporalContextResolver;

    internal CalendarEntityQueryStartExecutor(
        CalendarQueryPolicy queryPolicy,
        CalendarQuerySnapshotPublication snapshotPublication,
        CalendarEntityQueryPageCodec pageCodec,
        CalendarQueryAcquisitionExecutor acquisitionExecutor,
        CalendarTemporalContextResolver temporalContextResolver)
    {
        _queryPolicy = queryPolicy;
        _snapshotPublication = snapshotPublication;
        _pageCodec = pageCodec;
        _acquisitionExecutor = acquisitionExecutor;
        _temporalContextResolver = temporalContextResolver;
    }

    internal async Task<QueryReply<CalendarEntityQueryItem>> ExecuteAsync(
        CalendarEntityQueryRequest.Start request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
            return Failure(CalendarQueryFailures.InvalidInput());
        var temporal = _temporalContextResolver.Resolve(new CalendarTemporalContextRequest(
            request.Query.From is not null,
            request.Query.EvaluationTimeZone,
            "Calendar Entity"));
        if (temporal.Error is not null)
            return Failure(temporal.Error);
        return await _queryPolicy.ExecuteStartAsync<CompletedCalendarEntityQuery, CalendarEntityQueryItem>(
            cancellationToken,
            "The query exceeded the Calendar limit.",
            execution => CompleteQueryAsync(request.Query, temporal.Context, execution),
            (completed, token) => completed.Error is not null
                ? Failure(completed.Error)
                : _snapshotPublication.Publish(
                    completed.ToSnapshotDraft(),
                    request.PageSize,
                    _pageCodec,
                    token)).ConfigureAwait(false);
    }

    private async Task<CompletedCalendarEntityQuery> CompleteQueryAsync(
        CalendarEntityQuery query,
        TemporalEvaluationContext? temporalContext,
        CalendarQueryPolicy.CalendarQueryExecution execution)
    {
        var acquired = await _acquisitionExecutor.ExecuteAsync(new CalendarQueryAcquisitionRequest(
                query.Scope,
                query.EntityKinds,
                query.From,
                query.To), execution.Token)
            .ConfigureAwait(false);
        execution.ThrowIfDeadlineExpired();
        if (acquired.Error is not null)
            return CompletedCalendarEntityQuery.Failure(acquired.Error);
        FilterResult filtered;
        using (CalendarQueryTelemetry.StartPhase("evaluation"))
            filtered = Filter(acquired.Resources, query, temporalContext, execution.Token);
        if (filtered.Error is not null)
            return CompletedCalendarEntityQuery.Failure(filtered.Error);
        using (CalendarQueryTelemetry.StartPhase("serialization"))
            return Project(
                filtered.Resources.Select(resource => resource.Snapshot).ToArray(),
                acquired.Diagnostics,
                temporalContext,
                execution.Token);
    }

    private static CompletedCalendarEntityQuery Project(
        IReadOnlyList<CalendarResourceSnapshot> snapshots,
        IReadOnlyList<QueryDiagnostic> diagnostics,
        TemporalEvaluationContext? temporalContext,
        CancellationToken cancellationToken)
    {
        var countFailure = CalendarQuerySnapshotPolicy.Validate(snapshots.Count, 0);
        if (countFailure is not null)
            return CompletedCalendarEntityQuery.Failure(countFailure);
        var projected = ImmutableArray.CreateBuilder<StoredCalendarEntityQueryItem>(snapshots.Count);
        long itemBytes = 0;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = CalendarEntityQueryProjector.Project(snapshot);
            CalendarQueryTelemetry.Add("caldav.query.serialization_count");
            projected.Add(item);
            itemBytes += item.JsonByteCount;
            var byteFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, itemBytes);
            if (byteFailure is not null)
                return CompletedCalendarEntityQuery.Failure(byteFailure);
        }
        var diagnosticsUtf8 = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostics);
        var temporalContextUtf8 = CalendarTemporalEvaluationContextCodec.Encode(temporalContext);
        var retainedBytes = itemBytes + diagnosticsUtf8.Length + temporalContextUtf8.Length;
        var retainedFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, retainedBytes);
        if (retainedFailure is not null)
            return CompletedCalendarEntityQuery.Failure(retainedFailure);
        return CompletedCalendarEntityQuery.Success(
            projected.MoveToImmutable(),
            diagnosticsUtf8,
            retainedBytes,
            temporalContextUtf8);
    }

    private static FilterResult Filter(
        IReadOnlyList<AcquiredCalendarResource> resources,
        CalendarEntityQuery query,
        TemporalEvaluationContext? temporalContext,
        CancellationToken cancellationToken)
    {
        var filtered = new List<AcquiredCalendarResource>();
        var occurrenceCount = 0;
        foreach (var resource in resources.Where(resource => MatchesKind(resource.Snapshot, query.EntityKinds)))
        {
            var snapshot = resource.Snapshot;
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            {
                filtered.Add(resource);
                continue;
            }
            CalendarQueryTelemetry.Add("caldav.query.evaluation_count");
            var temporal = CalendarEntityTemporalMatcher.Matches(
                snapshot,
                resource.Document!,
                resource.TypedCalendar,
                query.From,
                query.To,
                temporalContext?.TimeZone,
                cancellationToken);
            occurrenceCount += temporal.OccurrenceCount;
            var failure = TemporalFailure(temporal.Match, occurrenceCount);
            if (failure is not null)
                return FilterResult.Failure(failure);
            if (temporal.Match != CalendarEntityTemporalMatch.NoMatch)
                filtered.Add(resource);
        }
        return FilterResult.Success(filtered
            .OrderBy(resource => resource.Snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(resource => resource.Snapshot.ResourceHref, StringComparer.Ordinal)
            .ToArray());
    }

    private static QueryFailure? TemporalFailure(CalendarEntityTemporalMatch match, int occurrenceCount) => match switch
    {
        CalendarEntityTemporalMatch.LimitExhausted => CalendarQueryFailures.Limit(
            "The Calendar Entity query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: occurrenceCount)),
        _ when occurrenceCount > CalendarQueryPolicy.MaximumOccurrences => CalendarQueryFailures.Limit(
            "The Calendar Entity query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: occurrenceCount)),
        CalendarEntityTemporalMatch.Unresolved => CalendarQueryFailures.TemporalUnresolved(),
        CalendarEntityTemporalMatch.Unevaluable => CalendarQueryFailures.RecurrenceUnevaluable(),
        _ => null
    };

    private static bool IsValid(CalendarEntityQueryRequest.Start request) =>
        request.Query is not null
        && request.PageSize is >= 1 and <= CalendarEntityQueryPageCodec.MaximumPageSize;

    private static bool MatchesKind(CalendarResourceSnapshot snapshot, IReadOnlyList<CalendarEntityKind> kinds) =>
        snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque
        || kinds.Any(kind => kind == CalendarEntityKind.Event
            ? snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            : snapshot.Projection.Kind == CalendarResourceProjectionKind.Todo);

    private static QueryReply<CalendarEntityQueryItem>.Failure Failure(QueryFailure failure) => new(failure);

    private sealed record FilterResult(IReadOnlyList<AcquiredCalendarResource> Resources, QueryFailure? Error)
    {
        internal static FilterResult Success(IReadOnlyList<AcquiredCalendarResource> resources) => new(resources, null);

        internal static FilterResult Failure(QueryFailure error) => new([], error);
    }

}

internal sealed class CalendarEntityQueryContinueExecutor(
    CalendarQuerySnapshotReplay snapshotReplay,
    CalendarEntityQueryPageCodec pageCodec)
{
    internal Task<QueryReply<CalendarEntityQueryItem>> ExecuteAsync(
        CalendarEntityQueryRequest.Continue request,
        CancellationToken cancellationToken) => Task.FromResult(
        snapshotReplay.Replay(
            request.Cursor,
            request.PageSize,
            pageCodec,
            cancellationToken));
}

internal sealed record CompletedCalendarEntityQuery(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8,
    QueryFailure? Error)
{
    internal static CompletedCalendarEntityQuery Success(
        ImmutableArray<StoredCalendarEntityQueryItem> items,
        ReadOnlyMemory<byte> diagnosticsUtf8,
        long retainedBytes,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8) =>
        new(items, diagnosticsUtf8, retainedBytes, temporalEvaluationContextUtf8, null);

    internal static CompletedCalendarEntityQuery Failure(QueryFailure error) =>
        new([], ReadOnlyMemory<byte>.Empty, 0, ReadOnlyMemory<byte>.Empty, error);

    internal CalendarQuerySnapshotDraft ToSnapshotDraft() => new(
        Items,
        DiagnosticsUtf8,
        RetainedBytes,
        TemporalEvaluationContextUtf8);
}

internal static class CalendarQueryFailures
{
    internal static QueryFailure InvalidInput(string message = "The Calendar Entity query input is invalid.") =>
        new(QueryFailureCode.InvalidInput, QueryFailureCategory.Input, message, false,
            QueryFailurePhase.SchemaLexicalDiscriminator);

    internal static QueryFailure InvalidCursor() =>
        new(QueryFailureCode.InvalidInput, QueryFailureCategory.Input, "The continuation cursor is invalid.", false,
            QueryFailurePhase.Pagination);

    internal static QueryFailure UnsafeHref() => new(
        QueryFailureCode.InvalidInput,
        QueryFailureCategory.Input,
        "The Calendar Entity query Calendar href is unsafe.",
        false,
        QueryFailurePhase.OriginScopeAuthorization);

    internal static QueryFailure CursorExpired() => new(
        QueryFailureCode.CursorExpired,
        QueryFailureCategory.State,
        "The Query Result Snapshot expired; start a new cursorless query.",
        false,
        QueryFailurePhase.Pagination);

    internal static QueryFailure Limit(string message, QueryExecutionLimits? limits = null) =>
        new(QueryFailureCode.LimitExhausted, QueryFailureCategory.LimitsAndAdmission, message, false,
            QueryFailurePhase.Execution, limits);

    internal static QueryFailure ElapsedLimit()
    {
        var limitMilliseconds = checked((long)CalendarQueryPolicy.ExecutionTimeout.TotalMilliseconds);
        return Limit(
            "The query exhausted the elapsed_time execution budget.",
            new QueryExecutionLimits(
                Dimension: QueryLimitDimension.ElapsedTime,
                Observed: limitMilliseconds,
                Limit: limitMilliseconds));
    }

    internal static QueryFailure Busy(int retryAfterMs) => new(
        QueryFailureCode.Busy,
        QueryFailureCategory.LimitsAndAdmission,
        "Query Result Snapshot capacity is temporarily unavailable.",
        true,
        QueryFailurePhase.Pagination,
        RetryAfterMs: retryAfterMs);

    internal static QueryFailure PayloadTooLarge(string message, int? byteCount = null) => new(
        QueryFailureCode.PayloadTooLarge,
        QueryFailureCategory.LimitsAndAdmission,
        message,
        false,
        QueryFailurePhase.AdmissionAndPayload,
        byteCount is null ? null : new QueryExecutionLimits(ByteCount: byteCount));

    internal static QueryFailure Protocol() => new(
        QueryFailureCode.UpstreamProtocolError,
        QueryFailureCategory.Upstream,
        "The Calendar Entity query returned an invalid response.",
        false,
        QueryFailurePhase.Execution);

    internal static QueryFailure UnsupportedCapability() => new(
        QueryFailureCode.UnsupportedCapability,
        QueryFailureCategory.CapabilityAndProjection,
        "The server does not support the required Calendar query capability.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability);

    internal static QueryFailure ConcurrencyUnavailable() => new(
        QueryFailureCode.ConcurrencyUnavailable,
        QueryFailureCategory.State,
        "A query candidate did not provide a strong Entity Tag.",
        false,
        QueryFailurePhase.TargetRevision);

    internal static QueryFailure TemporalUnresolved() => new(
        QueryFailureCode.TemporalUnresolved,
        QueryFailureCategory.CapabilityAndProjection,
        "Temporal evaluation could not be resolved.",
        false,
        QueryFailurePhase.CompleteResourceSemantics);

    internal static QueryFailure RecurrenceUnevaluable() => new(
        QueryFailureCode.RecurrenceUnevaluable,
        QueryFailureCategory.CapabilityAndProjection,
        "The Recurrence Set could not be evaluated.",
        false,
        QueryFailurePhase.CompleteResourceSemantics);

    internal static QueryFailure UpstreamUnavailable() => new(
        QueryFailureCode.UpstreamUnavailable,
        QueryFailureCategory.Upstream,
        "The Calendar Entity query is temporarily unavailable.",
        true,
        QueryFailurePhase.Execution);

    internal static QueryFailure NotFound(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.NotFound,
        QueryFailureCategory.Selection,
        "No matching authorized Calendar was found.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure Ambiguous(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.Ambiguous,
        QueryFailureCategory.Selection,
        "The Calendar selector matched more than one authorized Calendar.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure OutsideScope(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.OutsideScope,
        QueryFailureCategory.Selection,
        "The selected Calendar is outside the configured Calendar Scope.",
        false,
        QueryFailurePhase.OriginScopeAuthorization,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure FromHttp(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Upstream(QueryFailureCode.UpstreamUnauthorized,
            "The Calendar Entity query was not authorized."),
        HttpStatusCode.Forbidden => Upstream(QueryFailureCode.UpstreamForbidden,
            "The Calendar Entity query was forbidden."),
        HttpStatusCode.TooManyRequests => Upstream(
            QueryFailureCode.UpstreamRateLimited,
            "The Calendar Entity query is rate limited.",
            true),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => UnsupportedCapability(),
        HttpStatusCode.RequestEntityTooLarge => PayloadTooLarge("The Calendar Entity query response is too large."),
        HttpStatusCode.RequestTimeout or null => UpstreamUnavailable(),
        >= HttpStatusCode.InternalServerError => UpstreamUnavailable(),
        _ => Protocol()
    };

    private static QueryFailure Upstream(QueryFailureCode code, string message, bool retryable = false) =>
        new(code, QueryFailureCategory.Upstream, message, retryable, QueryFailurePhase.Execution);

    private static IReadOnlyList<QueryAuthorizedCandidate>? Candidates(IReadOnlyList<CalendarDescriptor> candidates) =>
        candidates.Count == 0
            ? null
            : candidates.Take(32).Select(calendar => new QueryAuthorizedCandidate(
                calendar.Href,
                calendar.DisplayName,
                calendar.EventSupport,
                calendar.TodoSupport,
                calendar.EventEvidence,
                calendar.TodoEvidence)).ToArray();
}
