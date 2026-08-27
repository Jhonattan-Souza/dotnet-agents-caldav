using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarQueryPageAdmission(CalendarQueryCursorIssuer cursorIssuer)
{
    internal static bool IsValidPageSize<TItem>(
        int pageSize,
        ICalendarQueryPageCodec<TItem> pageCodec) => pageSize is >= 1
        && pageSize <= pageCodec.Constraints.MaximumPageSize;

    internal CalendarQueryPagePlanAdmission Plan<TItem>(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        ICalendarQueryPageCodec<TItem> pageCodec,
        CancellationToken cancellationToken)
    {
        var constraints = pageCodec.Constraints;
        if (!IsValidPageSize(pageSize, pageCodec)
            || position < 0
            || snapshot.Items.Length > 0 && position >= snapshot.Items.Length
            || snapshot.Items.Length == 0 && position != 0)
        {
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.InvalidInput());
        }

        var fixedBudget = pageCodec.MeasureFixedBudget(snapshot);
        if (fixedBudget.HumanReadableBytes > constraints.MaximumHumanReadableBytes)
        {
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                constraints.HumanReadablePayloadTooLargeMessage));
        }

        var admitted = new List<StoredCalendarEntityQueryItem>(
            Math.Min(pageSize, snapshot.Items.Length - position));
        var admittedBytes = 0L;
        string? nextCursor = null;
        while (admitted.Count < pageSize && position + admitted.Count < snapshot.Items.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = snapshot.Items[position + admitted.Count];
            var candidateCount = admitted.Count + 1;
            var candidatePosition = position + candidateCount;
            var candidateCursor = candidatePosition < snapshot.Items.Length
                ? cursorIssuer.Issue(
                    pageCodec.ToolName,
                    snapshot.Id,
                    candidatePosition,
                    snapshot.ExpiresAt,
                    snapshot.TemporalEvaluationContextUtf8)
                : null;
            var candidateBytes = admittedBytes + stored.JsonByteCount;
            var measured = fixedBudget.CallToolResultBytes
                + candidateBytes
                + Math.Max(0, candidateCount - 1)
                + CursorDelta(candidateCursor);
            if (measured > constraints.MaximumCallToolResultBytes)
                break;
            admitted.Add(stored);
            admittedBytes = candidateBytes;
            nextCursor = candidateCursor;
        }

        if (admitted.Count == 0 && snapshot.Items.Length > 0)
        {
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                constraints.ItemPayloadTooLargeMessage));
        }

        var measuredBytes = checked((int)(fixedBudget.CallToolResultBytes
            + admittedBytes
            + Math.Max(0, admitted.Count - 1)
            + CursorDelta(nextCursor)));
        return CalendarQueryPagePlanAdmission.Page(new CalendarQueryPagePlan(
            admitted,
            nextCursor,
            measuredBytes));
    }

    private static int CursorDelta(string? cursor) => cursor is null ? 0 : cursor.Length - 2;
}

internal sealed class CalendarQuerySnapshotPublication(
    CalendarQueryPolicy queryPolicy,
    CalendarQuerySnapshotWriter snapshotWriter,
    CalendarQueryPageAdmission pageAdmission)
{
    internal QueryReply<TItem> Publish<TItem>(
        CalendarQuerySnapshotDraft draft,
        int pageSize,
        ICalendarQueryPageCodec<TItem> pageCodec,
        CancellationToken cancellationToken)
    {
        var snapshot = draft.CreateSnapshot(queryPolicy.GetSnapshotExpiry());
        CalendarQueryPagePlanAdmission planned;
        using (CalendarQueryTelemetry.StartPhase(CalendarQueryPhase.PageAdmission))
        {
            planned = pageAdmission.Plan(snapshot, 0, pageSize, pageCodec, cancellationToken);
            CalendarQueryTelemetry.Add(CalendarQueryCounter.PageAdmission);
            if (planned.Error is not null)
                return Failure<TItem>(planned.Error);
            if (planned.Value!.NextCursor is null)
                return new QueryReply<TItem>.Page(pageCodec.Materialize(snapshot, planned.Value));
        }

        using var reservationPhase = CalendarQueryTelemetry.StartPhase(CalendarQueryPhase.Reservation);
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
    CalendarQuerySnapshotReader snapshotReader,
    CalendarQueryPageAdmission pageAdmission)
{
    internal QueryReply<TItem> Replay<TItem>(
        string cursorValue,
        int? requestedPageSize,
        ICalendarQueryPageCodec<TItem> pageCodec,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = requestedPageSize ?? pageCodec.Constraints.DefaultPageSize;
        if (string.IsNullOrEmpty(cursorValue)
            || !CalendarQueryPageAdmission.IsValidPageSize(pageSize, pageCodec))
        {
            return Failure<TItem>(CalendarQueryFailures.InvalidInput());
        }

        CalendarQueryCursor cursor;
        CalendarQuerySnapshot? snapshot;
        using (CalendarQueryTelemetry.StartPhase(CalendarQueryPhase.SnapshotLookup))
        {
            var authentication = cursorAuthenticator.Authenticate(cursorValue, pageCodec.ToolName);
            if (authentication.Code == CalendarQueryCursorAuthenticationCode.Expired)
                return Failure<TItem>(CalendarQueryFailures.CursorExpired());
            if (authentication.Code != CalendarQueryCursorAuthenticationCode.Valid)
                return Failure<TItem>(CalendarQueryFailures.InvalidCursor());
            cursor = authentication.Cursor!;
            snapshot = snapshotReader.Get(cursor.SnapshotId);
            CalendarQueryTelemetry.Add(CalendarQueryCounter.SnapshotLookup);
            if (!MatchesSnapshot(cursor, snapshot))
                return Failure<TItem>(CalendarQueryFailures.InvalidCursor());
        }

        using var pagePhase = CalendarQueryTelemetry.StartPhase(CalendarQueryPhase.PageAdmission);
        var admitted = pageAdmission.Plan(snapshot!, cursor.Position, pageSize, pageCodec, cancellationToken);
        CalendarQueryTelemetry.Add(CalendarQueryCounter.PageAdmission);
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
