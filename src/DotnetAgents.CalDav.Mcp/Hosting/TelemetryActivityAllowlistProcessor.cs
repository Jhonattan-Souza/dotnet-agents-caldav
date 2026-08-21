using System.Buffers;
using System.Diagnostics;
using OpenTelemetry;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal sealed class TelemetryActivityAllowlistProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> AllowedTagNames = new(StringComparer.Ordinal)
    {
        "caldav.tool.name",
        "caldav.entity.kind",
        "caldav.phase",
        "caldav.outcome",
        "caldav.error.code",
        "caldav.error.category",
        "caldav.mutation.state",
        "error.type",
        "gen_ai.operation.name",
        "gen_ai.tool.name",
        "http.request.method",
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
    private static readonly SearchValues<char> ProtocolVersionCharacters =
        SearchValues.Create("0123456789-");
    private static readonly SearchValues<char> ExceptionTypeCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._+`");

    public override void OnStart(Activity data) => data.TraceStateString = null;

    public override void OnEnd(Activity data)
    {
        data.TraceStateString = null;
        data.SetStatus(data.Status);
        if (data.Source.Name == OpenTelemetryHostConfiguration.McpInstrumentationName)
            SanitizeMcpActivity(data);
        else if (data.Source.Name == "System.Net.Http")
            SanitizeHttpActivity(data);
        else if (data.Source.Name == OpenTelemetryHostConfiguration.InstrumentationName)
            data.DisplayName = data.OperationName;

        foreach (var tag in data.TagObjects.Where(tag => !AllowedTagNames.Contains(tag.Key)).ToArray())
            data.SetTag(tag.Key, null);

        SanitizeTextTag(data, "error.type", MaximumExceptionTypeLength, ExceptionTypeCharacters);
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

    private static string NormalizeMcpMethod(string? method) =>
        method is not null && KnownMcpMethods.Contains(method) ? method : "mcp.request";

    private static string NormalizeHttpMethod(string? method) =>
        method is not null && KnownHttpMethods.Contains(method) ? method : "HTTP";

    private static string? NormalizeProtocolVersion(string? version) =>
        version is { Length: > 0 and <= 16 }
        && version.AsSpan().IndexOfAnyExcept(ProtocolVersionCharacters) < 0
            ? version
            : null;

    private static void SanitizeTextTag(
        Activity activity,
        string tagName,
        int maximumLength,
        SearchValues<char> allowedCharacters)
    {
        if (activity.GetTagItem(tagName) is not string value
            || value.Length == 0
            || value.Length > maximumLength
            || value.AsSpan().IndexOfAnyExcept(allowedCharacters) >= 0)
        {
            activity.SetTag(tagName, null);
        }
    }

    private const int MaximumExceptionTypeLength = 160;
}
