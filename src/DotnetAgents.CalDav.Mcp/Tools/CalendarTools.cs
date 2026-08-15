using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Unified MCP Calendar discovery tools.</summary>
[McpServerToolType]
public sealed class CalendarTools
{
    private readonly ICalendarService _calendarService;

    public CalendarTools(ICalendarService calendarService)
    {
        _calendarService = calendarService;
    }

    /// <summary>Lists all Calendars in the configured Calendar Scope.</summary>
    [McpServerTool(
        Name = "calendars.list",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarListResult)),
     Description("List every configured Calendar with independent Event and To-do capability evidence.")]
    public async Task<CallToolResult> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var discovery = await _calendarService.GetCalendarsAsync(cancellationToken);
            return CreateResult(new CalendarListResult(
                "success",
                discovery.Items.Select(CalendarListItem.FromDescriptor).ToArray(),
                discovery.Diagnostics.Select(CalendarDiagnosticResult.FromDiagnostic).ToArray(),
                new CalendarPagination("non_snapshot", null)));
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return CreateResult(
                new CalendarErrorResult("limit_exhausted", "Calendar discovery exceeded the safe item limit.", false, "admissionAndPayload", "limitsAndAdmission", new CalendarExecutionLimits(exception.CalendarCount)),
                isError: true);
        }
        catch (HttpRequestException exception)
        {
            return CreateResult(MapHttpFailure(exception), isError: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateResult(new CalendarErrorResult("upstream_unavailable", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability"), isError: true);
        }
        catch (TimeoutException)
        {
            return CreateResult(new CalendarErrorResult("upstream_unavailable", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability"), isError: true);
        }
        catch (XmlException)
        {
            return CreateResult(new CalendarErrorResult("upstream_protocol_error", "Calendar discovery returned an invalid response.", false, "selectionDiscoveryCapability"), isError: true);
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return CreateResult(new CalendarErrorResult("upstream_protocol_error", "Calendar discovery returned an invalid response.", false, "selectionDiscoveryCapability"), isError: true);
        }
    }

    private static CalendarErrorResult MapHttpFailure(HttpRequestException exception) => exception.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new("upstream_unauthorized", "Calendar discovery was not authorized.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.Forbidden => new("upstream_forbidden", "Calendar discovery was forbidden.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.TooManyRequests => new("upstream_rate_limited", "Calendar discovery is rate limited.", true, "selectionDiscoveryCapability"),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => new("unsupported_capability", "The upstream Calendar server does not support discovery.", false, "selectionDiscoveryCapability", "capabilityAndProjection"),
        HttpStatusCode.RequestEntityTooLarge => new("payload_too_large", "Calendar discovery returned an oversized response.", false, "admissionAndPayload", "limitsAndAdmission"),
        HttpStatusCode.InsufficientStorage => new("upstream_unavailable", "Calendar discovery is temporarily unavailable.", false, "selectionDiscoveryCapability"),
        null => new("upstream_unavailable", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability"),
        >= HttpStatusCode.InternalServerError => new("upstream_unavailable", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability"),
        _ => new("upstream_protocol_error", "Calendar discovery returned an invalid response.", false, "selectionDiscoveryCapability")
    };

    private static CallToolResult CreateResult(object content, bool isError = false) => new()
    {
        IsError = isError,
        StructuredContent = JsonSerializer.SerializeToElement(content),
        Content = [new TextContentBlock { Text = isError ? "Calendar discovery failed." : "Calendar discovery completed." }]
    };
}

/// <summary>Structured success outcome for <c>calendars.list</c>.</summary>
public sealed record CalendarListResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarListItem> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

/// <summary>Structured error outcome for a failed Calendar discovery request.</summary>
public sealed record CalendarErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("category")] string Category = "upstream",
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarExecutionLimits? Limits = null);

/// <summary>Bounded execution limits returned with an admission failure.</summary>
public sealed record CalendarExecutionLimits([property: JsonPropertyName("calendarCount")] int CalendarCount);

/// <summary>Public Calendar descriptor with href-only identity.</summary>
public sealed record CalendarListItem(
    [property: JsonPropertyName("calendar")] CalendarHref Calendar,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("displayNameProvenance")] string DisplayNameProvenance,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("entityKinds")] CalendarEntityKinds EntityKinds)
{
    internal static CalendarListItem FromDescriptor(CalendarDescriptor descriptor) => new(
        new CalendarHref(descriptor.Href),
        descriptor.DisplayName,
        ToWireValue(descriptor.DisplayNameProvenance),
        descriptor.Description,
        GetSchemaColor(descriptor.Color),
        new CalendarEntityKinds(
            CalendarEntityKindCapability.From(descriptor.EventSupport, descriptor.EventEvidence),
            CalendarEntityKindCapability.From(descriptor.TodoSupport, descriptor.TodoEvidence)));

    private static string ToWireValue(DisplayNameProvenance provenance) => provenance switch
    {
        DotnetAgents.CalDav.Core.Models.DisplayNameProvenance.DavDisplayName => "dav-displayname",
        DotnetAgents.CalDav.Core.Models.DisplayNameProvenance.DerivedFromHref => "derived-from-href",
        DotnetAgents.CalDav.Core.Models.DisplayNameProvenance.Missing => "missing",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, null)
    };

    private static string? GetSchemaColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.AsSpan(1).ToString().All(Uri.IsHexDigit)
            ? color
            : null;
}

/// <summary>Canonical Calendar href identity.</summary>
public sealed record CalendarHref([property: JsonPropertyName("href")] string Href);

/// <summary>Independent Event and To-do capability evidence.</summary>
public sealed record CalendarEntityKinds(
    [property: JsonPropertyName("event")] CalendarEntityKindCapability Event,
    [property: JsonPropertyName("todo")] CalendarEntityKindCapability Todo);

/// <summary>Advertised, explicitly absent, or unknown capability evidence.</summary>
public sealed record CalendarEntityKindCapability(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("evidence")] IReadOnlyList<CalendarCapabilityEvidence> Evidence)
{
    internal static CalendarEntityKindCapability From(
        EntityKindSupport support,
        IReadOnlyList<CapabilityEvidence> evidence) => new(
        support switch
        {
            EntityKindSupport.Advertised => "advertised",
            EntityKindSupport.NotAdvertised => "not_advertised",
            EntityKindSupport.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(support), support, null)
        },
        evidence.Select(item => new CalendarCapabilityEvidence(item.Source, item.Value)).ToArray());
}

/// <summary>Raw, inert capability evidence.</summary>
public sealed record CalendarCapabilityEvidence(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Bounded diagnostic for the current discovery result.</summary>
public sealed record CalendarDiagnosticResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] string Severity)
{
    internal static CalendarDiagnosticResult FromDiagnostic(CalendarDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarDiagnosticSeverity.Info => "info",
            CalendarDiagnosticSeverity.Warning => "warning",
            CalendarDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic), diagnostic.Severity, null)
        });

    internal static CalendarDiagnosticResult FromResourceDiagnostic(CalendarResourceDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarResourceDiagnosticSeverity.Info => "info",
            CalendarResourceDiagnosticSeverity.Warning => "warning",
            CalendarResourceDiagnosticSeverity.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic), diagnostic.Severity, null)
        });
}

/// <summary>List pagination deliberately has no snapshot continuity guarantee.</summary>
public sealed record CalendarPagination(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);
