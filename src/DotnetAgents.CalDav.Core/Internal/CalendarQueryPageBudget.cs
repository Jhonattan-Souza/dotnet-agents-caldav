using System.Buffers;
using System.Text.Json;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarQueryPageBudget
{
    internal static CalendarQueryFixedBudget Measure(
        ReadOnlyMemory<byte> structured, ReadOnlyMemory<byte> diagnostics)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", structured.Span);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            writer.WriteRawValue(structured.Span, skipInputValidation: true);
            writer.WriteBoolean("isError", false);
            writer.WriteNull("_meta");
            writer.WriteNull("resultType");
            writer.WriteEndObject();
        }
        return new CalendarQueryFixedBudget(buffer.WrittenCount, diagnostics.Length);
    }

    internal static int ItemHumanBytes(ReadOnlyMemory<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        var item = document.RootElement;
        if (item.ValueKind != JsonValueKind.Object)
            return 0;
        var bytes = DiagnosticBytes(item);
        if (item.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object)
            bytes += DiagnosticBytes(snapshot);
        return bytes;
    }

    private static int DiagnosticBytes(JsonElement value) =>
        value.TryGetProperty("diagnostics", out var diagnostics)
            ? JsonSerializer.SerializeToUtf8Bytes(diagnostics).Length
            : 0;

    // Prepared once with each item. No escaped copy is retained in the snapshot.
    internal static int EscapedBytes(ReadOnlySpan<byte> utf8) =>
        JsonEncodedText.Encode(utf8).EncodedUtf8Bytes.Length;
}
