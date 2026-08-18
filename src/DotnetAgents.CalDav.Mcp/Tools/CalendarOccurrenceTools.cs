using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Bounded derived Calendar Occurrence queries.</summary>
[McpServerToolType]
public sealed class CalendarOccurrenceTools
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    private readonly ICalendarService _calendarService;
    private readonly CalendarEntityCursorProtector _cursorProtector;
    private readonly TimeProvider _timeProvider;

    public CalendarOccurrenceTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<CalendarEntityCursorProtector>(),
            services.GetRequiredService<TimeProvider>())
    {
    }

    internal CalendarOccurrenceTools(
        ICalendarService calendarService,
        CalendarEntityCursorProtector cursorProtector,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _cursorProtector = cursorProtector;
        _timeProvider = timeProvider;
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
     Description("Query bounded derived occurrences over a half-open UTC range.")]
    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        QueryRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> QueryRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is not null && JsonSerializer.SerializeToUtf8Bytes(arguments).Length > MaximumArgumentBytes)
            return EnsureBoundedResult(CreateInputGuardError(payloadTooLarge: true));
        if (!TryDeserializeRawArguments(arguments, out var parsed))
            return EnsureBoundedResult(CreateInputGuardError(payloadTooLarge: false));
        return await QueryCoreAsync(
            parsed.Scope,
            parsed.From,
            parsed.To,
            cancellationToken,
            parsed.EvaluationTimeZone,
            parsed.PageSize,
            parsed.Cursor,
            arguments);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => payloadTooLarge
        ? Error("payload_too_large", "limitsAndAdmission", "The Occurrence query arguments exceed the safe payload limit.", false, "admissionAndPayload")
        : Error("invalid_input", "input", "The Occurrence query input is invalid.", false, "schemaLexicalDiscriminator");

    internal async Task<CallToolResult> QueryCoreAsync(
        CalendarEntityScopeArgument scope,
        CalendarEntityUtcArgument from,
        CalendarEntityUtcArgument to,
        CancellationToken cancellationToken,
        string? evaluationTimeZone = null,
        int? pageSize = null,
        string? cursor = null,
        IDictionary<string, JsonElement>? rawArguments = null)
    {
        if (MeasureArguments(scope, from, to, evaluationTimeZone, pageSize, cursor, rawArguments) > MaximumArgumentBytes)
            return EnsureBoundedResult(CreateInputGuardError(payloadTooLarge: true));
        if (!TryPrepareQuery(
                scope, from, to, evaluationTimeZone, pageSize, cursor, rawArguments, out var query, out var effectivePageSize))
            return EnsureBoundedResult(CreateInputGuardError(payloadTooLarge: false));

        var queryContext = CreateQueryContext(query, effectivePageSize);
        CalendarOccurrenceContinuation? continuation = null;
        if (cursor is not null && !TryDecodeCursor(cursor, queryContext, out continuation))
            return EnsureBoundedResult(Error("invalid_input", "input", "The continuation cursor is invalid or expired.", false, "schemaLexicalDiscriminator"));

        return EnsureBoundedResult(await ExecuteQuerySafelyAsync(
            query, continuation, queryContext, effectivePageSize, cancellationToken));
    }

    private async Task<CallToolResult> ExecuteQuerySafelyAsync(
        CalendarOccurrenceQuery query,
        CalendarOccurrenceContinuation? continuation,
        string queryContext,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var deadlineAt = _timeProvider.GetUtcNow().AddSeconds(30);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _calendarService.QueryOccurrencesAsync(query, linked.Token);
            ThrowIfDeadlineExpired(deadlineAt, linked.Token);
            return result.Code == CalendarOccurrenceQueryCode.Success
                ? CreatePage(result, continuation, queryContext, pageSize, deadlineAt, linked.Token)
                : MapQueryFailure(result);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The Occurrence query exceeded the Calendar limit.", false,
                "admissionAndPayload", new CalendarEntityExecutionLimits(CalendarCount: exception.CalendarCount));
        }
        catch (HttpRequestException exception)
        {
            return MapHttpFailure(exception.StatusCode);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return DeadlineError();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The Occurrence query is temporarily unavailable.", true, "execution");
        }
        catch (TimeoutException)
        {
            return Error("upstream_unavailable", "upstream", "The Occurrence query is temporarily unavailable.", true, "execution");
        }
        catch (XmlException)
        {
            return SelectionProtocolError();
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return SelectionProtocolError();
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error("unsupported_capability", "capabilityAndProjection",
                "The server does not support the required Calendar query capability.", false,
                "selectionDiscoveryCapability");
        }
        catch (CalendarOccurrenceDeadlineException)
        {
            return DeadlineError();
        }
    }

    private CallToolResult CreatePage(
        CalendarOccurrenceQueryResult result,
        CalendarOccurrenceContinuation? continuation,
        string queryContext,
        int pageSize,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken)
    {
        var eligible = result.Items.Where(item => continuation is null || IsAfter(item, continuation.Value)).ToArray();
        var page = new List<CalendarOccurrenceSnapshotResult>(Math.Min(pageSize, eligible.Length));
        for (var index = 0; index < eligible.Length && page.Count < pageSize; index++)
        {
            ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
            page.Add(CalendarOccurrenceSnapshotResult.FromSnapshot(eligible[index]));
            var hasMore = index + 1 < eligible.Length;
            if (!TryCreateCursor(hasMore, queryContext, eligible[index], out var candidateCursor))
                return CursorPayloadError();
            var candidate = CreateSuccess(page, result.Diagnostics, hasMore ? candidateCursor : null);
            if (CalendarQueryToolSupport.MeasureResult(candidate)
                <= CalendarQueryToolSupport.MaximumStructuredResultBytes)
                continue;

            page.RemoveAt(page.Count - 1);
            if (page.Count == 0)
                return Error("payload_too_large", "limitsAndAdmission", "One Occurrence cannot fit in a result page.", false, "admissionAndPayload");
            break;
        }

        ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
        string? nextCursor = null;
        if (page.Count < eligible.Length && !TryCreateCursor(true, queryContext, eligible[page.Count - 1], out nextCursor))
            return CursorPayloadError();
        return CreateSuccess(page, result.Diagnostics, nextCursor);
    }

    private bool TryCreateCursor(
        bool required,
        string queryContext,
        CalendarOccurrenceSnapshot item,
        out string? cursor)
    {
        cursor = null;
        if (!required)
            return true;
        var continuation = CalendarOccurrenceContinuation.FromSnapshot(item);
        return _cursorProtector.TryProtect(
            queryContext,
            continuation.EffectiveStartUtc,
            JsonSerializer.Serialize(new CalendarOccurrenceContinuationTail(
                continuation.CalendarHref,
                continuation.EntityUid,
                continuation.RecurrenceIdentity)),
            out cursor);
    }

    private bool TryDecodeCursor(
        string cursor,
        string queryContext,
        out CalendarOccurrenceContinuation? continuation)
    {
        continuation = null;
        if (!_cursorProtector.TryUnprotect(cursor, queryContext, out var decoded, out _))
            return false;
        try
        {
            var tail = JsonSerializer.Deserialize<CalendarOccurrenceContinuationTail>(decoded.ResourceHref);
            if (tail is null || string.IsNullOrEmpty(decoded.CalendarHref)
                || string.IsNullOrEmpty(tail.CalendarHref)
                || string.IsNullOrEmpty(tail.EntityUid)
                || string.IsNullOrEmpty(tail.RecurrenceIdentity))
                return false;
            continuation = new CalendarOccurrenceContinuation(
                decoded.CalendarHref, tail.CalendarHref, tail.EntityUid, tail.RecurrenceIdentity);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ThrowIfDeadlineExpired(DateTimeOffset deadlineAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() >= deadlineAt)
            throw new CalendarOccurrenceDeadlineException();
    }

    private static bool IsAfter(CalendarOccurrenceSnapshot item, CalendarOccurrenceContinuation continuation)
    {
        var current = CalendarOccurrenceContinuation.FromSnapshot(item);
        return Compare(current, continuation) > 0;
    }

    private static int Compare(CalendarOccurrenceContinuation left, CalendarOccurrenceContinuation right)
    {
        var effective = string.CompareOrdinal(left.EffectiveStartUtc, right.EffectiveStartUtc);
        if (effective != 0)
            return effective;
        var calendar = string.CompareOrdinal(left.CalendarHref, right.CalendarHref);
        if (calendar != 0)
            return calendar;
        var uid = string.CompareOrdinal(left.EntityUid, right.EntityUid);
        return uid != 0 ? uid : string.CompareOrdinal(left.RecurrenceIdentity, right.RecurrenceIdentity);
    }

    private static CallToolResult CreateSuccess(
        IReadOnlyList<CalendarOccurrenceSnapshotResult> items,
        IReadOnlyList<CalendarResourceDiagnostic> diagnostics,
        string? nextCursor) => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarOccurrenceQuerySuccessResult(
                "success",
                items,
                diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray(),
                new CalendarPagination("non_snapshot", nextCursor))),
            Content = [new TextContentBlock { Text = "Occurrence query completed." }]
        };

    private static CallToolResult MapQueryFailure(CalendarOccurrenceQueryResult result)
    {
        var candidates = result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray();
        return result.Code switch
        {
            CalendarOccurrenceQueryCode.InvalidInput => Error("invalid_input", "input", "The Occurrence query input is invalid.", false, "schemaLexicalDiscriminator"),
            CalendarOccurrenceQueryCode.UnsafeScope => Error("invalid_input", "input", "The Occurrence query Calendar href is unsafe.", false, "originScopeAuthorization"),
            CalendarOccurrenceQueryCode.NotFound => Error("not_found", "selection", "No matching authorized Calendar was found.", false, "selectionDiscoveryCapability", candidates: candidates),
            CalendarOccurrenceQueryCode.Ambiguous => Error("ambiguous", "selection", "The Calendar selector matched more than one authorized Calendar.", false, "selectionDiscoveryCapability", candidates: candidates),
            CalendarOccurrenceQueryCode.OutsideScope => Error("outside_scope", "selection", "The selected Calendar is outside the configured Calendar Scope.", false, "originScopeAuthorization", candidates: candidates),
            CalendarOccurrenceQueryCode.UnsupportedCapability => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
            CalendarOccurrenceQueryCode.ConcurrencyUnavailable => Error("concurrency_unavailable", "state", "A query candidate did not provide a strong Entity Tag.", false, "targetRevision"),
            CalendarOccurrenceQueryCode.LimitExhausted => Error("limit_exhausted", "limitsAndAdmission", "The Occurrence query exhausted its execution budget.", false, "execution", ToLimits(result.Limits)),
            CalendarOccurrenceQueryCode.PayloadTooLarge => Error("payload_too_large", "limitsAndAdmission", "A Calendar Object Resource exceeds the safe payload limit.", false, "admissionAndPayload", ToLimits(result.Limits)),
            CalendarOccurrenceQueryCode.TemporalUnresolved => Error("temporal_unresolved", "capabilityAndProjection", "Temporal evaluation could not be resolved.", false, "completeResourceSemantics"),
            CalendarOccurrenceQueryCode.RecurrenceUnevaluable => Error("recurrence_unevaluable", "capabilityAndProjection", "The Recurrence Set could not be evaluated.", false, "completeResourceSemantics"),
            _ => ProtocolError()
        };
    }

    private static CalendarEntityExecutionLimits? ToLimits(CalendarOccurrenceQueryExecutionLimits? limits) => limits is null
        ? null
        : new CalendarEntityExecutionLimits(limits.ResourcesInspected, OccurrenceCount: limits.OccurrenceCount, ByteCount: limits.ByteCount);

    private static CallToolResult MapHttpFailure(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Error("upstream_unauthorized", "upstream", "The Occurrence query was not authorized.", false, "execution"),
        HttpStatusCode.Forbidden => Error("upstream_forbidden", "upstream", "The Occurrence query was forbidden.", false, "execution"),
        HttpStatusCode.TooManyRequests => Error("upstream_rate_limited", "upstream", "The Occurrence query is rate limited.", true, "execution"),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.NotFound => SelectionProtocolError(),
        HttpStatusCode.RequestEntityTooLarge => Error("payload_too_large", "limitsAndAdmission", "The Occurrence query response is too large.", false, "admissionAndPayload"),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => Error("conflict", "state", "The Occurrence query encountered an upstream state conflict.", false, "execution"),
        HttpStatusCode.RequestTimeout => Error("upstream_unavailable", "upstream", "The Occurrence query is temporarily unavailable.", true, "execution"),
        HttpStatusCode.InsufficientStorage => Error("upstream_unavailable", "upstream", "The Occurrence query is unavailable.", false, "execution"),
        null => Error("upstream_unavailable", "upstream", "The Occurrence query is temporarily unavailable.", true, "execution"),
        >= HttpStatusCode.InternalServerError => Error("upstream_unavailable", "upstream", "The Occurrence query is temporarily unavailable.", true, "execution"),
        _ => ProtocolError()
    };

    private static CallToolResult ProtocolError() => Error(
        "upstream_protocol_error", "upstream", "The Occurrence query returned an invalid response.", false, "execution");

    private static CallToolResult SelectionProtocolError() => Error(
        "upstream_protocol_error", "upstream", "Calendar discovery returned an invalid response.", false,
        "selectionDiscoveryCapability");

    private static CallToolResult DeadlineError() => Error(
        "limit_exhausted", "limitsAndAdmission", "The Occurrence query exhausted the elapsed_time execution budget.", false, "execution");

    private static CallToolResult CursorPayloadError() => Error(
        "payload_too_large", "limitsAndAdmission", "The continuation cursor exceeds the safe payload limit.", false, "admissionAndPayload");

    private static CallToolResult Error(
        string code,
        string category,
        string message,
        bool retryable,
        string phase,
        CalendarEntityExecutionLimits? limits = null,
        IReadOnlyList<CalendarAuthorizedCandidateResult>? candidates = null) => new()
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarOccurrenceQueryErrorResult(
                code, category, message, retryable, phase, limits,
                candidates is { Count: > 0 } ? candidates : null)),
            Content = [new TextContentBlock { Text = "Occurrence query failed." }]
        };

    private static CallToolResult EnsureBoundedResult(CallToolResult result) =>
        CalendarQueryToolSupport.EnsureBoundedResult(result, CreatePayloadLimitError);

    private static CallToolResult CreatePayloadLimitError(int byteCount, bool humanReadable) => Error(
        "payload_too_large",
        "limitsAndAdmission",
        humanReadable
            ? "The Occurrence query human-readable result exceeds the safe payload limit."
            : "The Occurrence query result exceeds the safe payload limit.",
        false,
        "admissionAndPayload",
        new CalendarEntityExecutionLimits(ByteCount: byteCount));

    private static bool TryPrepareQuery(
        CalendarEntityScopeArgument scope,
        CalendarEntityUtcArgument from,
        CalendarEntityUtcArgument to,
        string? evaluationTimeZone,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments,
        out CalendarOccurrenceQuery query,
        out int effectivePageSize)
    {
        query = null!;
        effectivePageSize = pageSize ?? DefaultPageSize;
        if (!HasFrozenRawShape(rawArguments)
            || !HasValidPagination(effectivePageSize, cursor)
            || !TryCreateDomainQuery(scope, from, to, evaluationTimeZone, out query))
            return false;
        return true;
    }

    private static bool HasValidPagination(int pageSize, string? cursor) =>
        pageSize is >= 1 and <= MaximumPageSize
        && cursor is not { Length: > CalendarEntityCursorProtector.MaximumCursorCharacters };

    private static bool TryCreateDomainQuery(
        CalendarEntityScopeArgument scope,
        CalendarEntityUtcArgument from,
        CalendarEntityUtcArgument to,
        string? evaluationTimeZone,
        out CalendarOccurrenceQuery query)
    {
        query = null!;
        if (!CalendarQueryToolSupport.TryCreateScope(scope, out var domainScope)
            || !CalendarQueryToolSupport.TryParseUtc(from, out var domainFrom)
            || !CalendarQueryToolSupport.TryParseUtc(to, out var domainTo)
            || domainFrom is null || domainTo is null
            || evaluationTimeZone is { Length: 0 })
            return false;
        query = new CalendarOccurrenceQuery(domainScope, domainFrom.Value, domainTo.Value, evaluationTimeZone);
        return true;
    }

    private static bool TryDeserializeRawArguments(
        IDictionary<string, JsonElement>? arguments,
        out CalendarOccurrenceRawArguments parsed)
    {
        parsed = null!;
        if (!TryGetRequiredArguments(arguments, out var scopeElement, out var fromElement, out var toElement))
            return false;
        var validArguments = arguments!;
        try
        {
            var scope = scopeElement.Deserialize<CalendarEntityScopeArgument>();
            var from = fromElement.Deserialize<CalendarEntityUtcArgument>();
            var to = toElement.Deserialize<CalendarEntityUtcArgument>();
            if (scope is null || from is null || to is null)
                return false;
            parsed = new CalendarOccurrenceRawArguments(
                scope,
                from,
                to,
                DeserializeOptional<string>(validArguments, "evaluationTimeZone"),
                DeserializeOptional<int?>(validArguments, "pageSize"),
                DeserializeOptional<string>(validArguments, "cursor"));
            return !HasAnyPresentNull(validArguments, "evaluationTimeZone", "pageSize", "cursor");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetRequiredArguments(
        IDictionary<string, JsonElement>? arguments,
        out JsonElement scope,
        out JsonElement from,
        out JsonElement to)
    {
        scope = default;
        from = default;
        to = default;
        return arguments is not null
            && arguments.TryGetValue("scope", out scope)
            && arguments.TryGetValue("from", out from)
            && arguments.TryGetValue("to", out to)
            && HasFrozenRawShape(arguments);
    }

    private static bool HasFrozenRawShape(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return true;
        var allowed = new[] { "scope", "from", "to", "evaluationTimeZone", "pageSize", "cursor" };
        return !arguments.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))
            && (!arguments.TryGetValue("scope", out var scope) || CalendarQueryToolSupport.HasScopeShape(scope))
            && (!arguments.TryGetValue("from", out var from) || CalendarQueryToolSupport.HasTemporalShape(from))
            && (!arguments.TryGetValue("to", out var to) || CalendarQueryToolSupport.HasTemporalShape(to));
    }

    private static T? DeserializeOptional<T>(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var element) ? element.Deserialize<T>() : default;

    private static bool HasPresentNull(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Null;

    private static bool HasAnyPresentNull(
        IDictionary<string, JsonElement> arguments,
        params string[] names) => names.Any(name => HasPresentNull(arguments, name));

    private static int MeasureArguments(
        CalendarEntityScopeArgument scope,
        CalendarEntityUtcArgument from,
        CalendarEntityUtcArgument to,
        string? evaluationTimeZone,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments) => CalendarQueryToolSupport.MeasureArguments(
            rawArguments,
            new { scope, from, to, evaluationTimeZone, pageSize, cursor });

    private static string CreateQueryContext(CalendarOccurrenceQuery query, int pageSize)
    {
        var selectorKind = query.Scope.Calendar?.Name is not null ? "name" : "href";
        var selectorValue = query.Scope.Calendar?.Name is not null
            ? query.Scope.Calendar.Name.ToUpperInvariant()
            : query.Scope.Calendar?.Href;
        return JsonSerializer.Serialize(new CalendarOccurrenceCursorQueryBinding(
            query.Scope.Mode,
            selectorKind,
            selectorValue,
            query.From.ToString("O", CultureInfo.InvariantCulture),
            query.To.ToString("O", CultureInfo.InvariantCulture),
            query.EvaluationTimeZone,
            pageSize));
    }

    private sealed record CalendarOccurrenceCursorQueryBinding(
        CalendarEntityScopeMode ScopeMode,
        string SelectorKind,
        string? SelectorValue,
        string From,
        string To,
        string? EvaluationTimeZone,
        int PageSize);

    private sealed record CalendarOccurrenceRawArguments(
        CalendarEntityScopeArgument Scope,
        CalendarEntityUtcArgument From,
        CalendarEntityUtcArgument To,
        string? EvaluationTimeZone,
        int? PageSize,
        string? Cursor);

    private sealed record CalendarOccurrenceContinuationTail(
        string CalendarHref,
        string EntityUid,
        string RecurrenceIdentity);

    private sealed class CalendarOccurrenceDeadlineException : Exception;
}

public sealed record CalendarOccurrenceQuerySuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarOccurrenceSnapshotResult> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarOccurrenceQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null);

public sealed record CalendarOccurrenceSnapshotResult(
    [property: JsonPropertyName("snapshot")] CalendarSnapshotResult Snapshot,
    [property: JsonPropertyName("recurrenceIdentity")] CalendarRecurrenceIdentityResult RecurrenceIdentity,
    [property: JsonPropertyName("timing")] CalendarOccurrenceTimingResult Timing)
{
    internal static CalendarOccurrenceSnapshotResult FromSnapshot(CalendarOccurrenceSnapshot snapshot) => new(
        CalendarSnapshotResult.FromSnapshot(snapshot.Snapshot),
        new CalendarRecurrenceIdentityResult(CalendarTemporalResult.FromValue(snapshot.RecurrenceIdentity)),
        CalendarOccurrenceTimingResult.FromTiming(snapshot.Timing));
}

public sealed record CalendarRecurrenceIdentityResult(
    [property: JsonPropertyName("value")] CalendarTemporalResult Value);

public sealed record CalendarTemporalResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("timeZoneId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZoneId = null)
{
    internal static CalendarTemporalResult FromValue(CalendarTemporalValue value) => new(
        value.Kind switch
        {
            CalendarTemporalKind.Date => "date",
            CalendarTemporalKind.FloatingDateTime => "floatingDateTime",
            CalendarTemporalKind.UtcDateTime => "utcDateTime",
            CalendarTemporalKind.ZonedDateTime => "zonedDateTime",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, null)
        },
        value.Value,
        value.TimeZoneId);
}

public sealed record CalendarOccurrenceTimingResult(
    [property: JsonPropertyName("sourceStart")] CalendarTemporalResult SourceStart,
    [property: JsonPropertyName("effectiveStart")] CalendarTemporalResult EffectiveStart,
    [property: JsonPropertyName("sourceEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? SourceEnd = null,
    [property: JsonPropertyName("effectiveEnd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EffectiveEnd = null,
    [property: JsonPropertyName("sourceDuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceDuration = null,
    [property: JsonPropertyName("effectiveDuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveDuration = null,
    [property: JsonPropertyName("evaluatedStartUtc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EvaluatedStartUtc = null,
    [property: JsonPropertyName("evaluatedEndUtc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? EvaluatedEndUtc = null,
    [property: JsonPropertyName("evaluationTimeZone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EvaluationTimeZone = null)
{
    internal static CalendarOccurrenceTimingResult FromTiming(CalendarOccurrenceTiming timing) => new(
        CalendarTemporalResult.FromValue(timing.SourceStart),
        CalendarTemporalResult.FromValue(timing.EffectiveStart),
        timing.SourceEnd is null ? null : CalendarTemporalResult.FromValue(timing.SourceEnd),
        timing.EffectiveEnd is null ? null : CalendarTemporalResult.FromValue(timing.EffectiveEnd),
        timing.SourceDuration,
        timing.EffectiveDuration,
        timing.EvaluatedStartUtc is null ? null : CalendarTemporalResult.FromValue(timing.EvaluatedStartUtc),
        timing.EvaluatedEndUtc is null ? null : CalendarTemporalResult.FromValue(timing.EvaluatedEndUtc),
        timing.EvaluationTimeZone);
}

internal readonly record struct CalendarOccurrenceContinuation(
    string EffectiveStartUtc,
    string CalendarHref,
    string EntityUid,
    string RecurrenceIdentity)
{
    public static CalendarOccurrenceContinuation FromSnapshot(CalendarOccurrenceSnapshot snapshot) => new(
        snapshot.Timing.EvaluatedStartUtc!.Value,
        snapshot.Snapshot.CalendarHref,
        snapshot.Snapshot.Projection.EntityUid!,
        snapshot.RecurrenceIdentity.GetCanonicalSortKey());
}
