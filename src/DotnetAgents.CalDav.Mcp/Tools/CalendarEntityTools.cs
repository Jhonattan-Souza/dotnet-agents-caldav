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

/// <summary>Bounded persisted Calendar Entity queries.</summary>
[McpServerToolType]
public sealed class CalendarEntityTools
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;
    internal const int MaximumArgumentBytes = 256 * 1024;
    internal const int MaximumHumanReadableBytes = 64 * 1024;
    internal const int MaximumStructuredResultBytes = 4 * 1024 * 1024;
    private readonly ICalendarService _calendarService;
    private readonly CalendarEntityCursorProtector _cursorProtector;
    private readonly TimeProvider _timeProvider;

    public CalendarEntityTools(IServiceProvider services)
        : this(
            services.GetRequiredService<ICalendarService>(),
            services.GetRequiredService<CalendarEntityCursorProtector>(),
            services.GetRequiredService<TimeProvider>())
    {
    }

    internal CalendarEntityTools(
        ICalendarService calendarService,
        CalendarEntityCursorProtector cursorProtector,
        TimeProvider timeProvider)
    {
        _calendarService = calendarService;
        _cursorProtector = cursorProtector;
        _timeProvider = timeProvider;
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
     Description("Query persisted Event or To-do resource snapshots within an explicit Calendar Scope.")]
    public Task<CallToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) =>
        QueryRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal async Task<CallToolResult> QueryRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (arguments is not null && JsonSerializer.SerializeToUtf8Bytes(arguments).Length > MaximumArgumentBytes)
            return EnsureBoundedResult(Error("payload_too_large", "limitsAndAdmission", "The query arguments exceed the safe payload limit.", false, "admissionAndPayload"));
        if (!TryDeserializeRawArguments(arguments, out var parsed))
            return EnsureBoundedResult(Error("invalid_input", "input", "The Calendar Entity query input is invalid.", false, "schemaLexicalDiscriminator"));
        return await QueryCoreAsync(
            parsed.Scope,
            parsed.EntityKinds,
            cancellationToken,
            parsed.From,
            parsed.To,
            parsed.PageSize,
            parsed.Cursor,
            arguments);
    }

    internal static CallToolResult CreateInputGuardError(bool payloadTooLarge) => EnsureBoundedResult(
        payloadTooLarge
            ? Error("payload_too_large", "limitsAndAdmission", "The query arguments exceed the safe payload limit.", false, "admissionAndPayload")
            : Error("invalid_input", "input", "The Calendar Entity query input is invalid.", false, "schemaLexicalDiscriminator"));

    internal async Task<CallToolResult> QueryCoreAsync(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string> entityKinds,
        CancellationToken cancellationToken,
        CalendarEntityUtcArgument? from = null,
        CalendarEntityUtcArgument? to = null,
        int? pageSize = null,
        string? cursor = null,
        IDictionary<string, JsonElement>? rawArguments = null)
    {
        if (MeasureArguments(scope, entityKinds, from, to, pageSize, cursor, rawArguments) > MaximumArgumentBytes)
            return EnsureBoundedResult(Error("payload_too_large", "limitsAndAdmission", "The query arguments exceed the safe payload limit.", false, "admissionAndPayload"));
        if (!TryPrepareQuery(
                scope,
                entityKinds,
                from,
                to,
                pageSize,
                cursor,
                rawArguments,
                out var query,
                out var effectivePageSize))
        {
            return EnsureBoundedResult(Error("invalid_input", "input", "The Calendar Entity query input is invalid.", false, "schemaLexicalDiscriminator"));
        }

        var queryContext = CreateQueryContext(query, effectivePageSize);
        CalendarEntityContinuation? continuation = null;
        if (cursor is not null)
        {
            if (!_cursorProtector.TryUnprotect(cursor, queryContext, out var decoded, out _))
                return EnsureBoundedResult(Error("invalid_input", "input", "The continuation cursor is invalid or expired.", false, "schemaLexicalDiscriminator"));
            continuation = decoded;
        }

        var result = await ExecuteQuerySafelyAsync(
            query,
            continuation,
            queryContext,
            effectivePageSize,
            cancellationToken);
        return EnsureBoundedResult(result);
    }

    private static bool TryPrepareQuery(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string> entityKinds,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments,
        out CalendarEntityQuery query,
        out int effectivePageSize)
    {
        query = null!;
        effectivePageSize = pageSize ?? DefaultPageSize;
        return HasFrozenRawShape(rawArguments)
            && TryCreateQuery(scope, entityKinds, from, to, out query)
            && effectivePageSize is >= 1 and <= MaximumPageSize
            && (cursor is null || cursor.Length <= CalendarEntityCursorProtector.MaximumCursorCharacters);
    }

    private async Task<CallToolResult> ExecuteQuerySafelyAsync(
        CalendarEntityQuery query,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int effectivePageSize,
        CancellationToken cancellationToken)
    {
        var deadlineAt = _timeProvider.GetUtcNow().AddSeconds(30);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await _calendarService.QueryEntitiesAsync(query, linked.Token);
            ThrowIfDeadlineExpired(deadlineAt, linked.Token);
            if (result.Code != CalendarEntityQueryCode.Success)
                return MapQueryFailure(result);
            return CreatePage(result, continuation, queryContext, effectivePageSize, deadlineAt, linked.Token);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Error("limit_exhausted", "limitsAndAdmission", "The query exceeded the Calendar limit.", false,
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
            return Error("upstream_unavailable", "upstream", "The Calendar Entity query is temporarily unavailable.", true, "execution");
        }
        catch (TimeoutException)
        {
            return Error("upstream_unavailable", "upstream", "The Calendar Entity query is temporarily unavailable.", true, "execution");
        }
        catch (XmlException)
        {
            return ProtocolError();
        }
        catch (CalendarDiscoveryProtocolException)
        {
            return ProtocolError();
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Error("unsupported_capability", "capabilityAndProjection",
                "The server does not support the required Calendar query capability.", false,
                "selectionDiscoveryCapability");
        }
        catch (CalendarEntityDeadlineException)
        {
            return DeadlineError();
        }
    }

    private CallToolResult CreatePage(
        CalendarEntityQueryResult result,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int pageSize,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken)
    {
        var eligible = result.Items.Where(item => continuation is null || IsAfter(item, continuation.Value)).ToArray();
        var page = new List<CalendarSnapshotResult>(Math.Min(pageSize, eligible.Length));
        for (var index = 0; index < eligible.Length && page.Count < pageSize; index++)
        {
            ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
            page.Add(CalendarSnapshotResult.FromSnapshot(eligible[index]));
            var hasMore = index + 1 < eligible.Length;
            if (!TryCreateCursor(hasMore, queryContext, eligible[index], out var candidateCursor))
                return CursorPayloadError();
            var candidate = CreateSuccess(page, result.Diagnostics, hasMore ? candidateCursor : null);
            if (MeasureResult(candidate) <= MaximumStructuredResultBytes)
                continue;

            page.RemoveAt(page.Count - 1);
            if (page.Count == 0)
                return Error("payload_too_large", "limitsAndAdmission", "One Calendar Entity cannot fit in a result page.", false, "admissionAndPayload");
            break;
        }

        ThrowIfDeadlineExpired(deadlineAt, cancellationToken);
        string? nextCursor = null;
        if (page.Count < eligible.Length && !_cursorProtector.TryProtect(
                queryContext, page[^1].Calendar.Href, page[^1].ResourceRevision.Href, out nextCursor))
            return CursorPayloadError();
        return CreateSuccess(page, result.Diagnostics, nextCursor);
    }

    private bool TryCreateCursor(
        bool required,
        string queryContext,
        CalendarResourceSnapshot item,
        out string? cursor)
    {
        cursor = null;
        return !required || _cursorProtector.TryProtect(
            queryContext,
            item.CalendarHref,
            item.ResourceHref,
            out cursor);
    }

    private void ThrowIfDeadlineExpired(DateTimeOffset deadlineAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() >= deadlineAt)
            throw new CalendarEntityDeadlineException();
    }

    private static CallToolResult DeadlineError() => Error(
        "limit_exhausted",
        "limitsAndAdmission",
        "The Calendar Entity query exhausted the elapsed_time execution budget.",
        false,
        "execution");

    private static CallToolResult CursorPayloadError() => Error(
        "payload_too_large",
        "limitsAndAdmission",
        "The continuation cursor exceeds the safe payload limit.",
        false,
        "admissionAndPayload");

    private static CallToolResult CreateSuccess(
        IReadOnlyList<CalendarSnapshotResult> items,
        IReadOnlyList<CalendarResourceDiagnostic> diagnostics,
        string? nextCursor) => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityQuerySuccessResult(
                "success",
                items,
                diagnostics.Select(CalendarDiagnosticResult.FromResourceDiagnostic).ToArray(),
                new CalendarPagination("non_snapshot", nextCursor))),
            Content = [new TextContentBlock { Text = "Calendar Entity query completed." }]
        };

    private static bool IsAfter(CalendarResourceSnapshot item, CalendarEntityContinuation continuation)
    {
        var calendarComparison = string.CompareOrdinal(item.CalendarHref, continuation.CalendarHref);
        return calendarComparison > 0
            || calendarComparison == 0 && string.CompareOrdinal(item.ResourceHref, continuation.ResourceHref) > 0;
    }

    private static CallToolResult MapQueryFailure(CalendarEntityQueryResult result)
    {
        var candidates = result.AuthorizedCandidates.Select(CalendarAuthorizedCandidateResult.FromDescriptor).ToArray();
        return result.Code switch
        {
            CalendarEntityQueryCode.InvalidInput => Error("invalid_input", "input", "The Calendar Entity query input is invalid.", false, "schemaLexicalDiscriminator"),
            CalendarEntityQueryCode.UnsafeScope => Error("invalid_input", "input", "The Calendar Entity query Calendar href is unsafe.", false, "originScopeAuthorization"),
            CalendarEntityQueryCode.NotFound => Error("not_found", "selection", "No matching authorized Calendar was found.", false, "selectionDiscoveryCapability", candidates: candidates),
            CalendarEntityQueryCode.Ambiguous => Error("ambiguous", "selection", "The Calendar selector matched more than one authorized Calendar.", false, "selectionDiscoveryCapability", candidates: candidates),
            CalendarEntityQueryCode.OutsideScope => Error("outside_scope", "selection", "The selected Calendar is outside the configured Calendar Scope.", false, "originScopeAuthorization", candidates: candidates),
            CalendarEntityQueryCode.EntityKindMismatch => Error("entity_kind_mismatch", "selection", "The requested Entity Kind does not match the resource.", false, "completeResourceSemantics"),
            CalendarEntityQueryCode.UnsupportedCapability => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
            CalendarEntityQueryCode.ConcurrencyUnavailable => Error("concurrency_unavailable", "state", "A query candidate did not provide a strong Entity Tag.", false, "targetRevision"),
            CalendarEntityQueryCode.LimitExhausted => Error(
                "limit_exhausted",
                "limitsAndAdmission",
                "The Calendar Entity query exhausted its execution budget.",
                false,
                "execution",
                result.Limits is null
                    ? null
                    : new CalendarEntityExecutionLimits(
                        result.Limits.ResourcesInspected,
                        OccurrenceCount: result.Limits.OccurrenceCount,
                        ByteCount: result.Limits.ByteCount)),
            CalendarEntityQueryCode.PayloadTooLarge => Error(
                "payload_too_large",
                "limitsAndAdmission",
                "A Calendar Object Resource exceeds the safe payload limit.",
                false,
                "admissionAndPayload",
                result.Limits?.ByteCount is { } byteCount
                    ? new CalendarEntityExecutionLimits(ByteCount: byteCount)
                    : null),
            CalendarEntityQueryCode.TemporalUnresolved => Error("temporal_unresolved", "capabilityAndProjection", "Temporal evaluation could not be resolved.", false, "completeResourceSemantics"),
            CalendarEntityQueryCode.RecurrenceUnevaluable => Error("recurrence_unevaluable", "capabilityAndProjection", "The Recurrence Set could not be evaluated.", false, "completeResourceSemantics"),
            _ => ProtocolError()
        };
    }

    private static CallToolResult MapHttpFailure(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Error("upstream_unauthorized", "upstream", "The Calendar Entity query was not authorized.", false, "execution"),
        HttpStatusCode.Forbidden => Error("upstream_forbidden", "upstream", "The Calendar Entity query was forbidden.", false, "execution"),
        HttpStatusCode.TooManyRequests => Error("upstream_rate_limited", "upstream", "The Calendar Entity query is rate limited.", true, "execution"),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => Error("unsupported_capability", "capabilityAndProjection", "The server does not support the required Calendar query capability.", false, "selectionDiscoveryCapability"),
        HttpStatusCode.RequestEntityTooLarge => Error("payload_too_large", "limitsAndAdmission", "The Calendar Entity query response is too large.", false, "admissionAndPayload"),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => Error("conflict", "state", "The Calendar Entity query encountered an upstream state conflict.", false, "execution"),
        HttpStatusCode.RequestTimeout => Error("upstream_unavailable", "upstream", "The Calendar Entity query is temporarily unavailable.", true, "execution"),
        HttpStatusCode.InsufficientStorage => Error("upstream_unavailable", "upstream", "The Calendar Entity query is unavailable.", false, "execution"),
        null => Error("upstream_unavailable", "upstream", "The Calendar Entity query is temporarily unavailable.", true, "execution"),
        >= HttpStatusCode.InternalServerError => Error("upstream_unavailable", "upstream", "The Calendar Entity query is temporarily unavailable.", true, "execution"),
        _ => ProtocolError()
    };

    private static CallToolResult ProtocolError() => Error(
        "upstream_protocol_error",
        "upstream",
        "The Calendar Entity query returned an invalid response.",
        false,
        "execution");

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
            StructuredContent = JsonSerializer.SerializeToElement(new CalendarEntityQueryErrorResult(
                code,
                category,
                message,
                retryable,
                phase,
                limits,
                candidates is { Count: > 0 } ? candidates : null)),
            Content = [new TextContentBlock { Text = "Calendar Entity query failed." }]
        };

    private static bool TryCreateQuery(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string> entityKinds,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        out CalendarEntityQuery query)
    {
        query = null!;
        if (!TryCreateScope(scope, out var domainScope)
            || !TryCreateKinds(entityKinds, out var domainKinds)
            || !TryCreateWindow(from, to, out var domainFrom, out var domainTo))
            return false;
        query = new CalendarEntityQuery(domainScope, domainKinds, domainFrom, domainTo);
        return true;
    }

    private static bool TryCreateScope(CalendarEntityScopeArgument scope, out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (scope.Mode == "default" && scope.Calendar is null)
        {
            domainScope = CalendarEntityScope.Default;
            return true;
        }
        if (scope.Mode == "all" && scope.Calendar is null)
        {
            domainScope = CalendarEntityScope.All;
            return true;
        }
        if (scope.Mode != "selected" || scope.Calendar is null)
            return false;
        return scope.Calendar.By switch
        {
            "name" => TryCreateNameScope(scope.Calendar, out domainScope),
            "href" => TryCreateHrefScope(scope.Calendar, out domainScope),
            _ => false
        };
    }

    private static bool TryCreateNameScope(
        CalendarEntityReferenceArgument reference,
        out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (reference.Name is not { Length: > 0 } name || name != name.Trim() || reference.Href is not null)
            return false;
        domainScope = CalendarEntityScope.Selected(new CalendarReference(Name: name));
        return true;
    }

    private static bool TryCreateHrefScope(
        CalendarEntityReferenceArgument reference,
        out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (reference.Href is not { Length: > 0 } href || reference.Name is not null)
            return false;
        domainScope = CalendarEntityScope.Selected(new CalendarReference(Href: href));
        return true;
    }

    private static bool TryCreateKinds(IReadOnlyList<string> values, out IReadOnlyList<CalendarEntityKind> kinds)
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
        return TryParseUtc(from, out domainFrom) && TryParseUtc(to, out domainTo);
    }

    private static bool TryParseUtc(CalendarEntityUtcArgument argument, out DateTimeOffset? value)
    {
        value = null;
        if (argument.Kind != "utcDateTime")
            return false;
        var lexical = argument.Value;
        var hasFraction = lexical.Length > 20 && lexical.Length >= 22 && lexical[19] == '.';
        var secondLexical = hasFraction ? lexical[..19] + "Z" : lexical;
        if (!DateTimeOffset.TryParseExact(
                secondLexical,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return false;
        if (!hasFraction)
        {
            value = parsed;
            return true;
        }
        var fraction = lexical.AsSpan(20, lexical.Length - 21);
        if (lexical[^1] != 'Z' || fraction.IsEmpty || !fraction.ToString().All(char.IsAsciiDigit))
            return false;
        return TryApplyFraction(parsed, fraction, out value);
    }

    private static bool TryApplyFraction(
        DateTimeOffset parsed,
        ReadOnlySpan<char> fraction,
        out DateTimeOffset? value)
    {
        value = null;
        var representedDigits = Math.Min(7, fraction.Length);
        var ticksText = fraction[..representedDigits].ToString().PadRight(7, '0');
        var ticks = int.Parse(ticksText, CultureInfo.InvariantCulture);
        if (fraction[representedDigits..].ContainsAnyExcept('0'))
            ticks++;
        if (ticks == TimeSpan.TicksPerSecond)
        {
            if (parsed.Ticks > DateTimeOffset.MaxValue.Ticks - TimeSpan.TicksPerSecond)
                return false;
            parsed = parsed.AddSeconds(1);
            ticks = 0;
        }
        value = parsed.AddTicks(ticks);
        return true;
    }

    private static int MeasureArguments(
        CalendarEntityScopeArgument scope,
        IReadOnlyList<string> entityKinds,
        CalendarEntityUtcArgument? from,
        CalendarEntityUtcArgument? to,
        int? pageSize,
        string? cursor,
        IDictionary<string, JsonElement>? rawArguments) => rawArguments is { } raw
            ? JsonSerializer.SerializeToUtf8Bytes(raw).Length
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                scope,
                entityKinds,
                from,
                to,
                pageSize,
                cursor
            }).Length;

    private static bool TryDeserializeRawArguments(
        IDictionary<string, JsonElement>? arguments,
        out CalendarEntityRawArguments parsed)
    {
        parsed = null!;
        if (arguments is null
            || !arguments.TryGetValue("scope", out var scopeElement)
            || !arguments.TryGetValue("entityKinds", out var kindsElement)
            || !HasFrozenRawShape(arguments))
            return false;
        try
        {
            var scope = scopeElement.Deserialize<CalendarEntityScopeArgument>();
            var kinds = kindsElement.Deserialize<string[]>();
            if (scope is null || kinds is null)
                return false;
            parsed = new CalendarEntityRawArguments(
                scope,
                kinds,
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "from"),
                DeserializeOptional<CalendarEntityUtcArgument>(arguments, "to"),
                DeserializeOptional<int?>(arguments, "pageSize"),
                DeserializeOptional<string>(arguments, "cursor"));
            return !HasPresentNull(arguments, "from")
                && !HasPresentNull(arguments, "to")
                && !HasPresentNull(arguments, "pageSize")
                && !HasPresentNull(arguments, "cursor");
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

    private static bool HasPresentNull(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Null;

    internal static bool HasFrozenRawShape(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return true;
        var allowed = new[] { "scope", "entityKinds", "from", "to", "pageSize", "cursor" };
        if (arguments.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
            return false;
        return (!arguments.TryGetValue("scope", out var scope) || HasScopeShape(scope))
            && (!arguments.TryGetValue("from", out var from) || HasTemporalShape(from))
            && (!arguments.TryGetValue("to", out var to) || HasTemporalShape(to));
    }

    private static bool HasScopeShape(JsonElement scope)
    {
        if (!TryGetUniqueProperties(scope, out var properties)
            || !properties.TryGetValue("mode", out var modeElement)
            || modeElement.ValueKind != JsonValueKind.String)
            return false;
        var mode = modeElement.GetString();
        if (mode is "default" or "all")
            return properties.Count == 1;
        return mode == "selected"
            && properties.Count == 2
            && properties.TryGetValue("calendar", out var calendar)
            && HasCalendarReferenceShape(calendar);
    }

    private static bool HasCalendarReferenceShape(JsonElement calendar)
    {
        if (!TryGetUniqueProperties(calendar, out var properties)
            || !properties.TryGetValue("by", out var byElement)
            || byElement.ValueKind != JsonValueKind.String
            || properties.Count != 2)
            return false;
        var by = byElement.GetString();
        return by == "name" && properties.ContainsKey("name")
            || by == "href" && properties.ContainsKey("href");
    }

    private static bool HasTemporalShape(JsonElement temporal) =>
        TryGetUniqueProperties(temporal, out var properties)
        && properties.Count == 2
        && properties.ContainsKey("kind")
        && properties.ContainsKey("value");

    private static bool TryGetUniqueProperties(
        JsonElement element,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
                return false;
        }
        return true;
    }

    internal static int MeasureResult(CallToolResult result) => JsonSerializer.SerializeToUtf8Bytes(result).Length;

    internal static int MeasureHumanReadableResult(CallToolResult result)
    {
        var diagnostics = result.StructuredContent is { ValueKind: JsonValueKind.Object } structured
            && structured.TryGetProperty("diagnostics", out var value)
            ? value
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        return JsonSerializer.SerializeToUtf8Bytes(new CalendarHumanReadableBudget(
            result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray(),
            diagnostics)).Length;
    }

    internal static CallToolResult EnsureBoundedResult(CallToolResult result)
    {
        var humanReadableBytes = MeasureHumanReadableResult(result);
        if (humanReadableBytes > MaximumHumanReadableBytes)
        {
            return Error(
                "payload_too_large",
                "limitsAndAdmission",
                "The Calendar Entity query human-readable result exceeds the safe payload limit.",
                false,
                "admissionAndPayload",
                new CalendarEntityExecutionLimits(ByteCount: humanReadableBytes));
        }
        var observedBytes = MeasureResult(result);
        return observedBytes <= MaximumStructuredResultBytes
            ? result
            : Error(
                "payload_too_large",
                "limitsAndAdmission",
                "The Calendar Entity query result exceeds the safe payload limit.",
                false,
                "admissionAndPayload",
                new CalendarEntityExecutionLimits(ByteCount: observedBytes));
    }

    private static string CreateQueryContext(CalendarEntityQuery query, int pageSize)
    {
        var selectorKind = query.Scope.Calendar?.Name is not null ? "name" : "href";
        var selectorValue = query.Scope.Calendar?.Name is not null
            ? query.Scope.Calendar.Name.ToUpperInvariant()
            : query.Scope.Calendar?.Href;
        return JsonSerializer.Serialize(new CalendarEntityCursorQueryBinding(
            query.Scope.Mode,
            selectorKind,
            selectorValue,
            query.EntityKinds.Order().Select(kind => kind.ToString()).ToArray(),
            query.From?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            query.To?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            pageSize));
    }

    private sealed record CalendarEntityCursorQueryBinding(
        CalendarEntityScopeMode ScopeMode,
        string SelectorKind,
        string? SelectorValue,
        IReadOnlyList<string> EntityKinds,
        string From,
        string To,
        int PageSize);

    private sealed record CalendarEntityRawArguments(
        CalendarEntityScopeArgument Scope,
        IReadOnlyList<string> EntityKinds,
        CalendarEntityUtcArgument? From,
        CalendarEntityUtcArgument? To,
        int? PageSize,
        string? Cursor);

    private sealed record CalendarHumanReadableBudget(
        IReadOnlyList<string> Text,
        JsonElement Diagnostics);

    private sealed class CalendarEntityDeadlineException : Exception
    {
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
    [property: JsonPropertyName("pagination")] CalendarPagination Pagination);

public sealed record CalendarEntityQueryErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("limits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalendarEntityExecutionLimits? Limits = null,
    [property: JsonPropertyName("authorizedCandidates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CalendarAuthorizedCandidateResult>? AuthorizedCandidates = null);

public sealed record CalendarEntityExecutionLimits(
    [property: JsonPropertyName("resourcesInspected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ResourcesInspected = null,
    [property: JsonPropertyName("calendarCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CalendarCount = null,
    [property: JsonPropertyName("occurrenceCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OccurrenceCount = null,
    [property: JsonPropertyName("byteCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ByteCount = null);

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
