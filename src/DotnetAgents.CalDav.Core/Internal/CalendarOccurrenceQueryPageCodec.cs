using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarOccurrenceQueryPageCodec(CalendarQueryCursorIssuer cursorIssuer)
{
    internal const string ToolName = "calendar_occurrences.query";
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;
    private const int MaximumCallToolResultBytes = 4 * 1024 * 1024;
    private const int MaximumHumanReadableBytes = 64 * 1024;
    private const string SuccessText = "Occurrence query completed.";

    internal CalendarOccurrencePagePlanAdmission Plan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > MaximumPageSize
            || position < 0
            || snapshot.Items.Length > 0 && position >= snapshot.Items.Length
            || snapshot.Items.Length == 0 && position != 0)
            return CalendarOccurrencePagePlanAdmission.Failure(CalendarQueryFailures.InvalidInput());
        var fixedBudget = MeasureFixedBudget(snapshot.DiagnosticsUtf8, snapshot.TemporalEvaluationContextUtf8);
        if (fixedBudget.HumanReadableBytes > MaximumHumanReadableBytes)
        {
            return CalendarOccurrencePagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "The Occurrence query human-readable result exceeds the safe payload limit."));
        }
        return BuildPlan(snapshot, position, pageSize, fixedBudget.CallToolResultBytes, cancellationToken);
    }

    internal CalendarOccurrencePageAdmission Admit(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var planned = Plan(snapshot, position, pageSize, cancellationToken);
        return planned.Error is null
            ? CalendarOccurrencePageAdmission.Page(Materialize(snapshot, planned.Value!))
            : CalendarOccurrencePageAdmission.Failure(planned.Error);
    }

    internal static QueryPage<CalendarOccurrenceQueryItem> Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarOccurrencePagePlan plan)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("outcome", "success");
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (var item in plan.Items)
                writer.WriteRawValue(item.JsonUtf8.Span, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WritePropertyName("diagnostics");
            writer.WriteRawValue(snapshot.DiagnosticsUtf8.Span, skipInputValidation: true);
            WriteTemporalContext(writer, snapshot.TemporalEvaluationContextUtf8);
            writer.WritePropertyName("pagination");
            writer.WriteStartObject();
            writer.WriteString("mode", "query_result_snapshot");
            if (plan.NextCursor is null)
                writer.WriteNull("nextCursor");
            else
                writer.WriteString("nextCursor", plan.NextCursor);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var structuredContent = document.RootElement.Clone();
        var items = structuredContent.GetProperty("items").EnumerateArray()
            .Select(item => new CalendarOccurrenceQueryItem(item.Clone()))
            .ToArray();
        var diagnostics = structuredContent.GetProperty("diagnostics").Deserialize<QueryDiagnostic[]>() ?? [];
        return new QueryPage<CalendarOccurrenceQueryItem>(
            items,
            diagnostics,
            plan.NextCursor,
            structuredContent,
            SuccessText,
            plan.MeasuredCallToolResultBytes,
            TemporalEvaluationContext: CalendarTemporalEvaluationContextCodec.Decode(
                snapshot.TemporalEvaluationContextUtf8));
    }

    private CalendarOccurrencePagePlanAdmission BuildPlan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        int fixedCallToolResultBytes,
        CancellationToken cancellationToken)
    {
        var admitted = new List<StoredCalendarEntityQueryItem>(Math.Min(pageSize, snapshot.Items.Length - position));
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
                    ToolName,
                    snapshot.Id,
                    candidatePosition,
                    snapshot.ExpiresAt,
                    snapshot.TemporalEvaluationContextUtf8)
                : null;
            var candidateBytes = admittedBytes + stored.JsonByteCount;
            var measured = fixedCallToolResultBytes
                + candidateBytes
                + Math.Max(0, candidateCount - 1)
                + CursorDelta(candidateCursor);
            if (measured > MaximumCallToolResultBytes)
                break;
            admitted.Add(stored);
            admittedBytes = candidateBytes;
            nextCursor = candidateCursor;
        }
        if (admitted.Count == 0 && snapshot.Items.Length > 0)
        {
            return CalendarOccurrencePagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "One Occurrence cannot fit in a result page."));
        }
        var measuredBytes = checked((int)(fixedCallToolResultBytes
            + admittedBytes
            + Math.Max(0, admitted.Count - 1)
            + CursorDelta(nextCursor)));
        return CalendarOccurrencePagePlanAdmission.Page(new CalendarOccurrencePagePlan(
            admitted,
            nextCursor,
            measuredBytes));
    }

    private static CalendarQueryFixedBudget MeasureFixedBudget(
        ReadOnlyMemory<byte> diagnosticsUtf8,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8)
    {
        var callToolResult = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(callToolResult))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", SuccessText);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            WriteEmptyStructuredContent(writer, diagnosticsUtf8, temporalEvaluationContextUtf8);
            writer.WriteBoolean("isError", false);
            writer.WriteNull("_meta");
            writer.WriteNull("resultType");
            writer.WriteEndObject();
        }
        var human = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(human))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Text");
            writer.WriteStartArray();
            writer.WriteStringValue(SuccessText);
            writer.WriteEndArray();
            writer.WritePropertyName("Diagnostics");
            writer.WriteRawValue(diagnosticsUtf8.Span, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return new CalendarQueryFixedBudget(callToolResult.WrittenCount, human.WrittenCount);
    }

    private static void WriteEmptyStructuredContent(
        Utf8JsonWriter writer,
        ReadOnlyMemory<byte> diagnosticsUtf8,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8)
    {
        writer.WriteStartObject();
        writer.WriteString("outcome", "success");
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WritePropertyName("diagnostics");
        writer.WriteRawValue(diagnosticsUtf8.Span, skipInputValidation: true);
        WriteTemporalContext(writer, temporalEvaluationContextUtf8);
        writer.WritePropertyName("pagination");
        writer.WriteStartObject();
        writer.WriteString("mode", "query_result_snapshot");
        writer.WriteNull("nextCursor");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTemporalContext(Utf8JsonWriter writer, ReadOnlyMemory<byte> temporalContext)
    {
        if (temporalContext.IsEmpty)
            return;
        writer.WritePropertyName("temporalEvaluationContext");
        writer.WriteRawValue(temporalContext.Span, skipInputValidation: true);
    }

    private static int CursorDelta(string? cursor) => cursor is null ? 0 : cursor.Length - 2;
}

internal sealed record CalendarOccurrencePageAdmission(
    QueryPage<CalendarOccurrenceQueryItem>? Value,
    QueryFailure? Error)
{
    internal static CalendarOccurrencePageAdmission Page(QueryPage<CalendarOccurrenceQueryItem> value) =>
        new(value, null);

    internal static CalendarOccurrencePageAdmission Failure(QueryFailure error) => new(null, error);
}

internal sealed record CalendarOccurrencePagePlan(
    IReadOnlyList<StoredCalendarEntityQueryItem> Items,
    string? NextCursor,
    int MeasuredCallToolResultBytes);

internal sealed record CalendarOccurrencePagePlanAdmission(CalendarOccurrencePagePlan? Value, QueryFailure? Error)
{
    internal static CalendarOccurrencePagePlanAdmission Page(CalendarOccurrencePagePlan value) => new(value, null);

    internal static CalendarOccurrencePagePlanAdmission Failure(QueryFailure error) => new(null, error);
}
