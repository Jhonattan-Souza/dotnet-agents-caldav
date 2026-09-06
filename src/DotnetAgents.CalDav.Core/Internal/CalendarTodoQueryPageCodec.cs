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
            structuredContent.GetRawText(),
            plan.MeasuredCallToolResultBytes,
            TemporalEvaluationContext: CalendarTemporalEvaluationContextCodec.Decode(
                snapshot.TemporalEvaluationContextUtf8));
    }

    private static CalendarQueryFixedBudget MeasureFixedBudget(CalendarQuerySnapshot snapshot)
    {
        var structured = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(structured))
            WriteStructuredContent(writer, snapshot, [], null);
        return CalendarQueryPageBudget.Measure(structured.WrittenMemory, snapshot.DiagnosticsUtf8);
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
