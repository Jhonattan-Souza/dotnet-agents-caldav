using System.Collections.Immutable;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarOccurrenceQueryStartExecutor(
    CalendarQueryPolicy queryPolicy,
    CalendarQuerySnapshotPublication snapshotPublication,
    CalendarOccurrenceQueryPageCodec pageCodec,
    CalendarQueryAcquisitionExecutor acquisitionExecutor,
    CalendarTemporalContextResolver temporalContextResolver)
{
    internal async Task<QueryReply<CalendarOccurrenceQueryItem>> ExecuteAsync(
        CalendarOccurrenceQueryRequest.Start request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
            return Failure(CalendarQueryFailures.InvalidInput("The Occurrence query input is invalid."));
        var temporal = temporalContextResolver.Resolve(new CalendarTemporalContextRequest(
            true,
            request.Query.EvaluationTimeZone,
            "Occurrence"));
        if (temporal.Error is not null)
            return Failure(temporal.Error);
        return await queryPolicy.ExecuteStartAsync<CompletedCalendarOccurrenceQuery, CalendarOccurrenceQueryItem>(
            cancellationToken,
            "The Occurrence query exceeded the Calendar limit.",
            async execution =>
            {
                var acquired = await acquisitionExecutor.ExecuteAsync(
                        AcquisitionRequest(request.Query),
                        execution.Token)
                    .ConfigureAwait(false);
                execution.ThrowIfDeadlineExpired();
                return acquired.Error is not null
                    ? CompletedCalendarOccurrenceQuery.Failure(acquired.Error)
                    : Complete(acquired, request.Query, temporal.Context!, execution.Token);
            },
            (completed, token) => completed.Error is not null
                ? Failure(completed.Error)
                : snapshotPublication.Publish(
                    completed.ToSnapshotDraft(),
                    request.PageSize,
                    pageCodec,
                    token)).ConfigureAwait(false);
    }

    private static CompletedCalendarOccurrenceQuery Complete(
        AcquiredCalendarQuery acquired,
        CalendarOccurrenceQuery query,
        TemporalEvaluationContext temporalContext,
        CancellationToken cancellationToken)
    {
        var effectiveQuery = query with { EvaluationTimeZone = temporalContext.TimeZone };
        var occurrences = new List<EvaluatedOccurrence>();
        var observedCount = 0;
        using (CalendarQueryTelemetry.StartPhase("evaluation"))
        {
            foreach (var resource in acquired.Resources)
            {
                var snapshot = resource.Snapshot;
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
                    continue;
                CalendarQueryTelemetry.Add("caldav.query.evaluation_count");
                if (CalendarOccurrenceEvaluator.HasInvalidComponentStructure(snapshot))
                    return CompletedCalendarOccurrenceQuery.Failure(CalendarQueryFailures.RecurrenceUnevaluable());
                var evaluated = CalendarOccurrenceEvaluator.Evaluate(
                    snapshot,
                    effectiveQuery,
                    resource.Document!,
                    resource.TypedCalendar,
                    cancellationToken);
                observedCount += evaluated.ObservedOccurrenceCount;
                var failure = EvaluationFailure(evaluated.Code, observedCount);
                if (failure is not null)
                    return CompletedCalendarOccurrenceQuery.Failure(failure);
                occurrences.AddRange(evaluated.Items);
            }
        }
        var ordered = occurrences
            .OrderBy(item => item.Timing.EvaluatedStartUtc!.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(item => item.Snapshot.Projection.EntityUid, StringComparer.Ordinal)
            .ThenBy(item => CalendarOccurrenceEvaluator.GetIdentitySortKey(item.RecurrenceIdentity), StringComparer.Ordinal)
            .ThenBy(item => item.Snapshot.ResourceHref, StringComparer.Ordinal)
            .ToArray();
        return Project(ordered, acquired.Diagnostics, temporalContext, cancellationToken);
    }

    private static CompletedCalendarOccurrenceQuery Project(
        IReadOnlyList<EvaluatedOccurrence> occurrences,
        IReadOnlyList<QueryDiagnostic> diagnostics,
        TemporalEvaluationContext temporalContext,
        CancellationToken cancellationToken)
    {
        var countFailure = CalendarQuerySnapshotPolicy.Validate(occurrences.Count, 0);
        if (countFailure is not null)
            return CompletedCalendarOccurrenceQuery.Failure(countFailure);
        var projected = ImmutableArray.CreateBuilder<StoredCalendarEntityQueryItem>(occurrences.Count);
        long itemBytes = 0;
        using (CalendarQueryTelemetry.StartPhase("serialization"))
        {
            foreach (var occurrence in occurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = CalendarOccurrenceQueryProjector.Project(occurrence);
                CalendarQueryTelemetry.Add("caldav.query.serialization_count");
                projected.Add(item);
                itemBytes += item.JsonByteCount;
                var byteFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, itemBytes);
                if (byteFailure is not null)
                    return CompletedCalendarOccurrenceQuery.Failure(byteFailure);
            }
        }
        var diagnosticsUtf8 = JsonSerializer.SerializeToUtf8Bytes(diagnostics);
        var temporalContextUtf8 = CalendarTemporalEvaluationContextCodec.Encode(temporalContext);
        var retainedBytes = itemBytes + diagnosticsUtf8.Length + temporalContextUtf8.Length;
        var retainedFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, retainedBytes);
        return retainedFailure is null
            ? CompletedCalendarOccurrenceQuery.Success(
                projected.MoveToImmutable(), diagnosticsUtf8, retainedBytes, temporalContextUtf8)
            : CompletedCalendarOccurrenceQuery.Failure(retainedFailure);
    }

    private static CalendarQueryAcquisitionRequest AcquisitionRequest(CalendarOccurrenceQuery query) => new(
        query.Scope,
        [CalendarEntityKind.Event, CalendarEntityKind.Todo],
        query.From,
        query.To);

    private static bool IsValid(CalendarOccurrenceQueryRequest.Start request) => request.Query is not null
        && request.PageSize is >= 1 and <= CalendarOccurrenceQueryPageCodec.MaximumPageSize
        && request.Query.From.Offset == TimeSpan.Zero
        && request.Query.To.Offset == TimeSpan.Zero
        && request.Query.To > request.Query.From
        && request.Query.To - request.Query.From <= TimeSpan.FromDays(366);

    private static QueryFailure? EvaluationFailure(CalendarOccurrenceEvaluationCode code, int observedCount) => code switch
    {
        CalendarOccurrenceEvaluationCode.Success when observedCount <= CalendarQueryPolicy.MaximumOccurrences => null,
        CalendarOccurrenceEvaluationCode.LimitExhausted or CalendarOccurrenceEvaluationCode.Success => CalendarQueryFailures.Limit(
            "The Occurrence query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: observedCount)),
        CalendarOccurrenceEvaluationCode.TemporalUnresolved => CalendarQueryFailures.TemporalUnresolved(),
        CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable => CalendarQueryFailures.RecurrenceUnevaluable(),
        _ => CalendarQueryFailures.Protocol()
    };

    private static QueryReply<CalendarOccurrenceQueryItem>.Failure Failure(QueryFailure failure) => new(failure);
}

internal sealed class CalendarOccurrenceQueryContinueExecutor(
    CalendarQuerySnapshotReplay snapshotReplay,
    CalendarOccurrenceQueryPageCodec pageCodec)
{
    internal Task<QueryReply<CalendarOccurrenceQueryItem>> ExecuteAsync(
        CalendarOccurrenceQueryRequest.Continue request,
        CancellationToken cancellationToken) => Task.FromResult(
        snapshotReplay.Replay(
            request.Cursor,
            request.PageSize,
            pageCodec,
            cancellationToken));
}

internal sealed record CompletedCalendarOccurrenceQuery(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8,
    QueryFailure? Error)
{
    internal static CompletedCalendarOccurrenceQuery Success(
        ImmutableArray<StoredCalendarEntityQueryItem> items,
        ReadOnlyMemory<byte> diagnosticsUtf8,
        long retainedBytes,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8) =>
        new(items, diagnosticsUtf8, retainedBytes, temporalEvaluationContextUtf8, null);

    internal static CompletedCalendarOccurrenceQuery Failure(QueryFailure error) =>
        new([], ReadOnlyMemory<byte>.Empty, 0, ReadOnlyMemory<byte>.Empty, error);

    internal CalendarQuerySnapshotDraft ToSnapshotDraft() => new(
        Items,
        DiagnosticsUtf8,
        RetainedBytes,
        TemporalEvaluationContextUtf8);
}
