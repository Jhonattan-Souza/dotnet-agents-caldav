using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarTodoQueryPageCodec : ICalendarQueryPageCodec<CalendarTodoQueryPageItem>
{
    internal const string ToolName = "todos.query";
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;
    internal const int MaximumCallToolResultBytes = 4 * 1024 * 1024;
    internal const int MaximumHumanReadableBytes = 64 * 1024;
    internal const string SuccessText = "Compact To-do query completed.";
    private static readonly CalendarQueryPageConstraints PageConstraints = new(
        DefaultPageSize,
        MaximumPageSize,
        MaximumCallToolResultBytes,
        MaximumHumanReadableBytes,
        "The To-do query human-readable result exceeds the safe payload limit.",
        "One To-do cannot fit in a result page.");

    string ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.ToolName => ToolName;

    CalendarQueryPageConstraints ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.Constraints => PageConstraints;

    CalendarQueryFixedBudget ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.MeasureFixedBudget(
        CalendarQuerySnapshot snapshot) => MeasureFixedBudget(snapshot);

    QueryPage<CalendarTodoQueryPageItem> ICalendarQueryPageCodec<CalendarTodoQueryPageItem>.Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan) => Materialize(snapshot, plan);

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

}
