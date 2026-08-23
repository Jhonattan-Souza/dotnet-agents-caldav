using System.Buffers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarTemporalEvaluationContextCodec
{
    internal static ReadOnlyMemory<byte> Encode(TemporalEvaluationContext? context)
    {
        if (context is null)
            return ReadOnlyMemory<byte>.Empty;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timeZone", context.TimeZone);
            writer.WriteString("source", context.Source == TemporalEvaluationContextSource.Caller
                ? "caller"
                : "configuration");
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }

    internal static TemporalEvaluationContext? Decode(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.IsEmpty)
            return null;
        using var document = JsonDocument.Parse(encoded);
        var root = document.RootElement;
        var source = root.GetProperty("source").GetString() switch
        {
            "caller" => TemporalEvaluationContextSource.Caller,
            "configuration" => TemporalEvaluationContextSource.Configuration,
            _ => throw new InvalidOperationException("The retained Temporal Evaluation Context is invalid.")
        };
        return new TemporalEvaluationContext(root.GetProperty("timeZone").GetString()!, source);
    }
}
