using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarEntityQueryPageCodec : ICalendarQueryPageCodec<CalendarEntityQueryItem>
{
    private readonly CalendarQueryCursorIssuer _cursorIssuer;
    private readonly CalendarQueryPageWorkCounter? _workCounter;

    internal CalendarEntityQueryPageCodec(
        CalendarQueryCursorIssuer cursorIssuer,
        CalendarQueryPageWorkCounter? workCounter = null)
    {
        _cursorIssuer = cursorIssuer;
        _workCounter = workCounter;
    }

    internal const string ToolName = "calendar_entities.query";
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;
    internal const int MaximumCallToolResultBytes = 4 * 1024 * 1024;
    internal const int MaximumHumanReadableBytes = 64 * 1024;
    internal const string SuccessText = "Calendar Entity query completed.";

    string ICalendarQueryPageCodec<CalendarEntityQueryItem>.ToolName => ToolName;

    int ICalendarQueryPageCodec<CalendarEntityQueryItem>.DefaultPageSize => DefaultPageSize;

    int ICalendarQueryPageCodec<CalendarEntityQueryItem>.MaximumPageSize => MaximumPageSize;

    CalendarQueryPagePlanAdmission ICalendarQueryPageCodec<CalendarEntityQueryItem>.Plan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken) => Plan(snapshot, position, pageSize, cancellationToken);

    QueryPage<CalendarEntityQueryItem> ICalendarQueryPageCodec<CalendarEntityQueryItem>.Materialize(
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
        var fixedBudget = MeasureFixedBudget(snapshot.DiagnosticsUtf8, snapshot.TemporalEvaluationContextUtf8);
        _workCounter?.RecordAdmissionEnvelopeSerialization();
        if (fixedBudget.HumanReadableBytes > MaximumHumanReadableBytes)
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "The Calendar Entity query human-readable result exceeds the safe payload limit."));

        return BuildPlan(snapshot, position, pageSize, fixedBudget.CallToolResultBytes, cancellationToken);
    }

    private CalendarQueryPagePlanAdmission BuildPlan(
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
            var hasMore = candidatePosition < snapshot.Items.Length;
            var candidateCursor = hasMore
                ? _cursorIssuer.Issue(
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
            return CalendarQueryPagePlanAdmission.Failure(CalendarQueryFailures.PayloadTooLarge(
                "One Calendar Entity cannot fit in a result page."));
        var measuredBytes = checked((int)(fixedCallToolResultBytes
            + admittedBytes
            + Math.Max(0, admitted.Count - 1)
            + CursorDelta(nextCursor)));
        return CalendarQueryPagePlanAdmission.Page(new CalendarQueryPagePlan(admitted, nextCursor, measuredBytes));
    }

    private static bool IsInvalidRequest(int itemCount, int position, int pageSize) =>
        pageSize is < 1 or > MaximumPageSize
        || position < 0
        || itemCount > 0 && position >= itemCount
        || itemCount == 0 && position != 0;

    internal QueryPage<CalendarEntityQueryItem> Materialize(
        CalendarQuerySnapshot snapshot,
        CalendarQueryPagePlan plan)
    {
        _workCounter?.RecordFinalMaterialization();
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
            .Select(item => new CalendarEntityQueryItem(item.Clone()))
            .ToArray();
        var diagnostics = structuredContent.GetProperty("diagnostics").Deserialize<QueryDiagnostic[]>() ?? [];
        return new QueryPage<CalendarEntityQueryItem>(
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

    private static void WriteTemporalContext(
        Utf8JsonWriter writer,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8)
    {
        if (temporalEvaluationContextUtf8.IsEmpty)
            return;
        writer.WritePropertyName("temporalEvaluationContext");
        writer.WriteRawValue(temporalEvaluationContextUtf8.Span, skipInputValidation: true);
    }

    private static int CursorDelta(string? cursor) => cursor is null ? 0 : cursor.Length - 2;
}

internal sealed class CalendarQueryPageWorkCounter(Action? onFinalMaterialization = null)
{
    private int _admissionEnvelopeSerializationCount;
    private int _finalMaterializationCount;

    internal int AdmissionEnvelopeSerializationCount => Volatile.Read(ref _admissionEnvelopeSerializationCount);

    internal int FinalMaterializationCount => Volatile.Read(ref _finalMaterializationCount);

    internal void RecordAdmissionEnvelopeSerialization() =>
        Interlocked.Increment(ref _admissionEnvelopeSerializationCount);

    internal void RecordFinalMaterialization()
    {
        Interlocked.Increment(ref _finalMaterializationCount);
        onFinalMaterialization?.Invoke();
    }
}
