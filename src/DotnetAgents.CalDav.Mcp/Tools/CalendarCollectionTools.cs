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
            return InputError(true, "The Calendar collection create arguments exceed the safe payload limit.");
        return !CalendarCollectionArgumentParser.TryParseCreate(arguments, out var request)
            ? InputError(false, "The Calendar collection create input is invalid.")
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
            return InputError(true, "The Calendar collection delete arguments exceed the safe payload limit.");
        if (!CalendarCollectionArgumentParser.TryParseDelete(arguments, out var request))
            return InputError(false, "The Calendar collection delete input is invalid.");
        return await ExecuteDeleteAsync(request, requestState, inputResponses, mrtrSupported, cancellationToken).ConfigureAwait(false);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(
        payloadTooLarge,
        payloadTooLarge
            ? "The Calendar collection arguments exceed the safe payload limit."
            : "The Calendar collection input is invalid.");

    private async Task<CallToolResult> ExecuteCreateAsync(
        CalendarCollectionCreateRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(BeforeDispatchDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _module.CreateAsync(request, linked.Token).ConfigureAwait(false);
            return result.Code == CalendarCollectionCreateCode.Success && result.Calendar is not null
                ? CalendarToolResult.Success(CreateSuccess(result.Calendar), result.MutationState).FinalizeResult()
                : Error(result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(new(CalendarTelemetryErrorCode.LimitExhausted,
                CalendarTelemetryErrorCategory.LimitsAndAdmission, CalendarTelemetryErrorPhase.Execution, false),
                "The Calendar mutation exhausted its elapsed_time execution budget.", CalendarMutationState.Unknown);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(DiscoveryUnavailable(), "Calendar discovery is temporarily unavailable.",
                CalendarMutationState.NotAttempted);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error(new(CalendarTelemetryErrorCode.LimitExhausted,
                    CalendarTelemetryErrorCategory.LimitsAndAdmission,
                    CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false),
                "The Calendar mutation exceeded its Calendar discovery limit.", CalendarMutationState.NotAttempted,
                limits: new CalendarCollectionLimits(exception.CalendarCount));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error(DiscoveryUnsupported(),
                "The server does not support the required Calendar discovery capability.",
                CalendarMutationState.NotAttempted);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return Error(DiscoveryProtocolError(), "Calendar discovery returned an invalid response.",
                CalendarMutationState.NotAttempted);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Error(DiscoveryUnavailable(), "Calendar discovery is temporarily unavailable.",
                CalendarMutationState.NotAttempted);
        }
        catch (HttpRequestException exception)
        {
            return Error(MapHttpFacts(exception.StatusCode),
                "The Calendar collection operation was rejected by the server.",
                CalendarMutationState.NotAttempted);
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
            return Error(MrtrUnsupported(), "Calendar collection deletion requires MRTR confirmation support.",
                CalendarMutationState.NotAttempted);

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
                    return Error(MrtrUnsupported(),
                        "Calendar collection deletion requires MRTR confirmation support.",
                        CalendarMutationState.NotAttempted);
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
                return Error(new(
                        expired
                            ? CalendarTelemetryErrorCode.ConfirmationExpired
                            : CalendarTelemetryErrorCode.ConfirmationMismatch,
                        CalendarTelemetryErrorCategory.Confirmation,
                        CalendarTelemetryErrorPhase.Mrtr,
                        false),
                    expired
                        ? "The mutation confirmation has expired."
                        : "The mutation confirmation does not match the reviewed request.",
                    CalendarMutationState.NotAttempted);
            if (decision == ConfirmationDecision.Declined)
                return DeleteDeclined();

            var result = await _module.ExecuteConfirmedDeleteAsync(request, review.Binding!, linked.Token).ConfigureAwait(false);
            return result.Code == CalendarCollectionDeleteCode.Success && result.Calendar is not null
                ? CalendarToolResult.Success(DeleteSuccess(result.Calendar!), result.MutationState).FinalizeResult()
                : Error(result);
        }
        catch (InputRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(new(CalendarTelemetryErrorCode.LimitExhausted,
                CalendarTelemetryErrorCategory.LimitsAndAdmission, CalendarTelemetryErrorPhase.Execution, false),
                "The Calendar mutation exhausted its elapsed_time execution budget.", CalendarMutationState.Unknown);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(DiscoveryUnavailable(), "The Calendar collection is temporarily unavailable.",
                CalendarMutationState.NotAttempted);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error(new(CalendarTelemetryErrorCode.LimitExhausted,
                    CalendarTelemetryErrorCategory.LimitsAndAdmission,
                    CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false),
                "The Calendar mutation exceeded its Calendar discovery limit.", CalendarMutationState.NotAttempted,
                limits: new CalendarCollectionLimits(exception.CalendarCount));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error(DiscoveryUnsupported(),
                "The server does not support the required Calendar discovery capability.",
                CalendarMutationState.NotAttempted);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return Error(DiscoveryProtocolError(), "Calendar discovery returned an invalid response.",
                CalendarMutationState.NotAttempted);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Error(DiscoveryUnavailable(), "Calendar discovery is temporarily unavailable.",
                CalendarMutationState.NotAttempted);
        }
        catch (HttpRequestException exception)
        {
            return Error(MapHttpFacts(exception.StatusCode),
                "The Calendar collection operation was rejected by the server.",
                CalendarMutationState.NotAttempted);
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

    private static CallToolResult DeleteDeclined() => CalendarToolResult.Success(
        new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(
                new CalendarCollectionDeleteNonMutationResult("confirmation_declined", "not_attempted", [])),
            Content = [new TextContentBlock { Text = "Calendar collection deletion was declined." }]
        }, CalendarMutationState.NotAttempted).FinalizeResult();

    private static CallToolResult Error(CalendarCollectionCreateResult result) => Error(
        CalendarTelemetryFacts.From(result),
        Message(result.Code),
        result.MutationState,
        result.RetryAfterMilliseconds);

    private static CallToolResult Error(CalendarCollectionDeleteResult result) => Error(
        CalendarTelemetryFacts.From(result),
        Message(result.Code),
        result.MutationState,
        result.RetryAfterMilliseconds);

    private static CallToolResult Error(
        CalendarStructuredErrorFacts facts,
        string message,
        CalendarMutationState mutationState,
        int? retryAfterMs = null,
        CalendarCollectionLimits? limits = null) => CalendarToolResult.Error(
            new CallToolResult
            {
                IsError = true,
                StructuredContent = JsonSerializer.SerializeToElement(new CalendarCollectionErrorResult(
                    facts.CodeName,
                    facts.CategoryName,
                    message,
                    facts.Retryable,
                    facts.PhaseName,
                    CalendarTelemetryVocabulary.MutationStateName(mutationState),
                    retryAfterMs,
                    limits)),
                Content = [new TextContentBlock { Text = "Calendar collection operation failed." }]
            },
            facts,
            mutationState).FinalizeResult();

    private static CallToolResult InputError(bool payloadTooLarge, string message) => Error(
        CalendarTelemetryFacts.FromInputGuard(payloadTooLarge),
        message,
        CalendarMutationState.NotAttempted);

    private static string Message(CalendarCollectionCreateCode code) => code switch
    {
        CalendarCollectionCreateCode.InvalidInput => "The Calendar collection create input is invalid.",
        CalendarCollectionCreateCode.OutsideScope => "The Calendar collection target is outside the configured Calendar Scope.",
        CalendarCollectionCreateCode.Conflict => "A Calendar with the requested display name already exists.",
        CalendarCollectionCreateCode.DestinationConflict => "The Calendar collection destination already exists.",
        CalendarCollectionCreateCode.UnsupportedCapability => "The CalDAV server does not support Calendar collection creation.",
        CalendarCollectionCreateCode.PayloadTooLarge => "The Calendar collection request exceeds the safe payload limit.",
        CalendarCollectionCreateCode.UpstreamUnauthorized => "The Calendar collection operation was not authorized.",
        CalendarCollectionCreateCode.UpstreamForbidden => "The Calendar collection operation was forbidden.",
        CalendarCollectionCreateCode.UpstreamRateLimited => "The Calendar collection operation is rate limited.",
        CalendarCollectionCreateCode.UpstreamUnavailable => "The Calendar collection is temporarily unavailable.",
        CalendarCollectionCreateCode.UpstreamProtocolError => "The CalDAV server returned an invalid or unsupported collection response.",
        CalendarCollectionCreateCode.CommittedButUnverified => "The Calendar collection was created but its descriptor could not be verified.",
        _ => "The Calendar collection operation outcome is indeterminate."
    };

    private static string Message(CalendarCollectionDeleteCode code) => code switch
    {
        CalendarCollectionDeleteCode.InvalidInput => "The Calendar collection delete input is invalid.",
        CalendarCollectionDeleteCode.NotFound => "The Calendar collection was not found.",
        CalendarCollectionDeleteCode.OutsideScope => "The Calendar collection is outside the configured Calendar Scope.",
        CalendarCollectionDeleteCode.Conflict => "The Calendar collection changed before confirmation.",
        CalendarCollectionDeleteCode.ConfirmationMismatch => "The mutation confirmation does not match the reviewed collection.",
        CalendarCollectionDeleteCode.UnsupportedCapability => "The CalDAV server does not support Calendar collection deletion.",
        CalendarCollectionDeleteCode.PayloadTooLarge => "The Calendar collection response exceeds the safe payload limit.",
        CalendarCollectionDeleteCode.UpstreamUnauthorized => "The Calendar collection operation was not authorized.",
        CalendarCollectionDeleteCode.UpstreamForbidden => "The Calendar collection operation was forbidden.",
        CalendarCollectionDeleteCode.UpstreamRateLimited => "The Calendar collection operation is rate limited.",
        CalendarCollectionDeleteCode.UpstreamUnavailable => "The Calendar collection is temporarily unavailable.",
        CalendarCollectionDeleteCode.UpstreamProtocolError => "The CalDAV server returned an invalid or unsupported collection response.",
        CalendarCollectionDeleteCode.CommittedButUnverified => "The Calendar collection delete could not be verified.",
        _ => "The Calendar collection deletion outcome is indeterminate."
    };

    private static CalendarStructuredErrorFacts MapHttpFacts(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => new(CalendarTelemetryErrorCode.UpstreamUnauthorized,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.Execution, false),
        System.Net.HttpStatusCode.Forbidden => new(CalendarTelemetryErrorCode.UpstreamForbidden,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.Execution, false),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented => new(
            CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarTelemetryErrorPhase.Execution, false),
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed => new(
            CalendarTelemetryErrorCode.Conflict, CalendarTelemetryErrorCategory.State,
            CalendarTelemetryErrorPhase.Execution, false),
        System.Net.HttpStatusCode.TooManyRequests => new(CalendarTelemetryErrorCode.UpstreamRateLimited,
            CalendarTelemetryErrorCategory.Upstream, CalendarTelemetryErrorPhase.Execution, false),
        _ => new(CalendarTelemetryErrorCode.UpstreamUnavailable, CalendarTelemetryErrorCategory.Upstream,
            CalendarTelemetryErrorPhase.Execution, false)
    };

    private static CalendarStructuredErrorFacts DiscoveryUnavailable() => new(
        CalendarTelemetryErrorCode.UpstreamUnavailable, CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, true);

    private static CalendarStructuredErrorFacts DiscoveryUnsupported() => new(
        CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false);

    private static CalendarStructuredErrorFacts DiscoveryProtocolError() => new(
        CalendarTelemetryErrorCode.UpstreamProtocolError, CalendarTelemetryErrorCategory.Upstream,
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability, false);

    private static CalendarStructuredErrorFacts MrtrUnsupported() => new(
        CalendarTelemetryErrorCode.UnsupportedCapability, CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarTelemetryErrorPhase.Mrtr, false);

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
