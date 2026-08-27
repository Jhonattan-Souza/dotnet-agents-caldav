using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
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
            return CalendarToolResult.Success(CreateResult(new CalendarListResult(
                "success",
                discovery.Items.Select(CalendarListItem.FromDescriptor).ToArray(),
                discovery.Diagnostics.Select(CalendarDiagnosticResult.FromDiagnostic).ToArray(),
                new CalendarPagination("non_snapshot", null)))).FinalizeResult();
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error(
                new(CalendarTelemetryErrorCode.LimitExhausted, CalendarTelemetryErrorCategory.LimitsAndAdmission,
                    CalendarTelemetryErrorPhase.AdmissionAndPayload, false),
                "Calendar discovery exceeded the safe item limit.",
                new CalendarExecutionLimits(exception.CalendarCount));
        }
        catch (HttpRequestException exception)
        {
            var failure = MapHttpFailure(exception);
            return Error(failure.Facts, failure.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(DiscoveryUnavailable(), "Calendar discovery is temporarily unavailable.");
        }
        catch (TimeoutException)
        {
            return Error(DiscoveryUnavailable(), "Calendar discovery is temporarily unavailable.");
        }
        catch (XmlException)
        {
            return Error(DiscoveryProtocolError(), "Calendar discovery returned an invalid response.");
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return Error(DiscoveryProtocolError(), "Calendar discovery returned an invalid response.");
        }
    }

    private static CalendarDiscoveryFailure MapHttpFailure(HttpRequestException exception) => exception.StatusCode switch
    {
        HttpStatusCode.Unauthorized => new(new(CalendarTelemetryErrorCode.UpstreamUnauthorized,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            false), "Calendar discovery was not authorized."),
        HttpStatusCode.Forbidden => new(new(CalendarTelemetryErrorCode.UpstreamForbidden,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            false), "Calendar discovery was forbidden."),
        HttpStatusCode.TooManyRequests => new(new(CalendarTelemetryErrorCode.UpstreamRateLimited,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            true), "Calendar discovery is rate limited."),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => new(new(
            CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false),
            "The upstream Calendar server does not support discovery."),
        HttpStatusCode.RequestEntityTooLarge => new(new(CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarTelemetryErrorCategory.LimitsAndAdmission, CalendarTelemetryErrorPhase.AdmissionAndPayload,
            false), "Calendar discovery returned an oversized response."),
        HttpStatusCode.InsufficientStorage => new(new(CalendarTelemetryErrorCode.UpstreamUnavailable,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            false), "Calendar discovery is temporarily unavailable."),
        null or >= HttpStatusCode.InternalServerError => new(DiscoveryUnavailable(),
            "Calendar discovery is temporarily unavailable."),
        _ => new(DiscoveryProtocolError(), "Calendar discovery returned an invalid response.")
    };

    private static CalendarStructuredErrorFacts DiscoveryUnavailable() => new(
        CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        true);

    private static CalendarStructuredErrorFacts DiscoveryProtocolError() => new(
        CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        false);

    private static CallToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarExecutionLimits? limits = null) => CalendarToolResult.Error(
            CreateResult(new CalendarErrorResult(
                facts.CodeName,
                message,
                facts.Retryable,
                facts.PhaseName,
                facts.CategoryName,
                limits), isError: true),
            facts).FinalizeResult();

    private static CallToolResult CreateResult(object content, bool isError = false) => new()
    {
        IsError = isError,
        StructuredContent = JsonSerializer.SerializeToElement(content),
        Content = [new TextContentBlock { Text = isError ? "Calendar discovery failed." : "Calendar discovery completed." }]
    };

    private readonly record struct CalendarDiscoveryFailure(
        CalendarStructuredErrorFacts Facts,
        string Message);
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
