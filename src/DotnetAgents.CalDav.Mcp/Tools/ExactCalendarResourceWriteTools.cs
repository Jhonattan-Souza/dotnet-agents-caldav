using System.ComponentModel;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Opt-in complete-resource writes protected by MCP Multi Round-Trip Requests.</summary>
[McpServerToolType]
public sealed class ExactCalendarResourceWriteTools
{
    internal const int MaximumDecodedResourceBytes = 4 * 1024 * 1024;
    private const int MaximumJsonBytesPerDecodedResourceByte = 6;
    internal const int MaximumMetadataArgumentBytes = 64 * 1024;
    internal const int MaximumArgumentBytes =
        (MaximumDecodedResourceBytes * MaximumJsonBytesPerDecodedResourceByte) + MaximumMetadataArgumentBytes;
    internal const int MaximumStructuredResultBytes = CalendarQueryToolSupport.MaximumStructuredResultBytes;
    private const string CreateOperation = "calendar_resources.exact_create";
    private const string ReplaceOperation = "calendar_resources.exact_replace";
    private const string MoveOperation = "calendar_resources.exact_move";
    private const string ConfirmationKey = "confirm_exact_write";
    private const string ConfirmationTitle = "Confirm exact write";
    private const string ConfirmationDescription =
        "Apply exactly the reviewed complete Calendar Object Resource write.";
    private static readonly TimeSpan ConfirmationDeadline = TimeSpan.FromSeconds(30);
    private readonly ICalendarService _calendarService;
    private readonly CalendarMutationRequestStateProtector _stateProtector;
    private readonly TimeProvider _timeProvider;

    public ExactCalendarResourceWriteTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<CalendarMutationRequestStateProtector>(),
            services.GetRequiredService<TimeProvider>())
    {
    }

    internal ExactCalendarResourceWriteTools(
        ICalendarService calendarService,
        CalendarMutationRequestStateProtector stateProtector,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _stateProtector = stateProtector;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = CreateOperation,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Create a complete caller-authored Calendar Object Resource at an explicitly provided absolute destination resource href.")]
    public Task<CallToolResult> CreateAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) => CreateRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    [McpServerTool(
        Name = "calendar_resources.exact_replace",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Confirm and replace one revision-bound resource at its explicitly provided absolute href with a complete caller-authored Calendar Object Resource.")]
    public Task<CallToolResult> ReplaceAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) => ReplaceRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    [McpServerTool(
        Name = "calendar_resources.exact_move",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Review, confirm, and atomically move one strong-revision-bound complete resource to an explicitly provided absolute destination href with constant work and authoritative-byte verification.")]
    public Task<CallToolResult> MoveAsync(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server,
        CancellationToken cancellationToken) => MoveRawAsync(
            requestContext.Params?.Arguments,
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            cancellationToken);

    internal async Task<CallToolResult> CreateRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        if (HasOversizedResourceArgument(arguments)
            || MeasureMetadataArguments(arguments) > MaximumMetadataArgumentBytes)
        {
            return InputError(payloadTooLarge: true);
        }
        if (!ExactCalendarResourceArgumentParser.TryParseCreate(arguments, out var request))
            return InputError(payloadTooLarge: false);
        if (request.AuthoritativeUtf8.Length > MaximumDecodedResourceBytes)
            return InputError(payloadTooLarge: true);
        return await ConfirmCreateAsync(
            request,
            requestState,
            inputResponses,
            mrtrSupported,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ConfirmCreateAsync(
        CalendarExactCreateRequest request,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ConfirmationDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ConfirmCreateWithinDeadlineAsync(
                request,
                requestState,
                inputResponses,
                mrtrSupported,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The exact write review exceeded its time limit.", false,
                "execution", "not_attempted", limits: new CalendarEntityCreateLimits(Dimension: "elapsed_time"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The exact write review is unavailable.", true,
                "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is not (InputRequiredException or OperationCanceledException))
        {
            return Error("upstream_protocol_error", "upstream", "The exact write could not be completed.", false,
                "execution", "not_attempted");
        }
    }

    private async Task<CallToolResult> ConfirmCreateWithinDeadlineAsync(
        CalendarExactCreateRequest request,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (requestState is null && inputResponses is null)
            return await BeginCreateConfirmationAsync(request, mrtrSupported, cancellationToken).ConfigureAwait(false);
        var requestBinding = BindCreate(request);
        var confirmation = ReadCreateConfirmation(requestBinding, requestState, inputResponses);
        if (confirmation.Decision == ConfirmationDecision.Declined)
            return ConfirmationDeclined();
        if (confirmation.Decision != ConfirmationDecision.Confirmed)
            return ConfirmationError(confirmation.Decision == ConfirmationDecision.Expired);
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        var continuationReview = await _calendarService
            .ReviewExactCreateResourceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (continuationReview.Outcome is not null)
            return ToToolResult(continuationReview.Outcome);
        if (!HasValidCreateReview(continuationReview)
            || !_stateProtector.MatchesExactCreateBinding(
                confirmation.IntentBinding!,
                continuationReview.Binding!))
        {
            return ConfirmationError(expired: false);
        }
        return await ExecuteMutationAsync(
            token => _calendarService.ExactCreateResourceAsync(continuationReview.ReviewedCreate!, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> BeginCreateConfirmationAsync(
        CalendarExactCreateRequest request,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        var review = await _calendarService
            .ReviewExactCreateResourceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (review.Outcome is not null)
            return ToToolResult(review.Outcome);
        if (!HasValidCreateReview(review))
            return ProtocolError();
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        var confirmationMessage = ConfirmationMessage(review.Binding!);
        if (!IsConfirmationPreviewWithinBudget(confirmationMessage))
            return ConfirmationPreviewPayloadError();
        var state = _stateProtector.ProtectExactCreate(
            CreateOperation,
            BindCreate(request),
            review.Binding!);
        throw new InputRequiredException(CreateConfirmationRequests(confirmationMessage), state);
    }

    private Confirmation ReadCreateConfirmation(
        ReadOnlySpan<byte> requestBinding,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses)
    {
        if (!TryGetConfirmationResponse(requestState, inputResponses, out var response))
            return new Confirmation(ConfirmationDecision.Mismatch, null);
        if (!_stateProtector.TryUnprotectExactCreate(
                requestState!,
                CreateOperation,
                requestBinding,
                out var intentBinding,
                out var expired))
        {
            return new Confirmation(expired ? ConfirmationDecision.Expired : ConfirmationDecision.Mismatch, null);
        }
        if (!TryReadConfirmation(response, out var confirmed))
            return new Confirmation(ConfirmationDecision.Mismatch, null);
        return new Confirmation(
            confirmed ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined,
            intentBinding);
    }

    internal async Task<CallToolResult> ReplaceRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        if (HasOversizedResourceArgument(arguments)
            || MeasureMetadataArguments(arguments) > MaximumMetadataArgumentBytes)
        {
            return InputError(payloadTooLarge: true);
        }
        if (!ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out var request))
            return InputError(payloadTooLarge: false);
        if (request.AuthoritativeUtf8.Length > MaximumDecodedResourceBytes)
            return InputError(payloadTooLarge: true);
        return await ConfirmAsync(
            ReplaceOperation,
            request.Revision,
            BindReplace(request),
            null,
            requestState,
            inputResponses,
            mrtrSupported,
            token => _calendarService.ReviewExactReplaceResourceAsync(request, token),
            token => _calendarService.ExactReplaceResourceAsync(request, token),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CallToolResult> MoveRawAsync(
        IDictionary<string, JsonElement>? arguments,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (MeasureArguments(arguments) > MaximumMetadataArgumentBytes)
            return InputError(payloadTooLarge: true);
        if (!ExactCalendarResourceArgumentParser.TryParseMove(arguments, out var request))
            return InputError(payloadTooLarge: false);
        return await ConfirmMoveAsync(
            request,
            requestState,
            inputResponses,
            mrtrSupported,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ConfirmMoveAsync(
        CalendarExactMoveRequest request,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ConfirmationDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (requestState is null && inputResponses is null)
                return await BeginMoveConfirmationAsync(request, mrtrSupported, linked.Token).ConfigureAwait(false);
            var confirmation = ReadMoveConfirmation(request, requestState, inputResponses);
            if (confirmation.Decision == ConfirmationDecision.Declined)
                return ConfirmationDeclined();
            if (confirmation.Decision != ConfirmationDecision.Confirmed)
                return ConfirmationError(confirmation.Decision == ConfirmationDecision.Expired);
            if (!mrtrSupported)
                return UnsupportedMrtrError();
            return await ExecuteMutationAsync(
                token => _calendarService.ExecuteConfirmedExactMoveResourceAsync(
                    request,
                    confirmation.Binding!,
                    token),
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The exact write review exceeded its time limit.", false,
                "execution", "not_attempted");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The exact write review is unavailable.", true,
                "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is not (InputRequiredException or OperationCanceledException))
        {
            return Error("upstream_protocol_error", "upstream", "The exact write could not be completed.", false,
                "execution", "not_attempted");
        }
    }

    private async Task<CallToolResult> BeginMoveConfirmationAsync(
        CalendarExactMoveRequest request,
        bool mrtrSupported,
        CancellationToken cancellationToken)
    {
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        var review = await _calendarService
            .ReviewExactMoveResourceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (review.Outcome is not null)
            return ToToolResult(review.Outcome);
        if (!HasValidMoveReview(request, review))
            return ProtocolError();
        var message = ConfirmationMessage(MoveOperation, review.Binding!.Revision, review.Binding.DestinationHref);
        if (!IsConfirmationPreviewWithinBudget(message))
            return ConfirmationPreviewPayloadError();
        var state = _stateProtector.ProtectExactMove(MoveOperation, BindMove(request), review.Binding);
        throw new InputRequiredException(CreateConfirmationRequests(message), state);
    }

    private MoveConfirmation ReadMoveConfirmation(
        CalendarExactMoveRequest request,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses)
    {
        if (!TryGetConfirmationResponse(requestState, inputResponses, out var response))
            return new MoveConfirmation(ConfirmationDecision.Mismatch, null);
        if (!_stateProtector.TryUnprotectExactMove(
                requestState!,
                MoveOperation,
                request,
                BindMove(request),
                out var binding,
                out var expired))
        {
            return new MoveConfirmation(expired ? ConfirmationDecision.Expired : ConfirmationDecision.Mismatch, null);
        }
        if (!TryReadConfirmation(response, out var confirmed))
            return new MoveConfirmation(ConfirmationDecision.Mismatch, null);
        return new MoveConfirmation(
            confirmed ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined,
            binding);
    }

    private async Task<CallToolResult> ConfirmAsync(
        string operation,
        CalendarResourceRevisionReference stateRevision,
        byte[] binding,
        string? destinationHref,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        Func<CancellationToken, Task<CalendarExactResourceReviewResult>> review,
        Func<CancellationToken, Task<CalendarExactResourceResult>> execute,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ConfirmationDeadline, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ConfirmWithinDeadlineAsync(
                operation,
                stateRevision,
                binding,
                destinationHref,
                requestState,
                inputResponses,
                mrtrSupported,
                review,
                execute,
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The exact write review exceeded its time limit.", false,
                "execution", "not_attempted");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The exact write review is unavailable.", true,
                "selectionDiscoveryCapability", "not_attempted");
        }
        catch (Exception exception) when (exception is not (InputRequiredException or OperationCanceledException))
        {
            return Error("upstream_protocol_error", "upstream", "The exact write could not be completed.", false,
                "execution", "not_attempted");
        }
    }

    private async Task<CallToolResult> ConfirmWithinDeadlineAsync(
        string operation,
        CalendarResourceRevisionReference stateRevision,
        byte[] binding,
        string? destinationHref,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        Func<CancellationToken, Task<CalendarExactResourceReviewResult>> review,
        Func<CancellationToken, Task<CalendarExactResourceResult>> execute,
        CancellationToken cancellationToken)
    {
        if (requestState is null && inputResponses is null)
            return await BeginConfirmationAsync(
                operation,
                stateRevision,
                binding,
                destinationHref,
                mrtrSupported,
                review,
                cancellationToken);
        var confirmation = ReadConfirmation(operation, stateRevision, binding, requestState, inputResponses);
        if (confirmation.Decision == ConfirmationDecision.Declined)
            return ConfirmationDeclined();
        if (confirmation.Decision != ConfirmationDecision.Confirmed)
            return ConfirmationError(confirmation.Decision == ConfirmationDecision.Expired);
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        var continuationReview = await review(cancellationToken).ConfigureAwait(false);
        if (continuationReview.Outcome is not null)
            return ToToolResult(continuationReview.Outcome);
        if (!HasValidReview(continuationReview)
            || !_stateProtector.MatchesIntent(confirmation.IntentBinding!, continuationReview.IntentDigest.Span))
            return ConfirmationError(expired: false);
        return await ExecuteMutationAsync(execute, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CallToolResult> ExecuteMutationAsync(
        Func<CancellationToken, Task<CalendarExactResourceResult>> execute,
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
            return ToToolResult(new CalendarExactResourceResult(
                CalendarExactResourceCode.Indeterminate,
                CalendarMutationState.Unknown,
                Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation));
        }
    }

    private async Task<CallToolResult> BeginConfirmationAsync(
        string operation,
        CalendarResourceRevisionReference stateRevision,
        byte[] binding,
        string? destinationHref,
        bool mrtrSupported,
        Func<CancellationToken, Task<CalendarExactResourceReviewResult>> review,
        CancellationToken cancellationToken)
    {
        var initialReview = await review(cancellationToken).ConfigureAwait(false);
        if (initialReview.Outcome is not null)
            return ToToolResult(initialReview.Outcome);
        if (!HasValidReview(initialReview))
            return ProtocolError();
        if (!mrtrSupported)
            return UnsupportedMrtrError();
        var confirmationMessage = ConfirmationMessage(
            operation,
            initialReview.BindingRevision!,
            destinationHref);
        if (!IsConfirmationPreviewWithinBudget(confirmationMessage))
            return ConfirmationPreviewPayloadError();
        var state = _stateProtector.Protect(
            operation,
            stateRevision,
            binding,
            initialReview.IntentDigest.Span);
        throw new InputRequiredException(
            CreateConfirmationRequests(confirmationMessage),
            state);
    }

    private Confirmation ReadConfirmation(
        string operation,
        CalendarResourceRevisionReference revision,
        byte[] binding,
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses)
    {
        if (!TryGetConfirmationResponse(requestState, inputResponses, out var response))
            return new Confirmation(ConfirmationDecision.Mismatch, null);
        if (!_stateProtector.TryUnprotect(
                requestState!,
                operation,
                revision,
                binding,
                out var intentBinding,
                out var expired))
            return new Confirmation(expired ? ConfirmationDecision.Expired : ConfirmationDecision.Mismatch, null);
        if (!TryReadConfirmation(response, out var confirmed))
            return new Confirmation(ConfirmationDecision.Mismatch, null);
        return new Confirmation(
            confirmed ? ConfirmationDecision.Confirmed : ConfirmationDecision.Declined,
            intentBinding);
    }

    private static Dictionary<string, InputRequest> CreateConfirmationRequests(string confirmationMessage) => new()
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
    };

    private static string ConfirmationMessage(
        string operation,
        CalendarResourceRevisionReference revision,
        string? destinationHref)
    {
        var destination = destinationHref is null ? string.Empty : $", destination {destinationHref}";
        return $"Confirm {operation} for href {revision.Href}{destination}, UID {revision.EntityUid}, "
            + $"kind {Kind(revision.EntityKind)}, and expected ETag {revision.EntityTag}.";
    }

    private static string ConfirmationMessage(CalendarExactCreateReviewBinding binding) =>
        $"Confirm {CreateOperation} for destination {binding.DestinationHref}, UID {binding.EntityUid}, "
        + $"and kind {Kind(binding.EntityKind)}.";

    internal static bool IsConfirmationPreviewWithinBudget(string message) =>
        GetConfirmationPreviewByteCount(message) <= CalendarQueryToolSupport.MaximumHumanReadableBytes;

    internal static int GetConfirmationPreviewByteCount(string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new ConfirmationPreviewBudget(
            message,
            ConfirmationTitle,
            ConfirmationDescription)).Length;

    private static bool TryGetConfirmationResponse(
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        out InputResponse response)
    {
        response = default!;
        if (string.IsNullOrEmpty(requestState)
            || inputResponses?.Count != 1
            || !inputResponses.TryGetValue(ConfirmationKey, out var candidate)
            || candidate is null)
            return false;
        response = candidate;
        return true;
    }

    private static bool TryReadConfirmation(InputResponse response, out bool confirmed)
    {
        confirmed = false;
        var elicitation = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        if (elicitation?.Action is "decline" or "cancel")
            return true;
        if (elicitation?.Action != "accept"
            || elicitation.Content?.Count != 1
            || !elicitation.Content.TryGetValue("confirm", out var element)
            || element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        confirmed = element.GetBoolean();
        return true;
    }

    private static byte[] BindCreate(CalendarExactCreateRequest request)
    {
        var destination = Encoding.UTF8.GetBytes(request.DestinationHref);
        var combined = new byte[destination.Length + 1 + request.AuthoritativeUtf8.Length];
        destination.CopyTo(combined, 0);
        request.AuthoritativeUtf8.CopyTo(combined.AsMemory(destination.Length + 1));
        return SHA256.HashData(combined);
    }

    private static byte[] BindReplace(CalendarExactReplaceRequest request) =>
        BindRevisionAndValue(request.Revision, request.AuthoritativeUtf8.Span);

    private static byte[] BindMove(CalendarExactMoveRequest request) =>
        BindRevisionAndValue(request.Revision, Encoding.UTF8.GetBytes(request.DestinationHref));

    private static byte[] BindRevisionAndValue(
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> value)
    {
        var prefix = Encoding.UTF8.GetBytes(
            $"{revision.Href}\0{revision.EntityUid}\0{Kind(revision.EntityKind)}\0{revision.EntityTag}\0");
        var combined = new byte[prefix.Length + value.Length];
        prefix.CopyTo(combined, 0);
        value.CopyTo(combined.AsSpan(prefix.Length));
        return SHA256.HashData(combined);
    }

    private static bool HasValidReview(CalendarExactResourceReviewResult review) =>
        review.BindingRevision is not null && review.IntentDigest.Length == SHA256.HashSizeInBytes;

    private static bool HasValidMoveReview(
        CalendarExactMoveRequest request,
        CalendarExactMoveReviewResult review) => review.Binding is not null
        && review.Binding.Revision == request.Revision
        && string.Equals(review.Binding.DestinationHref, request.DestinationHref, StringComparison.Ordinal)
        && review.Binding.SourceIntentDigest.Length == SHA256.HashSizeInBytes
        && !string.IsNullOrWhiteSpace(review.Binding.PolicyVersion);

    private static bool HasValidCreateReview(CalendarExactCreateReviewResult review) =>
        review.Binding is not null
        && review.ReviewedCreate is not null
        && ReferenceEquals(review.Binding, review.ReviewedCreate.Binding)
        && review.Binding.IntentDigest.Length == SHA256.HashSizeInBytes;

    private static int MeasureArguments(IDictionary<string, JsonElement>? arguments) =>
        CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>());

    internal static int MeasureMetadataArguments(IDictionary<string, JsonElement>? arguments)
    {
        var metadata = arguments?.Where(argument => argument.Key is not "utf8Resource" and not "base64Utf8Resource")
            .ToDictionary(argument => argument.Key, argument => argument.Value)
            ?? new Dictionary<string, JsonElement>();
        return JsonSerializer.SerializeToUtf8Bytes(metadata).Length;
    }

    private static bool HasOversizedResourceArgument(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return false;
        if (arguments.TryGetValue("utf8Resource", out var text)
            && text.ValueKind == JsonValueKind.String
            && TryGetStringForAdmission(text, out var resource)
            && Encoding.UTF8.GetByteCount(resource) > MaximumDecodedResourceBytes)
        {
            return true;
        }
        return arguments.TryGetValue("base64Utf8Resource", out var base64)
            && base64.ValueKind == JsonValueKind.String
            && TryGetStringForAdmission(base64, out var encoded)
            && HasOversizedBase64Payload(encoded);
    }

    private static bool HasOversizedBase64Payload(string encoded)
    {
        return TryGetCanonicalDecodedLength(encoded, out var decodedLength)
            && decodedLength > MaximumDecodedResourceBytes;
    }

    private static bool TryGetCanonicalDecodedLength(string encoded, out int decodedLength)
    {
        decodedLength = 0;
        if (!Base64.IsValid(encoded, out var length)
            || ((length + 2) / 3) * 4 != encoded.Length
            || !HasCanonicalPaddingBits(encoded))
        {
            return false;
        }
        decodedLength = length;
        return true;
    }

    private static bool HasCanonicalPaddingBits(string encoded)
    {
        if (encoded.Length == 0 || !encoded.EndsWith('='))
            return true;
        var index = encoded.EndsWith("==", StringComparison.Ordinal) ? encoded.Length - 3 : encoded.Length - 2;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var value = alphabet.IndexOf(encoded[index]);
        return value >= 0 && (encoded.EndsWith("==", StringComparison.Ordinal) ? value % 16 == 0 : value % 4 == 0);
    }

    private static bool TryGetStringForAdmission(JsonElement element, out string value)
    {
        value = string.Empty;
        try
        {
            return element.GetString() is { } text && (value = text) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(payloadTooLarge);

    private static CallToolResult ToToolResult(CalendarExactResourceResult result)
    {
        var mapped = result.Code switch
        {
            CalendarExactResourceCode.Success when result.Snapshot is not null => Success(result.Snapshot),
            CalendarExactResourceCode.NoChange => NonMutation("no_change"),
            _ => ExactError(result)
        };
        return EnsureBoundedResult(mapped, result.MutationState);
    }

    internal static int MeasureResult(CallToolResult result) => CalendarQueryToolSupport.MeasureResult(result);

    internal static CallToolResult EnsureBoundedResult(
        CallToolResult result,
        CalendarMutationState mutationState) =>
        CalendarQueryToolSupport.EnsureBoundedResult(result, (_, _) => Error(
            "payload_too_large",
            "limitsAndAdmission",
            "The exact write result exceeds the safe payload limit.",
            false,
            "admissionAndPayload",
            MutationState(mutationState)));

    private static CallToolResult Success(CalendarResourceSnapshot snapshot) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateSuccessResult(
            "success",
            "committed",
            CalendarSnapshotResult.FromSnapshot(snapshot),
            snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = "Exact Calendar Object Resource write completed." }]
    };

    private static CallToolResult NonMutation(string outcome) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarResourceDeleteNonMutationResult(
            outcome,
            "not_attempted",
            [])),
        Content = [new TextContentBlock { Text = "Exact Calendar Object Resource write made no change." }]
    };

    private static CallToolResult ExactError(CalendarExactResourceResult result)
    {
        var description = Describe(result.Code);
        return Error(
            description.Code,
            description.Category,
            description.Message,
            result.Retryable,
            Phase(result.Phase),
            MutationState(result.MutationState),
            result.Snapshot is null ? null : ExactConflictSnapshotResult.FromSnapshot(result.Snapshot),
            result.RetryAfterMilliseconds,
            result.Limits is null ? null : CalendarEntityCreateLimits.FromLimits(result.Limits));
    }

    private static ExactErrorDescription Describe(CalendarExactResourceCode code) => code switch
    {
        CalendarExactResourceCode.InvalidInput => new("invalid_input", "input", "The exact write input is invalid."),
        CalendarExactResourceCode.InvalidCalendarData => new("invalid_calendar_data", "input", "The complete Calendar resource is invalid."),
        CalendarExactResourceCode.NotFound => new("not_found", "selection", "The exact write target was not found."),
        CalendarExactResourceCode.OutsideScope => new("outside_scope", "selection", "The exact write target is outside Calendar Scope."),
        CalendarExactResourceCode.EntityKindMismatch => new("entity_kind_mismatch", "state", "The Entity Kind changed."),
        CalendarExactResourceCode.UnsupportedCapability => new("unsupported_capability", "capabilityAndProjection", "The exact write is unsupported."),
        CalendarExactResourceCode.Conflict => new("conflict", "state", "The resource revision changed."),
        CalendarExactResourceCode.DestinationConflict => new("destination_conflict", "state", "The destination already exists."),
        CalendarExactResourceCode.ConcurrencyUnavailable => new("concurrency_unavailable", "state", "A strong Entity Tag is unavailable."),
        CalendarExactResourceCode.LimitExhausted => new("limit_exhausted", "limitsAndAdmission", "The exact write exceeded its work limit."),
        CalendarExactResourceCode.PayloadTooLarge => new("payload_too_large", "limitsAndAdmission", "The exact payload is too large."),
        CalendarExactResourceCode.UpstreamUnauthorized => new("upstream_unauthorized", "upstream", "The Calendar mutation was not authorized."),
        CalendarExactResourceCode.UpstreamForbidden => new("upstream_forbidden", "upstream", "The Calendar mutation was forbidden."),
        CalendarExactResourceCode.UpstreamRateLimited => new("upstream_rate_limited", "upstream", "The Calendar mutation is rate limited."),
        CalendarExactResourceCode.UpstreamUnavailable => new("upstream_unavailable", "upstream", "The Calendar service is unavailable."),
        CalendarExactResourceCode.FidelityFailure => new("fidelity_failure", "postWriteTruth", "The committed resource differs from the reviewed intent."),
        CalendarExactResourceCode.CommittedButUnverified => new("committed_but_unverified", "postWriteTruth", "The committed resource could not be verified."),
        CalendarExactResourceCode.CommittedButConcurrencyUnavailable => new("committed_but_concurrency_unavailable", "postWriteTruth", "The committed resource has no strong Entity Tag."),
        CalendarExactResourceCode.ConfirmationMismatch => new("confirmation_mismatch", "confirmation", "The mutation confirmation does not match the reviewed request."),
        CalendarExactResourceCode.Indeterminate => new("indeterminate", "postWriteTruth", "The mutation outcome is indeterminate."),
        _ => new("upstream_protocol_error", "upstream", "The Calendar service returned an invalid response.")
    };

    private static CallToolResult ConfirmationDeclined() => NonMutation("confirmation_declined");

    private static CallToolResult ConfirmationError(bool expired) => Error(
        expired ? "confirmation_expired" : "confirmation_mismatch",
        "confirmation",
        expired ? "The mutation confirmation expired." : "The mutation confirmation does not match the reviewed request.",
        false,
        "mrtr",
        "not_attempted");

    private static CallToolResult ConfirmationPreviewPayloadError() => Error(
        "payload_too_large",
        "limitsAndAdmission",
        "The exact write confirmation preview exceeds the safe human-readable limit.",
        false,
        "admissionAndPayload",
        "not_attempted");

    private static CallToolResult UnsupportedMrtrError() => Error(
        "unsupported_capability",
        "capabilityAndProjection",
        "Exact writes require MCP Multi Round-Trip Request support.",
        false,
        "mrtr",
        "not_attempted");

    private static CallToolResult ProtocolError() => Error(
        "upstream_protocol_error",
        "upstream",
        "The exact write review returned invalid evidence.",
        false,
        "completeResourceSemantics",
        "not_attempted");

    private static CallToolResult InputError(bool payloadTooLarge) => Error(
        payloadTooLarge ? "payload_too_large" : "invalid_input",
        payloadTooLarge ? "limitsAndAdmission" : "input",
        payloadTooLarge ? "The exact write arguments are too large." : "The exact write input is invalid.",
        false,
        payloadTooLarge ? "admissionAndPayload" : "schemaLexicalDiscriminator",
        "not_attempted");

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        string mutationState,
        ExactConflictSnapshotResult? currentSnapshot = null,
        int? retryAfterMs = null,
        CalendarEntityCreateLimits? limits = null) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new ExactCalendarResourceErrorResult(
            code,
            category,
            message,
            retryable,
            phase,
            mutationState,
            currentSnapshot,
            retryAfterMs,
            limits)),
        Content = [new TextContentBlock { Text = "Exact Calendar Object Resource write failed." }]
    };

    private static string Phase(CalendarExactResourcePhase phase) => phase switch
    {
        CalendarExactResourcePhase.SchemaLexicalDiscriminator => "schemaLexicalDiscriminator",
        CalendarExactResourcePhase.OriginScopeAuthorization => "originScopeAuthorization",
        CalendarExactResourcePhase.SelectionDiscoveryCapability => "selectionDiscoveryCapability",
        CalendarExactResourcePhase.TargetRevision => "targetRevision",
        CalendarExactResourcePhase.CompleteResourceSemantics => "completeResourceSemantics",
        CalendarExactResourcePhase.Mrtr => "mrtr",
        CalendarExactResourcePhase.PostWriteVerificationOrReconciliation => "postWriteVerificationOrReconciliation",
        _ => "execution"
    };

    private static string MutationState(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };

    private static string Kind(CalendarEntityKind kind) => kind == CalendarEntityKind.Event ? "event" : "todo";

    private enum ConfirmationDecision
    {
        Mismatch,
        Expired,
        Declined,
        Confirmed
    }

    private sealed record Confirmation(ConfirmationDecision Decision, string? IntentBinding);

    private sealed record MoveConfirmation(
        ConfirmationDecision Decision,
        CalendarExactMoveReviewBinding? Binding);

    private sealed record ConfirmationPreviewBudget(string Message, string Title, string Description);

    private sealed record ExactErrorDescription(string Code, string Category, string Message);

    private sealed record ExactCalendarResourceErrorResult(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("retryable")] bool Retryable,
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("mutationState")] string MutationState,
        [property: JsonPropertyName("currentSnapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            ExactConflictSnapshotResult? CurrentSnapshot,
        [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            int? RetryAfterMs,
        [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            CalendarEntityCreateLimits? Limits);

    private sealed record ExactConflictSnapshotResult(
        [property: JsonPropertyName("calendar")] CalendarHref Calendar,
        [property: JsonPropertyName("resourceRevision")] CalendarResourceRevisionResult ResourceRevision,
        [property: JsonPropertyName("entityRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            CalendarEntityRevisionResult? EntityRevision)
    {
        public static ExactConflictSnapshotResult FromSnapshot(CalendarResourceSnapshot snapshot) => new(
            new CalendarHref(snapshot.CalendarHref),
            new CalendarResourceRevisionResult(snapshot.ResourceHref, snapshot.EntityTag),
            snapshot.Projection.EntityUid is null || snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque
                ? null
                : new CalendarEntityRevisionResult(
                    snapshot.ResourceHref,
                    snapshot.Projection.EntityUid,
                    Kind(snapshot.Projection.Kind),
                    snapshot.EntityTag));

        private static string Kind(CalendarResourceProjectionKind kind) =>
            kind == CalendarResourceProjectionKind.Event ? "event" : "todo";
    }
}
