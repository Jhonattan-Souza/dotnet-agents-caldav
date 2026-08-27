using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarTodoQueryPageCodec(CalendarQueryCursorIssuer cursorIssuer)
    : ICalendarQueryPageCodec<CalendarTodoQueryPageItem>
{
    internal const string ToolName = "todos.query";
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;
    internal const int MaximumCallToolResultBytes = 4 * 1024 * 1024;
    internal const int MaximumHumanReadableBytes = 64 * 1024;
    internal const string SuccessText = "Compact To-do query completed.";

    string ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.ToolName => ToolName;

    int ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.DefaultPageSize => DefaultPageSize;

    int ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.MaximumPageSize => MaximumPageSize;

    CalendarQueryPagePlanAdmission ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.Plan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken) => Plan(snapshot, position, pageSize, cancellationToken);

    QueryPage<CalendarTodoQueryPageItem> ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan) => Materialize(snapshot, plan);

    internal CalendarQueryPagePlanAdmission Plan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (IsInvalidRequest(snapshot.Items.Length, position, pageSize))
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.InvalidInput());
        var fixedBudget = MeasureFixedBudget(snapshot);
        if (fixedBudget.HumanReadableBytes > MaximumHumanReadableBytes)
        {
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "The To-do query human-readable result exceeds the safe payload limit."));
        }

        return BuildPlan(snapshot, position, pageSize, fixedBudget.CallToolResultBytes, cancellationToken);
    }

    internal static QueryPage<CalendarTodoQueryPageItem> Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteStructuredContent(writer, snapshot, plan.Items, plan.NextCursor);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var structuredContent = document.RootElement.Clone();
        var items = structuredContent.GetProperty("items").EnumerateArray()
            .Select(item => new CalendarTodoQueryPageItem(item.Clone()))
            .ToArray();
        var diagnostics = structuredContent.GetProperty("diagnostics").Deserialize<QueryDiagnostic[]>() ?? [];
        return new QueryPage<CalendarTodoQueryPageItem>(
            items,
            diagnostics,
            plan.NextCursor,
            structuredContent,
            SuccessText,
            plan.MeasuredCallToolResultBytes,
            TemporalEvaluationContext: CalendarTemporalEvaluationContextCodec.Decode(
                snapshot.TemporalEvaluationContextUtf8));
    }

    private CalendarQueryPagePlanAdmission BuildPlan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        int fixedCallToolResultBytes,
        CancellationToken cancellationToken)
    {
        var admitted = new List<StoredCalendarEntityQueryItem>(Math.Min(pageSize, snapshot.Items.Length - position));
        long admittedBytes = 0;
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
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "One To-do cannot fit in a result page."));
        }

        var measuredBytes = checked((int)(fixedCallToolResultBytes
            + admittedBytes
            + Math.Max(0, admitted.Count - 1)
            + CursorDelta(nextCursor)));
        return CalendarQueryPagePlanAdmission.Page(new CalendarQueryPagePlan(
            admitted,
            nextCursor,
            measuredBytes));
    }

    private static CalendarQueryFixedBudget MeasureFixedBudget(CalendarQuerySnapshot snapshot)
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
            WriteStructuredContent(writer, snapshot, [], null);
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
            writer.WriteRawValue(snapshot.DiagnosticsUtf8.Span, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return new CalendarQueryFixedBudget(callToolResult.WrittenCount, human.WrittenCount);
    }

    private static void WriteStructuredContent(
        Utf8JsonWriter writer,
        CalendarQuerySnapshot snapshot,
        IReadOnlyList<StoredCalendarEntityQueryItem> items,
        string? nextCursor)
    {
        writer.WriteStartObject();
        writer.WriteString("outcome", "success");
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        foreach (var item in items)
            writer.WriteRawValue(item.JsonUtf8.Span, skipInputValidation: true);
        writer.WriteEndArray();
        writer.WritePropertyName("diagnostics");
        writer.WriteRawValue(snapshot.DiagnosticsUtf8.Span, skipInputValidation: true);
        writer.WritePropertyName("excludedIndeterminateCount");
        writer.WriteRawValue(snapshot.AdditionalContextUtf8.IsEmpty
            ? "0"u8
            : snapshot.AdditionalContextUtf8.Span,
            skipInputValidation: true);
        if (!snapshot.TemporalEvaluationContextUtf8.IsEmpty)
        {
            writer.WritePropertyName("temporalEvaluationContext");
            writer.WriteRawValue(snapshot.TemporalEvaluationContextUtf8.Span, skipInputValidation: true);
        }
        writer.WritePropertyName("pagination");
        writer.WriteStartObject();
        writer.WriteString("mode", "query_result_snapshot");
        if (nextCursor is null)
            writer.WriteNull("nextCursor");
        else
            writer.WriteString("nextCursor", nextCursor);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static bool IsInvalidRequest(int itemCount, int position, int pageSize) =>
        pageSize is < 1 or > MaximumPageSize
        || position < 0
        || itemCount > 0 && position >= itemCount
        || itemCount == 0 && position != 0;

    private static int CursorDelta(string? cursor) => cursor is null ? 0 : cursor.Length - 2;
}
