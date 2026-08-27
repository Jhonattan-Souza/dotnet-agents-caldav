using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Compact semantic To-do reads over immutable query result snapshots.</summary>
[McpServerToolType]
public sealed class CalendarTodoTools
{
    private const int MaximumCursorCharacters = 2048;
    private static readonly CalendarTodoProjectionField[] RoutineProjection =
    [
        CalendarTodoProjectionField.Summary,
        CalendarTodoProjectionField.Due,
        CalendarTodoProjectionField.Priority,
        CalendarTodoProjectionField.Categories
    ];
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private readonly ICalendarQueryModule _queryModule;

    public CalendarTodoTools(ICalendarQueryModule queryModule)
    {
        _queryModule = queryModule;
    }

    [McpServerTool(
        Name = "todos.query",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarTodoQuerySuccessResult)),
     Description("Start one compact To-do query or continue its immutable Query Result Snapshot. A Start resolves an explicit IANA Temporal Evaluation Context before CalDAV work and uses one VTODO-only authoritative corpus; Continue repeats the frozen page context without CalDAV or semantic work.")]
    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) => QueryRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> QueryRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is not null && JsonSerializer.SerializeToUtf8Bytes(arguments).Length > MaximumArgumentBytes)
            return CreateInputGuardError(payloadTooLarge: true);
        if (!TryCreateRequest(arguments, out var request))
            return CreateInputGuardError(payloadTooLarge: false);
        var reply = await _queryModule.QueryTodosAsync(request, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            QueryReply<CalendarTodoQueryPageItem>.Page page => Success(page.Value),
            QueryReply<CalendarTodoQueryPageItem>.Failure failure => Error(failure.Error),
            _ => Error(new QueryFailure(
                QueryFailureCode.UpstreamProtocolError,
                QueryFailureCategory.Upstream,
                "The To-do query returned an invalid response.",
                false,
                QueryFailurePhase.Execution))
        };
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => Error(new QueryFailure(
        payloadTooLarge ? QueryFailureCode.PayloadTooLarge : QueryFailureCode.InvalidInput,
        payloadTooLarge ? QueryFailureCategory.LimitsAndAdmission : QueryFailureCategory.Input,
        payloadTooLarge
            ? "The To-do query arguments exceed the safe payload limit."
            : "The To-do query input is invalid.",
        false,
        payloadTooLarge ? QueryFailurePhase.AdmissionAndPayload : QueryFailurePhase.SchemaLexicalDiscriminator));

    private static CallToolResult Success(QueryPage<CalendarTodoQueryPageItem> page) =>
        CalendarToolResult.Success(new CallToolResult
        {
            IsError = false,
            StructuredContent = page.StructuredContent,
            Content = [new TextContentBlock { Text = page.HumanText }]
        }).FinalizeBounded(PayloadLimitCandidate);

    private static CallToolResult Error(QueryFailure failure)
    {
        var facts = CalendarTelemetryFacts.From(failure);
        return ErrorCandidate(failure, facts).FinalizeBounded(PayloadLimitCandidate);
    }

    private static CalendarToolResult ErrorCandidate(
        QueryFailure failure,
        CalendarStructuredErrorFacts facts) => CalendarToolResult.Error(new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarTodoQueryErrorResult(
                facts.CodeName,
                facts.CategoryName,
                failure.Message,
                facts.Retryable,
                facts.PhaseName,
                failure.Limits is null ? null : new CalendarEntityExecutionLimits(
                    failure.Limits.ResourcesInspected,
                    failure.Limits.CalendarCount,
                    failure.Limits.OccurrenceCount,
                    failure.Limits.ByteCount,
                    failure.Limits.ItemCount,
                    failure.Limits.SnapshotCount,
                    LimitDimension(failure.Limits.Dimension),
                    failure.Limits.Observed,
                    failure.Limits.Limit),
                failure.AuthorizedCandidates?.Select(Candidate).ToArray(),
                failure.RetryAfterMs)),
            Content = [new TextContentBlock { Text = "Compact To-do query failed." }]
        }, facts);

    private static CalendarToolResult PayloadLimitCandidate(int byteCount, bool humanReadable)
    {
        var failure = new QueryFailure(
            QueryFailureCode.PayloadTooLarge,
            QueryFailureCategory.LimitsAndAdmission,
            humanReadable
                ? "The To-do query human-readable result exceeds the safe payload limit."
                : "The To-do query result exceeds the safe payload limit.",
            false,
            QueryFailurePhase.AdmissionAndPayload,
            new QueryExecutionLimits(ByteCount: byteCount));
        return ErrorCandidate(failure, CalendarTelemetryFacts.From(failure));
    }

    private static bool TryCreateRequest(
        IDictionary<string, JsonElement>? arguments,
        out CalendarTodoQueryRequest request)
    {
        request = null!;
        if (arguments is null)
            return false;
        return arguments.ContainsKey("cursor")
            ? TryCreateContinue(arguments, out request)
            : TryCreateStart(arguments, out request);
    }

    private static bool TryCreateContinue(
        IDictionary<string, JsonElement> arguments,
        out CalendarTodoQueryRequest request)
    {
        request = null!;
        if (arguments.Keys.Any(key => key is not ("cursor" or "pageSize"))
            || !arguments.TryGetValue("cursor", out var cursorElement)
            || cursorElement.ValueKind != JsonValueKind.String
            || !TryReadOptionalPageSize(arguments, out var pageSize))
            return false;
        var cursor = cursorElement.GetString();
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumCursorCharacters)
            return false;
        request = new CalendarTodoQueryRequest.Continue(cursor, pageSize);
        return true;
    }

    private static bool TryCreateStart(
        IDictionary<string, JsonElement> arguments,
        out CalendarTodoQueryRequest request)
    {
        request = null!;
        if (arguments.Keys.Any(key => key is not (
                "scope" or "completionStates" or "from" or "to" or "evaluationTimeZone"
                or "dueFrom" or "dueTo" or "projection" or "pageSize"))
            || !TryReadScope(arguments, out var scope)
            || !TryReadStates(arguments, out var states)
            || !TryReadWindow(arguments, "from", "to", out var from, out var to)
            || !TryReadWindow(arguments, "dueFrom", "dueTo", out var dueFrom, out var dueTo)
            || !TryReadOptionalString(arguments, "evaluationTimeZone", out var evaluationTimeZone)
            || !TryReadProjection(arguments, out var projection)
            || !TryReadOptionalPageSize(arguments, out var pageSize))
            return false;
        request = new CalendarTodoQueryRequest.Start(
            new CalendarTodoQuery(scope!, states, from, to, evaluationTimeZone, dueFrom, dueTo),
            projection,
            pageSize ?? 50);
        return true;
    }

    private static bool TryReadScope(
        IDictionary<string, JsonElement> arguments,
        out CalendarEntityScope? scope)
    {
        scope = null;
        if (!arguments.TryGetValue("scope", out var element)
            || !CalendarQueryToolSupport.HasScopeShape(element)
            || !TryDeserialize(element, out CalendarEntityScopeArgument? argument)
            || argument is null
            || !CalendarQueryToolSupport.TryCreateScope(argument, out var parsed)
            || parsed.Mode == CalendarEntityScopeMode.Default)
            return false;
        scope = parsed;
        return true;
    }

    private static bool TryReadStates(
        IDictionary<string, JsonElement> arguments,
        out IReadOnlyList<CalendarTodoCompletionState> states)
    {
        states = [CalendarTodoCompletionState.Open];
        if (!arguments.TryGetValue("completionStates", out var element))
            return true;
        if (!TryDeserialize(element, out string[]? values)
            || values is null
            || values.Length == 0
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            return false;
        var parsed = new List<CalendarTodoCompletionState>(values.Length);
        foreach (var value in values)
        {
            if (!TryParseState(value, out var state))
                return false;
            parsed.Add(state);
        }
        states = parsed;
        return true;
    }

    private static bool TryReadProjection(
        IDictionary<string, JsonElement> arguments,
        out IReadOnlyList<CalendarTodoProjectionField> projection)
    {
        projection = RoutineProjection;
        if (!arguments.TryGetValue("projection", out var element))
            return true;
        if (!TryDeserialize(element, out string[]? values)
            || values is null
            || values.Length == 0
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            return false;
        var parsed = new List<CalendarTodoProjectionField>(values.Length);
        foreach (var value in values)
        {
            if (!TryParseProjection(value, out var field))
                return false;
            parsed.Add(field);
        }
        projection = parsed;
        return true;
    }

    private static bool TryReadWindow(
        IDictionary<string, JsonElement> arguments,
        string fromName,
        string toName,
        out DateTimeOffset? from,
        out DateTimeOffset? to)
    {
        from = null;
        to = null;
        var hasFrom = arguments.TryGetValue(fromName, out var fromElement);
        var hasTo = arguments.TryGetValue(toName, out var toElement);
        if (hasFrom != hasTo)
            return false;
        if (!hasFrom)
            return true;
        return CalendarQueryToolSupport.HasTemporalShape(fromElement)
            && CalendarQueryToolSupport.HasTemporalShape(toElement)
            && TryDeserialize(fromElement, out CalendarEntityUtcArgument? fromArgument)
            && TryDeserialize(toElement, out CalendarEntityUtcArgument? toArgument)
            && fromArgument is not null
            && toArgument is not null
            && CalendarQueryToolSupport.TryParseUtc(fromArgument, out from)
            && CalendarQueryToolSupport.TryParseUtc(toArgument, out to);
    }

    private static bool TryReadOptionalString(
        IDictionary<string, JsonElement> arguments,
        string name,
        out string? value)
    {
        value = null;
        if (!arguments.TryGetValue(name, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return value is not null;
    }

    private static bool TryReadOptionalPageSize(
        IDictionary<string, JsonElement> arguments,
        out int? pageSize)
    {
        pageSize = null;
        if (!arguments.TryGetValue("pageSize", out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            return false;
        pageSize = value;
        return value is >= 1 and <= 200;
    }

    private static bool TryDeserialize<T>(JsonElement element, out T? value)
    {
        value = default;
        try
        {
            value = element.Deserialize<T>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseState(string value, out CalendarTodoCompletionState state)
    {
        state = value switch
        {
            "open" => CalendarTodoCompletionState.Open,
            "completed" => CalendarTodoCompletionState.Completed,
            "cancelled" => CalendarTodoCompletionState.Cancelled,
            "indeterminate" => CalendarTodoCompletionState.Indeterminate,
            _ => (CalendarTodoCompletionState)(-1)
        };
        return Enum.IsDefined(state);
    }

    private static bool TryParseProjection(string value, out CalendarTodoProjectionField field)
    {
        field = value switch
        {
            "summary" => CalendarTodoProjectionField.Summary,
            "status" => CalendarTodoProjectionField.Status,
            "completedAt" => CalendarTodoProjectionField.CompletedAt,
            "percentComplete" => CalendarTodoProjectionField.PercentComplete,
            "due" => CalendarTodoProjectionField.Due,
            "priority" => CalendarTodoProjectionField.Priority,
            "categories" => CalendarTodoProjectionField.Categories,
            "start" => CalendarTodoProjectionField.Start,
            "description" => CalendarTodoProjectionField.Description,
            "recurrence" => CalendarTodoProjectionField.Recurrence,
            _ => (CalendarTodoProjectionField)(-1)
        };
        return Enum.IsDefined(field);
    }

    private static CalendarAuthorizedCandidateResult Candidate(QueryAuthorizedCandidate candidate) => new(
        new CalendarHref(candidate.CalendarHref),
        candidate.DisplayName,
        new CalendarEntityKinds(
            CalendarEntityKindCapability.From(candidate.EventSupport, candidate.EventEvidence),
            CalendarEntityKindCapability.From(candidate.TodoSupport, candidate.TodoEvidence)));

    private static string? LimitDimension(QueryLimitDimension? dimension) => dimension switch
    {
        QueryLimitDimension.ResourceCount => "resource_count",
        QueryLimitDimension.AttemptCount => "attempt_count",
        QueryLimitDimension.ByteCount => "byte_count",
        QueryLimitDimension.ElapsedTime => "elapsed_time",
        _ => null
    };

}

public sealed record CalendarTodoQuerySuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarTodoCompactItemResult> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("excludedIndeterminateCount")] int ExcludedIndeterminateCount,
    [property: JsonPropertyName("temporalEvaluationContext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalEvaluationContextResult? TemporalEvaluationContext,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarTodoQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterMs = null);

public sealed record CalendarTodoCompactItemResult(
    [property: JsonPropertyName("resultKind")] string ResultKind,
    [property: JsonPropertyName("uid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uid,
    [property: JsonPropertyName("completionState")] string CompletionState,
    [property: JsonPropertyName("summary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Status,
    [property: JsonPropertyName("completedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? CompletedAt,
    [property: JsonPropertyName("percentComplete"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PercentComplete,
    [property: JsonPropertyName("due"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Due,
    [property: JsonPropertyName("priority"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Priority,
    [property: JsonPropertyName("categories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Categories,
    [property: JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Start,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("recurrence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Recurrence,
    [property: JsonPropertyName("completionTarget")] CalendarTodoCompletionTargetResult CompletionTarget,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics);

public sealed record CalendarTodoCompletionTargetResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("entityRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityRevisionResult? EntityRevision,
    [property: JsonPropertyName("recurrenceIdentity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? RecurrenceIdentity,
    [property: JsonPropertyName("resourceRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarResourceRevisionResult? ResourceRevision);
