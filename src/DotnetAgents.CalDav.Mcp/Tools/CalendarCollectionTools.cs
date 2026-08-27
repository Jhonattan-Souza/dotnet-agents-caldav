using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Creates Calendar collections and deletes them through MRTR confirmation.</summary>
[McpServerToolType]
internal sealed class CalendarCollectionTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private const string ConfirmationKey = "confirm_delete";
    private static readonly TimeSpan BeforeDispatchDeadline = TimeSpan.FromSeconds(60);
    private readonly ICalendarCollectionModule _module;
    private readonly CalendarMutationRequestStateProtector _stateProtector;
    private readonly TimeProvider _timeProvider;

    public CalendarCollectionTools(
        ICalendarCollectionModule module,
        CalendarMutationRequestStateProtector stateProtector,
        TimeProvider timeProvider)
    {
        _module = module;
        _stateProtector = stateProtector;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "calendars.create",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarCollectionCreateSuccessResult)),
     Description("Create one CalDAV Calendar collection for Events, To-dos, or both using MKCALENDAR.")]
    public Task<CallToolResult> CreateAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) => CreateRawAsync(
            requestContext.Params?.Arguments,
            cancellationToken);

    [McpServerTool(
        Name = "calendars.delete",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarCollectionDeleteSuccessResult)),
     Description("Confirm and delete one exact CalDAV Calendar collection, including its resources.")]
    public Task<CallToolResult> DeleteAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) => DeleteRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    internal async Task<CallToolResult> CreateRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return Error("payload_too_large", "limitsAndAdmission", "The Calendar collection create arguments exceed the safe payload limit.", false, "admissionAndPayload", "not_attempted");
        return !CalendarCollectionArgumentParser.TryParseCreate(arguments, out var request)
            ? Error("invalid_input", "input", "The Calendar collection create input is invalid.", false, "schemaLexicalDiscriminator", "not_attempted")
            : await ExecuteCreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CallToolResult> DeleteRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return Error("payload_too_large", "limitsAndAdmission", "The Calendar collection delete arguments exceed the safe payload limit.", false, "admissionAndPayload", "not_attempted");
        if (!CalendarCollectionArgumentParser.TryParseDelete(arguments, out var request))
            return Error("invalid_input", "input", "The Calendar collection delete input is invalid.", false, "schemaLexicalDiscriminator", "not_attempted");
        return await ExecuteDeleteAsync(request, requestState, inputResponses, mrtrSupported, cancellationToken).ConfigureAwait(false);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge)
    {
        CalendarTelemetry.ObserveStructuredError(
            CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
            CalendarMutationState.NotAttempted);
        return payloadTooLarge
            ? Error("payload_too_large", "limitsAndAdmission", "The Calendar collection arguments exceed the safe payload limit.", false, "admissionAndPayload", "not_attempted")
            : Error("invalid_input", "input", "The Calendar collection input is invalid.", false, "schemaLexicalDiscriminator", "not_attempted");
    }

    private async Task<CallToolResult> ExecuteCreateAsync(
        CalendarCollectionCreateRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(BeforeDispatchDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _module.CreateAsync(request, linked.Token).ConfigureAwait(false);
            CalendarTelemetry.ObserveMutationState(result.MutationState);
            return result.Code == CalendarCollectionCreateCode.Success && result.Calendar is not null
                ? CreateSuccess(result.Calendar)
                : Error(result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The Calendar mutation exhausted its elapsed_time execution budget.", false, "execution", "unknown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The Calendar mutation exceeded its Calendar discovery limit.", false, "selectionDiscoveryCapability", "not_attempted", limits: new CalendarCollectionLimits(exception.CalendarCount));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar discovery capability.", false, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return Error("upstream_protocol_error", "upstream", "Calendar discovery returned an invalid response.", false, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Error("upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (HttpRequestException exception)
        {
            return Error(MapHttpCode(exception.StatusCode), "upstream", "The Calendar collection operation was rejected by the server.", false, "execution", "not_attempted");
        }
    }

    private async Task<CallToolResult> ExecuteDeleteAsync(
        CalendarCollectionDeleteRequest request,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        var continuation = requestState is not null || inputResponses is not null;
        if (continuation && !mrtrSupported)
            return Error("unsupported_capability", "capabilityAndProjection", "Calendar collection deletion requires MRTR confirmation support.", false, "mrtr", "not_attempted");

        using var deadline = new CancellationTokenSource(BeforeDispatchDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var review = await _module.ReviewDeleteAsync(request, linked.Token).ConfigureAwait(false);
            if (review.Outcome is not null)
                return Error(review.Outcome);
            if (!continuation)
            {
                if (!mrtrSupported)
                    return Error("unsupported_capability", "capabilityAndProjection", "Calendar collection deletion requires MRTR confirmation support.", false, "mrtr", "not_attempted");
                var binding = review.Binding!;
                var state = _stateProtector.ProtectCalendarCollectionDelete(binding);
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        [ConfirmationKey] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "form",
                            Message = ConfirmationMessage(review.Calendar!),
                            RequestedSchema = new ElicitRequestParams.RequestSchema
                            {
                                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                                {
                                    ["confirm"] = new ElicitRequestParams.BooleanSchema
                                    {
                                        Title = "Confirm collection deletion",
                                        Description = "Delete the collection and all Calendar Object Resources below it.",
                                        Default = false
                                    }
                                },
                                Required = ["confirm"]
                            }
                        })
                    },
                    state);
            }

            if (!TryReadContinuation(requestState, inputResponses, review.Binding!, out var decision, out var expired))
                return Error(expired ? "confirmation_expired" : "confirmation_mismatch", "confirmation", expired
                    ? "The mutation confirmation has expired."
                    : "The mutation confirmation does not match the reviewed request.", false, "mrtr", "not_attempted");
            if (decision == ConfirmationDecision.Declined)
                return DeleteDeclined();

            var result = await _module.ExecuteConfirmedDeleteAsync(request, review.Binding!, linked.Token).ConfigureAwait(false);
            CalendarTelemetry.ObserveMutationState(result.MutationState);
            return result.Code == CalendarCollectionDeleteCode.Success
                ? DeleteSuccess(result.Calendar!)
                : Error(result);
        }
        catch (InputRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The Calendar mutation exhausted its elapsed_time execution budget.", false, "execution", "unknown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The Calendar collection is temporarily unavailable.", true, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The Calendar mutation exceeded its Calendar discovery limit.", false, "selectionDiscoveryCapability", "not_attempted", limits: new CalendarCollectionLimits(exception.CalendarCount));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar discovery capability.", false, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return Error("upstream_protocol_error", "upstream", "Calendar discovery returned an invalid response.", false, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Error("upstream_unavailable", "upstream", "Calendar discovery is temporarily unavailable.", true, "selectionDiscoveryCapability", "not_attempted");
        }
        catch (HttpRequestException exception)
        {
            return Error(MapHttpCode(exception.StatusCode), "upstream", "The Calendar collection operation was rejected by the server.", false, "execution", "not_attempted");
        }
    }

    private bool TryReadContinuation(
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        CalendarCollectionDeleteReviewBinding binding,
        out ConfirmationDecision decision,
        out bool expired)
    {
        decision = ConfirmationDecision.Declined;
        expired = false;
        if (string.IsNullOrEmpty(requestState)
            || inputResponses is null
            || inputResponses.Count != 1
            || !inputResponses.TryGetValue(ConfirmationKey, out var response)
            || response is null)
            return false;

        if (!_stateProtector.TryUnprotectCalendarCollectionDelete(requestState, binding, out expired))
            return false;

        var elicitation = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        if (elicitation is null)
            return false;
        if (elicitation.Action is "decline" or "cancel")
        {
            decision = ConfirmationDecision.Declined;
            return true;
        }
        if (!string.Equals(elicitation.Action, "accept", StringComparison.Ordinal)
            || elicitation.Content is null
            || elicitation.Content.Count != 1
            || !elicitation.Content.TryGetValue("confirm", out var confirmed)
            || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        decision = confirmed.GetBoolean() ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined;
        return true;
    }

    private static string ConfirmationMessage(CalendarDescriptor descriptor)
    {
        var kinds = string.Join(", ", new[]
        {
            descriptor.EventSupport == EntityKindSupport.Advertised ? "event" : null,
            descriptor.TodoSupport == EntityKindSupport.Advertised ? "todo" : null
        }.OfType<string>());
        return $"Delete Calendar collection '{descriptor.DisplayName ?? descriptor.Href}' at {descriptor.Href}, including all resources? Advertised kinds: {kinds}.";
    }

    private static CallToolResult CreateSuccess(CalendarDescriptor descriptor) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarCollectionCreateSuccessResult(
            "success",
            "committed",
            CalendarListItem.FromDescriptor(descriptor),
            [])),
        Content = [new TextContentBlock { Text = "Calendar collection operation completed." }]
    };

    private static CallToolResult DeleteSuccess(CalendarDescriptor descriptor) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarCollectionDeleteSuccessResult(
            "success",
            "committed",
            CalendarListItem.FromDescriptor(descriptor),
            [])),
        Content = [new TextContentBlock { Text = "Calendar collection deletion completed." }]
    };

    private static CallToolResult DeleteDeclined()
    {
        CalendarTelemetry.ObserveMutationState(CalendarMutationState.NotAttempted);
        return new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(
                new CalendarCollectionDeleteNonMutationResult("confirmation_declined", "not_attempted", [])),
            Content = [new TextContentBlock { Text = "Calendar collection deletion was declined." }]
        };
    }

    private static CallToolResult Error(CalendarCollectionCreateResult result) => Error(
        Describe(result.Code, result.MutationState),
        MutationState(result.MutationState),
        result.Retryable,
        result.RetryAfterMilliseconds);

    private static CallToolResult Error(CalendarCollectionDeleteResult result) => Error(
        Describe(result.Code, result.MutationState),
        MutationState(result.MutationState),
        result.Retryable,
        result.RetryAfterMilliseconds);

    private static CallToolResult Error(
        (string Code, string Category, string Message, string Phase) description,
        string mutationState,
        bool retryable,
        int? retryAfterMs = null,
        CalendarCollectionLimits? limits = null) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarCollectionErrorResult(
            description.Code,
            description.Category,
            description.Message,
            retryable,
            description.Phase,
            mutationState,
            retryAfterMs,
            limits)),
        Content = [new TextContentBlock { Text = "Calendar collection operation failed." }]
    };

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        string mutationState,
        CalendarCollectionLimits? limits = null) => Error((code, category, message, phase), mutationState, retryable, limits: limits);

    private static (string Code, string Category, string Message, string Phase) Describe(
        CalendarCollectionCreateCode code,
        CalendarMutationState state) => code switch
    {
        CalendarCollectionCreateCode.InvalidInput => ("invalid_input", "input", "The Calendar collection create input is invalid.", "schemaLexicalDiscriminator"),
        CalendarCollectionCreateCode.OutsideScope => ("outside_scope", "selection", "The Calendar collection target is outside the configured Calendar Scope.", "originScopeAuthorization"),
        CalendarCollectionCreateCode.Conflict => ("conflict", "state", "A Calendar with the requested display name already exists.", "selectionDiscoveryCapability"),
        CalendarCollectionCreateCode.DestinationConflict => ("destination_conflict", "state", "The Calendar collection destination already exists.", "execution"),
        CalendarCollectionCreateCode.UnsupportedCapability => ("unsupported_capability", "capabilityAndProjection", "The CalDAV server does not support Calendar collection creation.", "execution"),
        CalendarCollectionCreateCode.PayloadTooLarge => ("payload_too_large", "limitsAndAdmission", "The Calendar collection request exceeds the safe payload limit.", "admissionAndPayload"),
        CalendarCollectionCreateCode.UpstreamUnauthorized => ("upstream_unauthorized", "upstream", "The Calendar collection operation was not authorized.", "execution"),
        CalendarCollectionCreateCode.UpstreamForbidden => ("upstream_forbidden", "upstream", "The Calendar collection operation was forbidden.", "execution"),
        CalendarCollectionCreateCode.UpstreamRateLimited => ("upstream_rate_limited", "upstream", "The Calendar collection operation is rate limited.", "execution"),
        CalendarCollectionCreateCode.UpstreamUnavailable => ("upstream_unavailable", "upstream", "The Calendar collection is temporarily unavailable.", "execution"),
        CalendarCollectionCreateCode.UpstreamProtocolError => ("upstream_protocol_error", "upstream", "The CalDAV server returned an invalid or unsupported collection response.", "execution"),
        CalendarCollectionCreateCode.CommittedButUnverified => ("committed_but_unverified", "postWriteTruth", "The Calendar collection was created but its descriptor could not be verified.", "postWriteVerificationOrReconciliation"),
        _ => ("indeterminate", "postWriteTruth", "The Calendar collection operation outcome is indeterminate.", "postWriteVerificationOrReconciliation")
    };

    private static (string Code, string Category, string Message, string Phase) Describe(
        CalendarCollectionDeleteCode code,
        CalendarMutationState state) => code switch
    {
        CalendarCollectionDeleteCode.InvalidInput => ("invalid_input", "input", "The Calendar collection delete input is invalid.", "schemaLexicalDiscriminator"),
        CalendarCollectionDeleteCode.NotFound => ("not_found", "selection", "The Calendar collection was not found.", "selectionDiscoveryCapability"),
        CalendarCollectionDeleteCode.OutsideScope => ("outside_scope", "selection", "The Calendar collection is outside the configured Calendar Scope.", "originScopeAuthorization"),
        CalendarCollectionDeleteCode.Conflict => ("conflict", "state", "The Calendar collection changed before confirmation.", "targetRevision"),
        CalendarCollectionDeleteCode.ConfirmationMismatch => ("confirmation_mismatch", "confirmation", "The mutation confirmation does not match the reviewed collection.", "mrtr"),
        CalendarCollectionDeleteCode.UnsupportedCapability => ("unsupported_capability", "capabilityAndProjection", "The CalDAV server does not support Calendar collection deletion.", "execution"),
        CalendarCollectionDeleteCode.PayloadTooLarge => ("payload_too_large", "limitsAndAdmission", "The Calendar collection response exceeds the safe payload limit.", "admissionAndPayload"),
        CalendarCollectionDeleteCode.UpstreamUnauthorized => ("upstream_unauthorized", "upstream", "The Calendar collection operation was not authorized.", "execution"),
        CalendarCollectionDeleteCode.UpstreamForbidden => ("upstream_forbidden", "upstream", "The Calendar collection operation was forbidden.", "execution"),
        CalendarCollectionDeleteCode.UpstreamRateLimited => ("upstream_rate_limited", "upstream", "The Calendar collection operation is rate limited.", "execution"),
        CalendarCollectionDeleteCode.UpstreamUnavailable => ("upstream_unavailable", "upstream", "The Calendar collection is temporarily unavailable.", "execution"),
        CalendarCollectionDeleteCode.UpstreamProtocolError => ("upstream_protocol_error", "upstream", "The CalDAV server returned an invalid or unsupported collection response.", "execution"),
        CalendarCollectionDeleteCode.CommittedButUnverified => ("committed_but_unverified", "postWriteTruth", "The Calendar collection delete could not be verified.", "postWriteVerificationOrReconciliation"),
        _ => ("indeterminate", "postWriteTruth", "The Calendar collection deletion outcome is indeterminate.", "postWriteVerificationOrReconciliation")
    };

    private static string MapHttpCode(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => "upstream_unauthorized",
        System.Net.HttpStatusCode.Forbidden => "upstream_forbidden",
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented => "unsupported_capability",
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed => "conflict",
        System.Net.HttpStatusCode.TooManyRequests => "upstream_rate_limited",
        _ => "upstream_unavailable"
    };

    private static string MutationState(CalendarMutationState state)
    {
        CalendarTelemetry.ObserveMutationState(state);
        return CalendarTelemetryVocabulary.MutationStateName(state);
    }

    private static int MeasureArguments(IDictionary<string, JsonElement>? arguments) =>
        CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>());

    private enum ConfirmationDecision
    {
        Declined,
        Confirmed
    }
}

internal static class CalendarCollectionArgumentParser
{
    internal static bool TryParseCreate(
        IDictionary<string, JsonElement>? arguments,
        out CalendarCollectionCreateRequest request)
    {
        request = null!;
        if (arguments is null
            || arguments.Count is < 2 or > 3
            || !arguments.ContainsKey("displayName")
            || !arguments.ContainsKey("entityKinds")
            || arguments.Keys.Any(key => key is not ("displayName" or "entityKinds" or "destinationHref"))
            || !arguments["displayName"].TryGetString(out var displayName)
            || string.IsNullOrWhiteSpace(displayName)
            || arguments["entityKinds"].ValueKind != JsonValueKind.Array)
            return false;

        var kinds = new List<CalendarEntityKind>();
        foreach (var element in arguments["entityKinds"].EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                return false;
            var value = element.GetString();
            if (value == "event")
                kinds.Add(CalendarEntityKind.Event);
            else if (value == "todo")
                kinds.Add(CalendarEntityKind.Todo);
            else
                return false;
        }
        if (kinds.Count is < 1 or > 2 || kinds.Distinct().Count() != kinds.Count)
            return false;

        string? destination = null;
        if (arguments.TryGetValue("destinationHref", out var destinationElement))
        {
            if (!destinationElement.TryGetString(out destination) || string.IsNullOrWhiteSpace(destination))
                return false;
        }
        request = new CalendarCollectionCreateRequest(displayName, kinds, destination);
        return true;
    }

    internal static bool TryParseDelete(
        IDictionary<string, JsonElement>? arguments,
        out CalendarCollectionDeleteRequest request)
    {
        request = null!;
        return arguments is not null
            && arguments.Count == 1
            && arguments.TryGetValue("href", out var href)
            && href.TryGetString(out var value)
            && !string.IsNullOrWhiteSpace(value)
            && (request = new CalendarCollectionDeleteRequest(value)) is not null;
    }

    private static bool TryGetString(this JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }
}

public sealed record CalendarCollectionCreateSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("calendar")] CalendarListItem Calendar,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarCollectionDeleteSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("calendar")] CalendarListItem Calendar,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarCollectionDeleteNonMutationResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarCollectionErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("mutationState")] string MutationState,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterMs = null,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarCollectionLimits? Limits = null);

public sealed record CalendarCollectionLimits(
    [property: JsonPropertyName("calendarCount")] int CalendarCount);
