using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarTodoQueryStartExecutor(
    CalendarQueryPolicy queryPolicy,
    CalendarQuerySnapshotWriter snapshotWriter,
    CalendarTodoQueryPageCodec pageCodec,
    CalendarQueryAcquisitionExecutor acquisitionExecutor,
    CalendarTemporalContextResolver temporalContextResolver)
{
    internal async Task<QueryReply<CalendarTodoQueryPageItem>> ExecuteAsync(
        CalendarTodoQueryRequest.Start request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
            return Failure(CalendarQueryFailures.InvalidInput());
        var temporal = temporalContextResolver.Resolve(new CalendarTemporalContextRequest(
            true,
            request.Query.EvaluationTimeZone,
            "To-do"));
        if (temporal.Error is not null)
            return Failure(temporal.Error);
        return await queryPolicy.ExecuteStartAsync<CompletedCalendarTodoQuery, CalendarTodoQueryPageItem>(
            cancellationToken,
            "The To-do query exceeded the Calendar limit.",
            execution => CompleteAsync(request, temporal.Context!, execution),
            (completed, token) => completed.Error is not null
                ? Failure(completed.Error)
                : PublishFirstPage(completed, request.PageSize, token)).ConfigureAwait(false);
    }

    private async Task<CompletedCalendarTodoQuery> CompleteAsync(
        CalendarTodoQueryRequest.Start request,
        TemporalEvaluationContext temporalContext,
        CalendarQueryPolicy.CalendarQueryExecution execution)
    {
        var acquired = await acquisitionExecutor.ExecuteAsync(
            new CalendarQueryAcquisitionRequest(
                request.Query.Scope,
                [CalendarEntityKind.Todo],
                null,
                null),
            execution.Token).ConfigureAwait(false);
        execution.ThrowIfDeadlineExpired();
        if (acquired.Error is not null)
            return CompletedCalendarTodoQuery.Failure(acquired.Error);
        CalendarTodoEvaluationResult evaluated;
        using (CalendarQueryTelemetry.StartPhase("evaluation"))
        {
            evaluated = CalendarTodoSnapshotEvaluator.Evaluate(
                acquired.Resources,
                request.Query with { EvaluationTimeZone = temporalContext.TimeZone },
                request.Projection,
                execution.Token);
        }
        if (evaluated.Error is not null)
            return CompletedCalendarTodoQuery.Failure(evaluated.Error);
        var diagnosticsUtf8 = JsonSerializer.SerializeToUtf8Bytes(acquired.Diagnostics);
        var temporalContextUtf8 = CalendarTemporalEvaluationContextCodec.Encode(temporalContext);
        var additionalContextUtf8 = Encoding.UTF8.GetBytes(
            evaluated.ExcludedIndeterminateCount.ToString(CultureInfo.InvariantCulture));
        var retainedBytes = evaluated.ProjectedBytes
            + diagnosticsUtf8.Length
            + temporalContextUtf8.Length
            + additionalContextUtf8.Length;
        var retainedFailure = CalendarQuerySnapshotPolicy.Validate(evaluated.Items.Length, retainedBytes);
        return retainedFailure is null
            ? CompletedCalendarTodoQuery.Success(
                evaluated.Items,
                diagnosticsUtf8,
                retainedBytes,
                temporalContextUtf8,
                additionalContextUtf8)
            : CompletedCalendarTodoQuery.Failure(retainedFailure);
    }

    private QueryReply<CalendarTodoQueryPageItem> PublishFirstPage(
        CompletedCalendarTodoQuery completed,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            queryPolicy.GetSnapshotExpiry(),
            completed.Items,
            completed.DiagnosticsUtf8,
            completed.RetainedBytes,
            completed.TemporalEvaluationContextUtf8,
            completed.AdditionalContextUtf8);
        CalendarTodoPagePlanAdmission planned;
        using (CalendarQueryTelemetry.StartPhase("page_admission"))
        {
            planned = pageCodec.Plan(snapshot, 0, pageSize, cancellationToken);
            CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
            if (planned.Error is not null)
                return Failure(planned.Error);
            if (planned.Value!.NextCursor is null)
            {
                return new QueryReply<CalendarTodoQueryPageItem>.Page(
                    CalendarTodoQueryPageCodec.Materialize(snapshot, planned.Value));
            }
        }
        using var reservationPhase = CalendarQueryTelemetry.StartPhase("reservation");
        var reservation = snapshotWriter.TryReserve(snapshot);
        if (!reservation.IsAccepted)
            return Failure(CalendarQueryFailures.Busy(reservation.RetryAfterMs!.Value));
        using var lease = reservation.Lease!;
        cancellationToken.ThrowIfCancellationRequested();
        var page = CalendarTodoQueryPageCodec.Materialize(snapshot, planned.Value);
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.Commit())
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        return new QueryReply<CalendarTodoQueryPageItem>.Page(page);
    }

    private static bool IsValid(CalendarTodoQueryRequest.Start request) => request.Query is not null
        && IsValidProjection(request.Projection)
        && request.PageSize is >= 1 and <= CalendarTodoQueryPageCodec.MaximumPageSize
        && IsValidQuery(request.Query);

    private static bool IsValidProjection(IReadOnlyList<CalendarTodoProjectionField> projection) =>
        projection is { Count: >= 1 }
        && projection.Distinct().Count() == projection.Count
        && projection.All(Enum.IsDefined);

    private static bool IsValidQuery(CalendarTodoQuery query) =>
        query.Scope.Mode is CalendarEntityScopeMode.Selected or CalendarEntityScopeMode.All
        && IsValidWindow(query.From, query.To)
        && IsValidWindow(query.DueFrom, query.DueTo)
        && IsValidStates(query.CompletionStates);

    private static bool IsValidStates(IReadOnlyList<CalendarTodoCompletionState>? states) => states is null
        || states.Count >= 1
            && states.Distinct().Count() == states.Count
            && states.All(Enum.IsDefined);

    private static bool IsValidWindow(DateTimeOffset? from, DateTimeOffset? to) => from is null || to is null
        ? from is null && to is null
        : from.Value.Offset == TimeSpan.Zero
            && to.Value.Offset == TimeSpan.Zero
            && to > from
            && to - from <= TimeSpan.FromDays(366);

    private static QueryReply<CalendarTodoQueryPageItem>.Failure Failure(QueryFailure failure) => new(failure);
}

internal sealed class CalendarTodoQueryContinueExecutor(
    CalendarQueryCursorAuthenticator cursorAuthenticator,
    CalendarQuerySnapshotReader snapshotReader,
    CalendarTodoQueryPageCodec pageCodec)
{
    internal Task<QueryReply<CalendarTodoQueryPageItem>> ExecuteAsync(
        CalendarTodoQueryRequest.Continue request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = request.PageSize ?? CalendarTodoQueryPageCodec.DefaultPageSize;
        if (string.IsNullOrEmpty(request.Cursor)
            || pageSize is < 1 or > CalendarTodoQueryPageCodec.MaximumPageSize)
            return Task.FromResult<QueryReply<CalendarTodoQueryPageItem>>(Failure(CalendarQueryFailures.InvalidInput()));
        CalendarQueryCursor cursor;
        CalendarQuerySnapshot? snapshot;
        using (CalendarQueryTelemetry.StartPhase("snapshot_lookup"))
        {
            var authentication = cursorAuthenticator.Authenticate(request.Cursor, CalendarTodoQueryPageCodec.ToolName);
            if (authentication.Code == CalendarQueryCursorAuthenticationCode.Expired)
                return Task.FromResult<QueryReply<CalendarTodoQueryPageItem>>(Failure(CalendarQueryFailures.CursorExpired()));
            if (authentication.Code != CalendarQueryCursorAuthenticationCode.Valid)
                return Task.FromResult<QueryReply<CalendarTodoQueryPageItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
            cursor = authentication.Cursor!;
            snapshot = snapshotReader.Get(cursor.SnapshotId);
            CalendarQueryTelemetry.Add("caldav.query.snapshot_lookup_count");
            if (!MatchesSnapshot(cursor, snapshot))
                return Task.FromResult<QueryReply<CalendarTodoQueryPageItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
        }
        using var pagePhase = CalendarQueryTelemetry.StartPhase("page_admission");
        var admitted = pageCodec.Admit(snapshot!, cursor.Position, pageSize, cancellationToken);
        CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
        return Task.FromResult(admitted.Error is null
            ? new QueryReply<CalendarTodoQueryPageItem>.Page(admitted.Value!) as QueryReply<CalendarTodoQueryPageItem>
            : Failure(admitted.Error));
    }

    private bool MatchesSnapshot(CalendarQueryCursor cursor, CalendarQuerySnapshot? snapshot) => snapshot is not null
        && cursor.ExpiresAtUnixMilliseconds == snapshot.ExpiresAt.ToUnixTimeMilliseconds()
        && cursor.Position > 0
        && cursor.Position < snapshot.Items.Length
        && cursorAuthenticator.MatchesTemporalContext(cursor, snapshot.TemporalEvaluationContextUtf8.Span);

    private static QueryReply<CalendarTodoQueryPageItem>.Failure Failure(QueryFailure failure) => new(failure);
}

internal sealed record CompletedCalendarTodoQuery(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8,
    ReadOnlyMemory<byte> AdditionalContextUtf8,
    QueryFailure? Error)
{
    internal static CompletedCalendarTodoQuery Success(
        ImmutableArray<StoredCalendarEntityQueryItem> items,
        ReadOnlyMemory<byte> diagnosticsUtf8,
        long retainedBytes,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8,
        ReadOnlyMemory<byte> additionalContextUtf8) => new(
        items,
        diagnosticsUtf8,
        retainedBytes,
        temporalEvaluationContextUtf8,
        additionalContextUtf8,
        null);

    internal static CompletedCalendarTodoQuery Failure(QueryFailure error) => new(
        [],
        ReadOnlyMemory<byte>.Empty,
        0,
        ReadOnlyMemory<byte>.Empty,
        ReadOnlyMemory<byte>.Empty,
        error);
}
