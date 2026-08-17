namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Outcome of an authoritative Calendar Object Resource read.</summary>
public enum CalendarResourceReadCode
{
    Success,
    NotFound,
    InvalidInput,
    OutsideScope,
    ConcurrencyUnavailable,
    PayloadTooLarge,
    UnsupportedCapability,
    UpstreamProtocolError
}

/// <summary>Closed semantic projection kind for a Calendar Object Resource.</summary>
public enum CalendarResourceProjectionKind
{
    Event,
    Todo,
    Opaque
}

/// <summary>Safe diagnostic severity for a Calendar Object Resource projection.</summary>
public enum CalendarResourceDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A content-free diagnostic safe to expose to MCP clients.</summary>
public sealed record CalendarResourceDiagnostic(
    string Code,
    string Message,
    CalendarResourceDiagnosticSeverity Severity);

/// <summary>Lossless content-line value classification.</summary>
public enum CalendarPropertyValueType
{
    Text,
    Uri,
    Date,
    DateTime,
    Duration,
    Integer,
    Float,
    Period,
    Recur,
    Binary,
    Unknown
}

/// <summary>One component in the path that owns a content line.</summary>
public sealed record CalendarComponentPathSegment(string Name, int Occurrence);

/// <summary>One parameter occurrence; duplicate names remain separate occurrences.</summary>
public sealed record CalendarParameter(string Name, IReadOnlyList<string> Values);

/// <summary>A lossless indexed iCalendar property.</summary>
public sealed record CalendarProperty(
    IReadOnlyList<CalendarComponentPathSegment> ComponentPath,
    string Name,
    IReadOnlyList<CalendarParameter> Parameters,
    CalendarPropertyValueType ValueType,
    string RawEncodedValue,
    string OriginalSlice);

/// <summary>Minimal typed Event, To-do, or opaque projection over authoritative content.</summary>
public sealed record CalendarResourceProjection(
    CalendarResourceProjectionKind Kind,
    string? EntityUid,
    string? Summary);

/// <summary>An immutable, revision-coherent Calendar Object Resource snapshot.</summary>
public sealed record CalendarResourceSnapshot(
    string CalendarHref,
    string ResourceHref,
    string EntityTag,
    ReadOnlyMemory<byte> AuthoritativeUtf8,
    IReadOnlyList<CalendarProperty> CalendarProperties,
    CalendarResourceProjection Projection,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics)
{
    /// <summary>Whether this snapshot may be used as the base of a semantic mutation.</summary>
    public bool SemanticMutationAvailable => Projection.Kind != CalendarResourceProjectionKind.Opaque;
}

/// <summary>Read transport/result envelope used across the Calendar Service boundary.</summary>
public sealed record CalendarResourceRead(
    CalendarResourceReadCode Code,
    string? ResourceHref = null,
    string? EntityTag = null,
    ReadOnlyMemory<byte> AuthoritativeUtf8 = default,
    CalendarResourceSnapshot? Snapshot = null,
    int? ObservedByteCount = null)
{
    public static CalendarResourceRead Success(string resourceHref, string entityTag, ReadOnlyMemory<byte> authoritativeUtf8) =>
        new(CalendarResourceReadCode.Success, resourceHref, entityTag, authoritativeUtf8);
}
