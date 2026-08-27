using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Creates complete Events and To-dos, including typed recurrence.</summary>
[McpServerToolType]
public sealed class CalendarEntityCreateTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private readonly ICalendarService _calendarService;
    private readonly TimeProvider _timeProvider;

    public CalendarEntityCreateTools(ICalendarService calendarService, TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "events.create",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Create one typed Event in the default or explicitly selected Calendar.")]
    public Task<CallToolResult> CreateEventAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        CreateEventRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "todos.create",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Create one typed To-do in the default or explicitly selected Calendar.")]
    public Task<CallToolResult> CreateTodoAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        CreateTodoRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> CreateEventRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        return !CalendarEntityCreateArgumentParser.TryParseEvent(arguments, out var request)
            ? InputError(payloadTooLarge: false)
            : await ExecuteAsync(
                token => _calendarService.CreateEventAsync(request, token),
                cancellationToken);
    }

    internal async Task<CallToolResult> CreateTodoRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        return !CalendarEntityCreateArgumentParser.TryParseTodo(arguments, out var request)
            ? InputError(payloadTooLarge: false)
            : await ExecuteAsync(
                token => _calendarService.CreateTodoAsync(request, token),
                cancellationToken);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(payloadTooLarge);

    private async Task<CallToolResult> ExecuteAsync(
        Func<CancellationToken, Task<CalendarEntityCreateResult>> create,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await create(linked.Token);
            var terminal = result.Code == CalendarEntityCreateCode.Success && result.Snapshot is not null
                ? CalendarToolResult.Success(Success(result.Snapshot), result.MutationState)
                : Error(result);
            return terminal.FinalizeBounded((_, _) => PayloadLimitError(result.MutationState));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.LimitExhausted,
                CalendarTelemetryErrorCategory.LimitsAndAdmission,
                CalendarTelemetryErrorPhase.Execution,
                false),
                "The Calendar mutation exhausted its elapsed_time execution budget.",
                CalendarMutationState.Unknown).FinalizeResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SelectionUnavailableError();
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.LimitExhausted,
                CalendarTelemetryErrorCategory.LimitsAndAdmission,
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
                false),
                "The Calendar mutation exceeded its Calendar discovery limit.",
                CalendarMutationState.NotAttempted,
                limits: new CalendarEntityCreateLimits(CalendarCount: exception.CalendarCount)).FinalizeResult();
        }
        catch (HttpRequestException)
        {
            return SelectionUnavailableError();
        }
        catch (TimeoutException)
        {
            return SelectionUnavailableError();
        }
        catch (IOException)
        {
            return SelectionUnavailableError();
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Error(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.UpstreamProtocolError,
                CalendarTelemetryErrorCategory.Upstream,
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
                false),
                "Calendar discovery returned an invalid response.",
                CalendarMutationState.NotAttempted).FinalizeResult();
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.UnsupportedCapability,
                CalendarTelemetryErrorCategory.CapabilityAndProjection,
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
                false),
                "The server does not support the required Calendar discovery capability.",
                CalendarMutationState.NotAttempted).FinalizeResult();
        }
    }

    private static CallToolResult SelectionUnavailableError() => Error(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        true),
        "Calendar discovery is temporarily unavailable.",
        CalendarMutationState.NotAttempted).FinalizeResult();

    private static int MeasureArguments(IDictionary<string, JsonElement>? arguments) =>
        CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>());

    private static CallToolResult Success(CalendarResourceSnapshot snapshot) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateSuccessResult(
            "success",
            "committed",
            CalendarSnapshotResult.FromSnapshot(snapshot),
            snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = "Calendar Entity creation completed." }]
    };

    private static CalendarToolResult Error(CalendarEntityCreateResult result)
    {
        var facts = CalendarTelemetryFacts.From(result);
        var message = Describe(result.Code);
        return Error(
            facts,
            message,
            result.MutationState,
            result.AuthorizedCandidates is { Count: > 0 }
                ? result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray()
                : null,
            result.Snapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.Snapshot),
            limits: result.Limits is null ? null : CalendarEntityCreateLimits.FromLimits(result.Limits));
    }

    private static string Describe(
        CalendarEntityCreateCode code) => code switch
    {
        CalendarEntityCreateCode.InvalidInput =>
            "The Calendar Entity create input is invalid.",
        CalendarEntityCreateCode.InvalidCalendarData => "The complete Calendar Entity is invalid.",
        CalendarEntityCreateCode.NotFound => "No matching authorized Calendar was found.",
        CalendarEntityCreateCode.Ambiguous => "The Calendar selector matched more than one authorized Calendar.",
        CalendarEntityCreateCode.OutsideScope => "The selected Calendar is outside the configured Calendar Scope.",
        CalendarEntityCreateCode.UnsupportedCapability => "The selected Calendar does not advertise the requested Entity Kind.",
        CalendarEntityCreateCode.RecurrenceUnevaluable => "The Recurrence Set could not be evaluated.",
        CalendarEntityCreateCode.OpaqueResource => "An existing Calendar resource cannot be projected safely.",
        CalendarEntityCreateCode.ConcurrencyUnavailable => "An existing Calendar revision has no strong Entity Tag.",
        CalendarEntityCreateCode.DestinationConflict => "The destination Calendar resource already exists.",
        CalendarEntityCreateCode.Conflict => "The requested Calendar Entity identity already exists.",
        CalendarEntityCreateCode.LimitExhausted => "The Calendar mutation exceeded its bounded work limit.",
        CalendarEntityCreateCode.PayloadTooLarge => "The Calendar Entity exceeds the safe payload limit.",
        CalendarEntityCreateCode.UpstreamUnauthorized => "The Calendar mutation was not authorized.",
        CalendarEntityCreateCode.UpstreamForbidden => "The Calendar mutation was forbidden.",
        CalendarEntityCreateCode.UpstreamRateLimited => "The Calendar mutation is rate limited.",
        CalendarEntityCreateCode.UpstreamUnavailable => "The Calendar mutation is unavailable.",
        CalendarEntityCreateCode.UpstreamProtocolError => "The Calendar mutation returned an invalid response.",
        CalendarEntityCreateCode.FidelityFailure => "The committed server revision differs from the requested semantics.",
        CalendarEntityCreateCode.CommittedButUnverified => "The committed Calendar mutation could not be verified.",
        CalendarEntityCreateCode.CommittedButConcurrencyUnavailable => "The committed server revision has no strong Entity Tag.",
        _ => "The Calendar mutation outcome is indeterminate."
    };

    private static CallToolResult InputError(bool payloadTooLarge) => Error(
        CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
        payloadTooLarge
            ? "The Calendar Entity create arguments exceed the safe payload limit."
            : "The Calendar Entity create input is invalid.",
        CalendarMutationState.NotAttempted).FinalizeResult();

    private static CalendarToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarMutationState mutationState,
        IReadOnlyList<CalendarAuthorizedCandidateResult>? candidates = null,
        CalendarSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => CalendarToolResult.Error(new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
                facts.CodeName,
                facts.CategoryName,
                message,
                facts.Retryable,
                facts.PhaseName,
                CalendarTelemetryVocabulary.MutationStateName(mutationState),
                candidates,
                currentSnapshot,
                retryAfterMs,
                limits)),
            Content = [new TextContentBlock { Text = "Calendar Entity creation failed." }]
        }, facts, mutationState);

    private static CalendarToolResult PayloadLimitError(CalendarMutationState mutationState) => Error(
        CalendarTelemetryFacts.FromInputGuard(payloadTooLarge: true),
        "The Calendar mutation result exceeds the safe payload limit.",
        mutationState);
}

public sealed record CalendarEntityCreateSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("snapshot")] CalendarSnapshotResult Snapshot,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarEntityCreateErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null,
    [property: JsonPropertyName("currentSnapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CalendarSnapshotResult? CurrentSnapshot = null,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? RetryAfterMs = null,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CalendarEntityCreateLimits? Limits = null);

public sealed record CalendarEntityCreateLimits(
    [property: JsonPropertyName("resourcesInspected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? ResourcesInspected = null,
    [property: JsonPropertyName("calendarCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? CalendarCount = null,
    [property: JsonPropertyName("byteCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? ByteCount = null,
    [property: JsonPropertyName("dimension"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Dimension = null)
{
    internal static CalendarEntityCreateLimits FromLimits(CalendarEntityCreateExecutionLimits limits) =>
        new(
            limits.ResourcesInspected,
            limits.CalendarCount,
            limits.ByteCount,
            limits.Dimension switch
            {
                CalendarEntityCreateLimitDimension.ElapsedTime => "elapsed_time",
                CalendarEntityCreateLimitDimension.ResourcesInspected => "resources_inspected",
                CalendarEntityCreateLimitDimension.CalendarCount => "calendar_count",
                CalendarEntityCreateLimitDimension.ByteCount => "byte_count",
                _ => null
            });
}
