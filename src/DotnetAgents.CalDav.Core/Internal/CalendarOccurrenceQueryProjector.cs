using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarOccurrenceQueryProjector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static StoredCalendarEntityQueryItem Project(CalendarOccurrenceSnapshot occurrence)
    {
        var snapshot = CalendarEntityQueryProjector.Project(occurrence.Snapshot);
        using var document = JsonDocument.Parse(snapshot.JsonUtf8);
        var wire = new OccurrenceWire(
            document.RootElement.Clone(),
            new RecurrenceIdentityWire(Temporal(occurrence.RecurrenceIdentity)),
            new TimingWire(
                Temporal(occurrence.Timing.SourceStart),
                Temporal(occurrence.Timing.EffectiveStart),
                OptionalTemporal(occurrence.Timing.SourceEnd),
                OptionalTemporal(occurrence.Timing.EffectiveEnd),
                occurrence.Timing.SourceDuration,
                occurrence.Timing.EffectiveDuration,
                OptionalTemporal(occurrence.Timing.EvaluatedStartUtc),
                OptionalTemporal(occurrence.Timing.EvaluatedEndUtc),
                occurrence.Timing.EvaluationTimeZone));
        return new StoredCalendarEntityQueryItem(JsonSerializer.SerializeToUtf8Bytes(wire, SerializerOptions));
    }

    private static TemporalWire? OptionalTemporal(CalendarTemporalValue? value) =>
        value is null ? null : Temporal(value);

    private static TemporalWire Temporal(CalendarTemporalValue value) => new(
        value.Kind switch
        {
            CalendarTemporalKind.Date => "date",
            CalendarTemporalKind.FloatingDateTime => "floatingDateTime",
            CalendarTemporalKind.UtcDateTime => "utcDateTime",
            CalendarTemporalKind.ZonedDateTime => "zonedDateTime",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, null)
        },
        value.Value,
        value.TimeZoneId);

    private sealed record OccurrenceWire(
        [property: JsonPropertyName("snapshot")] JsonElement Snapshot,
        [property: JsonPropertyName("recurrenceIdentity")] RecurrenceIdentityWire RecurrenceIdentity,
        [property: JsonPropertyName("timing")] TimingWire Timing);

    private sealed record RecurrenceIdentityWire(
        [property: JsonPropertyName("value")] TemporalWire Value);

    private sealed record TemporalWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("timeZoneId")] string? TimeZoneId);

    private sealed record TimingWire(
        [property: JsonPropertyName("sourceStart")] TemporalWire SourceStart,
        [property: JsonPropertyName("effectiveStart")] TemporalWire EffectiveStart,
        [property: JsonPropertyName("sourceEnd")] TemporalWire? SourceEnd,
        [property: JsonPropertyName("effectiveEnd")] TemporalWire? EffectiveEnd,
        [property: JsonPropertyName("sourceDuration")] string? SourceDuration,
        [property: JsonPropertyName("effectiveDuration")] string? EffectiveDuration,
        [property: JsonPropertyName("evaluatedStartUtc")] TemporalWire? EvaluatedStartUtc,
        [property: JsonPropertyName("evaluatedEndUtc")] TemporalWire? EvaluatedEndUtc,
        [property: JsonPropertyName("evaluationTimeZone")] string? EvaluationTimeZone);
}
