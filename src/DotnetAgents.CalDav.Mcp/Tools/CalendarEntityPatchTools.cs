using System.ComponentModel;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Applies revision-bound lossless Event and To-do patches.</summary>
[McpServerToolType]
public sealed class CalendarEntityPatchTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private const string EventOperation = "events.patch";
    private const string TodoOperation = "todos.patch";
    private const string ConfirmationKey = "confirm_replace_all";
    private const string ConfirmationTitle = "Confirm replace all";
    private const string ConfirmationDescription = "Replace every addressed collection occurrence.";
    private static readonly TimeSpan ConfirmationDeadline = TimeSpan.FromSeconds(30);
    private readonly ICalendarService _calendarService;
    private readonly CalendarMutationRequestStateProtector? _stateProtector;
    private readonly CalendarMutationAdmission _admission;
    private readonly TimeProvider _timeProvider;

    public CalendarEntityPatchTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<CalendarMutationRequestStateProtector>(),
            services.GetRequiredService<CalendarMutationAdmission>())
    {
    }

    internal CalendarEntityPatchTools(ICalendarService calendarService, TimeProvider timeProvider)
        : this(calendarService, timeProvider, null, new CalendarMutationAdmission(timeProvider))
    {
    }

    internal CalendarEntityPatchTools(
        ICalendarService calendarService,
        TimeProvider timeProvider,
        CalendarMutationRequestStateProtector? stateProtector,
        CalendarMutationAdmission admission)
    {
        _calendarService = calendarService;
        _stateProtector = stateProtector;
        _admission = admission;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "events.patch",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Apply a revision-bound semantic patch to one Event resource at an explicitly supplied absolute snapshot href.")]
    public Task<CallToolResult> PatchEventAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) =>
        PatchEventRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    [McpServerTool(
        Name = "todos.patch",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Apply a revision-bound semantic patch to one To-do resource at an explicitly supplied absolute snapshot href; completion is reserved for todos.complete.")]
    public Task<CallToolResult> PatchTodoAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) =>
        PatchTodoRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    internal async Task<CallToolResult> PatchEventRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await PatchEventRawAsync(
        arguments, null, null, false, cancellationToken);

    internal async Task<CallToolResult> PatchEventRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>())
            > MaximumArgumentBytes)
            return CreateInputGuardError(payloadTooLarge: true);
        using var lease = await _admission.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return BusyError();
        if (!CalendarEntityPatchArgumentParser.TryParseEvent(arguments, out var request))
            return Error();
        if (RequiresConfirmation(request.Target, request.Patch))
            return await ConfirmEventAsync(request, arguments!, requestState, inputResponses, mrtrSupported, cancellationToken);
        return await ExecuteMutationAsync(
            token => _calendarService.PatchEventAsync(request, token), cancellationToken);
    }

    internal async Task<CallToolResult> PatchTodoRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await PatchTodoRawAsync(
        arguments, null, null, false, cancellationToken);

    internal async Task<CallToolResult> PatchTodoRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>())
            > MaximumArgumentBytes)
            return CreateInputGuardError(payloadTooLarge: true);
        using var lease = await _admission.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return BusyError();
        if (!CalendarEntityPatchArgumentParser.TryParseTodo(arguments, out var request))
            return Error();
        if (RequiresConfirmation(request.Target, request.Patch))
            return await ConfirmTodoAsync(request, arguments!, requestState, inputResponses, mrtrSupported, cancellationToken);
        return await ExecuteMutationAsync(
            token => _calendarService.PatchTodoAsync(request, token), cancellationToken);
    }

    private Task<CallToolResult> ConfirmEventAsync(
        CalendarEventPatchRequest request,
        IDictionary<string, JsonElement> arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken) => ConfirmAsync(
            EventOperation,
            request.Snapshot,
            request.Target,
            arguments,
        requestState,
        inputResponses,
        mrtrSupported,
        ReplaceAllFields(request.Patch.Categories, request.Patch.Collections),
        request.Patch.RecurrenceSet is not null,
        token => _calendarService.ReviewEventPatchAsync(request, token),
        token => _calendarService.PatchEventAsync(request, token),
        cancellationToken);

    private Task<CallToolResult> ConfirmTodoAsync(
        CalendarTodoPatchRequest request,
        IDictionary<string, JsonElement> arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken) => ConfirmAsync(
            TodoOperation,
            request.Snapshot,
            request.Target,
            arguments,
        requestState,
        inputResponses,
        mrtrSupported,
        ReplaceAllFields(request.Patch.Categories, request.Patch.Collections),
        request.Patch.RecurrenceSet is not null,
        token => _calendarService.ReviewTodoPatchAsync(request, token),
        token => _calendarService.PatchTodoAsync(request, token),
        cancellationToken);

    private async Task<CallToolResult> ConfirmAsync(
        string operation,
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        IDictionary<string, JsonElement> arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        IReadOnlyList<ReplaceAllField> fields,
        bool changesRecurrenceDefinition,
        Func<CancellationToken, Task<CalendarEntityPatchReviewResult>> review,
        Func<CancellationToken, Task<CalendarEntityPatchResult>> execute,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ConfirmationDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ConfirmWithinDeadlineAsync(
                operation,
                revision,
                target,
                arguments,
                requestState,
                inputResponses,
                mrtrSupported,
                fields,
                changesRecurrenceDefinition,
                review,
                execute,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return ConfirmationDeadlineError();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PreviewUnavailableError();
        }
        catch (Exception exception) when (exception is not (InputRequiredException or OperationCanceledException))
        {
            return PreviewProtocolError();
        }
    }

    private async Task<CallToolResult> ConfirmWithinDeadlineAsync(
        string operation,
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        IDictionary<string, JsonElement> arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        IReadOnlyList<ReplaceAllField> fields,
        bool changesRecurrenceDefinition,
        Func<CancellationToken, Task<CalendarEntityPatchReviewResult>> review,
        Func<CancellationToken, Task<CalendarEntityPatchResult>> execute,
        CancellationToken cancellationToken)
    {
        var confirmationMessage = CreateConfirmationMessage(
            operation,
            revision,
            target,
            fields,
            changesRecurrenceDefinition);
        if (!IsConfirmationPreviewWithinBudget(confirmationMessage))
            return ConfirmationPreviewPayloadError();
        var binding = BindArguments(arguments);
        if (requestState is not null || inputResponses is not null)
        {
            if (_stateProtector is null)
                return UnsupportedMrtrError();
            return await ContinueAfterConfirmationAsync(
                operation,
                revision,
                binding,
                requestState,
                inputResponses,
                mrtrSupported,
                review,
                execute,
                cancellationToken).ConfigureAwait(false);
        }

        var initialReview = await review(cancellationToken).ConfigureAwait(false);
        if (initialReview.Outcome is not null)
            return ToToolResult(initialReview.Outcome);
        if (!HasValidIntentDigest(initialReview.IntentDigest))
            return PreviewProtocolError();
        if (!mrtrSupported || _stateProtector is null)
            return UnsupportedMrtrError();
        var state = _stateProtector.Protect(operation, revision, binding, initialReview.IntentDigest.Span);
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

    private async Task<CallToolResult> ContinueAfterConfirmationAsync(
        string operation,
        CalendarResourceRevisionReference revision,
        byte[] binding,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        Func<CancellationToken, Task<CalendarEntityPatchReviewResult>> review,
        Func<CancellationToken, Task<CalendarEntityPatchResult>> execute,
        CancellationToken cancellationToken)
    {
        var confirmation = ReadConfirmation(operation, revision, binding, requestState, inputResponses);
        if (confirmation.Decision == ConfirmationDecision.Declined)
            return ConfirmationDeclined();
        if (confirmation.Decision != ConfirmationDecision.Confirmed)
            return ConfirmationError(confirmation.Decision == ConfirmationDecision.Expired);
        var continuationReview = await review(cancellationToken).ConfigureAwait(false);
        if (continuationReview.Outcome is not null)
            return ToToolResult(continuationReview.Outcome);
        if (!HasValidIntentDigest(continuationReview.IntentDigest)
            || !_stateProtector!.MatchesIntent(confirmation.IntentBinding!, continuationReview.IntentDigest.Span))
            return ConfirmationError(expired: false);
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        return await ExecuteMutationAsync(execute, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CallToolResult> ExecuteMutationAsync(
        Func<CancellationToken, Task<CalendarEntityPatchResult>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToToolResult(await execute(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ToToolResult(new CalendarEntityPatchResult(
                CalendarEntityPatchCode.Indeterminate,
                CalendarMutationState.Unknown,
                Phase: CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation));
        }
    }

    private ConfirmationRead ReadConfirmation(
        string operation,
        CalendarResourceRevisionReference revision,
        byte[] binding,
        string? requestState,
        IDictionary<string, InputResponse>? responses)
    {
        if (!TryGetConfirmation(requestState, responses, out var response))
            return new(ConfirmationDecision.Mismatch);
        if (!_stateProtector!.TryUnprotect(
                requestState!, operation, revision, binding, out var intentBinding, out var expired))
            return new(expired ? ConfirmationDecision.Expired : ConfirmationDecision.Mismatch);
        var elicitation = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        if (elicitation?.Action is "decline" or "cancel")
            return new(ConfirmationDecision.Declined);
        if (!TryReadConfirmationValue(elicitation, out var confirmed))
            return new(ConfirmationDecision.Mismatch);
        return new(confirmed ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined, intentBinding);
    }

    private static bool TryGetConfirmation(
        string? requestState,
        IDictionary<string, InputResponse>? responses,
        out InputResponse response)
    {
        response = null!;
        if (string.IsNullOrEmpty(requestState)
            || responses is null
            || responses.Count != 1
            || !responses.TryGetValue(ConfirmationKey, out var candidate)
            || candidate is null)
            return false;
        response = candidate;
        return true;
    }

    private static bool TryReadConfirmationValue(ElicitResult? elicitation, out bool confirmed)
    {
        confirmed = false;
        if (elicitation is null
            || !string.Equals(elicitation.Action, "accept", StringComparison.Ordinal)
            || elicitation.Content is null
            || elicitation.Content.Count != 1
            || !elicitation.Content.TryGetValue("confirm", out var value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;
        confirmed = value.GetBoolean();
        return true;
    }

    private static byte[] BindArguments(IDictionary<string, JsonElement> arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteCanonicalJson(writer, pair.Value);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalJson(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
                WriteCanonicalJson(writer, item);
            writer.WriteEndArray();
            return;
        }
        value.WriteTo(writer);
    }

    private static bool HasValidIntentDigest(ReadOnlyMemory<byte> digest) => digest.Length == 32;

    private static IReadOnlyList<ReplaceAllField> ReplaceAllFields(
        CalendarCollectionPatch<string>? categories,
        IReadOnlyList<ICalendarCollectionPatch>? collections)
    {
        var fields = new List<ReplaceAllField>();
        if (categories?.Operation == CalendarCollectionPatchOperation.ReplaceAll)
            fields.Add(new("categories", categories.Values!.Count));
        fields.AddRange((collections ?? [])
            .Where(item => item.Operation == CalendarCollectionPatchOperation.ReplaceAll)
            .Select(item => new ReplaceAllField(FieldName(item.Field), item.ReplacementValues!.Count)));
        return fields.OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
    }

    private static string FieldName(CalendarCollectionField field)
    {
        var value = field.ToString();
        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string CreateConfirmationMessage(
        string operation,
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        IReadOnlyList<ReplaceAllField> fields,
        bool changesRecurrenceDefinition)
    {
        var kind = revision.EntityKind == CalendarEntityKind.Event ? "event" : "todo";
        var replacements = string.Join(", ", fields.Select(field => $"{field.Name}={field.Count}"));
        var identity = target.RecurrenceIdentity is null
            ? string.Empty
            : $", original Recurrence Identity {target.RecurrenceIdentity.Value}"
                + (target.RecurrenceIdentity.TimeZoneId is null
                    ? string.Empty
                    : $" ({target.RecurrenceIdentity.TimeZoneId})");
        if (target.Scope is "this-and-future" or "entire-set" || changesRecurrenceDefinition)
        {
            var impact = changesRecurrenceDefinition
                ? "recurrence definition and explicitly reconciled orphans"
                : $"{target.Scope} recurrence scope";
            return $"Confirm {operation} for href {revision.Href}, UID {revision.EntityUid}, kind {kind}, scope {target.Scope}{identity}, expected ETag {revision.EntityTag}. High-impact change: {impact}.";
        }
        return $"Confirm {operation} replaceAll for href {revision.Href}, UID {revision.EntityUid}, kind {kind}, scope {target.Scope}{identity}, expected ETag {revision.EntityTag}. Destructive fields and replacement counts: {replacements}.";
    }

    internal static bool IsConfirmationPreviewWithinBudget(string message) =>
        GetConfirmationPreviewByteCount(message) <= CalendarQueryToolSupport.MaximumHumanReadableBytes;

    internal static int GetConfirmationPreviewByteCount(string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new ConfirmationPreviewBudget(
            message,
            ConfirmationTitle,
            ConfirmationDescription)).Length;

    private static bool RequiresConfirmation(CalendarMutationTarget target, CalendarEventPatch patch) =>
        target.Scope is "this-and-future" or "entire-set"
        || patch.RecurrenceSet is not null
        || patch.Categories?.Operation == CalendarCollectionPatchOperation.ReplaceAll
        || patch.Collections?.Any(item => item.Operation == CalendarCollectionPatchOperation.ReplaceAll) == true;

    private static bool RequiresConfirmation(CalendarMutationTarget target, CalendarTodoPatch patch) =>
        target.Scope is "this-and-future" or "entire-set"
        || patch.RecurrenceSet is not null
        || patch.Categories?.Operation == CalendarCollectionPatchOperation.ReplaceAll
        || patch.Collections?.Any(item => item.Operation == CalendarCollectionPatchOperation.ReplaceAll) == true;

    private static CallToolResult Success(CalendarResourceSnapshot snapshot) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateSuccessResult(
            "success",
            "committed",
            CalendarSnapshotResult.FromSnapshot(snapshot),
            snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = "Calendar Entity patch completed." }]
    };

    private static CallToolResult ToToolResult(CalendarEntityPatchResult result)
    {
        var mapped = result.Code switch
        {
            CalendarEntityPatchCode.Success when result.Snapshot is not null => Success(result.Snapshot),
            CalendarEntityPatchCode.NoChange => NoChange(result.Snapshot?.Diagnostics ?? []),
            _ => Error(result)
        };
        return CalendarQueryToolSupport.EnsureBoundedResult(mapped, (_, _) => NamedError(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar Entity patch result exceeds the safe payload limit.",
            "admissionAndPayload",
            mutationState: MutationState(result.MutationState)));
    }

    private static CallToolResult NoChange(IReadOnlyList<CalendarResourceDiagnostic> diagnostics) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityPatchNoChangeResult(
            "no_change",
            "not_attempted",
            diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = "Calendar Entity patch made no change." }]
    };

    private static CallToolResult ConfirmationDeclined() => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityPatchNoChangeResult(
            "confirmation_declined",
            "not_attempted",
            [])),
        Content = [new TextContentBlock { Text = "Calendar Entity patch confirmation was declined." }]
    };

    private static CallToolResult Error(CalendarEntityPatchResult result) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
            Code(result.Code),
            Category(result.Code),
            Message(result.Code),
            result.Retryable,
            Phase(result.Phase),
            MutationState(result.MutationState),
            CurrentSnapshot: result.Snapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.Snapshot),
            RetryAfterMs: result.RetryAfterMilliseconds,
            Limits: result.LimitDimension is null
                ? null
                : new CalendarEntityCreateLimits(Dimension: LimitDimension(result.LimitDimension.Value)))),
        Content = [new TextContentBlock { Text = "Calendar Entity patch failed." }]
    };

    private static string Code(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.RemovalNotFound => "not_found",
        CalendarEntityPatchCode.RemovalAmbiguous => "ambiguous",
        _ => string.Concat(code.ToString().SelectMany((character, index) =>
            char.IsUpper(character) && index > 0 ? new[] { '_', char.ToLowerInvariant(character) } : [char.ToLowerInvariant(character)]))
    };

    private static string Message(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.RemovalNotFound => "No requested collection occurrence matched.",
        CalendarEntityPatchCode.RemovalAmbiguous => "The requested collection removal was ambiguous.",
        CalendarEntityPatchCode.LimitExhausted => "The Calendar Entity patch exceeded its execution time limit.",
        _ => "The Calendar Entity patch could not be completed."
    };

    private static string Category(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.InvalidInput or CalendarEntityPatchCode.InvalidCalendarData => "input",
        CalendarEntityPatchCode.NotFound or CalendarEntityPatchCode.RemovalNotFound
            or CalendarEntityPatchCode.RemovalAmbiguous or CalendarEntityPatchCode.OutsideScope
            or CalendarEntityPatchCode.EntityKindMismatch => "selection",
        CalendarEntityPatchCode.OpaqueResource or CalendarEntityPatchCode.TemporalUnresolved
            or CalendarEntityPatchCode.RecurrenceUnevaluable
            or CalendarEntityPatchCode.UnsupportedCapability => "capabilityAndProjection",
        CalendarEntityPatchCode.Conflict or CalendarEntityPatchCode.ConcurrencyUnavailable
            or CalendarEntityPatchCode.CompletionStateConflict => "state",
        CalendarEntityPatchCode.PayloadTooLarge or CalendarEntityPatchCode.LimitExhausted => "limitsAndAdmission",
        CalendarEntityPatchCode.FidelityFailure or CalendarEntityPatchCode.CommittedButUnverified
            or CalendarEntityPatchCode.CommittedButConcurrencyUnavailable or CalendarEntityPatchCode.Indeterminate => "postWriteTruth",
        _ => "upstream"
    };

    private static string Phase(CalendarEntityPatchPhase phase) => phase switch
    {
        CalendarEntityPatchPhase.SchemaLexicalDiscriminator => "schemaLexicalDiscriminator",
        CalendarEntityPatchPhase.SelectionDiscoveryCapability => "selectionDiscoveryCapability",
        CalendarEntityPatchPhase.OriginScopeAuthorization => "originScopeAuthorization",
        CalendarEntityPatchPhase.TargetRevision => "targetRevision",
        CalendarEntityPatchPhase.CompleteResourceSemantics => "completeResourceSemantics",
        CalendarEntityPatchPhase.AdmissionAndPayload => "admissionAndPayload",
        CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation => "postWriteVerificationOrReconciliation",
        _ => "execution"
    };

    private static string MutationState(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };

    private static string LimitDimension(CalendarEntityPatchLimitDimension dimension) => dimension switch
    {
        CalendarEntityPatchLimitDimension.ElapsedTime => "elapsed_time",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null)
    };

    private static CallToolResult Error() => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
            "invalid_input",
            "input",
            "The Calendar Entity patch input is invalid.",
            false,
            "schemaLexicalDiscriminator",
            "not_attempted")),
        Content = [new TextContentBlock { Text = "Calendar Entity patch failed." }]
    };

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => payloadTooLarge
        ? NamedError(
            "payload_too_large",
            "limitsAndAdmission",
            "The Calendar Entity patch arguments exceed the safe payload limit.",
            "admissionAndPayload")
        : Error();

    private static CallToolResult BusyError() => NamedError(
        "busy",
        "limitsAndAdmission",
        "Calendar mutation admission is busy.",
        "admissionAndPayload",
        retryable: true,
        retryAfterMs: CalendarMutationAdmission.RetryAfterMilliseconds);

    private static CallToolResult UnsupportedMrtrError() => NamedError(
        "unsupported_capability",
        "capabilityAndProjection",
        "The client does not support required mutation confirmation.",
        "mrtr");

    private static CallToolResult ConfirmationError(bool expired) => NamedError(
        expired ? "confirmation_expired" : "confirmation_mismatch",
        "confirmation",
        expired ? "The mutation confirmation expired." : "The mutation confirmation did not match the reviewed request.",
        "mrtr");

    private static CallToolResult ConfirmationPreviewPayloadError() => NamedError(
        "payload_too_large",
        "limitsAndAdmission",
        "The replaceAll confirmation preview exceeds the safe human-readable limit.",
        "admissionAndPayload");

    private static CallToolResult ConfirmationDeadlineError() => NamedError(
        "limit_exhausted",
        "limitsAndAdmission",
        "The replaceAll confirmation review exceeded its deadline.",
        "execution",
        limits: new CalendarEntityCreateLimits(Dimension: "elapsed_time"));

    private static CallToolResult PreviewUnavailableError() => NamedError(
        "upstream_unavailable",
        "upstream",
        "The Calendar Entity patch confirmation review is temporarily unavailable.",
        "targetRevision",
        retryable: true);

    private static CallToolResult PreviewProtocolError() => NamedError(
        "upstream_protocol_error",
        "upstream",
        "The Calendar Entity patch confirmation review failed.",
        "targetRevision");

    private static CallToolResult NamedError(
        string code,
        string category,
        string message,
        string phase,
        string mutationState = "not_attempted",
        bool retryable = false,
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
            RetryAfterMs: retryAfterMs,
            Limits: limits)),
        Content = [new TextContentBlock { Text = "Calendar Entity patch failed." }]
    };

    private enum ConfirmationDecision
    {
        Confirmed,
        Declined,
        Expired,
        Mismatch
    }

    private sealed record ReplaceAllField(string Name, int Count);

    private sealed record ConfirmationRead(ConfirmationDecision Decision, string? IntentBinding = null);

    private sealed record ConfirmationPreviewBudget(string Message, string Title, string Description);
}

public sealed record CalendarEntityPatchNoChangeResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("outcome")] string Outcome,
    [property: System.Text.Json.Serialization.JsonPropertyName("mutationState")] string MutationState,
    [property: System.Text.Json.Serialization.JsonPropertyName("diagnostics")]
        IReadOnlyList<CalendarDiagnosticResult> Diagnostics);
