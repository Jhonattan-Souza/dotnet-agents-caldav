using System.Text.Json.Serialization;

namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Standard collection properties observed in one target PROPFIND response.</summary>
public sealed record CalendarMetadataSnapshot(
    [property: JsonPropertyName("calendarHref")] string CalendarHref,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("descriptionLanguage")] string? DescriptionLanguage,
    [property: JsonPropertyName("reportSupport")] string ReportSupport,
    [property: JsonPropertyName("reports")] IReadOnlyList<CalendarProtocolName> Reports,
    [property: JsonPropertyName("privilegeSupport")] string PrivilegeSupport,
    [property: JsonPropertyName("privileges")] IReadOnlyList<CalendarProtocolName> Privileges,
    [property: JsonPropertyName("limits")] CalendarAdvertisedLimits Limits,
    [property: JsonPropertyName("timeZoneIds")] IReadOnlyList<string> TimeZoneIds,
    [property: JsonPropertyName("properties")] IReadOnlyList<CalendarPropertyObservation> Properties,
    [property: JsonPropertyName("scheduling")] CalendarSchedulingObservation Scheduling);

/// <summary>A namespace-qualified protocol name, retained as inert capability evidence.</summary>
public sealed record CalendarProtocolName(
    [property: JsonPropertyName("namespaceUri")] string NamespaceUri,
    [property: JsonPropertyName("localName")] string LocalName);

/// <summary>Missing status is unknown; an unsuccessful property status is not a value.</summary>
public sealed record CalendarPropertyObservation(
    [property: JsonPropertyName("namespaceUri")] string NamespaceUri,
    [property: JsonPropertyName("localName")] string LocalName,
    [property: JsonPropertyName("statusCode")] int? StatusCode);

/// <summary>Limits advertised by the server, independent of client execution limits.</summary>
public sealed record CalendarAdvertisedLimits(
    [property: JsonPropertyName("maximumResourceBytes")] long? MaximumResourceBytes,
    [property: JsonPropertyName("maximumInstances")] long? MaximumInstances,
    [property: JsonPropertyName("maximumAttendeesPerInstance")] long? MaximumAttendeesPerInstance,
    [property: JsonPropertyName("minimumDateTime")] string? MinimumDateTime,
    [property: JsonPropertyName("maximumDateTime")] string? MaximumDateTime);

/// <summary>Fresh OPTIONS advertisement evidence; this does not authorize scheduling.</summary>
public sealed record CalendarSchedulingObservation(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("statusCode")] int? StatusCode);

/// <summary>Only explicitly addressed metadata properties change.</summary>
public sealed record CalendarMetadataPatch(
    [property: JsonPropertyName("displayName")] CalendarMetadataTextPatch? DisplayName = null,
    [property: JsonPropertyName("description")] CalendarMetadataTextPatch? Description = null);

/// <summary>Set one property's complete value or remove the property.</summary>
public sealed record CalendarMetadataTextPatch(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("language")] string? Language = null);

/// <summary>The mutation state remains independent of the observed post-write metadata.</summary>
public sealed record CalendarMetadataPatchResult(
    CalendarMutationState MutationState,
    CalendarMetadataSnapshot? Calendar = null,
    CalendarProtocolException? Error = null);
