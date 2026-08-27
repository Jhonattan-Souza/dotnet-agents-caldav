using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
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

    public CalendarEntityCreateTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<TimeProvider>())
    {
    }

    internal CalendarEntityCreateTools(ICalendarService calendarService, TimeProvider timeProvider)
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
            var mapped = result.Code == CalendarEntityCreateCode.Success && result.Snapshot is not null
                ? Success(result.Snapshot)
                : Error(result);
            return EnsureBounded(mapped, result.MutationState);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(
                "limit_exhausted",
                "limitsAndAdmission",
                "The Calendar mutation exhausted its elapsed_time execution budget.",
                false,
                "execution",
                "unknown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SelectionUnavailableError();
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error(
                "limit_exhausted",
                "limitsAndAdmission",
                "The Calendar mutation exceeded its Calendar discovery limit.",
                false,
                "selectionDiscoveryCapability",
                "not_attempted",
                limits: new CalendarEntityCreateLimits(CalendarCount: exception.CalendarCount));
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
            return Error(
                "upstream_protocol_error",
                "upstream",
                "Calendar discovery returned an invalid response.",
                false,
                "selectionDiscoveryCapability",
                "not_attempted");
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error(
                "unsupported_capability",
                "capabilityAndProjection",
                "The server does not support the required Calendar discovery capability.",
                false,
                "selectionDiscoveryCapability",
                "not_attempted");
        }
    }

    private static CallToolResult SelectionUnavailableError() => Error(
        "upstream_unavailable",
        "upstream",
        "Calendar discovery is temporarily unavailable.",
        true,
        "selectionDiscoveryCapability",
        "not_attempted");

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

    private static CallToolResult Error(CalendarEntityCreateResult result)
    {
        var (code, category, message, phase) = Describe(result.Code);
        if (result.Code == CalendarEntityCreateCode.NotFound
            && result.MutationState == CalendarMutationState.NotCommitted)
        {
            phase = "execution";
        }
        else if (result.MutationState == CalendarMutationState.NotAttempted
            && result.Code is CalendarEntityCreateCode.UpstreamUnavailable
                or CalendarEntityCreateCode.UpstreamProtocolError)
        {
            phase = "selectionDiscoveryCapability";
        }
        return Error(
            code,
            category,
            message,
            retryable: result.Code == CalendarEntityCreateCode.UpstreamRateLimited
                && result.MutationState == CalendarMutationState.NotCommitted,
            phase,
            MutationState(result.MutationState),
            result.AuthorizedCandidates is { Count: > 0 }
                ? result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray()
                : null,
            result.Snapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.Snapshot),
            limits: result.Limits is null ? null : CalendarEntityCreateLimits.FromLimits(result.Limits));
    }

    private static (string Code, string Category, string Message, string Phase) Describe(
        CalendarEntityCreateCode code) => code switch
    {
        CalendarEntityCreateCode.InvalidInput =>
            ("invalid_input", "input", "The Calendar Entity create input is invalid.", "schemaLexicalDiscriminator"),
        CalendarEntityCreateCode.InvalidCalendarData =>
            ("invalid_calendar_data", "input", "The complete Calendar Entity is invalid.", "completeResourceSemantics"),
        CalendarEntityCreateCode.NotFound =>
            ("not_found", "selection", "No matching authorized Calendar was found.", "selectionDiscoveryCapability"),
        CalendarEntityCreateCode.Ambiguous =>
            ("ambiguous", "selection", "The Calendar selector matched more than one authorized Calendar.", "selectionDiscoveryCapability"),
        CalendarEntityCreateCode.OutsideScope =>
            ("outside_scope", "selection", "The selected Calendar is outside the configured Calendar Scope.", "originScopeAuthorization"),
        CalendarEntityCreateCode.UnsupportedCapability =>
            ("unsupported_capability", "capabilityAndProjection", "The selected Calendar does not advertise the requested Entity Kind.", "selectionDiscoveryCapability"),
        CalendarEntityCreateCode.RecurrenceUnevaluable =>
            ("recurrence_unevaluable", "capabilityAndProjection", "The Recurrence Set could not be evaluated.", "completeResourceSemantics"),
        CalendarEntityCreateCode.OpaqueResource =>
            ("opaque_resource", "capabilityAndProjection", "An existing Calendar resource cannot be projected safely.", "targetRevision"),
        CalendarEntityCreateCode.ConcurrencyUnavailable =>
            ("concurrency_unavailable", "state", "An existing Calendar revision has no strong Entity Tag.", "targetRevision"),
        CalendarEntityCreateCode.DestinationConflict =>
            ("destination_conflict", "state", "The destination Calendar resource already exists.", "execution"),
        CalendarEntityCreateCode.Conflict =>
            ("conflict", "state", "The requested Calendar Entity identity already exists.", "execution"),
        CalendarEntityCreateCode.LimitExhausted =>
            ("limit_exhausted", "limitsAndAdmission", "The Calendar mutation exceeded its bounded work limit.", "execution"),
        CalendarEntityCreateCode.PayloadTooLarge =>
            ("payload_too_large", "limitsAndAdmission", "The Calendar Entity exceeds the safe payload limit.", "admissionAndPayload"),
        CalendarEntityCreateCode.UpstreamUnauthorized =>
            ("upstream_unauthorized", "upstream", "The Calendar mutation was not authorized.", "execution"),
        CalendarEntityCreateCode.UpstreamForbidden =>
            ("upstream_forbidden", "upstream", "The Calendar mutation was forbidden.", "execution"),
        CalendarEntityCreateCode.UpstreamRateLimited =>
            ("upstream_rate_limited", "upstream", "The Calendar mutation is rate limited.", "execution"),
        CalendarEntityCreateCode.UpstreamUnavailable =>
            ("upstream_unavailable", "upstream", "The Calendar mutation is unavailable.", "execution"),
        CalendarEntityCreateCode.UpstreamProtocolError =>
            ("upstream_protocol_error", "upstream", "The Calendar mutation returned an invalid response.", "execution"),
        CalendarEntityCreateCode.FidelityFailure =>
            ("fidelity_failure", "postWriteTruth", "The committed server revision differs from the requested semantics.", "postWriteVerificationOrReconciliation"),
        CalendarEntityCreateCode.CommittedButUnverified =>
            ("committed_but_unverified", "postWriteTruth", "The committed Calendar mutation could not be verified.", "postWriteVerificationOrReconciliation"),
        CalendarEntityCreateCode.CommittedButConcurrencyUnavailable =>
            ("committed_but_concurrency_unavailable", "postWriteTruth", "The committed server revision has no strong Entity Tag.", "postWriteVerificationOrReconciliation"),
        _ =>
            ("indeterminate", "postWriteTruth", "The Calendar mutation outcome is indeterminate.", "postWriteVerificationOrReconciliation")
    };

    private static CallToolResult InputError(bool payloadTooLarge) => payloadTooLarge
        ? Error(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar Entity create arguments exceed the safe payload limit.",
            false,
            "admissionAndPayload",
            "not_attempted")
        : Error(
            "invalid_input",
            "input",
            "The Calendar Entity create input is invalid.",
            false,
            "schemaLexicalDiscriminator",
            "not_attempted");

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        string mutationState,
        IReadOnlyList<CalendarAuthorizedCandidateResult>? candidates = null,
        CalendarSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => new()
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
                code,
                category,
                message,
                retryable,
                phase,
                mutationState,
                candidates,
                currentSnapshot,
                retryAfterMs,
                limits)),
            Content = [new TextContentBlock { Text = "Calendar Entity creation failed." }]
        };

    private static CallToolResult EnsureBounded(
        CallToolResult result,
        CalendarMutationState mutationState) =>
        CalendarQueryToolSupport.EnsureBoundedResult(result, (_, _) => Error(
                "payload_too_large",
                "limitsAndAdmission",
                "The Calendar mutation result exceeds the safe payload limit.",
                false,
                "admissionAndPayload",
                MutationState(mutationState)));

    private static string MutationState(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };
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
