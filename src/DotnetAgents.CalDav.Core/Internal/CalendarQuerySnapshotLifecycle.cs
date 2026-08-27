using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarQuerySnapshotPublication(
    CalendarQueryPolicy queryPolicy,
    CalendarQuerySnapshotWriter snapshotWriter)
{
    internal QueryReply<TItem> Publish<TItem>(
        CalendarQuerySnapshotDraft draft,
        int pageSize,
        ICalendarQueryPageCodec<TItem> pageCodec,
        CancellationToken cancellationToken)
    {
        var snapshot = draft.CreateSnapshot(queryPolicy.GetSnapshotExpiry());
        CalendarQueryPagePlanAdmission planned;
        using (CalendarQueryTelemetry.StartPhase("page_admission"))
        {
            planned = pageCodec.Plan(snapshot, 0, pageSize, cancellationToken);
            CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
            if (planned.Error is not null)
                return Failure<TItem>(planned.Error);
            if (planned.Value!.NextCursor is null)
                return new QueryReply<TItem>.Page(pageCodec.Materialize(snapshot, planned.Value));
        }

        using var reservationPhase = CalendarQueryTelemetry.StartPhase("reservation");
        var reservation = snapshotWriter.TryReserve(snapshot);
        if (!reservation.IsAccepted)
            return Failure<TItem>(CalendarQueryFailures.Busy(reservation.RetryAfterMs!.Value));
        using var lease = reservation.Lease!;
        cancellationToken.ThrowIfCancellationRequested();
        var page = pageCodec.Materialize(snapshot, planned.Value);
        cancellationToken.ThrowIfCancellationRequested();
        QueryReply<TItem> reply = new QueryReply<TItem>.Page(page);
        return lease.Commit()
            ? reply
            : Failure<TItem>(CalendarQueryFailures.UpstreamUnavailable());
    }

    private static QueryReply<TItem>.Failure Failure<TItem>(QueryFailure failure) => new(failure);
}

internal sealed class CalendarQuerySnapshotReplay(
    CalendarQueryCursorAuthenticator cursorAuthenticator,
    CalendarQuerySnapshotReader snapshotReader)
{
    internal QueryReply<TItem> Replay<TItem>(
        string cursorValue,
        int? requestedPageSize,
        ICalendarQueryPageCodec<TItem> pageCodec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = requestedPageSize ?? pageCodec.DefaultPageSize;
        if (string.IsNullOrEmpty(cursorValue)
            || pageSize is < 1
            || pageSize > pageCodec.MaximumPageSize)
        {
            return Failure<TItem>(CalendarQueryFailures.InvalidInput());
        }

        CalendarQueryCursor cursor;
        CalendarQuerySnapshot? snapshot;
        using (CalendarQueryTelemetry.StartPhase("snapshot_lookup"))
        {
            var authentication = cursorAuthenticator.Authenticate(cursorValue, pageCodec.ToolName);
            if (authentication.Code == CalendarQueryCursorAuthenticationCode.Expired)
                return Failure<TItem>(CalendarQueryFailures.CursorExpired());
            if (authentication.Code != CalendarQueryCursorAuthenticationCode.Valid)
                return Failure<TItem>(CalendarQueryFailures.InvalidCursor());
            cursor = authentication.Cursor!;
            snapshot = snapshotReader.Get(cursor.SnapshotId);
            CalendarQueryTelemetry.Add("caldav.query.snapshot_lookup_count");
            if (!MatchesSnapshot(cursor, snapshot))
                return Failure<TItem>(CalendarQueryFailures.InvalidCursor());
        }

        using var pagePhase = CalendarQueryTelemetry.StartPhase("page_admission");
        var admitted = pageCodec.Plan(snapshot!, cursor.Position, pageSize, cancellationToken);
        CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
        return admitted.Error is null
            ? new QueryReply<TItem>.Page(pageCodec.Materialize(snapshot!, admitted.Value!))
            : Failure<TItem>(admitted.Error);
    }

    private bool MatchesSnapshot(CalendarQueryCursor cursor, CalendarQuerySnapshot? snapshot) => snapshot is not null
        && cursor.ExpiresAtUnixMilliseconds == snapshot.ExpiresAt.ToUnixTimeMilliseconds()
        && cursor.Position > 0
        && cursor.Position < snapshot.Items.Length
        && cursorAuthenticator.MatchesTemporalContext(cursor, snapshot.TemporalEvaluationContextUtf8.Span);

    private static QueryReply<TItem>.Failure Failure<TItem>(QueryFailure failure) => new(failure);
}

internal sealed record CalendarQuerySnapshotDraft(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8,
    ReadOnlyMemory<byte> AdditionalContextUtf8 = default)
{
    internal CalendarQuerySnapshot CreateSnapshot(DateTimeOffset expiresAt) => new(
        Guid.NewGuid(),
        expiresAt,
        Items,
        DiagnosticsUtf8,
        RetainedBytes,
        TemporalEvaluationContextUtf8,
        AdditionalContextUtf8);
}
