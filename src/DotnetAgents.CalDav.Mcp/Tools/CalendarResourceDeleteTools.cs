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
            return Error(
                "payload_too_large",
                "limitsAndAdmission",
                "The Calendar Object Resource delete arguments exceed the safe payload limit.",
                false,
                "admissionAndPayload",
                "not_attempted");
        }

        if (!CalendarResourceDeleteArgumentParser.TryParse(arguments, out var revision))
            return InputError();
        if (CalendarResourceDeleteArgumentParser.IsWeakEntityTag(revision.EntityTag))
            return Error(Failure(CalendarResourceDeleteCode.ConcurrencyUnavailable));

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
            return Error(
                "unsupported_capability",
                "capabilityAndProjection",
                "The server does not support the required Calendar discovery capability.",
                false,
                "selectionDiscoveryCapability",
                "not_attempted");
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
            return Error(preview.Failure);
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
            return Error(current.Failure);

        CalendarResourceDeleteResult result;
        try
        {
            result = await _calendarService.DeleteResourceAsync(revision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(new CalendarResourceDeleteResult(
                CalendarResourceDeleteCode.Indeterminate,
                CalendarMutationState.Unknown));
        }
        var mapped = result.Code == CalendarResourceDeleteCode.Success && result.DeletionReceipt is not null
            ? Success(result.DeletionReceipt)
            : Error(result);
        return CalendarQueryToolSupport.EnsureBoundedResult(mapped, (_, _) => Error(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar mutation result exceeds the safe payload limit.",
            false,
            "admissionAndPayload",
            MutationState(result.MutationState)));
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

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge)
    {
        CalendarTelemetry.ObserveStructuredError(
            CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
            CalendarMutationState.NotAttempted);
        return payloadTooLarge ? Error(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar Object Resource delete arguments exceed the safe payload limit.",
            false,
            "admissionAndPayload",
            "not_attempted")
        : InputError();
    }

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

    private static CallToolResult Error(CalendarResourceDeleteResult result)
    {
        CalendarTelemetry.ObserveStructuredError(
            new CalendarStructuredErrorFacts(
                TelemetryCode(result.Code),
                TelemetryCategory(result.Code),
                TelemetryPhase(result),
                result.Retryable),
            result.MutationState);
        var (code, category, message, defaultPhase) = Describe(result.Code);
        return Error(
            code,
            category,
            message,
            result.Retryable,
            ResolvePhase(result, defaultPhase),
            MutationState(result.MutationState),
            result.CurrentSnapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.CurrentSnapshot),
            retryAfterMs: result.RetryAfterMilliseconds);
    }

    private static CallToolResult Success(CalendarResourceDeletionReceipt receipt)
    {
        CalendarTelemetry.ObserveMutationState(CalendarMutationState.Committed);
        return new CallToolResult
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
    }

    private static CallToolResult ConfirmationDeclined()
    {
        CalendarTelemetry.ObserveMutationState(CalendarMutationState.NotAttempted);
        return new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteNonMutationResult(
                "confirmation_declined",
                "not_attempted",
                [])),
            Content = [new TextContentBlock { Text = "Calendar Object Resource deletion was declined." }]
        };
    }

    private static CallToolResult ConfirmationMismatch() => Error(
        "confirmation_mismatch",
        "confirmation",
        "The mutation confirmation does not match the reviewed request.",
        false,
        "mrtr",
        "not_attempted");

    private static CallToolResult ConfirmationExpired() => Error(
        "confirmation_expired",
        "confirmation",
        "The mutation confirmation has expired.",
        false,
        "mrtr",
        "not_attempted");

    private static (string Code, string Category, string Message, string Phase) Describe(
        CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.InvalidInput =>
            ("invalid_input", "input", "The Calendar Object Resource delete input is invalid.", "schemaLexicalDiscriminator"),
        CalendarResourceDeleteCode.NotFound =>
            ("not_found", "selection", "The Calendar Object Resource was not found.", "targetRevision"),
        CalendarResourceDeleteCode.OutsideScope =>
            ("outside_scope", "selection", "The Calendar Object Resource is outside the configured Calendar Scope.", "originScopeAuthorization"),
        CalendarResourceDeleteCode.EntityKindMismatch =>
            ("entity_kind_mismatch", "state", "The Calendar Object Resource Entity Kind has changed.", "targetRevision"),
        CalendarResourceDeleteCode.OpaqueResource =>
            ("opaque_resource", "capabilityAndProjection", "The Calendar Object Resource cannot be projected safely.", "targetRevision"),
        CalendarResourceDeleteCode.Conflict =>
            ("conflict", "state", "The Calendar Object Resource revision has changed.", "targetRevision"),
        CalendarResourceDeleteCode.ConcurrencyUnavailable =>
            ("concurrency_unavailable", "state", "The Calendar Object Resource has no strong Entity Tag.", "targetRevision"),
        CalendarResourceDeleteCode.UnsupportedCapability =>
            ("unsupported_capability", "capabilityAndProjection", "The server does not support conditional Calendar Object Resource deletion.", "selectionDiscoveryCapability"),
        CalendarResourceDeleteCode.PayloadTooLarge =>
            ("payload_too_large", "limitsAndAdmission", "The Calendar Object Resource exceeds the safe payload limit.", "admissionAndPayload"),
        CalendarResourceDeleteCode.UpstreamUnauthorized =>
            ("upstream_unauthorized", "upstream", "The Calendar mutation was not authorized.", "execution"),
        CalendarResourceDeleteCode.UpstreamForbidden =>
            ("upstream_forbidden", "upstream", "The Calendar mutation was forbidden.", "execution"),
        CalendarResourceDeleteCode.UpstreamRateLimited =>
            ("upstream_rate_limited", "upstream", "The Calendar mutation is rate limited.", "execution"),
        CalendarResourceDeleteCode.UpstreamUnavailable =>
            ("upstream_unavailable", "upstream", "The Calendar Object Resource is temporarily unavailable.", "targetRevision"),
        CalendarResourceDeleteCode.UpstreamProtocolError =>
            ("upstream_protocol_error", "upstream", "The Calendar mutation returned an invalid response.", "execution"),
        CalendarResourceDeleteCode.CommittedButUnverified =>
            ("committed_but_unverified", "postWriteTruth", "The committed deletion could not be verified.", "postWriteVerificationOrReconciliation"),
        CalendarResourceDeleteCode.Indeterminate =>
            ("indeterminate", "postWriteTruth", "The Calendar mutation outcome is indeterminate.", "postWriteVerificationOrReconciliation"),
        _ =>
            ("upstream_protocol_error", "upstream", "The Calendar Object Resource returned an invalid response.", "targetRevision")
    };

    private static string ResolvePhase(CalendarResourceDeleteResult result, string defaultPhase)
    {
        if (result.MutationState is CalendarMutationState.Committed or CalendarMutationState.Unknown)
            return "postWriteVerificationOrReconciliation";
        if (result.MutationState == CalendarMutationState.NotCommitted)
        {
            return result.Code == CalendarResourceDeleteCode.UpstreamUnavailable
                && result.CurrentSnapshot is not null
                ? "postWriteVerificationOrReconciliation"
                : "execution";
        }
        if (result.Code == CalendarResourceDeleteCode.InvalidInput)
            return "originScopeAuthorization";
        return defaultPhase;
    }

    private static CalendarTelemetryErrorCode TelemetryCode(CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarResourceDeleteCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        CalendarResourceDeleteCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarResourceDeleteCode.EntityKindMismatch => CalendarTelemetryErrorCode.EntityKindMismatch,
        CalendarResourceDeleteCode.OpaqueResource => CalendarTelemetryErrorCode.OpaqueResource,
        CalendarResourceDeleteCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarResourceDeleteCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarResourceDeleteCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarResourceDeleteCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarResourceDeleteCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarResourceDeleteCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarResourceDeleteCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarResourceDeleteCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarResourceDeleteCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        CalendarResourceDeleteCode.Success => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    private static CalendarTelemetryErrorCategory TelemetryCategory(CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
        CalendarResourceDeleteCode.NotFound or CalendarResourceDeleteCode.OutsideScope =>
            CalendarTelemetryErrorCategory.Selection,
        CalendarResourceDeleteCode.EntityKindMismatch or CalendarResourceDeleteCode.Conflict
            or CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorCategory.State,
        CalendarResourceDeleteCode.OpaqueResource or CalendarResourceDeleteCode.UnsupportedCapability =>
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarResourceDeleteCode.CommittedButUnverified or CalendarResourceDeleteCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        CalendarResourceDeleteCode.UpstreamUnauthorized or CalendarResourceDeleteCode.UpstreamForbidden
            or CalendarResourceDeleteCode.UpstreamRateLimited or CalendarResourceDeleteCode.UpstreamUnavailable
            or CalendarResourceDeleteCode.UpstreamProtocolError => CalendarTelemetryErrorCategory.Upstream,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    private static CalendarTelemetryErrorPhase TelemetryPhase(CalendarResourceDeleteResult result)
    {
        if (result.MutationState is CalendarMutationState.Committed or CalendarMutationState.Unknown)
            return CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation;
        if (result.MutationState == CalendarMutationState.NotCommitted)
        {
            return result.Code == CalendarResourceDeleteCode.UpstreamUnavailable
                && result.CurrentSnapshot is not null
                    ? CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation
                    : CalendarTelemetryErrorPhase.Execution;
        }
        return result.Code switch
        {
            CalendarResourceDeleteCode.InvalidInput => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarResourceDeleteCode.NotFound or CalendarResourceDeleteCode.EntityKindMismatch
                or CalendarResourceDeleteCode.OpaqueResource or CalendarResourceDeleteCode.Conflict
                or CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorPhase.TargetRevision,
            CalendarResourceDeleteCode.OutsideScope => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarResourceDeleteCode.UnsupportedCapability =>
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarResourceDeleteCode.CommittedButUnverified or CalendarResourceDeleteCode.Indeterminate =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };
    }

    private static CallToolResult UnsupportedMrtrError() => Error(
        "unsupported_capability",
        "capabilityAndProjection",
        "Calendar Object Resource deletion requires MRTR confirmation support.",
        false,
        "mrtr",
        "not_attempted");

    private static CallToolResult ConfirmationPreviewPayloadError() => Error(
        "payload_too_large",
        "limitsAndAdmission",
        "The Calendar Object Resource deletion confirmation exceeds the safe human-readable limit.",
        false,
        "admissionAndPayload",
        "not_attempted");

    private static CallToolResult InputError() => Error(
        "invalid_input",
        "input",
        "The Calendar Object Resource delete input is invalid.",
        false,
        "schemaLexicalDiscriminator",
        "not_attempted");

    private static CallToolResult DeadlineError() => Error(
        "limit_exhausted",
        "limitsAndAdmission",
        "The Calendar mutation exhausted its elapsed_time execution budget.",
        false,
        "execution",
        "not_attempted");

    private static CallToolResult PreviewUnavailableError() => Error(
        "upstream_unavailable",
        "upstream",
        "The Calendar Object Resource is temporarily unavailable.",
        true,
        "targetRevision",
        "not_attempted");

    private static CallToolResult PreviewProtocolError() => Error(
        "upstream_protocol_error",
        "upstream",
        "The Calendar Object Resource returned an invalid response.",
        false,
        "targetRevision",
        "not_attempted");

    private static CallToolResult PreviewHttpError(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => Error(
            "upstream_unauthorized", "upstream", "Calendar discovery was not authorized.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.Forbidden => Error(
            "upstream_forbidden", "upstream", "Calendar discovery was forbidden.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.NotFound => Error(
            "upstream_protocol_error", "upstream", "Calendar discovery returned an invalid response.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed => Error(
            "conflict", "state", "Calendar discovery reported a conflicting state.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.RequestEntityTooLarge => Error(
            "payload_too_large", "limitsAndAdmission", "Calendar discovery exceeded the safe payload limit.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.TooManyRequests => Error(
            "upstream_rate_limited", "upstream", "Calendar discovery is rate limited.", true,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented => Error(
            "unsupported_capability", "capabilityAndProjection", "Calendar discovery is not supported.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        System.Net.HttpStatusCode.InsufficientStorage => Error(
            "upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", false,
            "selectionDiscoveryCapability", "not_attempted"),
        >= System.Net.HttpStatusCode.InternalServerError => Error(
            "upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", true,
            "selectionDiscoveryCapability", "not_attempted"),
        _ => Error(
            "upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", true,
            "selectionDiscoveryCapability", "not_attempted")
    };

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        string mutationState,
        CalendarSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => new()
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteErrorResult(
                code,
                category,
                message,
                retryable,
                phase,
                mutationState,
                currentSnapshot,
                retryAfterMs,
                limits)),
            Content = [new TextContentBlock { Text = "Calendar Object Resource deletion failed." }]
        };

    private static string MutationState(CalendarMutationState state)
    {
        CalendarTelemetry.ObserveMutationState(state);
        return CalendarTelemetryVocabulary.MutationStateName(state);
    }

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
