using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Thin MCP adapter for bounded derived Calendar Occurrence queries.</summary>
[McpServerToolType]
public sealed class CalendarOccurrenceTools
{
    private const int MaximumCursorCharacters = 2048;
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private readonly ICalendarQueryModule _queryModule;

    public CalendarOccurrenceTools(IServiceProvider services)
        : this(services.GetRequiredService<ICalendarQueryModule>())
    {
    }

    internal CalendarOccurrenceTools(ICalendarQueryModule queryModule)
    {
        _queryModule = queryModule;
    }

    /// <summary>Queries derived Event and To-do Occurrences.</summary>
    [McpServerTool(
        Name = "calendar_occurrences.query",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarOccurrenceQuerySuccessResult)),
     Description("Start one bounded Occurrence query or continue its immutable Query Result Snapshot. Start evaluates recurrence once under an explicit IANA Temporal Evaluation Context from evaluationTimeZone or validated configuration; Continue accepts only cursor and optional pageSize and performs no CalDAV or semantic work.")]
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
        var reply = await _queryModule.QueryOccurrencesAsync(request, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            QueryReply<CalendarOccurrenceQueryItem>.Page page => Success(page.Value),
            QueryReply<CalendarOccurrenceQueryItem>.Failure failure => Error(failure.Error),
            _ => Error(ProtocolFailure())
        };
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => Error(new QueryFailure(
        payloadTooLarge ? QueryFailureCode.PayloadTooLarge : QueryFailureCode.InvalidInput,
        payloadTooLarge ? QueryFailureCategory.LimitsAndAdmission : QueryFailureCategory.Input,
        payloadTooLarge
            ? "The Occurrence query arguments exceed the safe payload limit."
            : "The Occurrence query input is invalid.",
        false,
        payloadTooLarge ? QueryFailurePhase.AdmissionAndPayload : QueryFailurePhase.SchemaLexicalDiscriminator));

    private static CallToolResult Success(QueryPage<CalendarOccurrenceQueryItem> page) => new()
    {
        IsError = false,
        StructuredContent = page.StructuredContent,
        Content = [new TextContentBlock { Text = page.HumanText }]
    };

    private static CallToolResult Error(QueryFailure failure) => CalendarQueryToolSupport.EnsureBoundedResult(
        ErrorWithoutBounding(failure),
        (byteCount, humanReadable) => ErrorWithoutBounding(new QueryFailure(
            QueryFailureCode.PayloadTooLarge,
            QueryFailureCategory.LimitsAndAdmission,
            humanReadable
                ? "The Occurrence query human-readable result exceeds the safe payload limit."
                : "The Occurrence query result exceeds the safe payload limit.",
            false,
            QueryFailurePhase.AdmissionAndPayload,
            new QueryExecutionLimits(ByteCount: byteCount))));

    private static CallToolResult ErrorWithoutBounding(QueryFailure failure) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(new CalendarOccurrenceQueryErrorResult(
            Code(failure.Code),
            Category(failure.Category),
            failure.Message,
            failure.Retryable,
            Phase(failure.Phase),
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
        Content = [new TextContentBlock { Text = "Occurrence query failed." }]
    };

    private static bool TryCreateRequest(
        IDictionary<string, JsonElement>? arguments,
        out CalendarOccurrenceQueryRequest request)
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
        out CalendarOccurrenceQueryRequest request)
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
        request = new CalendarOccurrenceQueryRequest.Continue(cursor, pageSize);
        return true;
    }

    private static bool TryCreateStart(
        IDictionary<string, JsonElement> arguments,
        out CalendarOccurrenceQueryRequest request)
    {
        request = null!;
        if (!TryReadStartValues(arguments, out var scope, out var from, out var to)
            || !CalendarQueryToolSupport.TryCreateScope(scope, out var domainScope)
            || !CalendarQueryToolSupport.TryParseUtc(from, out var domainFrom)
            || !CalendarQueryToolSupport.TryParseUtc(to, out var domainTo)
            || domainFrom is null || domainTo is null
            || !TryReadOptionalString(arguments, "evaluationTimeZone", out var evaluationTimeZone)
            || !TryReadOptionalPageSize(arguments, out var pageSize))
            return false;
        request = new CalendarOccurrenceQueryRequest.Start(
            new CalendarOccurrenceQuery(domainScope, domainFrom.Value, domainTo.Value, evaluationTimeZone),
            pageSize ?? 50);
        return true;
    }

    private static bool TryReadStartValues(
        IDictionary<string, JsonElement> arguments,
        out CalendarEntityScopeArgument scope,
        out CalendarEntityUtcArgument from,
        out CalendarEntityUtcArgument to)
    {
        scope = null!;
        from = null!;
        to = null!;
        if (!HasStartShape(arguments, out var scopeElement, out var fromElement, out var toElement)
            || !TryDeserialize(scopeElement, out CalendarEntityScopeArgument? parsedScope)
            || !TryDeserialize(fromElement, out CalendarEntityUtcArgument? parsedFrom)
            || !TryDeserialize(toElement, out CalendarEntityUtcArgument? parsedTo)
            || parsedScope is null || parsedFrom is null || parsedTo is null)
            return false;
        scope = parsedScope;
        from = parsedFrom;
        to = parsedTo;
        return true;
    }

    private static bool HasStartShape(
        IDictionary<string, JsonElement> arguments,
        out JsonElement scope,
        out JsonElement from,
        out JsonElement to)
    {
        scope = default;
        from = default;
        to = default;
        return !arguments.Keys.Any(key => key is not ("scope" or "from" or "to" or "evaluationTimeZone" or "pageSize"))
            && arguments.TryGetValue("scope", out scope)
            && arguments.TryGetValue("from", out from)
            && arguments.TryGetValue("to", out to)
            && CalendarQueryToolSupport.HasScopeShape(scope)
            && CalendarQueryToolSupport.HasTemporalShape(from)
            && CalendarQueryToolSupport.HasTemporalShape(to);
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
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryReadOptionalPageSize(
        IDictionary<string, JsonElement> arguments,
        out int? pageSize)
    {
        pageSize = null;
        if (!arguments.TryGetValue("pageSize", out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value)
            || value is < 1 or > 200)
            return false;
        pageSize = value;
        return true;
    }

    private static bool TryDeserialize<T>(JsonElement element, out T? value)
    {
        try
        {
            value = element.Deserialize<T>();
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static QueryFailure ProtocolFailure() => new(
        QueryFailureCode.UpstreamProtocolError,
        QueryFailureCategory.Upstream,
        "The Occurrence query returned an invalid response.",
        false,
        QueryFailurePhase.Execution);

    private static CalendarAuthorizedCandidateResult Candidate(QueryAuthorizedCandidate candidate) => new(
        new CalendarHref(candidate.CalendarHref),
        candidate.DisplayName,
        new CalendarEntityKinds(
            CalendarEntityKindCapability.From(candidate.EventSupport, candidate.EventEvidence),
            CalendarEntityKindCapability.From(candidate.TodoSupport, candidate.TodoEvidence)));

    private static string Code(QueryFailureCode code) => code switch
    {
        QueryFailureCode.InvalidInput => "invalid_input",
        QueryFailureCode.CursorExpired => "cursor_expired",
        QueryFailureCode.LimitExhausted => "limit_exhausted",
        QueryFailureCode.Busy => "busy",
        QueryFailureCode.PayloadTooLarge => "payload_too_large",
        QueryFailureCode.UpstreamProtocolError => "upstream_protocol_error",
        QueryFailureCode.UnsupportedCapability => "unsupported_capability",
        QueryFailureCode.ConcurrencyUnavailable => "concurrency_unavailable",
        QueryFailureCode.TemporalUnresolved => "temporal_unresolved",
        QueryFailureCode.RecurrenceUnevaluable => "recurrence_unevaluable",
        QueryFailureCode.UpstreamUnavailable => "upstream_unavailable",
        QueryFailureCode.UpstreamUnauthorized => "upstream_unauthorized",
        QueryFailureCode.UpstreamForbidden => "upstream_forbidden",
        QueryFailureCode.UpstreamRateLimited => "upstream_rate_limited",
        QueryFailureCode.NotFound => "not_found",
        QueryFailureCode.Ambiguous => "ambiguous",
        QueryFailureCode.OutsideScope => "outside_scope",
        _ => "upstream_protocol_error"
    };

    private static string Category(QueryFailureCategory category) => category switch
    {
        QueryFailureCategory.Input => "input",
        QueryFailureCategory.State => "state",
        QueryFailureCategory.LimitsAndAdmission => "limitsAndAdmission",
        QueryFailureCategory.Upstream => "upstream",
        QueryFailureCategory.CapabilityAndProjection => "capabilityAndProjection",
        QueryFailureCategory.Selection => "selection",
        _ => "upstream"
    };

    private static string Phase(QueryFailurePhase phase) => phase switch
    {
        QueryFailurePhase.SchemaLexicalDiscriminator => "schemaLexicalDiscriminator",
        QueryFailurePhase.Pagination => "pagination",
        QueryFailurePhase.Execution => "execution",
        QueryFailurePhase.AdmissionAndPayload => "admissionAndPayload",
        QueryFailurePhase.SelectionDiscoveryCapability => "selectionDiscoveryCapability",
        QueryFailurePhase.TargetRevision => "targetRevision",
        QueryFailurePhase.CompleteResourceSemantics => "completeResourceSemantics",
        QueryFailurePhase.OriginScopeAuthorization => "originScopeAuthorization",
        _ => "execution"
    };

    private static string? LimitDimension(QueryLimitDimension? dimension) => dimension switch
    {
        QueryLimitDimension.ResourceCount => "resource_count",
        QueryLimitDimension.AttemptCount => "attempt_count",
        QueryLimitDimension.ByteCount => "byte_count",
        QueryLimitDimension.ElapsedTime => "elapsed_time",
        _ => null
    };
}

public sealed record CalendarOccurrenceQuerySuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarOccurrenceSnapshotResult> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("temporalEvaluationContext"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalEvaluationContextResult? TemporalEvaluationContext,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarOccurrenceQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null,
    [property: JsonPropertyName("retryAfterMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterMs = null);

public sealed record CalendarOccurrenceSnapshotResult(
    [property: JsonPropertyName("snapshot")] CalendarSnapshotResult Snapshot,
    [property: JsonPropertyName("recurrenceIdentity")] CalendarRecurrenceIdentityResult RecurrenceIdentity,
    [property: JsonPropertyName("timing")] CalendarOccurrenceTimingResult Timing);

public sealed record CalendarRecurrenceIdentityResult(
    [property: JsonPropertyName("value")] CalendarTemporalResult Value);

public sealed record CalendarTemporalResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("timeZoneId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZoneId = null);

public sealed record CalendarOccurrenceTimingResult(
    [property: JsonPropertyName("sourceStart")] CalendarTemporalResult SourceStart,
    [property: JsonPropertyName("effectiveStart")] CalendarTemporalResult EffectiveStart,
    [property: JsonPropertyName("sourceEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? SourceEnd = null,
    [property: JsonPropertyName("effectiveEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EffectiveEnd = null,
    [property: JsonPropertyName("sourceDuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceDuration = null,
    [property: JsonPropertyName("effectiveDuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveDuration = null,
    [property: JsonPropertyName("evaluatedStartUtc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EvaluatedStartUtc = null,
    [property: JsonPropertyName("evaluatedEndUtc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EvaluatedEndUtc = null,
    [property: JsonPropertyName("evaluationTimeZone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvaluationTimeZone = null);
