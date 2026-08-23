using System.Buffers;
using System.Diagnostics;
using OpenTelemetry;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal sealed class TelemetryActivityAllowlistProcessor : BaseProcessor<Activity>
{
    private const string RetryAggregationKey = "DotnetAgents.CalDav.Telemetry.RetryAggregation";
    private static readonly HashSet<string> AllowedTagNames = new(StringComparer.Ordinal)
    {
        "caldav.tool.name",
        "caldav.entity.kind",
        "caldav.phase",
        "caldav.outcome",
        "caldav.error.code",
        "caldav.error.category",
        "caldav.error.phase",
        "caldav.error.retryable",
        "caldav.mutation.state",
        "caldav.http.request_purpose",
        "caldav.http.observation",
        "caldav.transport.recovered",
        "caldav.transport.retry_count",
        "caldav.query.mode",
        "caldav.query.fetch_mode",
        "caldav.query.phase",
        "caldav.query.candidate_count",
        "caldav.query.multiget_resource_count",
        "caldav.query.snapshot_count",
        "caldav.query.evaluation_count",
        "caldav.query.serialization_count",
        "caldav.query.snapshot_lookup_count",
        "caldav.query.page_admission_count",
        "error.type",
        "gen_ai.operation.name",
        "gen_ai.tool.name",
        "http.request.method",
        "http.request.resend_count",
        "http.response.status_code",
        "mcp.method.name",
        "mcp.protocol.version",
        "network.protocol.name",
        "network.protocol.version",
        "network.transport",
        "rpc.response.status_code"
    };
    private static readonly HashSet<string> KnownMcpMethods = new(StringComparer.Ordinal)
    {
        "completion/complete",
        "initialize",
        "logging/setLevel",
        "notifications/cancelled",
        "notifications/initialized",
        "notifications/progress",
        "ping",
        "prompts/get",
        "prompts/list",
        "resources/list",
        "resources/read",
        "resources/templates/list",
        "tools/call",
        "tools/list"
    };
    private static readonly HashSet<string> KnownHttpMethods = new(StringComparer.Ordinal)
    {
        "DELETE",
        "GET",
        "HEAD",
        "MKCALENDAR",
        "MKCOL",
        "MOVE",
        "OPTIONS",
        "POST",
        "PROPFIND",
        "PROPPATCH",
        "PUT",
        "REPORT"
    };
    private static readonly string[] QueryCounterNames =
    [
        "caldav.query.candidate_count",
        "caldav.query.multiget_resource_count",
        "caldav.query.snapshot_count",
        "caldav.query.evaluation_count",
        "caldav.query.serialization_count",
        "caldav.query.snapshot_lookup_count",
        "caldav.query.page_admission_count"
    ];
    private static readonly SearchValues<char> ProtocolVersionCharacters =
        SearchValues.Create("0123456789-");

    public override void OnStart(Activity data) => data.TraceStateString = null;

    public override void OnEnd(Activity data)
    {
        data.TraceStateString = null;
        data.SetStatus(data.Status);
        if (data.Source.Name == OpenTelemetryHostConfiguration.McpInstrumentationName)
            SanitizeMcpActivity(data);
        else if (data.Source.Name == OpenTelemetryHostConfiguration.HttpInstrumentationName)
        {
            ClassifyHttpObservation(data);
            ObserveRetry(data);
            SanitizeHttpActivity(data);
        }
        else if (data.Source.Name == OpenTelemetryHostConfiguration.InstrumentationName)
        {
            data.DisplayName = data.OperationName;
            SanitizeCalendarActivity(data);
        }
        if (data.Source.Name != OpenTelemetryHostConfiguration.InstrumentationName)
            RemoveQueryTags(data);

        foreach (var tag in data.TagObjects.Where(tag => !AllowedTagNames.Contains(tag.Key)).ToArray())
            data.SetTag(tag.Key, null);

        data.SetTag("error.type", CalendarTelemetryVocabulary.ErrorType(data.GetTagItem("error.type")));
    }

    private static void SanitizeMcpActivity(Activity activity)
    {
        var method = NormalizeMcpMethod(activity.GetTagItem("mcp.method.name") as string);
        activity.SetTag("mcp.method.name", method);
        var toolName = CalendarTelemetry.NormalizeToolName(
            activity.GetTagItem("gen_ai.tool.name") as string);
        if (toolName == "unknown")
            activity.SetTag("gen_ai.tool.name", null);
        else
            activity.SetTag("gen_ai.tool.name", toolName);
        activity.SetTag("mcp.protocol.version", NormalizeProtocolVersion(
            activity.GetTagItem("mcp.protocol.version") as string));
        activity.DisplayName = method == "tools/call" && toolName != "unknown"
            ? string.Concat(method, " ", toolName)
            : method;
    }

    private static void SanitizeHttpActivity(Activity activity)
    {
        var method = NormalizeHttpMethod(
            activity.GetTagItem("http.request.method_original") as string
            ?? activity.GetTagItem("http.request.method") as string);
        activity.SetTag("http.request.method", method);
        activity.DisplayName = method;
    }

    private static void ClassifyHttpObservation(Activity activity)
    {
        var purpose = activity.GetTagItem("caldav.http.request_purpose") as string;
        if (purpose == "absence_probe" && HasStatusCode(activity, 404))
        {
            activity.SetTag("caldav.http.observation", "expected_absence");
            activity.SetTag("error.type", null);
            activity.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity.SetTag("caldav.http.request_purpose", null);
        activity.SetTag("caldav.http.observation", null);
    }

    private static void ObserveRetry(Activity activity)
    {
        var resendCount = NumericTag(activity.GetTagItem("http.request.resend_count"));
        if (resendCount is not > 0)
            return;

        var operation = FindOperation(activity);
        if (operation is null)
            return;

        var aggregation = GetRetryAggregation(operation);
        var observation = aggregation.Observe(activity.Status != ActivityStatusCode.Error);
        operation.SetTag("caldav.transport.retry_count", observation.RetryCount);
        if (observation.Recovered)
            operation.SetTag("caldav.transport.recovered", true);
    }

    private static RetryAggregation GetRetryAggregation(Activity operation)
    {
        if (operation.GetCustomProperty(RetryAggregationKey) is RetryAggregation existing)
            return existing;
        lock (operation)
        {
            if (operation.GetCustomProperty(RetryAggregationKey) is RetryAggregation current)
                return current;
            var created = new RetryAggregation();
            operation.SetCustomProperty(RetryAggregationKey, created);
            return created;
        }
    }

    private static Activity? FindOperation(Activity activity)
    {
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Source.Name == OpenTelemetryHostConfiguration.InstrumentationName
                && parent.OperationName == "caldav.operation")
            {
                return parent;
            }
        }
        return null;
    }

    private static void SanitizeCalendarActivity(Activity activity)
    {
        activity.SetTag("caldav.outcome", ClosedOutcome(
            activity.GetTagItem("caldav.outcome")));
        activity.SetTag("caldav.mutation.state", ClosedMutationState(
            activity.GetTagItem("caldav.mutation.state")));
        activity.SetTag("caldav.error.retryable", BooleanTag(
            activity.GetTagItem("caldav.error.retryable")));
        activity.SetTag("caldav.error.code", CalendarTelemetryVocabulary.ErrorCode(
            activity.GetTagItem("caldav.error.code") as string));
        activity.SetTag("caldav.error.category", CalendarTelemetryVocabulary.ErrorCategory(
            activity.GetTagItem("caldav.error.category") as string));
        activity.SetTag("caldav.error.phase", CalendarTelemetryVocabulary.ErrorPhase(
            activity.GetTagItem("caldav.error.phase") as string));
        activity.SetTag("caldav.query.mode", ClosedQueryMode(activity.GetTagItem("caldav.query.mode")));
        activity.SetTag("caldav.query.fetch_mode", ClosedQueryFetchMode(
            activity.GetTagItem("caldav.query.fetch_mode")));
        activity.SetTag("caldav.query.phase", ClosedQueryPhase(activity.GetTagItem("caldav.query.phase")));
        foreach (var name in QueryCounterNames)
            activity.SetTag(name, NonNegativeCounter(activity.GetTagItem(name)));
        ApplyRetryAggregation(activity);
    }

    private static void ApplyRetryAggregation(Activity activity)
    {
        if (activity.GetCustomProperty(RetryAggregationKey) is not RetryAggregation aggregation)
        {
            activity.SetTag("caldav.transport.recovered", null);
            activity.SetTag("caldav.transport.retry_count", null);
            return;
        }

        var observation = aggregation.Snapshot();
        activity.SetTag("caldav.transport.retry_count", observation.RetryCount);
        activity.SetTag(
            "caldav.transport.recovered",
            Equals(activity.GetTagItem("caldav.outcome"), "success") && observation.Recovered
                ? true
                : null);
    }

    private static string NormalizeMcpMethod(string? method) =>
        method is not null && KnownMcpMethods.Contains(method) ? method : "mcp.request";

    private static string NormalizeHttpMethod(string? method) =>
        method is not null && KnownHttpMethods.Contains(method) ? method : "HTTP";

    private static string? NormalizeProtocolVersion(string? version) =>
        version is { Length: > 0 and <= 16 }
        && version.AsSpan().IndexOfAnyExcept(ProtocolVersionCharacters) < 0
            ? version
            : null;

    private static bool HasStatusCode(Activity activity, long expected) =>
        NumericTag(activity.GetTagItem("http.response.status_code")) == expected;

    private static long? NumericTag(object? value) => value switch
    {
        byte number => number,
        short number => number,
        int number => number,
        long number => number,
        _ => null
    };

    private static bool? BooleanTag(object? value) => value is bool boolean ? boolean : null;

    private static long? NonNegativeCounter(object? value)
    {
        var numeric = NumericTag(value);
        return numeric is >= 0 and <= 100_000_000 ? numeric : null;
    }

    private static string? ClosedQueryMode(object? value) => (value as string) switch
    {
        "start" => "start",
        "continue" => "continue",
        _ => null
    };

    private static string? ClosedQueryFetchMode(object? value) => (value as string) switch
    {
        "multiget" => "multiget",
        _ => null
    };

    private static string? ClosedQueryPhase(object? value) => (value as string) switch
    {
        "discovery" => "discovery",
        "candidate" => "candidate",
        "fetch" => "fetch",
        "evaluation" => "evaluation",
        "serialization" => "serialization",
        "reservation" => "reservation",
        "snapshot_lookup" => "snapshot_lookup",
        "page_admission" => "page_admission",
        _ => null
    };

    private static void RemoveQueryTags(Activity activity)
    {
        foreach (var tag in activity.TagObjects.Where(tag => tag.Key.StartsWith("caldav.query.", StringComparison.Ordinal)).ToArray())
            activity.SetTag(tag.Key, null);
    }

    private static string? ClosedOutcome(object? value) => (value as string) switch
    {
        "success" => "success",
        "input_required" => "input_required",
        "cancelled" => "cancelled",
        "error" => "error",
        _ => null
    };

    private static string? ClosedMutationState(object? value) => (value as string) switch
    {
        "not_attempted" => "not_attempted",
        "not_committed" => "not_committed",
        "committed" => "committed",
        "unknown" => "unknown",
        _ => null
    };

    private sealed class RetryAggregation
    {
        private readonly object _gate = new();
        private int _retryCount;
        private bool _recovered;

        internal RetryObservation Observe(bool recovered)
        {
            lock (_gate)
            {
                _retryCount++;
                _recovered |= recovered;
                return new RetryObservation(_retryCount, _recovered);
            }
        }

        internal RetryObservation Snapshot()
        {
            lock (_gate)
                return new RetryObservation(_retryCount, _recovered);
        }
    }

    private readonly record struct RetryObservation(int RetryCount, bool Recovered);

}
