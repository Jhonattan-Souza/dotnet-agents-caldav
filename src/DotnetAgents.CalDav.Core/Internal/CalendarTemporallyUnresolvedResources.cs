using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// Collects the Temporally Unresolved Resources a query excluded instead of guessing them into or out of its
/// result. It is frozen into the Query Result Snapshot as a complete count and a bounded, ordered href sample.
/// </summary>
internal sealed class CalendarTemporallyUnresolvedResources
{
    internal const int MaximumSampleSize = 25;
    private readonly SortedSet<string> _hrefs = new(StringComparer.Ordinal);

    internal void Add(string resourceHref) => _hrefs.Add(resourceHref);

    /// <summary>Returns the frozen JSON disclosure, or no bytes when nothing was excluded.</summary>
    internal byte[] Encode() => _hrefs.Count == 0
        ? []
        : JsonSerializer.SerializeToUtf8Bytes(new Disclosure(_hrefs.Count, _hrefs.Take(MaximumSampleSize).ToArray()));

    /// <summary>Writes the frozen disclosure as <c>temporallyUnresolved</c>, omitting it when nothing was excluded.</summary>
    internal static void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> disclosureUtf8)
    {
        if (disclosureUtf8.IsEmpty)
            return;
        writer.WritePropertyName("temporallyUnresolved");
        writer.WriteRawValue(disclosureUtf8.Span, skipInputValidation: true);
    }

    private sealed record Disclosure(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("hrefs")] IReadOnlyList<string> Hrefs);
}
