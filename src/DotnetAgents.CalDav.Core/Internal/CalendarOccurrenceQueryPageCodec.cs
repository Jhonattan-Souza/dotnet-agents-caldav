using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarOccurrenceQueryPageCodec : ICalendarQueryPageCodec<CalendarOccurrenceQueryItem>
{
    internal const string ToolName = "calendar_occurrences.query";
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;
    private const int MaximumCallToolResultBytes = 4 * 1024 * 1024;
    private const int MaximumHumanReadableBytes = 64 * 1024;
    private const string SuccessText = "Occurrence query completed.";
    private static readonly CalendarQueryPageConstraints PageConstraints = new(
        DefaultPageSize,
        MaximumPageSize,
        MaximumCallToolResultBytes,
        MaximumHumanReadableBytes,
        "The Occurrence query human-readable result exceeds the safe payload limit.",
        "One Occurrence cannot fit in a result page.");

    string ICalendarQueryPageCodec<CalendarOccurrenceQueryItem>.ToolName => ToolName;

    CalendarQueryPageConstraints ICalendarQueryPageCodec<CalendarOccurrenceQueryItem>.Constraints => PageConstraints;

    CalendarQueryFixedBudget ICalendarQueryPageCodec<CalendarOccurrenceQueryItem>.MeasureFixedBudget(
        CalendarQuerySnapshot snapshot) => MeasureFixedBudget(
        snapshot.DiagnosticsUtf8,
        snapshot.TemporalEvaluationContextUtf8);

    QueryPage<CalendarOccurrenceQueryItem> ICalendarQueryPageCodec<CalendarOccurrenceQueryItem>.Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan) => Materialize(snapshot, plan);

    internal static QueryPage<CalendarOccurrenceQueryItem> Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan)
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

}
