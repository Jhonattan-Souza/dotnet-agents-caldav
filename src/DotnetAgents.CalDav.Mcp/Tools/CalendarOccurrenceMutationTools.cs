using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Applies explicit revision-bound Occurrence membership and cancellation mutations.</summary>
[McpServerToolType]
public sealed class CalendarOccurrenceMutationTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private readonly ICalendarService _calendarService;

    public CalendarOccurrenceMutationTools(ICalendarService calendarService)
    {
        _calendarService = calendarService;
    }

    [McpServerTool(
        Name = "todos.complete",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Record completion for one revision-bound To-do or identified To-do occurrence at an explicitly supplied absolute snapshot href.")]
    public Task<CallToolResult> CompleteTodoAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        CompleteTodoRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "calendar_occurrences.add",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Add one explicit recurrence identity through recurrence data.")]
    public Task<CallToolResult> AddAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        AddRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "calendar_occurrences.exclude",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Add one explicit exclusion to a revision-bound recurrence set.")]
    public Task<CallToolResult> ExcludeAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        ExcludeRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "calendar_occurrences.restore_exclusion",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Remove only one explicit recurrence exclusion.")]
    public Task<CallToolResult> RestoreExclusionAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        RestoreExclusionRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "calendar_occurrences.cancel",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Create or update one cancelled recurrence override.")]
    public Task<CallToolResult> CancelAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        CancelRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(
        Name = "calendar_occurrences.restore_cancellation",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityCreateSuccessResult)),
     Description("Restore only one cancelled recurrence override.")]
    public Task<CallToolResult> RestoreCancellationAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        RestoreCancellationRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> AddRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseOccurrenceMutation,
        _calendarService.AddOccurrenceAsync,
        "Occurrence mutation",
        cancellationToken);

    internal async Task<CallToolResult> CompleteTodoRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseTodoCompletion,
        _calendarService.CompleteTodoAsync,
        "To-do Completion",
        cancellationToken);

    internal async Task<CallToolResult> ExcludeRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseOccurrenceMutation,
        _calendarService.ExcludeOccurrenceAsync,
        "Occurrence mutation",
        cancellationToken);

    internal async Task<CallToolResult> RestoreExclusionRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseOccurrenceMutation,
        _calendarService.RestoreOccurrenceExclusionAsync,
        "Occurrence mutation",
        cancellationToken);

    internal async Task<CallToolResult> CancelRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseOccurrenceMutation,
        _calendarService.CancelOccurrenceAsync,
        "Occurrence mutation",
        cancellationToken);

    internal async Task<CallToolResult> RestoreCancellationRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken) => await ExecuteRawAsync(
        arguments,
        ParseOccurrenceMutation,
        _calendarService.RestoreOccurrenceCancellationAsync,
        "Occurrence mutation",
        cancellationToken);

    private async Task<CallToolResult> ExecuteRawAsync<TRequest>(
        IDictionary<string, JsonElement>? arguments,
        Func<IDictionary<string, JsonElement>?, TRequest?> parse,
        Func<TRequest, CancellationToken, Task<CalendarEntityPatchResult>> execute,
        string operation,
        CancellationToken cancellationToken) where TRequest : class
    {
        if (CalendarQueryToolSupport.MeasureArguments(arguments, arguments ?? new Dictionary<string, JsonElement>())
            > MaximumArgumentBytes)
            return InputError(payloadTooLarge: true);
        var request = parse(arguments);
        if (request is null)
            return InputError(payloadTooLarge: false);
        try
        {
            return ToToolResult(
                await execute(request, cancellationToken).ConfigureAwait(false),
                operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ToToolResult(
                new CalendarEntityPatchResult(
                    CalendarEntityPatchCode.Indeterminate,
                    CalendarMutationState.Unknown,
                    Phase: CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation),
                operation);
        }
    }

    private static CalendarOccurrenceMutationRequest? ParseOccurrenceMutation(
        IDictionary<string, JsonElement>? arguments) => TryParse(arguments, out var request) ? request : null;

    private static CalendarTodoCompletionRequest? ParseTodoCompletion(
        IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null
            || arguments.Count is < 1 or > 2
            || !arguments.TryGetValue("snapshot", out var snapshot)
            || !TryRevision(snapshot, out var revision)
            || revision.EntityKind != CalendarEntityKind.Todo)
            return null;
        if (!arguments.TryGetValue("recurrenceIdentity", out var recurrenceIdentity))
            return arguments.Count == 1 ? new(revision) : null;
        return arguments.Count == 2 && TryRecurrenceIdentity(recurrenceIdentity, out var identity)
            ? new(revision, identity)
            : null;
    }

    private static bool TryParse(
        IDictionary<string, JsonElement>? arguments,
        out CalendarOccurrenceMutationRequest request)
    {
        request = null!;
        if (arguments is null
            || arguments.Count != 2
            || !arguments.TryGetValue("snapshot", out var snapshot)
            || !arguments.TryGetValue("recurrenceIdentity", out var recurrenceIdentity)
            || !TryRevision(snapshot, out var revision)
            || !TryRecurrenceIdentity(recurrenceIdentity, out var identity))
            return false;
        request = new(revision, identity);
        return true;
    }

    private static bool TryRecurrenceIdentity(JsonElement value, out CalendarTemporalValue identity)
    {
        identity = null!;
        if (!HasExactProperties(value, "value")
            || !value.TryGetProperty("value", out var temporal)
            || !CalendarEntityCreateArgumentParser.TryParsePatchScalarValue("start", temporal, out var parsed)
            || parsed is not CalendarTemporalValue candidate
            || !HasValidTemporalLexicalForm(candidate))
            return false;
        identity = candidate;
        return true;
    }

    private static bool TryRevision(JsonElement value, out CalendarResourceRevisionReference revision)
    {
        revision = null!;
        if (!HasExactProperties(value, "href", "entityUid", "entityKind", "entityTag")
            || !TryString(value, "href", out var href)
            || !TryString(value, "entityUid", out var uid)
            || !TryString(value, "entityKind", out var kind)
            || !TryString(value, "entityTag", out var tag)
            || kind is not ("event" or "todo"))
            return false;
        revision = new(
            href,
            uid,
            kind == "event" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            tag);
        return true;
    }

    private static bool HasExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == names.Length
            && actual.Distinct(StringComparer.Ordinal).Count() == actual.Length
            && names.All(name => actual.Contains(name, StringComparer.Ordinal));
    }

    private static bool TryString(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        text = property.GetString()!;
        return text.Length > 0;
    }

    private static bool HasValidTemporalLexicalForm(CalendarTemporalValue value) => value.Kind switch
    {
        CalendarTemporalKind.Date => value.TimeZoneId is null && DateOnly.TryParseExact(
            value.Value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _),
        CalendarTemporalKind.UtcDateTime => value.TimeZoneId is null
            && value.Value.EndsWith('Z')
            && TryDateTime(value.Value[..^1]),
        CalendarTemporalKind.FloatingDateTime => value.TimeZoneId is null && TryDateTime(value.Value),
        CalendarTemporalKind.ZonedDateTime => !string.IsNullOrEmpty(value.TimeZoneId) && TryDateTime(value.Value),
        _ => false
    };

    private static bool TryDateTime(string value) => DateTime.TryParseExact(
        value,
        "yyyy-MM-dd'T'HH:mm:ss",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out _);

    private static CallToolResult ToToolResult(CalendarEntityPatchResult result, string operation)
    {
        var mapped = result.Code switch
        {
            CalendarEntityPatchCode.Success when result.Snapshot is not null => Success(result.Snapshot, operation),
            CalendarEntityPatchCode.NoChange => NoChange(result.Snapshot?.Diagnostics ?? [], operation),
            _ => Error(result, operation)
        };
        return CalendarQueryToolSupport.EnsureBoundedResult(mapped, (_, _) => NamedError(
            "payload_too_large",
            "limitsAndAdmission",
            "The Occurrence mutation result exceeds the safe payload limit.",
            "admissionAndPayload",
            MutationState(result.MutationState)));
    }

    private static CallToolResult Success(CalendarResourceSnapshot snapshot, string operation) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateSuccessResult(
            "success",
            "committed",
            CalendarSnapshotResult.FromSnapshot(snapshot),
            snapshot.Diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = operation + " completed." }]
    };

    private static CallToolResult NoChange(
        IReadOnlyList<CalendarResourceDiagnostic> diagnostics,
        string operation) => new()
    {
        IsError = false,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityPatchNoChangeResult(
            "no_change",
            "not_attempted",
            diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray())),
        Content = [new TextContentBlock { Text = operation + " made no change." }]
    };

    private static CallToolResult Error(CalendarEntityPatchResult result, string operation) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
            Code(result.Code),
            Category(result.Code),
            $"The {operation} could not be completed.",
            result.Retryable,
            Phase(result.Phase),
            MutationState(result.MutationState),
            CurrentSnapshot: result.Snapshot is null ? null : CalendarSnapshotResult.FromSnapshot(result.Snapshot),
            RetryAfterMs: result.RetryAfterMilliseconds,
            Limits: result.LimitDimension is null
                ? null
                : new CalendarEntityCreateLimits(Dimension: "elapsed_time"))),
        Content = [new TextContentBlock { Text = operation + " failed." }]
    };

    private static string Code(CalendarEntityPatchCode code) => string.Concat(code.ToString().SelectMany(
        (character, index) => char.IsUpper(character) && index > 0
            ? new[] { '_', char.ToLowerInvariant(character) }
            : [char.ToLowerInvariant(character)]));

    private static string Category(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.InvalidInput or CalendarEntityPatchCode.InvalidCalendarData => "input",
        CalendarEntityPatchCode.NotFound or CalendarEntityPatchCode.OutsideScope
            or CalendarEntityPatchCode.EntityKindMismatch => "selection",
        CalendarEntityPatchCode.OpaqueResource or CalendarEntityPatchCode.TemporalUnresolved
            or CalendarEntityPatchCode.RecurrenceUnevaluable or CalendarEntityPatchCode.UnsupportedCapability =>
            "capabilityAndProjection",
        CalendarEntityPatchCode.Conflict or CalendarEntityPatchCode.ConcurrencyUnavailable
            or CalendarEntityPatchCode.CompletionStateConflict => "state",
        CalendarEntityPatchCode.PayloadTooLarge or CalendarEntityPatchCode.LimitExhausted => "limitsAndAdmission",
        CalendarEntityPatchCode.FidelityFailure or CalendarEntityPatchCode.CommittedButUnverified
            or CalendarEntityPatchCode.CommittedButConcurrencyUnavailable or CalendarEntityPatchCode.Indeterminate =>
            "postWriteTruth",
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

    private static CallToolResult InputError(bool payloadTooLarge) => payloadTooLarge
        ? NamedError(
            "payload_too_large",
            "limitsAndAdmission",
            "The Occurrence mutation arguments exceed the safe payload limit.",
            "admissionAndPayload",
            "not_attempted")
        : NamedError(
            "invalid_input",
            "input",
            "The Occurrence mutation input is invalid.",
            "schemaLexicalDiscriminator",
            "not_attempted");

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => InputError(payloadTooLarge);

    private static CallToolResult NamedError(
        string code,
        string category,
        string message,
        string phase,
        string mutationState,
        bool retryable = false,
        int? retryAfterMs = null) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityCreateErrorResult(
            code,
            category,
            message,
            retryable,
            phase,
            mutationState,
            RetryAfterMs: retryAfterMs)),
        Content = [new TextContentBlock { Text = "Occurrence mutation failed." }]
    };
}
