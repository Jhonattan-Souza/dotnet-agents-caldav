using System.ComponentModel;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
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

    public CalendarResourceMoveTools(
        ICalendarService calendarService,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "calendar_resources.move",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Move one revision-bound semantic resource with server-authoritative preconditions and bounded bilateral reconciliation; requires a verified interoperability profile.")]
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
            var terminal = result.Code == CalendarResourceMoveCode.Success && result.Snapshot is not null
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
                "The Calendar Object Resource move exhausted its elapsed_time execution budget.",
                CalendarMutationState.Unknown,
                limits: new CalendarEntityCreateLimits(Dimension: "elapsed_time")).FinalizeResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SelectionUnavailableError();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.UpstreamProtocolError,
                CalendarTelemetryErrorCategory.Upstream,
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
                false),
                "The Calendar Object Resource move could not be completed.",
                CalendarMutationState.Unknown).FinalizeResult();
        }
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(payloadTooLarge);

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
        Content = [new TextContentBlock { Text = "Calendar Object Resource move completed." }]
    };

    private static CalendarToolResult Error(CalendarResourceMoveResult result)
    {
        var facts = CalendarTelemetryFacts.From(result);
        return Error(
            facts,
            Message(result.Code),
            result.MutationState,
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

    private static string Message(CalendarResourceMoveCode code) => code switch
    {
        CalendarResourceMoveCode.InvalidInput => "The Calendar Object Resource move input is invalid.",
        CalendarResourceMoveCode.NotFound => "The source resource or destination Calendar was not found.",
        CalendarResourceMoveCode.Ambiguous => "The destination Calendar selector is ambiguous.",
        CalendarResourceMoveCode.OutsideScope => "The move target is outside the configured Calendar Scope.",
        CalendarResourceMoveCode.EntityKindMismatch => "The source Entity Kind has changed.",
        CalendarResourceMoveCode.UnsupportedCapability => "The destination or server does not support this move.",
        CalendarResourceMoveCode.OpaqueResource => "A resource cannot be projected safely for semantic move.",
        CalendarResourceMoveCode.Conflict => "The move was rejected by current source or UID state.",
        CalendarResourceMoveCode.DestinationConflict => "The move destination already exists.",
        CalendarResourceMoveCode.ConcurrencyUnavailable => "The source or committed destination has no strong Entity Tag.",
        CalendarResourceMoveCode.LimitExhausted => "The Calendar Object Resource move exceeded its bounded work limit.",
        CalendarResourceMoveCode.PayloadTooLarge => "The Calendar Object Resource exceeds the safe payload limit.",
        CalendarResourceMoveCode.UpstreamUnauthorized => "The Calendar mutation was not authorized.",
        CalendarResourceMoveCode.UpstreamForbidden => "The Calendar mutation was forbidden.",
        CalendarResourceMoveCode.UpstreamRateLimited => "The Calendar mutation is rate limited.",
        CalendarResourceMoveCode.UpstreamUnavailable => "The Calendar service is temporarily unavailable.",
        CalendarResourceMoveCode.FidelityFailure => "The committed destination differs from the reviewed source.",
        CalendarResourceMoveCode.CommittedButUnverified => "The committed move could not be verified.",
        CalendarResourceMoveCode.CommittedButConcurrencyUnavailable => "The move committed without a usable destination Entity Tag.",
        CalendarResourceMoveCode.Indeterminate => "The Calendar mutation outcome is indeterminate.",
        _ => "The Calendar mutation returned an invalid response."
    };

    private static string LimitDimension(CalendarResourceMoveLimitDimension dimension) => dimension switch
    {
        CalendarResourceMoveLimitDimension.ElapsedTime => "elapsed_time",
        _ => "unknown"
    };

    private static CallToolResult InputError(bool payloadTooLarge) => Error(
            CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
            payloadTooLarge
                ? "The Calendar Object Resource move arguments exceed the safe payload limit."
                : "The Calendar Object Resource move input is invalid.",
            CalendarMutationState.NotAttempted).FinalizeResult();

    private static CallToolResult SelectionUnavailableError() => Error(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        true),
        "Calendar discovery is temporarily unavailable.",
        CalendarMutationState.NotAttempted).FinalizeResult();

    private static CalendarToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarMutationState mutationState,
        IReadOnlyList<CalendarAuthorizedCandidateResult>? authorizedCandidates = null,
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
            AuthorizedCandidates: authorizedCandidates,
            CurrentSnapshot: currentSnapshot,
            RetryAfterMs: retryAfterMs,
            Limits: limits)),
        Content = [new TextContentBlock { Text = "Calendar Object Resource move failed." }]
    }, facts, mutationState);

    private static CalendarToolResult PayloadLimitError(CalendarMutationState mutationState) => Error(
        CalendarTelemetryFacts.FromInputGuard(payloadTooLarge: true),
        "The Calendar Object Resource move result exceeds the safe payload limit.",
        mutationState);

}
