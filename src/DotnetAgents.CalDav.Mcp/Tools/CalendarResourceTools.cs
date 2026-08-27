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
    private const int MaxStructuredResultBytes = 4 * 1024 * 1024;
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
            snapshot => CreateBoundedSuccess(CalendarResourceSuccessResult.FromSnapshot(snapshot)),
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

            return createSuccess(read.Snapshot);
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
            return Error(
                "limit_exhausted",
                "limitsAndAdmission",
                "Calendar discovery exceeded the safe item limit.",
                false,
                "admissionAndPayload",
                calendarCount: exception.CalendarCount);
        }
    }

    internal static CallToolResult CreateSuccess(object content, params ContentBlock[] additionalContent) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(content),
        Content = [new TextContentBlock { Text = "Calendar Object Resource read completed." }, .. additionalContent]
    };

    private static CallToolResult CreateBoundedSuccess(CalendarResourceSuccessResult content)
    {
        var structured = JsonSerializer.SerializeToElement(content);
        var candidate = new CallToolResult
        {
            IsError = false,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = "Calendar Object Resource read completed." }]
        };
        var byteCount = JsonSerializer.SerializeToUtf8Bytes(candidate).Length;
        return byteCount > MaxStructuredResultBytes
            ? Error("payload_too_large", "limitsAndAdmission", "The structured Calendar snapshot exceeds the safe payload limit.", false, "admissionAndPayload", byteCount)
            : candidate;
    }

    internal static CallToolResult CreateError(CalendarResourceReadCode code, int? observedByteCount = null)
    {
        CalendarTelemetry.ObserveStructuredError(CalendarTelemetryFacts.From(code));
        return code switch
        {
            CalendarResourceReadCode.InvalidInput => Error("invalid_input", "input", "The resource href is invalid.", false, "originScopeAuthorization"),
            CalendarResourceReadCode.OutsideScope => Error("outside_scope", "selection", "The resource is outside the configured Calendar Scope.", false, "originScopeAuthorization"),
            CalendarResourceReadCode.NotFound => Error("not_found", "selection", "The Calendar Object Resource was not found.", false, "targetRevision"),
            CalendarResourceReadCode.ConcurrencyUnavailable => Error("concurrency_unavailable", "state", "The server did not return a strong Entity Tag.", false, "targetRevision"),
            CalendarResourceReadCode.PayloadTooLarge => Error("payload_too_large", "limitsAndAdmission", "The Calendar Object Resource exceeds the safe payload limit.", false, "admissionAndPayload", observedByteCount),
            _ => Error("upstream_protocol_error", "upstream", "The Calendar Object Resource response was invalid.", false, "execution")
        };
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge)
    {
        CalendarTelemetry.ObserveStructuredError(CalendarTelemetryFacts.FromInputGuard(payloadTooLarge));
        return Error(
            payloadTooLarge ? "payload_too_large" : "invalid_input",
            payloadTooLarge ? "limitsAndAdmission" : "input",
            payloadTooLarge ? "The resource read arguments are too large." : "The resource read input is invalid.",
            false,
            payloadTooLarge ? "admissionAndPayload" : "schemaLexicalDiscriminator");
    }

    private static CallToolResult CreateHttpError(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Error("upstream_unauthorized", "upstream", "The Calendar Object Resource read was not authorized.", false, "execution"),
        HttpStatusCode.Forbidden => Error("upstream_forbidden", "upstream", "The Calendar Object Resource read was forbidden.", false, "execution"),
        HttpStatusCode.NotFound => CreateDiscoveryProtocolError(),
        HttpStatusCode.RequestEntityTooLarge => CreateError(CalendarResourceReadCode.PayloadTooLarge),
        HttpStatusCode.TooManyRequests => Error("upstream_rate_limited", "upstream", "The Calendar Object Resource read is rate limited.", true, "execution"),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => Error("unsupported_capability", "capabilityAndProjection", "The server does not support direct resource reads.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.InsufficientStorage => Error("upstream_unavailable", "upstream", "The Calendar Object Resource read is unavailable.", false, "execution"),
        null => Error("upstream_unavailable", "upstream", "The Calendar Object Resource read is unavailable.", true, "execution"),
        >= HttpStatusCode.InternalServerError => Error("upstream_unavailable", "upstream", "The Calendar Object Resource read is unavailable.", true, "execution"),
        _ => CreateError(CalendarResourceReadCode.UpstreamProtocolError)
    };

    private static CallToolResult CreateDiscoveryProtocolError() => Error(
        "upstream_protocol_error",
        "upstream",
        "Calendar discovery returned an invalid response.",
        false,
        "selectionDiscoveryCapability");

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        int? byteCount = null,
        int? calendarCount = null) => new()
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceErrorResult(
                code,
                category,
                message,
                retryable,
                phase,
                byteCount is null && calendarCount is null
                    ? null
                    : new CalendarResourceExecutionLimits(byteCount, calendarCount))),
            Content = [new TextContentBlock { Text = "Calendar Object Resource read failed." }]
        };
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
