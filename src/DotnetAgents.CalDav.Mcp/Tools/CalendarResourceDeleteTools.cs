using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Deletes one reviewed Calendar Object Resource through revision-bound MRTR confirmation.</summary>
[McpServerToolType]
internal sealed class CalendarResourceDeleteTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private const string ConfirmationKey = "confirm_delete";
    private const string ConfirmationTitle = "Confirm deletion";
    private const string ConfirmationDescription = "Delete exactly the reviewed resource revision.";
    private static readonly TimeSpan BeforeDispatchDeadline = TimeSpan.FromSeconds(30);
    private readonly ICalendarService _calendarService;
    private readonly CalendarMutationRequestStateProtector _stateProtector;
    private readonly TimeProvider _timeProvider;

    public CalendarResourceDeleteTools(
        ICalendarService calendarService,
        CalendarMutationRequestStateProtector stateProtector,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _stateProtector = stateProtector;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "calendar_resources.delete",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarResourceDeleteSuccessResult)),
     Description("Confirm and delete one revision-bound Calendar Object Resource.")]
    public Task<CallToolResult> DeleteAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) => DeleteRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    internal async Task<CallToolResult> DeleteRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (CalendarQueryToolSupport.MeasureArguments(
                arguments,
                arguments ?? new Dictionary<string, JsonElement>()) > MaximumArgumentBytes)
        {
            return TerminalError(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.PayloadTooLarge,
                CalendarTelemetryErrorCategory.LimitsAndAdmission,
                CalendarTelemetryErrorPhase.AdmissionAndPayload,
                false),
                "The Calendar Object Resource delete arguments exceed the safe payload limit.",
                CalendarMutationState.NotAttempted);
        }

        if (!CalendarResourceDeleteArgumentParser.TryParse(arguments, out var revision))
            return InputError();
        if (CalendarResourceDeleteArgumentParser.IsWeakEntityTag(revision.EntityTag))
            return Error(Failure(CalendarResourceDeleteCode.ConcurrencyUnavailable)).FinalizeResult();

        return await ExecuteWithDeadlineAsync(
            revision,
            requestState,
            inputResponses,
            mrtrSupported,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ExecuteWithDeadlineAsync(
        CalendarResourceRevisionReference revision,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(BeforeDispatchDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ExecuteRoundAsync(
                revision,
                requestState,
                inputResponses,
                mrtrSupported,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return DeadlineError();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PreviewUnavailableError();
        }
        catch (HttpRequestException exception)
        {
            return PreviewHttpError(exception.StatusCode);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return TerminalError(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.UnsupportedCapability,
                CalendarTelemetryErrorCategory.CapabilityAndProjection,
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
                false),
                "The server does not support the required Calendar discovery capability.",
                CalendarMutationState.NotAttempted);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return TerminalError(new CalendarStructuredErrorFacts(
                CalendarTelemetryErrorCode.LimitExhausted,
                CalendarTelemetryErrorCategory.LimitsAndAdmission,
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
                false),
                "The Calendar mutation exceeded its Calendar discovery limit.",
                CalendarMutationState.NotAttempted,
                limits: new CalendarEntityCreateLimits(CalendarCount: exception.CalendarCount));
        }
        catch (Exception exception) when (exception is not (InputRequiredException or OperationCanceledException))
        {
            return PreviewProtocolError();
        }
    }

    private async Task<CallToolResult> ExecuteRoundAsync(
        CalendarResourceRevisionReference revision,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        var hasContinuation = requestState is not null || inputResponses is not null;
        if (hasContinuation && !mrtrSupported)
            return UnsupportedMrtrError();
        if (hasContinuation)
            return await ContinueAsync(revision, requestState, inputResponses, cancellationToken).ConfigureAwait(false);

        var proposedMessage = CreateConfirmationMessage(
            revision.Href,
            revision.EntityUid,
            revision.EntityKind,
            revision.EntityTag);
        if (!IsConfirmationPreviewWithinBudget(proposedMessage))
            return ConfirmationPreviewPayloadError();

        var preview = await ReviewAsync(revision, cancellationToken).ConfigureAwait(false);
        if (preview.Failure is not null)
            return Error(preview.Failure).FinalizeResult();
        if (!mrtrSupported)
            return UnsupportedMrtrError();

        cancellationToken.ThrowIfCancellationRequested();
        var state = _stateProtector.Protect(revision);
        var reviewed = preview.Snapshot!;
        var confirmationMessage = CreateConfirmationMessage(
            reviewed.ResourceHref,
            reviewed.Projection.EntityUid!,
            reviewed.Projection.Kind == CalendarResourceProjectionKind.Event
                ? CalendarEntityKind.Event
                : CalendarEntityKind.Todo,
            reviewed.EntityTag);
        if (!IsConfirmationPreviewWithinBudget(confirmationMessage))
            return ConfirmationPreviewPayloadError();
        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                [ConfirmationKey] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Mode = "form",
                    Message = confirmationMessage,
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            ["confirm"] = new ElicitRequestParams.BooleanSchema
                            {
                                Title = ConfirmationTitle,
                                Description = ConfirmationDescription,
                                Default = false
                            }
                        },
                        Required = ["confirm"]
                    }
                })
            },
            state);
    }

    private async Task<CallToolResult> ContinueAsync(
        CalendarResourceRevisionReference revision,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        CancellationToken cancellationToken)
    {
        var confirmation = ReadContinuation(revision, requestState, inputResponses);
        if (confirmation == ConfirmationDecision.Mismatch)
        {
            return ConfirmationMismatch();
        }
        if (confirmation == ConfirmationDecision.Expired)
            return ConfirmationExpired();
        if (confirmation == ConfirmationDecision.Declined)
            return ConfirmationDeclined();

        var current = await ReviewAsync(revision, cancellationToken).ConfigureAwait(false);
        if (current.Failure is not null)
            return Error(current.Failure).FinalizeResult();

        CalendarResourceDeleteResult result;
        try
        {
            result = await _calendarService.DeleteResourceAsync(revision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(new CalendarResourceDeleteResult(
                CalendarResourceDeleteCode.Indeterminate,
                CalendarMutationState.Unknown)).FinalizeResult();
        }
        var mapped = result.Code == CalendarResourceDeleteCode.Success && result.DeletionReceipt is not null
            ? CalendarToolResult.Success(Success(result.DeletionReceipt), result.MutationState)
            : Error(result);
        return mapped.FinalizeBounded((_, _) => Error(new CalendarStructuredErrorFacts(
            CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarTelemetryErrorPhase.AdmissionAndPayload,
            false),
            "The Calendar mutation result exceeds the safe payload limit.",
            result.MutationState));
    }

    private ConfirmationDecision ReadContinuation(
        CalendarResourceRevisionReference revision,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses)
    {
        if (!TryGetConfirmationResponse(requestState, inputResponses, out var response))
            return ConfirmationDecision.Mismatch;
        if (!_stateProtector.TryUnprotect(requestState!, revision, out var expired))
            return expired ? ConfirmationDecision.Expired : ConfirmationDecision.Mismatch;
        return !TryReadConfirmation(response, out var confirmed)
            ? ConfirmationDecision.Mismatch
            : confirmed ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined;
    }

    private static bool TryGetConfirmationResponse(
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        out InputResponse response)
    {
        response = default!;
        if (string.IsNullOrEmpty(requestState)
            || inputResponses is null
            || inputResponses.Count != 1
            || !inputResponses.TryGetValue(ConfirmationKey, out var candidate)
            || candidate is null)
        {
            return false;
        }
        response = candidate;
        return true;
    }

    private static bool TryReadConfirmation(InputResponse response, out bool confirmed)
    {
        confirmed = false;
        var elicitation = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        if (elicitation is null)
            return false;
        if (elicitation.Action is "decline" or "cancel")
            return true;
        if (!string.Equals(elicitation.Action, "accept", StringComparison.Ordinal)
            || elicitation.Content is null
            || elicitation.Content.Count != 1
            || !elicitation.Content.TryGetValue("confirm", out var element)
            || element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        confirmed = element.GetBoolean();
        return true;
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => payloadTooLarge
        ? TerminalError(CalendarTelemetryFacts.FromInputGuard(payloadTooLarge: true),
            "The Calendar Object Resource delete arguments exceed the safe payload limit.",
            CalendarMutationState.NotAttempted)
        : InputError();

    private async Task<ReviewOutcome> ReviewAsync(
        CalendarResourceRevisionReference revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await _calendarService.GetResourceAsync(revision.Href, cancellationToken).ConfigureAwait(false);
            if (read.Code != CalendarResourceReadCode.Success || read.Snapshot is null)
                return new ReviewOutcome(null, FromReadFailure(read.Code));
            if (!read.Snapshot.SemanticMutationAvailable)
                return new ReviewOutcome(null, Failure(CalendarResourceDeleteCode.OpaqueResource, read.Snapshot));
            var kind = read.Snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
                ? CalendarEntityKind.Event
                : CalendarEntityKind.Todo;
            if (kind != revision.EntityKind)
                return new ReviewOutcome(null, Failure(CalendarResourceDeleteCode.EntityKindMismatch, read.Snapshot));
            return string.Equals(read.Snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
                && string.Equals(read.Snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal)
                ? new ReviewOutcome(read.Snapshot, null)
                : new ReviewOutcome(null, Failure(CalendarResourceDeleteCode.Conflict, read.Snapshot));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return new ReviewOutcome(
                null,
                Failure(CalendarResourceDeleteCode.UpstreamUnavailable, retryable: true));
        }
    }

    private static string CreateConfirmationMessage(
        string href,
        string entityUid,
        CalendarEntityKind entityKind,
        string entityTag)
    {
        var kind = entityKind == CalendarEntityKind.Event ? "event" : "todo";
        return $"Confirm calendar_resources.delete for href {href}, UID {entityUid}, kind {kind}, and expected ETag {entityTag}.";
    }

    private static bool IsConfirmationPreviewWithinBudget(string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new ConfirmationPreviewBudget(
            message,
            ConfirmationTitle,
            ConfirmationDescription)).Length <= CalendarQueryToolSupport.MaximumHumanReadableBytes;

    private static CalendarResourceDeleteResult FromReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.InvalidInput => Failure(CalendarResourceDeleteCode.InvalidInput),
        CalendarResourceReadCode.NotFound => Failure(CalendarResourceDeleteCode.NotFound),
        CalendarResourceReadCode.OutsideScope => Failure(CalendarResourceDeleteCode.OutsideScope),
        CalendarResourceReadCode.ConcurrencyUnavailable => Failure(CalendarResourceDeleteCode.ConcurrencyUnavailable),
        CalendarResourceReadCode.PayloadTooLarge => Failure(CalendarResourceDeleteCode.PayloadTooLarge),
        _ => Failure(CalendarResourceDeleteCode.UpstreamProtocolError)
    };

    private static CalendarResourceDeleteResult Failure(
        CalendarResourceDeleteCode code,
        CalendarResourceSnapshot? snapshot = null,
        bool retryable = false) =>
        new(
            code,
            CalendarMutationState.NotAttempted,
            CurrentSnapshot: snapshot,
            Retryable: retryable);

    private static CalendarToolResult Error(CalendarResourceDeleteResult result)
    {
        var facts = CalendarTelemetryFacts.From(result);
        return Error(
            facts,
            Message(result.Code),
            result.MutationState,
            result.CurrentSnapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.CurrentSnapshot),
            retryAfterMs: result.RetryAfterMilliseconds);
    }

    private static CallToolResult Success(CalendarResourceDeletionReceipt receipt) => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteSuccessResult(
                "success",
                "committed",
                new CalendarResourceDeletionReceiptResult(
                    receipt.Href,
                    receipt.EntityUid,
                    receipt.EntityKind == CalendarEntityKind.Event ? "event" : "todo",
                    receipt.ConsumedEntityTag),
                [])),
            Content = [new TextContentBlock { Text = "Calendar Object Resource deletion completed." }]
        };

    private static CallToolResult ConfirmationDeclined() => CalendarToolResult.Success(
        new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteNonMutationResult(
                "confirmation_declined",
                "not_attempted",
                [])),
            Content = [new TextContentBlock { Text = "Calendar Object Resource deletion was declined." }]
        }, CalendarMutationState.NotAttempted).FinalizeResult();

    private static CallToolResult ConfirmationMismatch() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.ConfirmationMismatch,
        CalendarTelemetryErrorCategory.Confirmation,
        CalendarTelemetryErrorPhase.Mrtr,
        false),
        "The mutation confirmation does not match the reviewed request.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult ConfirmationExpired() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.ConfirmationExpired,
        CalendarTelemetryErrorCategory.Confirmation,
        CalendarTelemetryErrorPhase.Mrtr,
        false),
        "The mutation confirmation has expired.",
        CalendarMutationState.NotAttempted);

    private static string Message(
        CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.InvalidInput => "The Calendar Object Resource delete input is invalid.",
        CalendarResourceDeleteCode.NotFound => "The Calendar Object Resource was not found.",
        CalendarResourceDeleteCode.OutsideScope => "The Calendar Object Resource is outside the configured Calendar Scope.",
        CalendarResourceDeleteCode.EntityKindMismatch => "The Calendar Object Resource Entity Kind has changed.",
        CalendarResourceDeleteCode.OpaqueResource => "The Calendar Object Resource cannot be projected safely.",
        CalendarResourceDeleteCode.Conflict => "The Calendar Object Resource revision has changed.",
        CalendarResourceDeleteCode.ConcurrencyUnavailable => "The Calendar Object Resource has no strong Entity Tag.",
        CalendarResourceDeleteCode.UnsupportedCapability => "The server does not support conditional Calendar Object Resource deletion.",
        CalendarResourceDeleteCode.PayloadTooLarge => "The Calendar Object Resource exceeds the safe payload limit.",
        CalendarResourceDeleteCode.UpstreamUnauthorized => "The Calendar mutation was not authorized.",
        CalendarResourceDeleteCode.UpstreamForbidden => "The Calendar mutation was forbidden.",
        CalendarResourceDeleteCode.UpstreamRateLimited => "The Calendar mutation is rate limited.",
        CalendarResourceDeleteCode.UpstreamUnavailable => "The Calendar Object Resource is temporarily unavailable.",
        CalendarResourceDeleteCode.UpstreamProtocolError => "The Calendar mutation returned an invalid response.",
        CalendarResourceDeleteCode.CommittedButUnverified => "The committed deletion could not be verified.",
        CalendarResourceDeleteCode.Indeterminate => "The Calendar mutation outcome is indeterminate.",
        _ => "The Calendar Object Resource returned an invalid response."
    };

    private static CallToolResult UnsupportedMrtrError() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarTelemetryErrorPhase.Mrtr,
        false),
        "Calendar Object Resource deletion requires MRTR confirmation support.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult ConfirmationPreviewPayloadError() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarTelemetryErrorPhase.AdmissionAndPayload,
        false),
        "The Calendar Object Resource deletion confirmation exceeds the safe human-readable limit.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult InputError() => TerminalError(CalendarTelemetryFacts.FromInputGuard(false),
        "The Calendar Object Resource delete input is invalid.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult DeadlineError() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.LimitExhausted,
        CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarTelemetryErrorPhase.Execution,
        false),
        "The Calendar mutation exhausted its elapsed_time execution budget.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult PreviewUnavailableError() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.TargetRevision,
        true),
        "The Calendar Object Resource is temporarily unavailable.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult PreviewProtocolError() => TerminalError(new CalendarStructuredErrorFacts(
        CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.TargetRevision,
        false),
        "The Calendar Object Resource returned an invalid response.",
        CalendarMutationState.NotAttempted);

    private static CallToolResult PreviewHttpError(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => PreviewError(
            CalendarTelemetryErrorCode.UpstreamUnauthorized, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery was not authorized."),
        System.Net.HttpStatusCode.Forbidden => PreviewError(
            CalendarTelemetryErrorCode.UpstreamForbidden, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery was forbidden."),
        System.Net.HttpStatusCode.NotFound => PreviewError(
            CalendarTelemetryErrorCode.UpstreamProtocolError, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery returned an invalid response."),
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed => PreviewError(
            CalendarTelemetryErrorCode.Conflict, CalendarTelemetryErrorCategory.State,
            "Calendar discovery reported a conflicting state."),
        System.Net.HttpStatusCode.RequestEntityTooLarge => PreviewError(
            CalendarTelemetryErrorCode.PayloadTooLarge, CalendarTelemetryErrorCategory.LimitsAndAdmission,
            "Calendar discovery exceeded the safe payload limit."),
        System.Net.HttpStatusCode.TooManyRequests => PreviewError(
            CalendarTelemetryErrorCode.UpstreamRateLimited, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery is rate limited.", retryable: true),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented => PreviewError(
            CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
            "Calendar discovery is not supported."),
        System.Net.HttpStatusCode.InsufficientStorage => PreviewError(
            CalendarTelemetryErrorCode.UpstreamUnavailable, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery is temporarily unavailable."),
        >= System.Net.HttpStatusCode.InternalServerError => PreviewError(
            CalendarTelemetryErrorCode.UpstreamUnavailable, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery is temporarily unavailable.", retryable: true),
        _ => PreviewError(
            CalendarTelemetryErrorCode.UpstreamUnavailable, CalendarTelemetryErrorCategory.Upstream,
            "Calendar discovery is temporarily unavailable.", retryable: true)
    };

    private static CallToolResult PreviewError(
        CalendarTelemetryErrorCode code,
        CalendarTelemetryErrorCategory category,
        string message,
        bool retryable = false) => TerminalError(new CalendarStructuredErrorFacts(
            code,
            category,
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            retryable),
        message,
        CalendarMutationState.NotAttempted);

    private static CalendarToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarMutationState mutationState,
        CalendarSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => CalendarToolResult.Error(new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteErrorResult(
                facts.CodeName,
                facts.CategoryName,
                message,
                facts.Retryable,
                facts.PhaseName,
                CalendarTelemetryVocabulary.MutationStateName(mutationState),
                currentSnapshot,
                retryAfterMs,
                limits)),
            Content = [new TextContentBlock { Text = "Calendar Object Resource deletion failed." }]
        }, facts, mutationState);

    private static CallToolResult TerminalError(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarMutationState mutationState,
        CalendarSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => Error(
            facts,
            message,
            mutationState,
            currentSnapshot,
            retryAfterMs,
            limits).FinalizeResult();

    private enum ConfirmationDecision
    {
        Mismatch,
        Expired,
        Declined,
        Confirmed
    }

    private sealed record ReviewOutcome(
        CalendarResourceSnapshot? Snapshot,
        CalendarResourceDeleteResult? Failure);

    private sealed record ConfirmationPreviewBudget(
        string Message,
        string Title,
        string Description);
}

public sealed record CalendarResourceDeleteSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("deletionReceipt")] CalendarResourceDeletionReceiptResult DeletionReceipt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarResourceDeletionReceiptResult(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("entityUid")] string EntityUid,
    [property: JsonPropertyName("entityKind")] string EntityKind,
    [property: JsonPropertyName("consumedEntityTag")] string ConsumedEntityTag);

public sealed record CalendarResourceDeleteNonMutationResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarResourceDeleteErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("currentSnapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CalendarSnapshotResult? CurrentSnapshot = null,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? RetryAfterMs = null,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CalendarEntityCreateLimits? Limits = null);
