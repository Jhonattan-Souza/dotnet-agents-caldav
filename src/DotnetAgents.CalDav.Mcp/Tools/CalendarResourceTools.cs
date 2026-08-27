using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Core.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Authoritative Calendar Object Resource semantic reads.</summary>
[McpServerToolType]
public sealed class CalendarResourceTools
{
    private readonly ICalendarService _calendarService;

    public CalendarResourceTools(ICalendarService calendarService)
    {
        _calendarService = calendarService;
    }

    /// <summary>Reads one complete, revision-coherent Calendar Object Resource snapshot.</summary>
    [McpServerTool(
        Name = "calendar_resources.get",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarResourceSuccessResult)),
     Description("Read one semantic Calendar Object Resource by an explicitly confirmed absolute href.")]
    public async Task<CallToolResult> GetAsync(
        [Description("Canonical absolute Calendar Object Resource href.")] string href,
        CancellationToken cancellationToken)
    {
        return await ExecuteReadAsync(
            _calendarService,
            href,
            snapshot => CreateSuccess(CalendarResourceSuccessResult.FromSnapshot(snapshot)),
            cancellationToken);
    }

    internal static async Task<CallToolResult> ExecuteReadAsync(
        ICalendarService calendarService,
        string href,
        Func<CalendarResourceSnapshot, CallToolResult> createSuccess,
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await calendarService.GetResourceAsync(href, cancellationToken);
            if (read.Code != CalendarResourceReadCode.Success || read.Snapshot is null)
                return CreateError(read.Code, read.ObservedByteCount);

            return CalendarToolResult.Success(createSuccess(read.Snapshot))
                .FinalizeBounded(PayloadLimitError);
        }
        catch (HttpRequestException exception)
        {
            return CreateHttpError(exception.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateHttpError(null);
        }
        catch (TimeoutException)
        {
            return CreateHttpError(null);
        }
        catch (XmlException)
        {
            return CreateDiscoveryProtocolError();
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return CreateDiscoveryProtocolError();
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return FinalizeError(
                new(CalendarTelemetryErrorCode.LimitExhausted,
                    CalendarTelemetryErrorCategory.LimitsAndAdmission,
                    CalendarTelemetryErrorPhase.AdmissionAndPayload, false),
                "Calendar discovery exceeded the safe item limit.",
                calendarCount: exception.CalendarCount);
        }
    }

    internal static CallToolResult CreateSuccess(object content, params ContentBlock[] additionalContent) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(content),
        Content = [new TextContentBlock { Text = "Calendar Object Resource read completed." }, .. additionalContent]
    };

    internal static CallToolResult CreateError(CalendarResourceReadCode code, int? observedByteCount = null)
    {
        var facts = CalendarTelemetryFacts.From(code);
        return FinalizeError(facts, code switch
        {
            CalendarResourceReadCode.InvalidInput => "The resource href is invalid.",
            CalendarResourceReadCode.OutsideScope => "The resource is outside the configured Calendar Scope.",
            CalendarResourceReadCode.NotFound => "The Calendar Object Resource was not found.",
            CalendarResourceReadCode.ConcurrencyUnavailable => "The server did not return a strong Entity Tag.",
            CalendarResourceReadCode.PayloadTooLarge => "The Calendar Object Resource exceeds the safe payload limit.",
            _ => "The Calendar Object Resource response was invalid."
        }, observedByteCount);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => FinalizeError(
            CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
            payloadTooLarge ? "The resource read arguments are too large." : "The resource read input is invalid.",
            null);

    private static CallToolResult CreateHttpError(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => FinalizeError(HttpFacts(CalendarTelemetryErrorCode.UpstreamUnauthorized), "The Calendar Object Resource read was not authorized."),
        HttpStatusCode.Forbidden => FinalizeError(HttpFacts(CalendarTelemetryErrorCode.UpstreamForbidden), "The Calendar Object Resource read was forbidden."),
        HttpStatusCode.NotFound => CreateDiscoveryProtocolError(),
        HttpStatusCode.RequestEntityTooLarge => CreateError(CalendarResourceReadCode.PayloadTooLarge),
        HttpStatusCode.TooManyRequests => FinalizeError(HttpFacts(CalendarTelemetryErrorCode.UpstreamRateLimited, true), "The Calendar Object Resource read is rate limited."),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => FinalizeError(new(
            CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false), "The server does not support direct resource reads."),
        HttpStatusCode.InsufficientStorage => FinalizeError(HttpFacts(CalendarTelemetryErrorCode.UpstreamUnavailable), "The Calendar Object Resource read is unavailable."),
        null or >= HttpStatusCode.InternalServerError => FinalizeError(HttpFacts(CalendarTelemetryErrorCode.UpstreamUnavailable, true), "The Calendar Object Resource read is unavailable."),
        _ => CreateError(CalendarResourceReadCode.UpstreamProtocolError)
    };

    private static CallToolResult CreateDiscoveryProtocolError() => FinalizeError(new(
        CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        false),
        "Calendar discovery returned an invalid response.",
        null);

    private static CalendarStructuredErrorFacts HttpFacts(
        CalendarTelemetryErrorCode code,
        bool retryable = false) => new(
            code,
            CalendarTelemetryErrorCategory.Upstream,
            CalendarTelemetryErrorPhase.Execution,
            retryable);

    private static CallToolResult FinalizeError(
        CalendarStructuredErrorFacts facts,
        string message,
        int? byteCount = null,
        int? calendarCount = null) => Error(facts, message, byteCount, calendarCount)
            .FinalizeBounded(PayloadLimitError);

    private static CalendarToolResult PayloadLimitError(int byteCount, bool _) => Error(
        CalendarTelemetryFacts.From(CalendarResourceReadCode.PayloadTooLarge),
        "The structured Calendar snapshot exceeds the safe payload limit.",
        byteCount);

    private static CalendarToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        int? byteCount = null,
        int? calendarCount = null) => CalendarToolResult.Error(new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceErrorResult(
                facts.CodeName,
                facts.CategoryName,
                message,
                facts.Retryable,
                facts.PhaseName,
                byteCount is null && calendarCount is null
                    ? null
                    : new CalendarResourceExecutionLimits(byteCount, calendarCount))),
            Content = [new TextContentBlock { Text = "Calendar Object Resource read failed." }]
        }, facts);
}

/// <summary>Structured success outcome for a direct resource read.</summary>
public sealed record CalendarResourceSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("snapshot")] CalendarSnapshotResult Snapshot)
{
    internal static CalendarResourceSuccessResult FromSnapshot(CalendarResourceSnapshot snapshot) =>
        new("success", CalendarSnapshotResult.FromSnapshot(snapshot));
}

/// <summary>Frozen MCP snapshot representation.</summary>
public sealed record CalendarSnapshotResult(
    [property: JsonPropertyName("calendar")] CalendarHref Calendar,
    [property: JsonPropertyName("resourceRevision")] CalendarResourceRevisionResult ResourceRevision,
    [property: JsonPropertyName("calendarProperties")] IReadOnlyList<CalendarPropertyResult> CalendarProperties,
    [property: JsonPropertyName("projection")] object Projection,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("entityRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityRevisionResult? EntityRevision)
{
    internal static CalendarSnapshotResult FromSnapshot(CalendarResourceSnapshot snapshot) => new(
        new CalendarHref(snapshot.CalendarHref),
        new CalendarResourceRevisionResult(snapshot.ResourceHref, snapshot.EntityTag),
        snapshot.CalendarProperties.Select(CalendarPropertyResult.FromProperty).ToArray(),
        CreateProjection(snapshot),
        snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray(),
        CreateEntityRevision(snapshot));

    private static object CreateProjection(CalendarResourceSnapshot snapshot) => snapshot.Projection.Kind switch
    {
        CalendarResourceProjectionKind.Event => new CalendarEventProjectionResult(
            "event",
            snapshot.Projection.EntityUid!,
            CalendarResourceSemanticProjector.Event(snapshot)),
        CalendarResourceProjectionKind.Todo => new CalendarTodoProjectionResult(
            "todo",
            snapshot.Projection.EntityUid!,
            CalendarResourceSemanticProjector.Todo(snapshot),
            CalendarResourceSemanticProjector.TodoCompletedAt(snapshot)),
        _ => new CalendarOpaqueProjectionResult("opaque", snapshot.CalendarProperties.Select(CalendarPropertyResult.FromProperty).ToArray())
    };

    private static CalendarEntityRevisionResult? CreateEntityRevision(CalendarResourceSnapshot snapshot) =>
        snapshot.SemanticMutationAvailable
            ? new CalendarEntityRevisionResult(
                snapshot.ResourceHref,
                snapshot.Projection.EntityUid!,
                snapshot.Projection.Kind == CalendarResourceProjectionKind.Event ? "event" : "todo",
                snapshot.EntityTag)
            : null;
}

public sealed record CalendarResourceRevisionResult(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("entityTag")] string EntityTag);

public sealed record CalendarEntityRevisionResult(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("entityUid")] string EntityUid,
    [property: JsonPropertyName("entityKind")] string EntityKind,
    [property: JsonPropertyName("entityTag")] string EntityTag);

public sealed record CalendarEventProjectionResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("fields")] JsonElement Fields);

public sealed record CalendarTodoProjectionResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("fields")] JsonElement Fields,
    [property: JsonPropertyName("completedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? CompletedAt);

public sealed record CalendarOpaqueProjectionResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("properties")] IReadOnlyList<CalendarPropertyResult> Properties);

public sealed record CalendarEventFieldsResult(
    [property: JsonPropertyName("summary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? Start,
    [property: JsonPropertyName("end"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? End,
    [property: JsonPropertyName("duration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Duration,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    [property: JsonPropertyName("geo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarGeoResult? Geo,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarOpenEnumResult? Status,
    [property: JsonPropertyName("transparency"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarOpenEnumResult? Transparency,
    [property: JsonPropertyName("classification"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarOpenEnumResult? Classification,
    [property: JsonPropertyName("priority"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Priority,
    [property: JsonPropertyName("categories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Categories,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url,
    [property: JsonPropertyName("recurrenceSet"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? RecurrenceSet,
    [property: JsonPropertyName("structuredData"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? StructuredData);

public sealed record CalendarTodoFieldsResult(
    [property: JsonPropertyName("summary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? Start,
    [property: JsonPropertyName("due"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? Due,
    [property: JsonPropertyName("duration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Duration,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarOpenEnumResult? Status,
    [property: JsonPropertyName("classification"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarOpenEnumResult? Classification,
    [property: JsonPropertyName("priority"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Priority,
    [property: JsonPropertyName("percentComplete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PercentComplete,
    [property: JsonPropertyName("categories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Categories,
    [property: JsonPropertyName("recurrenceSet"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? RecurrenceSet,
    [property: JsonPropertyName("structuredData"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? StructuredData);

public sealed record CalendarGeoResult(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude);

public sealed record CalendarRecurrenceRuleResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("originalSlice")] string OriginalSlice);

public sealed record CalendarOpenEnumResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rawValue")] string RawValue);

public sealed record CalendarPropertyResult(
    [property: JsonPropertyName("componentPath")] IReadOnlyList<CalendarComponentPathResult> ComponentPath,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parameters")] IReadOnlyList<CalendarParameterResult> Parameters,
    [property: JsonPropertyName("valueType")] string ValueType,
    [property: JsonPropertyName("rawEncodedValue")] string RawEncodedValue,
    [property: JsonPropertyName("originalSlice")] string OriginalSlice)
{
    internal static CalendarPropertyResult FromProperty(CalendarProperty property) => new(
        property.ComponentPath.Select(item => new CalendarComponentPathResult(item.Name, item.Occurrence)).ToArray(),
        property.Name,
        property.Parameters.Select(item => new CalendarParameterResult(item.Name, item.Values)).ToArray(),
        property.ValueType switch
        {
            CalendarPropertyValueType.DateTime => "date-time",
            _ => property.ValueType.ToString().ToLowerInvariant()
        },
        property.RawEncodedValue,
        property.OriginalSlice);
}

public sealed record CalendarComponentPathResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("occurrence")] int Occurrence);

public sealed record CalendarParameterResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] IReadOnlyList<string> Values);

public sealed record CalendarResourceErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarResourceExecutionLimits? Limits = null);

public sealed record CalendarResourceExecutionLimits(
    [property: JsonPropertyName("byteCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ByteCount = null,
    [property: JsonPropertyName("calendarCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CalendarCount = null);
