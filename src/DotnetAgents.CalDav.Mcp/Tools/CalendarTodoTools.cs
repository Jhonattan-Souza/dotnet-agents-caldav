using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Compact semantic To-do reads for routine agent task-list questions.</summary>
[McpServerToolType]
public sealed class CalendarTodoTools
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;
    internal const int MaximumArgumentBytes = CalendarQueryToolSupport.MaximumArgumentBytes;
    internal const int MaximumStructuredResultBytes = 64 * 1024;
    private static readonly string[] RoutineProjection = ["summary", "due", "priority", "categories"];
    private static readonly string[] AllowedProjection =
        ["summary", "status", "completedAt", "percentComplete", "due", "priority", "categories", "start", "description", "recurrence"];
    private readonly ICalendarService _calendarService;
    private readonly CalendarEntityCursorProtector _cursorProtector;
    private readonly TimeProvider _timeProvider;

    public CalendarTodoTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<CalendarEntityCursorProtector>(),
            services.GetRequiredService<TimeProvider>())
    {
    }

    internal CalendarTodoTools(
        ICalendarService calendarService,
        CalendarEntityCursorProtector cursorProtector,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _cursorProtector = cursorProtector;
        _timeProvider = timeProvider;
    }

    [McpServerTool(
        Name = "todos.query",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarTodoQuerySuccessResult)),
     Description("Query compact normalized To-do results in an explicitly selected Calendar Scope; use this for routine open To-do lists.")]
    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) => QueryRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> QueryRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is not null && JsonSerializer.SerializeToUtf8Bytes(arguments).Length > MaximumArgumentBytes)
            return EnsureBoundedResult(Error("payload_too_large", "limitsAndAdmission", "The To-do query arguments exceed the safe payload limit.", false, "admissionAndPayload"));
        if (!TryDeserializeRawArguments(arguments, out var parsed))
            return EnsureBoundedResult(CreateInputGuardError(false));
        return await QueryCoreAsync(
            parsed.Scope,
            parsed.CompletionStates,
            cancellationToken,
            parsed.From,
            parsed.To,
            parsed.EvaluationTimeZone,
            parsed.DueFrom,
            parsed.DueTo,
            parsed.Projection,
            parsed.PageSize,
            parsed.Cursor,
            arguments).ConfigureAwait(false);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => Error(
        payloadTooLarge ? "payload_too_large" : "invalid_input",
        payloadTooLarge ? "limitsAndAdmission" : "input",
        payloadTooLarge ? "The To-do query arguments exceed the safe payload limit." : "The To-do query input is invalid.",
        false,
        payloadTooLarge ? "admissionAndPayload" : "schemaLexicalDiscriminator");

    internal async Task<CallToolResult> QueryCoreAsync(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string>? completionStates,
        CancellationToken cancellationToken,
        CalendarEntityUtcArgument? from = null,
        CalendarEntityUtcArgument? to = null,
        string? evaluationTimeZone = null,
        CalendarEntityUtcArgument? dueFrom = null,
        CalendarEntityUtcArgument? dueTo = null,
        IReadOnlyList<string>? projection = null,
        int? pageSize = null,
        string? cursor = null,
        IDictionary<string, JsonElement>? rawArguments = null)
    {
        if (MeasureArguments(scope, completionStates, from, to, evaluationTimeZone, dueFrom, dueTo, projection, pageSize, cursor, rawArguments) > MaximumArgumentBytes)
            return EnsureBoundedResult(CreateInputGuardError(true));
        if (!TryPrepareQuery(
                scope,
                completionStates,
                from,
                to,
                evaluationTimeZone,
                dueFrom,
                dueTo,
                projection,
                pageSize,
                cursor,
                rawArguments,
                out var query,
                out var effectivePageSize,
                out var effectiveProjection))
            return EnsureBoundedResult(CreateInputGuardError(false));

        var queryContext = CreateQueryContext(query, effectivePageSize, effectiveProjection);
        CalendarEntityContinuation? continuation = null;
        if (cursor is not null)
        {
            if (!_cursorProtector.TryUnprotect(cursor, queryContext, out var decoded, out _))
                return EnsureBoundedResult(Error("invalid_input", "input", "The continuation cursor is invalid or expired.", false, "schemaLexicalDiscriminator"));
            if (!TryReadPosition(decoded.ResourceHref, out _))
                return EnsureBoundedResult(Error("invalid_input", "input", "The continuation cursor is invalid or expired.", false, "schemaLexicalDiscriminator"));
            continuation = decoded;
        }

        var result = await ExecuteQuerySafelyAsync(query, continuation, queryContext, effectivePageSize, effectiveProjection, cancellationToken).ConfigureAwait(false);
        return EnsureBoundedResult(result);
    }

    private async Task<CallToolResult> ExecuteQuerySafelyAsync(
        CalendarTodoQuery query,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int pageSize,
        IReadOnlyList<string> projection,
        CancellationToken cancellationToken)
    {
        var deadlineAt = _timeProvider.GetUtcNow().AddSeconds(30);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _calendarService.QueryTodosAsync(query, linked.Token).ConfigureAwait(false);
            ThrowIfDeadlineExpired(deadlineAt, linked.Token);
            return result.Code == CalendarTodoQueryCode.Success
                ? CreatePage(result, continuation, queryContext, pageSize, projection, deadlineAt, linked.Token)
                : MapQueryFailure(result);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The To-do query exceeded the Calendar limit.", false, "admissionAndPayload",
                new CalendarEntityExecutionLimits(CalendarCount: exception.CalendarCount));
        }
        catch (HttpRequestException exception)
        {
            return MapHttpFailure(exception.StatusCode);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The To-do query exhausted the elapsed_time execution budget.", false, "execution");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("upstream_unavailable", "upstream", "The To-do query is temporarily unavailable.", true, "execution");
        }
        catch (TimeoutException)
        {
            return Error("upstream_unavailable", "upstream", "The To-do query is temporarily unavailable.", true, "execution");
        }
        catch (XmlException)
        {
            return ProtocolError();
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return ProtocolError();
        }
    }

    private CallToolResult CreatePage(
        CalendarTodoQueryResult result,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int pageSize,
        IReadOnlyList<string> projection,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken)
    {
        var eligible = result.Items
            .Where(item => continuation is null || IsAfter(item, continuation.Value))
            .ToArray();
        var page = new List<CalendarTodoCompactItemResult>(Math.Min(pageSize, eligible.Length));
        for (var index = 0; index < eligible.Length && page.Count < pageSize; index++)
        {
            ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
            page.Add(CalendarTodoCompactItemResult.FromItem(eligible[index], projection));
            var hasMore = index + 1 < eligible.Length;
            if (!TryCreateCursor(hasMore, queryContext, eligible[index], out var candidateCursor))
                return CursorPayloadError();
            var candidate = CreateSuccess(page, result.Diagnostics, result.ExcludedIndeterminateCount, hasMore ? candidateCursor : null);
            if (CalendarQueryToolSupport.MeasureResult(candidate) <= MaximumStructuredResultBytes)
                continue;
            page.RemoveAt(page.Count - 1);
            if (page.Count == 0)
                return Error("payload_too_large", "limitsAndAdmission", "One To-do cannot fit in a result page.", false, "admissionAndPayload");
            break;
        }

        ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
        string? nextCursor = null;
        if (page.Count < eligible.Length && !_cursorProtector.TryProtect(
                queryContext,
                eligible[page.Count - 1].Snapshot.CalendarHref,
                CreatePosition(eligible[page.Count - 1]),
                out nextCursor))
            return CursorPayloadError();
        return CreateSuccess(page, result.Diagnostics, result.ExcludedIndeterminateCount, nextCursor);
    }

    private bool TryCreateCursor(bool required, string queryContext, CalendarTodoQueryItem item, out string? cursor)
    {
        cursor = null;
        return !required || _cursorProtector.TryProtect(queryContext, item.Snapshot.CalendarHref, CreatePosition(item), out cursor);
    }

    private static bool IsAfter(CalendarTodoQueryItem item, CalendarEntityContinuation continuation)
    {
        return TryReadPosition(continuation.ResourceHref, out var position)
            && Compare(ToPosition(item), position) > 0;
    }

    private static string CreatePosition(CalendarTodoQueryItem item) =>
        JsonSerializer.Serialize(ToPosition(item));

    private static TodoCursorPosition ToPosition(CalendarTodoQueryItem item) => new(
        item.EvaluatedDueUtc,
        item.EvaluatedStartUtc,
        item.Snapshot.CalendarHref,
        item.Snapshot.Projection.EntityUid ?? string.Empty,
        item.Snapshot.ResourceHref,
        item.Occurrence?.RecurrenceIdentity.GetCanonicalSortKey() ?? string.Empty);

    private static bool TryReadPosition(string encoded, out TodoCursorPosition position)
    {
        try
        {
            position = JsonSerializer.Deserialize<TodoCursorPosition>(encoded)!;
            return position is not null
                && position.CalendarHref is not null
                && position.ResourceHref is not null
                && position.EntityUid is not null
                && position.RecurrenceIdentity is not null;
        }
        catch (JsonException)
        {
            position = null!;
            return false;
        }
    }

    private static int Compare(TodoCursorPosition left, TodoCursorPosition right)
    {
        var comparison = CompareOptional(left.DueUtc, right.DueUtc);
        if (comparison != 0)
            return comparison;
        comparison = CompareOptional(left.StartUtc, right.StartUtc);
        if (comparison != 0)
            return comparison;
        comparison = string.CompareOrdinal(left.CalendarHref, right.CalendarHref);
        if (comparison != 0)
            return comparison;
        comparison = string.CompareOrdinal(left.EntityUid, right.EntityUid);
        if (comparison != 0)
            return comparison;
        comparison = string.CompareOrdinal(left.ResourceHref, right.ResourceHref);
        return comparison != 0 ? comparison : string.CompareOrdinal(left.RecurrenceIdentity, right.RecurrenceIdentity);
    }

    private static int CompareOptional(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right is null ? 0 : 1;
        if (right is null)
            return -1;
        return left.Value.CompareTo(right.Value);
    }

    private static CallToolResult CreateSuccess(
        IReadOnlyList<CalendarTodoCompactItemResult> items,
        IReadOnlyList<CalendarResourceDiagnostic> diagnostics,
        int excludedIndeterminateCount,
        string? nextCursor) => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarTodoQuerySuccessResult(
                "success",
                items,
                diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray(),
                excludedIndeterminateCount,
                new CalendarPagination("non_snapshot", nextCursor))),
            Content = [new TextContentBlock { Text = "Compact To-do query completed." }]
        };

    private static CallToolResult MapQueryFailure(CalendarTodoQueryResult result) => result.Code switch
    {
        CalendarTodoQueryCode.InvalidInput => Error("invalid_input", "input", "The To-do query input is invalid.", false, "schemaLexicalDiscriminator"),
        CalendarTodoQueryCode.UnsafeScope => Error("invalid_input", "input", "The To-do query Calendar href is unsafe.", false, "originScopeAuthorization"),
        CalendarTodoQueryCode.NotFound => Error("not_found", "selection", "No matching authorized Calendar was found.", false, "selectionDiscoveryCapability", candidates: Candidates(result)),
        CalendarTodoQueryCode.Ambiguous => Error("ambiguous", "selection", "The Calendar selector matched more than one authorized Calendar.", false, "selectionDiscoveryCapability", candidates: Candidates(result)),
        CalendarTodoQueryCode.OutsideScope => Error("outside_scope", "selection", "The selected Calendar is outside the configured Calendar Scope.", false, "originScopeAuthorization", candidates: Candidates(result)),
        CalendarTodoQueryCode.UnsupportedCapability => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
        CalendarTodoQueryCode.ConcurrencyUnavailable => Error("concurrency_unavailable", "state", "A query candidate did not provide a strong Entity Tag.", false, "targetRevision"),
        CalendarTodoQueryCode.LimitExhausted => Error("limit_exhausted", "limitsAndAdmission", "The To-do query exhausted its execution budget.", false, "execution", ToLimits(result.Limits)),
        CalendarTodoQueryCode.PayloadTooLarge => Error("payload_too_large", "limitsAndAdmission", "A Calendar Object Resource exceeds the safe payload limit.", false, "admissionAndPayload", ToLimits(result.Limits)),
        CalendarTodoQueryCode.TemporalUnresolved => Error("temporal_unresolved", "capabilityAndProjection", "Temporal evaluation could not be resolved.", false, "completeResourceSemantics"),
        CalendarTodoQueryCode.RecurrenceUnevaluable => Error("recurrence_unevaluable", "capabilityAndProjection", "The Recurrence Set could not be evaluated.", false, "completeResourceSemantics"),
        _ => ProtocolError()
    };

    private static IReadOnlyList<CalendarAuthorizedCandidateResult>? Candidates(CalendarTodoQueryResult result) =>
        result.AuthorizedCandidates.Count == 0
            ? null
            : result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray();

    private static CalendarEntityExecutionLimits? ToLimits(CalendarEntityQueryExecutionLimits? limits) => limits is null
        ? null
        : new CalendarEntityExecutionLimits(limits.ResourcesInspected, OccurrenceCount: limits.OccurrenceCount, ByteCount: limits.ByteCount);

    private static CallToolResult MapHttpFailure(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Error("upstream_unauthorized", "upstream", "The To-do query was not authorized.", false, "execution"),
        HttpStatusCode.Forbidden => Error("upstream_forbidden", "upstream", "The To-do query was forbidden.", false, "execution"),
        HttpStatusCode.TooManyRequests => Error("upstream_rate_limited", "upstream", "The To-do query is rate limited.", true, "execution"),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.RequestEntityTooLarge => Error("payload_too_large", "limitsAndAdmission", "The To-do query response is too large.", false, "admissionAndPayload"),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => Error("conflict", "state", "The To-do query encountered an upstream state conflict.", false, "execution"),
        null => Error("upstream_unavailable", "upstream", "The To-do query is temporarily unavailable.", true, "execution"),
        >= HttpStatusCode.InternalServerError => Error("upstream_unavailable", "upstream", "The To-do query is temporarily unavailable.", true, "execution"),
        _ => ProtocolError()
    };

    private static CallToolResult ProtocolError() => Error("upstream_protocol_error", "upstream", "The To-do query returned an invalid response.", false, "execution");

    private static CallToolResult CursorPayloadError() => Error("payload_too_large", "limitsAndAdmission", "The continuation cursor exceeds the safe payload limit.", false, "admissionAndPayload");

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
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarTodoQueryErrorResult(
                code,
                category,
                message,
                retryable,
                phase,
                limits,
                candidates)),
            Content = [new TextContentBlock { Text = "Compact To-do query failed." }]
        };

    private static CallToolResult EnsureBoundedResult(CallToolResult result)
    {
        if (CalendarQueryToolSupport.MeasureHumanReadableResult(result) > CalendarQueryToolSupport.MaximumHumanReadableBytes)
            return Error("payload_too_large", "limitsAndAdmission", "The To-do query human-readable result exceeds the safe payload limit.", false, "admissionAndPayload");
        return CalendarQueryToolSupport.MeasureResult(result) <= MaximumStructuredResultBytes
            ? result
            : Error("payload_too_large", "limitsAndAdmission", "The To-do query result exceeds the safe payload limit.", false, "admissionAndPayload");
    }

    private void ThrowIfDeadlineExpired(DateTimeOffset deadlineAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() >= deadlineAt)
            throw new TimeoutException();
    }

    private static bool TryPrepareQuery(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string>? completionStates,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        string? evaluationTimeZone,
        CalendarEntityUtcArgument? dueFrom,
        CalendarEntityUtcArgument? dueTo,
        IReadOnlyList<string>? projection,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments,
        out CalendarTodoQuery query,
        out int effectivePageSize,
        out IReadOnlyList<string> effectiveProjection)
    {
        query = null!;
        effectivePageSize = pageSize ?? DefaultPageSize;
        effectiveProjection = [];
        if (!HasFrozenRawShape(rawArguments))
            return false;
        if (effectivePageSize is < 1 or > MaximumPageSize)
            return false;
        if (cursor is { Length: > CalendarEntityCursorProtector.MaximumCursorCharacters })
            return false;
        if (!CalendarQueryToolSupport.TryCreateScope(scope, out var domainScope))
            return false;
        if (domainScope.Mode == CalendarEntityScopeMode.Default)
            return false;
        if (!TryCreateQueryParts(
                completionStates,
                from,
                to,
                dueFrom,
                dueTo,
                projection,
                out var states,
                out var domainFrom,
                out var domainTo,
                out var domainDueFrom,
                out var domainDueTo,
                out effectiveProjection))
            return false;
        query = new CalendarTodoQuery(domainScope, states, domainFrom, domainTo, evaluationTimeZone, domainDueFrom, domainDueTo);
        return true;
    }

    private static bool TryCreateQueryParts(
        IReadOnlyList<string>? completionStates,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        CalendarEntityUtcArgument? dueFrom,
        CalendarEntityUtcArgument? dueTo,
        IReadOnlyList<string>? projection,
        out IReadOnlyList<CalendarTodoCompletionState> states,
        out DateTimeOffset? domainFrom,
        out DateTimeOffset? domainTo,
        out DateTimeOffset? domainDueFrom,
        out DateTimeOffset? domainDueTo,
        out IReadOnlyList<string> effectiveProjection)
    {
        states = [];
        domainFrom = null;
        domainTo = null;
        domainDueFrom = null;
        domainDueTo = null;
        effectiveProjection = [];
        if (!TryCreateStates(completionStates, out states))
            return false;
        if (!TryCreateWindow(from, to, out domainFrom, out domainTo))
            return false;
        if (!TryCreateWindow(dueFrom, dueTo, out domainDueFrom, out domainDueTo))
            return false;
        return TryCreateProjection(projection, out effectiveProjection);
    }

    private static bool TryCreateStates(IReadOnlyList<string>? values, out IReadOnlyList<CalendarTodoCompletionState> states)
    {
        states = [CalendarTodoCompletionState.Open];
        if (values is null)
            return true;
        if (values.Count == 0 || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            return false;
        var parsed = new List<CalendarTodoCompletionState>(values.Count);
        foreach (var value in values)
        {
            if (!TryParseState(value, out var state))
                return false;
            parsed.Add(state);
        }
        states = parsed;
        return true;
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

    private static bool TryCreateProjection(IReadOnlyList<string>? values, out IReadOnlyList<string> projection)
    {
        projection = values is null ? RoutineProjection : values;
        return projection.Distinct(StringComparer.Ordinal).Count() == projection.Count
            && projection.All(value => AllowedProjection.Contains(value, StringComparer.Ordinal));
    }

    private static bool TryCreateWindow(
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        out DateTimeOffset? domainFrom,
        out DateTimeOffset? domainTo)
    {
        domainFrom = null;
        domainTo = null;
        if (from is null || to is null)
            return from is null && to is null;
        return CalendarQueryToolSupport.TryParseUtc(from, out domainFrom)
            && CalendarQueryToolSupport.TryParseUtc(to, out domainTo)
            && domainFrom is not null
            && domainTo is not null
            && domainTo > domainFrom
            && domainTo - domainFrom <= TimeSpan.FromDays(366);
    }

    private static int MeasureArguments(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string>? completionStates,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        string? evaluationTimeZone,
        CalendarEntityUtcArgument? dueFrom,
        CalendarEntityUtcArgument? dueTo,
        IReadOnlyList<string>? projection,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments) => CalendarQueryToolSupport.MeasureArguments(
        rawArguments,
        new { scope, completionStates, from, to, evaluationTimeZone, dueFrom, dueTo, projection, pageSize, cursor });

    private static bool TryDeserializeRawArguments(
        IDictionary<string, JsonElement>? arguments,
        out CalendarTodoRawArguments parsed)
    {
        parsed = null!;
        if (arguments is null
            || !arguments.TryGetValue("scope", out var scopeElement)
            || !HasFrozenRawShape(arguments))
            return false;
        try
        {
            var scope = scopeElement.Deserialize<CalendarEntityScopeArgument>();
            if (scope is null)
                return false;
            parsed = new(
                scope,
                DeserializeOptional<string[]>(arguments, "completionStates"),
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "from"),
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "to"),
                DeserializeOptional<string>(arguments, "evaluationTimeZone"),
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "dueFrom"),
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "dueTo"),
                DeserializeOptional<string[]>(arguments, "projection"),
                DeserializeOptional<int?>(arguments, "pageSize"),
                DeserializeOptional<string>(arguments, "cursor"));
            return !HasPresentNull(arguments);
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

    private static T? DeserializeOptional<T>(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var element) ? element.Deserialize<T>() : default;

    private static bool HasPresentNull(IDictionary<string, JsonElement> arguments) =>
        arguments.Any(pair => pair.Value.ValueKind == JsonValueKind.Null);

    internal static bool HasFrozenRawShape(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return true;
        var allowed = new[] { "scope", "completionStates", "from", "to", "evaluationTimeZone", "dueFrom", "dueTo", "projection", "pageSize", "cursor" };
        return arguments.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal))
            && arguments.TryGetValue("scope", out var scope)
            && CalendarQueryToolSupport.HasScopeShape(scope)
            && HasTemporalIfPresent(arguments, "from")
            && HasTemporalIfPresent(arguments, "to")
            && HasTemporalIfPresent(arguments, "dueFrom")
            && HasTemporalIfPresent(arguments, "dueTo");
    }

    private static bool HasTemporalIfPresent(IDictionary<string, JsonElement> arguments, string name) =>
        !arguments.TryGetValue(name, out var temporal) || CalendarQueryToolSupport.HasTemporalShape(temporal);

    private static string CreateQueryContext(
        CalendarTodoQuery query,
        int pageSize,
        IReadOnlyList<string> projection) => JsonSerializer.Serialize(new
        {
            scope = query.Scope,
            states = query.CompletionStates,
            query.From,
            query.To,
            query.EvaluationTimeZone,
            query.DueFrom,
            query.DueTo,
            pageSize,
            projection
        });

    private sealed record CalendarTodoRawArguments(
        CalendarEntityScopeArgument Scope,
        IReadOnlyList<string>? CompletionStates,
        CalendarEntityUtcArgument? From,
        CalendarEntityUtcArgument? To,
        string? EvaluationTimeZone,
        CalendarEntityUtcArgument? DueFrom,
        CalendarEntityUtcArgument? DueTo,
        IReadOnlyList<string>? Projection,
        int? PageSize,
        string? Cursor);

    private sealed record TodoCursorPosition(
        DateTimeOffset? DueUtc,
        DateTimeOffset? StartUtc,
        string CalendarHref,
        string EntityUid,
        string ResourceHref,
        string RecurrenceIdentity);
}

public sealed record CalendarTodoQuerySuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("items")] IReadOnlyList<CalendarTodoCompactItemResult> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics,
    [property: JsonPropertyName("excludedIndeterminateCount")] int ExcludedIndeterminateCount,
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarTodoQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null);

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
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CalendarDiagnosticResult> Diagnostics)
{
    internal static CalendarTodoCompactItemResult FromItem(
        CalendarTodoQueryItem item,
        IReadOnlyList<string> projection)
    {
        var fields = item.Occurrence is { } occurrence
            ? CalendarResourceSemanticProjector.TodoForOccurrence(item.Snapshot, occurrence.RecurrenceIdentity)
            : CalendarResourceSemanticProjector.Todo(item.Snapshot);
        return new(
            ToWire(item.ResultKind),
            item.Snapshot.Projection.EntityUid,
            ToWire(item.Completion.State),
            StringField(fields, projection, "summary"),
            JsonField(fields, projection, "status"),
            projection.Contains("completedAt", StringComparer.Ordinal)
                ? item.Occurrence is { } occurrenceForCompletedAt
                    ? CalendarResourceSemanticProjector.TodoCompletedAtForOccurrence(item.Snapshot, occurrenceForCompletedAt.RecurrenceIdentity)
                    : CalendarResourceSemanticProjector.TodoCompletedAt(item.Snapshot)
                : null,
            IntegerField(fields, projection, "percentComplete"),
            TemporalField(fields, projection, "due", item.Due, item.EvaluatedDueUtc),
            IntegerField(fields, projection, "priority"),
            CategoriesField(fields, projection),
            TemporalField(fields, projection, "start", item.Start, item.EvaluatedStartUtc),
            StringField(fields, projection, "description"),
            RecurrenceField(fields, projection),
            CreateTarget(item),
            item.Snapshot.Diagnostics
                .Concat(item.Completion.Diagnostics)
                .Select(CalendarDiagnosticResult.FromResourceDiagnostic)
                .ToArray());
    }

    private static CalendarTodoCompletionTargetResult CreateTarget(CalendarTodoQueryItem item)
    {
        var entityRevision = item.Snapshot.SemanticMutationAvailable
            ? new CalendarEntityRevisionResult(
                item.Snapshot.ResourceHref,
                item.Snapshot.Projection.EntityUid!,
                "todo",
                item.Snapshot.EntityTag)
            : null;
        if (item.ResultKind == CalendarTodoQueryResultKind.Unresolved)
            return new("unavailable", null, null, new CalendarResourceRevisionResult(item.Snapshot.ResourceHref, item.Snapshot.EntityTag));
        if (item.RequiresOccurrenceTarget)
            return new("occurrence_required", entityRevision, null, null);
        var recurrenceIdentity = item.IsRecurring && item.Occurrence?.RecurrenceIdentity is { } identity
            ? new CalendarTemporalResult(identity.Kind switch
            {
                CalendarTemporalKind.Date => "date",
                CalendarTemporalKind.FloatingDateTime => "floatingDateTime",
                CalendarTemporalKind.UtcDateTime => "utcDateTime",
                CalendarTemporalKind.ZonedDateTime => "zonedDateTime",
                _ => "unknown"
            }, identity.Value, identity.TimeZoneId)
            : null;
        return new("direct", entityRevision, recurrenceIdentity, null);
    }

    private static string ToWire(CalendarTodoQueryResultKind kind) => kind switch
    {
        CalendarTodoQueryResultKind.Entity => "entity",
        CalendarTodoQueryResultKind.Occurrence => "occurrence",
        _ => "unresolved"
    };

    private static string ToWire(CalendarTodoCompletionState state) => state switch
    {
        CalendarTodoCompletionState.Open => "open",
        CalendarTodoCompletionState.Completed => "completed",
        CalendarTodoCompletionState.Cancelled => "cancelled",
        _ => "indeterminate"
    };

    private static string? StringField(JsonElement fields, IReadOnlyList<string> projection, string name) =>
        projection.Contains(name, StringComparer.Ordinal)
        && fields.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntegerField(JsonElement fields, IReadOnlyList<string> projection, string name) =>
        projection.Contains(name, StringComparer.Ordinal)
        && fields.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var integer)
            ? integer
            : null;

    private static JsonElement? JsonField(JsonElement fields, IReadOnlyList<string> projection, string name) =>
        projection.Contains(name, StringComparer.Ordinal)
        && fields.TryGetProperty(name, out var value)
            ? value
            : null;

    private static IReadOnlyList<string>? CategoriesField(JsonElement fields, IReadOnlyList<string> projection)
    {
        if (!projection.Contains("categories", StringComparer.Ordinal))
            return null;
        return fields.TryGetProperty("categories", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
    }

    private static JsonElement? RecurrenceField(JsonElement fields, IReadOnlyList<string> projection) =>
        projection.Contains("recurrence", StringComparer.Ordinal)
        && fields.TryGetProperty("recurrenceSet", out var value)
            ? value
            : null;

    private static JsonElement? TemporalField(
        JsonElement fields,
        IReadOnlyList<string> projection,
        string name,
        CalendarTemporalValue? source,
        DateTimeOffset? evaluatedUtc)
    {
        if (!projection.Contains(name, StringComparer.Ordinal)
            || !fields.TryGetProperty(name, out var original))
            return null;
        if (source is null || evaluatedUtc is null)
            return original;
        var node = new JsonObject
        {
            ["source"] = JsonNode.Parse(original.GetRawText()),
            ["evaluatedUtc"] = new JsonObject
            {
                ["kind"] = "utcDateTime",
                ["value"] = evaluatedUtc.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            }
        };
        return JsonSerializer.SerializeToElement(node);
    }
}

public sealed record CalendarTodoCompletionTargetResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("entityRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityRevisionResult? EntityRevision,
    [property: JsonPropertyName("recurrenceIdentity"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarTemporalResult? RecurrenceIdentity,
    [property: JsonPropertyName("resourceRevision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarResourceRevisionResult? ResourceRevision);
