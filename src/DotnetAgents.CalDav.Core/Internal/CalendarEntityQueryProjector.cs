using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarEntityQueryProjector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static StoredCalendarEntityQueryItem Project(CalendarResourceSnapshot snapshot)
    {
        var wire = new SnapshotWire(
            new HrefWire(snapshot.CalendarHref),
            new ResourceRevisionWire(snapshot.ResourceHref, snapshot.EntityTag),
            snapshot.CalendarProperties.Select(Property).ToArray(),
            Projection(snapshot),
            snapshot.Diagnostics.Select(Diagnostic).ToArray(),
            snapshot.SemanticMutationAvailable
                ? new EntityRevisionWire(
                    snapshot.ResourceHref,
                    snapshot.Projection.EntityUid!,
                    snapshot.Projection.Kind == CalendarResourceProjectionKind.Event ? "event" : "todo",
                    snapshot.EntityTag)
                : null);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(wire, SerializerOptions);
        return new StoredCalendarEntityQueryItem(utf8);
    }

    internal static QueryDiagnostic Diagnostic(CalendarResourceDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarResourceDiagnosticSeverity.Info => "info",
            CalendarResourceDiagnosticSeverity.Warning => "warning",
            CalendarResourceDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic), diagnostic.Severity, null)
        });

    private static object Projection(CalendarResourceSnapshot snapshot) => snapshot.Projection.Kind switch
    {
        CalendarResourceProjectionKind.Event => new EventProjectionWire(
            "event",
            snapshot.Projection.EntityUid!,
            CalendarResourceSemanticProjector.Event(snapshot)),
        CalendarResourceProjectionKind.Todo => new TodoProjectionWire(
            "todo",
            snapshot.Projection.EntityUid!,
            CalendarResourceSemanticProjector.Todo(snapshot),
            CalendarResourceSemanticProjector.TodoCompletedAt(snapshot)),
        _ => new OpaqueProjectionWire("opaque", snapshot.CalendarProperties.Select(Property).ToArray())
    };

    private static PropertyWire Property(CalendarProperty property) => new(
        property.ComponentPath.Select(item => new ComponentPathWire(item.Name, item.Occurrence)).ToArray(),
        property.Name,
        property.Parameters.Select(item => new ParameterWire(item.Name, item.Values)).ToArray(),
        property.ValueType == CalendarPropertyValueType.DateTime
            ? "date-time"
            : property.ValueType.ToString().ToLowerInvariant(),
        property.RawEncodedValue,
        property.OriginalSlice);

    private sealed record SnapshotWire(
        [property: JsonPropertyName("calendar")] HrefWire Calendar,
        [property: JsonPropertyName("resourceRevision")] ResourceRevisionWire ResourceRevision,
        [property: JsonPropertyName("calendarProperties")] IReadOnlyList<PropertyWire> CalendarProperties,
        [property: JsonPropertyName("projection")] object Projection,
        [property: JsonPropertyName("diagnostics")] IReadOnlyList<QueryDiagnostic> Diagnostics,
        [property: JsonPropertyName("entityRevision")] EntityRevisionWire? EntityRevision);

    private sealed record HrefWire([property: JsonPropertyName("href")] string Href);

    private sealed record ResourceRevisionWire(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("entityTag")] string EntityTag);

    private sealed record EntityRevisionWire(
        [property: JsonPropertyName("href")] string Href,
        [property: JsonPropertyName("entityUid")] string EntityUid,
        [property: JsonPropertyName("entityKind")] string EntityKind,
        [property: JsonPropertyName("entityTag")] string EntityTag);

    private sealed record EventProjectionWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("fields")] JsonElement Fields);

    private sealed record TodoProjectionWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("fields")] JsonElement Fields,
        [property: JsonPropertyName("completedAt")] JsonElement? CompletedAt);

    private sealed record OpaqueProjectionWire(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("properties")] IReadOnlyList<PropertyWire> Properties);

    private sealed record PropertyWire(
        [property: JsonPropertyName("componentPath")] IReadOnlyList<ComponentPathWire> ComponentPath,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("parameters")] IReadOnlyList<ParameterWire> Parameters,
        [property: JsonPropertyName("valueType")] string ValueType,
        [property: JsonPropertyName("rawEncodedValue")] string RawEncodedValue,
        [property: JsonPropertyName("originalSlice")] string OriginalSlice);

    private sealed record ComponentPathWire(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("occurrence")] int Occurrence);

    private sealed record ParameterWire(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("values")] IReadOnlyList<string> Values);
}
