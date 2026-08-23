using System.ComponentModel;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Moves one reviewed resource atomically to a selected compatible Calendar.</summary>
[McpServerToolType]
public sealed class CalendarResourceMoveTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(60);
    private readonly ICalendarService _calendarService;
    private readonly TimeProvider _timeProvider;
    private readonly CalendarMutationAdmission _admission;

    public CalendarResourceMoveTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<CalendarMutationAdmission>())
    {
    }

    internal CalendarResourceMoveTools(
        ICalendarService calendarService,
        TimeProvider timeProvider,
        CalendarMutationAdmission admission)
    {
        _calendarService = calendarService;
        _timeProvider = timeProvider;
        _admission = admission;
    }

    [McpServerTool(
        Name = "calendar_resources.move",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Move one revision-bound semantic resource to the default or selected Calendar.")]
    public Task<CallToolResult> MoveAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) => MoveRawAsync(
            requestContext.Params?.Arguments,
            cancellationToken);

    internal async Task<CallToolResult> MoveRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        using var lease = await _admission.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return BusyError();
        if (!CalendarResourceMoveArgumentParser.TryParse(arguments, out var request))
            return InputError(payloadTooLarge: false);
        return await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ExecuteAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ExecutionDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _calendarService.MoveResourceAsync(request, linked.Token).ConfigureAwait(false);
            var mapped = result.Code == CalendarResourceMoveCode.Success && result.Snapshot is not null
                ? Success(result.Snapshot)
                : Error(result);
            return EnsureBounded(mapped, result.MutationState);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(
                "limit_exhausted",
                "limitsAndAdmission",
                "The Calendar Object Resource move exhausted its elapsed_time execution budget.",
                false,
                "execution",
                "unknown",
                limits: new CalendarEntityCreateLimits(Dimension: "elapsed_time"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SelectionUnavailableError();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(
                "upstream_protocol_error",
                "upstream",
                "The Calendar Object Resource move could not be completed.",
                false,
                "postWriteVerificationOrReconciliation",
                "unknown");
        }
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(payloadTooLarge);

    private static int MeasureArguments(IDictionary<string, JsonElement>? arguments) =>
        CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>());

    private static CallToolResult EnsureBounded(CallToolResult result, CalendarMutationState state) =>
        CalendarQueryToolSupport.EnsureBoundedResult(result, (_, _) => Error(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar Object Resource move result exceeds the safe payload limit.",
            false,
            "admissionAndPayload",
            MutationState(state)));

    private static CallToolResult Success(CalendarResourceSnapshot snapshot) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateSuccessResult(
            "success",
            "committed",
            CalendarSnapshotResult.FromSnapshot(snapshot),
            snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = "Calendar Object Resource move completed." }]
    };

    private static CallToolResult Error(CalendarResourceMoveResult result)
    {
        var description = Describe(result.Code);
        return Error(
            description.Code,
            description.Category,
            description.Message,
            result.Retryable,
            ResolvePhase(result, description.Phase),
            MutationState(result.MutationState),
            result.AuthorizedCandidates is { Count: > 0 }
                ? result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray()
                : null,
            result.Snapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.Snapshot),
            retryAfterMs: result.RetryAfterMilliseconds,
            limits: result.LimitDimension is null
                && result.CalendarCount is null
                ? null
                : new CalendarEntityCreateLimits(
                    CalendarCount: result.CalendarCount,
                    Dimension: result.LimitDimension is null
                        ? null
                        : LimitDimension(result.LimitDimension.Value)));
    }

    private static MoveErrorDescription Describe(CalendarResourceMoveCode code) => code switch
    {
        CalendarResourceMoveCode.InvalidInput =>
            new("invalid_input", "input", "The Calendar Object Resource move input is invalid.", "schemaLexicalDiscriminator"),
        CalendarResourceMoveCode.NotFound =>
            new("not_found", "selection", "The source resource or destination Calendar was not found.", "selectionDiscoveryCapability"),
        CalendarResourceMoveCode.Ambiguous =>
            new("ambiguous", "selection", "The destination Calendar selector is ambiguous.", "selectionDiscoveryCapability"),
        CalendarResourceMoveCode.OutsideScope =>
            new("outside_scope", "selection", "The move target is outside the configured Calendar Scope.", "originScopeAuthorization"),
        CalendarResourceMoveCode.EntityKindMismatch =>
            new("entity_kind_mismatch", "state", "The source Entity Kind has changed.", "targetRevision"),
        CalendarResourceMoveCode.UnsupportedCapability =>
            new("unsupported_capability", "capabilityAndProjection", "The destination or server does not support this move.", "selectionDiscoveryCapability"),
        CalendarResourceMoveCode.OpaqueResource =>
            new("opaque_resource", "capabilityAndProjection", "A resource cannot be projected safely for semantic move.", "completeResourceSemantics"),
        CalendarResourceMoveCode.Conflict =>
            new("conflict", "state", "The move was rejected by current source or UID state.", "targetRevision"),
        CalendarResourceMoveCode.DestinationConflict =>
            new("destination_conflict", "state", "The move destination already exists.", "execution"),
        CalendarResourceMoveCode.ConcurrencyUnavailable =>
            new("concurrency_unavailable", "state", "The source or committed destination has no strong Entity Tag.", "targetRevision"),
        CalendarResourceMoveCode.LimitExhausted =>
            new("limit_exhausted", "limitsAndAdmission", "The Calendar Object Resource move exceeded its bounded work limit.", "execution"),
        CalendarResourceMoveCode.PayloadTooLarge =>
            new("payload_too_large", "limitsAndAdmission", "The Calendar Object Resource exceeds the safe payload limit.", "admissionAndPayload"),
        CalendarResourceMoveCode.UpstreamUnauthorized =>
            new("upstream_unauthorized", "upstream", "The Calendar mutation was not authorized.", "execution"),
        CalendarResourceMoveCode.UpstreamForbidden =>
            new("upstream_forbidden", "upstream", "The Calendar mutation was forbidden.", "execution"),
        CalendarResourceMoveCode.UpstreamRateLimited =>
            new("upstream_rate_limited", "upstream", "The Calendar mutation is rate limited.", "execution"),
        CalendarResourceMoveCode.UpstreamUnavailable =>
            new("upstream_unavailable", "upstream", "The Calendar service is temporarily unavailable.", "execution"),
        CalendarResourceMoveCode.FidelityFailure =>
            new("fidelity_failure", "postWriteTruth", "The committed destination differs from the reviewed source.", "postWriteVerificationOrReconciliation"),
        CalendarResourceMoveCode.CommittedButUnverified =>
            new("committed_but_unverified", "postWriteTruth", "The committed move could not be verified.", "postWriteVerificationOrReconciliation"),
        CalendarResourceMoveCode.CommittedButConcurrencyUnavailable =>
            new("committed_but_concurrency_unavailable", "postWriteTruth", "The move committed without a usable destination Entity Tag.", "postWriteVerificationOrReconciliation"),
        CalendarResourceMoveCode.Indeterminate =>
            new("indeterminate", "postWriteTruth", "The Calendar mutation outcome is indeterminate.", "postWriteVerificationOrReconciliation"),
        _ => new("upstream_protocol_error", "upstream", "The Calendar mutation returned an invalid response.", "execution")
    };

    private static string ResolvePhase(CalendarResourceMoveResult result, string defaultPhase)
    {
        if (result.MutationState is CalendarMutationState.Committed or CalendarMutationState.Unknown)
            return "postWriteVerificationOrReconciliation";
        if (result.Phase is not null)
            return Phase(result.Phase.Value);
        return result.MutationState == CalendarMutationState.NotCommitted ? "execution" : defaultPhase;
    }

    private static string Phase(CalendarResourceMovePhase phase) => phase switch
    {
        CalendarResourceMovePhase.SchemaLexicalDiscriminator => "schemaLexicalDiscriminator",
        CalendarResourceMovePhase.OriginScopeAuthorization => "originScopeAuthorization",
        CalendarResourceMovePhase.SelectionDiscoveryCapability => "selectionDiscoveryCapability",
        CalendarResourceMovePhase.TargetRevision => "targetRevision",
        CalendarResourceMovePhase.CompleteResourceSemantics => "completeResourceSemantics",
        CalendarResourceMovePhase.AdmissionAndPayload => "admissionAndPayload",
        CalendarResourceMovePhase.PostWriteVerificationOrReconciliation => "postWriteVerificationOrReconciliation",
        _ => "execution"
    };

    private static string MutationState(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };

    private static string LimitDimension(CalendarResourceMoveLimitDimension dimension) => dimension switch
    {
        CalendarResourceMoveLimitDimension.ElapsedTime => "elapsed_time",
        _ => "unknown"
    };

    private static CallToolResult InputError(bool payloadTooLarge) => Error(
        payloadTooLarge ? "payload_too_large" : "invalid_input",
        payloadTooLarge ? "limitsAndAdmission" : "input",
        payloadTooLarge
            ? "The Calendar Object Resource move arguments exceed the safe payload limit."
            : "The Calendar Object Resource move input is invalid.",
        false,
        payloadTooLarge ? "admissionAndPayload" : "schemaLexicalDiscriminator",
        "not_attempted");

    private static CallToolResult BusyError() => Error(
        "busy",
        "limitsAndAdmission",
        "Calendar mutation admission is busy.",
        true,
        "admissionAndPayload",
        "not_attempted",
        retryAfterMs: CalendarMutationAdmission.RetryAfterMilliseconds);

    private static CallToolResult SelectionUnavailableError() => Error(
        "upstream_unavailable",
        "upstream",
        "Calendar discovery is temporarily unavailable.",
        true,
        "selectionDiscoveryCapability",
        "not_attempted");

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        string mutationState,
        IReadOnlyList<CalendarAuthorizedCandidateResult>? authorizedCandidates = null,
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
            AuthorizedCandidates: authorizedCandidates,
            CurrentSnapshot: currentSnapshot,
            RetryAfterMs: retryAfterMs,
            Limits: limits)),
        Content = [new TextContentBlock { Text = "Calendar Object Resource move failed." }]
    };

    private sealed record MoveErrorDescription(string Code, string Category, string Message, string Phase);
}
