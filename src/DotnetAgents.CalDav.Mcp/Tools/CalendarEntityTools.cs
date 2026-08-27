using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Bounded persisted Calendar Entity queries.</summary>
[McpServerToolType]
public sealed class CalendarEntityTools
{
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    internal const int MaximumHumanReadableBytes = CalendarQueryToolSupport.MaximumHumanReadableBytes;
    internal const int MaximumStructuredResultBytes = CalendarQueryToolSupport.MaximumStructuredResultBytes;
    private const int MaximumCursorCharacters = 2048;
    private readonly ICalendarQueryModule _queryModule;

    public CalendarEntityTools(ICalendarQueryModule queryModule)
    {
        _queryModule = queryModule;
    }

    /// <summary>Queries persisted Event and To-do Calendar Object Resources.</summary>
    [McpServerTool(
        Name = "calendar_entities.query",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarEntityQuerySuccessResult)),
     Description("Start one Calendar Entity query or continue its immutable Query Result Snapshot. A bounded Start requires an explicit IANA Temporal Evaluation Context from evaluationTimeZone or validated configuration; Continue repeats the frozen context without CalDAV or semantic work.")]
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
        var reply = await _queryModule.QueryEntitiesAsync(request, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            QueryReply<CalendarEntityQueryItem>.Page page => Success(page.Value),
            QueryReply<CalendarEntityQueryItem>.Failure failure => Error(failure.Error),
            _ => Error(new QueryFailure(
                QueryFailureCode.UpstreamProtocolError,
                QueryFailureCategory.Upstream,
                "The Calendar Entity query returned an invalid response.",
                false,
                QueryFailurePhase.Execution))
        };
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => Error(new QueryFailure(
        payloadTooLarge ? QueryFailureCode.PayloadTooLarge : QueryFailureCode.InvalidInput,
        payloadTooLarge ? QueryFailureCategory.LimitsAndAdmission : QueryFailureCategory.Input,
        payloadTooLarge
            ? "The query arguments exceed the safe payload limit."
            : "The Calendar Entity query input is invalid.",
        false,
        payloadTooLarge ? QueryFailurePhase.AdmissionAndPayload : QueryFailurePhase.SchemaLexicalDiscriminator));

    internal static int MeasureResult(CallToolResult result) => CalendarQueryToolSupport.MeasureResult(result);

    internal static int MeasureHumanReadableResult(CallToolResult result) =>
        CalendarQueryToolSupport.MeasureHumanReadableResult(result);

    internal static CallToolResult EnsureBoundedResult(CallToolResult result) =>
        CalendarToolResult.Success(result).FinalizeBounded(PayloadLimitCandidate);

    private static CallToolResult Success(QueryPage<CalendarEntityQueryItem> page) => CalendarToolResult.Success(
        new CallToolResult
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
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityQueryErrorResult(
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
            Content = [new TextContentBlock { Text = "Calendar Entity query failed." }]
        }, facts);

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

    private static bool TryCreateRequest(
        IDictionary<string, JsonElement>? arguments,
        out CalendarEntityQueryRequest request)
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
        out CalendarEntityQueryRequest request)
    {
        request = null!;
        if (arguments.Keys.Any(key => key is not ("cursor" or "pageSize"))
            || !arguments.TryGetValue("cursor", out var cursorElement)
            || cursorElement.ValueKind != JsonValueKind.String)
            return false;
        var cursor = cursorElement.GetString();
        if (string.IsNullOrEmpty(cursor)
            || cursor.Length > MaximumCursorCharacters
            || !TryReadOptionalPageSize(arguments, out var pageSize))
            return false;
        request = new CalendarEntityQueryRequest.Continue(cursor, pageSize);
        return true;
    }

    private static bool TryCreateStart(
        IDictionary<string, JsonElement> arguments,
        out CalendarEntityQueryRequest request)
    {
        request = null!;
        if (!TryReadStartArguments(arguments, out var scope, out var kinds)
            || !TryCreateQuery(arguments, scope!, kinds!, out var query)
            || !TryReadOptionalPageSize(arguments, out var pageSize))
            return false;
        request = new CalendarEntityQueryRequest.Start(query, pageSize ?? 50);
        return true;
    }

    private static bool TryReadStartArguments(
        IDictionary<string, JsonElement> arguments,
        out CalendarEntityScopeArgument? scope,
        out IReadOnlyList<string>? kinds)
    {
        scope = null;
        kinds = null;
        if (arguments.Keys.Any(key => key is not ("scope" or "entityKinds" or "from" or "to" or "evaluationTimeZone" or "pageSize"))
            || !arguments.TryGetValue("scope", out var scopeElement)
            || !arguments.TryGetValue("entityKinds", out var kindsElement)
            || !CalendarQueryToolSupport.HasScopeShape(scopeElement)
            || !TryDeserialize(scopeElement, out scope)
            || !TryDeserialize(kindsElement, out string[]? parsedKinds)
            || scope is null
            || parsedKinds is null)
            return false;
        kinds = parsedKinds;
        return true;
    }

    private static bool TryCreateQuery(
        IDictionary<string, JsonElement> arguments,
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string> kinds,
        out CalendarEntityQuery query)
    {
        query = null!;
        if (!CalendarQueryToolSupport.TryCreateScope(scope, out var domainScope)
            || !TryCreateKinds(kinds, out var domainKinds)
            || !TryReadWindow(arguments, out var from, out var to)
            || !TryReadOptionalString(arguments, "evaluationTimeZone", out var evaluationTimeZone))
            return false;
        query = new CalendarEntityQuery(domainScope, domainKinds, from, to, evaluationTimeZone);
        return true;
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

    private static bool TryCreateKinds(
        IReadOnlyList<string> values,
        out IReadOnlyList<CalendarEntityKind> kinds)
    {
        kinds = [];
        if (values.Count is < 1 or > 2 || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            return false;
        var parsed = new List<CalendarEntityKind>(values.Count);
        foreach (var value in values)
        {
            if (value == "event")
                parsed.Add(CalendarEntityKind.Event);
            else if (value == "todo")
                parsed.Add(CalendarEntityKind.Todo);
            else
                return false;
        }
        kinds = parsed;
        return true;
    }

    private static bool TryReadWindow(
        IDictionary<string, JsonElement> arguments,
        out DateTimeOffset? from,
        out DateTimeOffset? to)
    {
        from = null;
        to = null;
        var hasFrom = arguments.TryGetValue("from", out var fromElement);
        var hasTo = arguments.TryGetValue("to", out var toElement);
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

    private static CalendarToolResult PayloadLimitCandidate(int byteCount, bool humanReadable)
    {
        var failure = new QueryFailure(
            QueryFailureCode.PayloadTooLarge,
            QueryFailureCategory.LimitsAndAdmission,
            humanReadable
                ? "The Calendar Entity query human-readable result exceeds the safe payload limit."
                : "The Calendar Entity query result exceeds the safe payload limit.",
            false,
            QueryFailurePhase.AdmissionAndPayload,
            new QueryExecutionLimits(ByteCount: byteCount));
        return ErrorCandidate(failure, CalendarTelemetryFacts.From(failure));
    }
}

public sealed record CalendarEntityScopeArgument(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("calendar")] CalendarEntityReferenceArgument? Calendar = null);

public sealed record CalendarEntityReferenceArgument(
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("href")] string? Href = null);

public sealed record CalendarEntityUtcArgument(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value);

public sealed record CalendarEntityQuerySuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarSnapshotResult> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("temporalEvaluationContext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalEvaluationContextResult? TemporalEvaluationContext,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarTemporalEvaluationContextResult(
    [property: JsonPropertyName("timeZone")] string TimeZone,
    [property: JsonPropertyName("source")] string Source);

public sealed record CalendarEntityQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterMs = null);

public sealed record CalendarEntityExecutionLimits(
    [property: JsonPropertyName("resourcesInspected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResourcesInspected = null,
    [property: JsonPropertyName("calendarCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CalendarCount = null,
    [property: JsonPropertyName("occurrenceCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OccurrenceCount = null,
    [property: JsonPropertyName("byteCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ByteCount = null,
    [property: JsonPropertyName("itemCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ItemCount = null,
    [property: JsonPropertyName("snapshotCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SnapshotCount = null,
    [property: JsonPropertyName("dimension"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dimension = null,
    [property: JsonPropertyName("observed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Observed = null,
    [property: JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Limit = null);

public sealed record CalendarAuthorizedCandidateResult(
    [property: JsonPropertyName("calendar")] CalendarHref Calendar,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("entityKinds")] CalendarEntityKinds EntityKinds)
{
    internal static CalendarAuthorizedCandidateResult FromDescriptor(CalendarDescriptor descriptor) => new(
        new CalendarHref(descriptor.Href),
        descriptor.DisplayName,
        new CalendarEntityKinds(
            CalendarEntityKindCapability.From(descriptor.EventSupport, descriptor.EventEvidence),
            CalendarEntityKindCapability.From(descriptor.TodoSupport, descriptor.TodoEvidence)));
}
