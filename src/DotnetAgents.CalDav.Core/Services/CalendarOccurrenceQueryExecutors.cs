using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarOccurrenceQueryStartExecutor(
    TimeProvider timeProvider,
    CalendarQuerySnapshotWriter snapshotWriter,
    CalendarOccurrenceQueryPageCodec pageCodec,
    CalendarQueryAcquisitionExecutor acquisitionExecutor,
    CalendarTemporalContextResolver temporalContextResolver)
{
    private const int MaximumOccurrences = 5000;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(10);

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
        var startedAt = timeProvider.GetUtcNow();
        using var deadline = new CancellationTokenSource(ExecutionTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var acquired = await acquisitionExecutor.ExecuteAsync(
                    AcquisitionRequest(request.Query),
                    linked.Token)
                .ConfigureAwait(false);
            ThrowIfDeadlineExpired(startedAt, linked.Token);
            if (acquired.Error is not null)
                return Failure(acquired.Error);
            var completed = Complete(acquired, request.Query, temporal.Context!, linked.Token);
            if (completed.Error is not null)
                return Failure(completed.Error);
            return PublishFirstPage(completed, request.PageSize, linked.Token);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Failure(CalendarQueryFailures.Limit(
                "The Occurrence query exceeded the Calendar limit.",
                new QueryExecutionLimits(CalendarCount: exception.CalendarCount)));
        }
        catch (HttpRequestException exception)
        {
            return Failure(CalendarQueryFailures.FromHttp(exception.StatusCode));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarQueryFailures.ElapsedLimit());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        }
        catch (Exception exception) when (exception is TimeoutException or TimeoutRejectedException
                                           or BrokenCircuitException)
        {
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarQueryFailures.Protocol());
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarQueryFailures.UnsupportedCapability());
        }
        catch (CalendarOccurrenceQueryDeadlineException)
        {
            return Failure(CalendarQueryFailures.ElapsedLimit());
        }
    }

    private static CompletedCalendarOccurrenceQuery Complete(
        AcquiredCalendarQuery acquired,
        CalendarOccurrenceQuery query,
        TemporalEvaluationContext temporalContext,
        CancellationToken cancellationToken)
    {
        var effectiveQuery = query with { EvaluationTimeZone = temporalContext.TimeZone };
        var occurrences = new List<CalendarOccurrenceSnapshot>();
        var observedCount = 0;
        using (CalendarQueryTelemetry.StartPhase("evaluation"))
        {
            foreach (var snapshot in acquired.Snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
                    continue;
                CalendarQueryTelemetry.Add("caldav.query.evaluation_count");
                if (CalendarOccurrenceEvaluator.HasInvalidComponentStructure(snapshot))
                    return CompletedCalendarOccurrenceQuery.Failure(CalendarQueryFailures.RecurrenceUnevaluable());
                var evaluated = CalendarOccurrenceEvaluator.Evaluate(snapshot, effectiveQuery, cancellationToken);
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
        IReadOnlyList<CalendarOccurrenceSnapshot> occurrences,
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

    private QueryReply<CalendarOccurrenceQueryItem> PublishFirstPage(
        CompletedCalendarOccurrenceQuery completed,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            timeProvider.GetUtcNow().Add(SnapshotLifetime),
            completed.Items,
            completed.DiagnosticsUtf8,
            completed.RetainedBytes,
            completed.TemporalEvaluationContextUtf8);
        CalendarOccurrencePagePlanAdmission planned;
        using (CalendarQueryTelemetry.StartPhase("page_admission"))
        {
            planned = pageCodec.Plan(snapshot, 0, pageSize, cancellationToken);
            CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
            if (planned.Error is not null)
                return Failure(planned.Error);
            if (planned.Value!.NextCursor is null)
                return new QueryReply<CalendarOccurrenceQueryItem>.Page(
                    CalendarOccurrenceQueryPageCodec.Materialize(snapshot, planned.Value));
        }
        using var reservationPhase = CalendarQueryTelemetry.StartPhase("reservation");
        var reservation = snapshotWriter.TryReserve(snapshot);
        if (!reservation.IsAccepted)
            return Failure(CalendarQueryFailures.Busy(reservation.RetryAfterMs!.Value));
        using var lease = reservation.Lease!;
        cancellationToken.ThrowIfCancellationRequested();
        var page = CalendarOccurrenceQueryPageCodec.Materialize(snapshot, planned.Value);
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.Commit())
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        return new QueryReply<CalendarOccurrenceQueryItem>.Page(page);
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

    private static QueryFailure? EvaluationFailure(CalendarOccurrenceQueryCode code, int observedCount) => code switch
    {
        CalendarOccurrenceQueryCode.Success when observedCount <= MaximumOccurrences => null,
        CalendarOccurrenceQueryCode.LimitExhausted or CalendarOccurrenceQueryCode.Success => CalendarQueryFailures.Limit(
            "The Occurrence query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: observedCount)),
        CalendarOccurrenceQueryCode.TemporalUnresolved => CalendarQueryFailures.TemporalUnresolved(),
        CalendarOccurrenceQueryCode.RecurrenceUnevaluable => CalendarQueryFailures.RecurrenceUnevaluable(),
        _ => CalendarQueryFailures.Protocol()
    };

    private void ThrowIfDeadlineExpired(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeProvider.GetUtcNow() >= startedAt.Add(ExecutionTimeout))
            throw new CalendarOccurrenceQueryDeadlineException();
    }

    private static QueryReply<CalendarOccurrenceQueryItem>.Failure Failure(QueryFailure failure) => new(failure);

    private sealed class CalendarOccurrenceQueryDeadlineException : Exception;
}

internal sealed class CalendarOccurrenceQueryContinueExecutor(
    CalendarQueryCursorAuthenticator cursorAuthenticator,
    CalendarQuerySnapshotReader snapshotReader,
    CalendarOccurrenceQueryPageCodec pageCodec)
{
    internal Task<QueryReply<CalendarOccurrenceQueryItem>> ExecuteAsync(
        CalendarOccurrenceQueryRequest.Continue request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = request.PageSize ?? CalendarOccurrenceQueryPageCodec.DefaultPageSize;
        if (string.IsNullOrEmpty(request.Cursor)
            || pageSize is < 1 or > CalendarOccurrenceQueryPageCodec.MaximumPageSize)
            return Task.FromResult<QueryReply<CalendarOccurrenceQueryItem>>(Failure(CalendarQueryFailures.InvalidInput()));
        CalendarQueryCursor cursor;
        CalendarQuerySnapshot? snapshot;
        using (CalendarQueryTelemetry.StartPhase("snapshot_lookup"))
        {
            var authentication = cursorAuthenticator.Authenticate(request.Cursor, CalendarOccurrenceQueryPageCodec.ToolName);
            if (authentication.Code == CalendarQueryCursorAuthenticationCode.Expired)
                return Task.FromResult<QueryReply<CalendarOccurrenceQueryItem>>(Failure(CalendarQueryFailures.CursorExpired()));
            if (authentication.Code != CalendarQueryCursorAuthenticationCode.Valid)
                return Task.FromResult<QueryReply<CalendarOccurrenceQueryItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
            cursor = authentication.Cursor!;
            snapshot = snapshotReader.Get(cursor.SnapshotId);
            CalendarQueryTelemetry.Add("caldav.query.snapshot_lookup_count");
            if (!MatchesSnapshot(cursor, snapshot))
                return Task.FromResult<QueryReply<CalendarOccurrenceQueryItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
        }
        using var pagePhase = CalendarQueryTelemetry.StartPhase("page_admission");
        var admitted = pageCodec.Admit(snapshot!, cursor.Position, pageSize, cancellationToken);
        CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
        return Task.FromResult(admitted.Error is null
            ? new QueryReply<CalendarOccurrenceQueryItem>.Page(admitted.Value!) as QueryReply<CalendarOccurrenceQueryItem>
            : Failure(admitted.Error));
    }

    private bool MatchesSnapshot(CalendarQueryCursor cursor, CalendarQuerySnapshot? snapshot) => snapshot is not null
        && cursor.ExpiresAtUnixMilliseconds == snapshot.ExpiresAt.ToUnixTimeMilliseconds()
        && cursor.Position > 0
        && cursor.Position < snapshot.Items.Length
        && cursorAuthenticator.MatchesTemporalContext(cursor, snapshot.TemporalEvaluationContextUtf8.Span);

    private static QueryReply<CalendarOccurrenceQueryItem>.Failure Failure(QueryFailure failure) => new(failure);
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
}
