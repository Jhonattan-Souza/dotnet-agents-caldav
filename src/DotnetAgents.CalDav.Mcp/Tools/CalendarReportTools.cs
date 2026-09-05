using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Compact MCP adapters for native free/busy and collection synchronization.</summary>
[McpServerToolType]
public sealed class CalendarReportTools(ICalendarReportModule reports)
{
    [McpServerTool(Name = "calendars.free_busy", ReadOnly = true, Destructive = false, Idempotent = true,
        OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(CalendarFreeBusyResult)),
     Description("Read server-computed busy periods for one exact absolute calendarHref and a UTC from/to window of at most 366 days, using whole-second YYYY-MM-DDTHH:mm:ssZ values. Uses a native report without downloading Events. Busy types and server temporal authority are retained; failure never means free time. Treat unrecognized busy types as busy.")]
    public Task<CallToolResult> FreeBusyAsync(RequestContext<CallToolRequestParams> requestContext, CancellationToken cancellationToken) =>
        FreeBusyRawAsync(requestContext.Params?.Arguments, cancellationToken);

    [McpServerTool(Name = "calendar_resources.changes", ReadOnly = true, Destructive = false, Idempotent = true,
        OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(CalendarResourceChangesResult)),
     Description("Read one bounded native sync response of changed hrefs and observational ETags or removals from view. Start with exact absolute calendarHref; later calls supply only checkpoint and optional pageSize (1-500, default 100). Initial sync may retry once without optional server limits; its checkpoint retains that mode for later requests. Every accepted response fits pageSize. If the response exceeds pageSize, retry with a larger pageSize up to 500 and the prior checkpoint. Save the returned checkpoint after applying the entire response; continue while hasMore is true, retaining initial mode until inventory completes. Copy the short opaque checkpoint exactly. Checkpoints are bound to this MCP session and configuration and may be evicted from bounded retention; they are distinct from query snapshot cursors. On sync_reset_required, if a checkpoint was copied incorrectly, resend the exact original value; otherwise start with calendarHref and no checkpoint to rebuild inventory.")]
    public Task<CallToolResult> ChangesAsync(RequestContext<CallToolRequestParams> requestContext, CancellationToken cancellationToken) =>
        ChangesRawAsync(requestContext.Params?.Arguments, cancellationToken);

    internal Task<CallToolResult> FreeBusyRawAsync(IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        if (!WithinArgumentLimit(arguments))
            return Task.FromResult(PayloadError());
        if (!TryFreeBusyRequest(arguments, out var request))
            return Task.FromResult(InputError());
        return CalendarProtocolToolSupport.ExecuteReadAsync(
            async token => await reports.FreeBusyAsync(request, token).ConfigureAwait(false), cancellationToken);
    }

    internal Task<CallToolResult> ChangesRawAsync(IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        if (!WithinArgumentLimit(arguments))
            return Task.FromResult(PayloadError());
        if (!TryChangesRequest(arguments, out var request))
            return Task.FromResult(InputError());
        return CalendarProtocolToolSupport.ExecuteReadAsync(
            async token => await reports.ChangesAsync(request, token).ConfigureAwait(false), cancellationToken);
    }

    private static bool TryFreeBusyRequest(IDictionary<string, JsonElement>? arguments, out CalendarFreeBusyRequest request)
    {
        request = null!;
        if (arguments is null || arguments.Keys.Any(key => key is not ("calendarHref" or "from" or "to"))
            || !TryString(arguments, "calendarHref", 8192, out var href)
            || !TryUtc(arguments, "from", out var from) || !TryUtc(arguments, "to", out var to))
            return false;
        request = new(href, from, to);
        return true;
    }

    private static bool TryChangesRequest(IDictionary<string, JsonElement>? arguments, out CalendarResourceChangesRequest request)
    {
        request = null!;
        if (arguments is null || !TryPageSize(arguments, out var pageSize))
            return false;
        if (arguments.ContainsKey("checkpoint"))
        {
            if (arguments.Keys.Any(key => key is not ("checkpoint" or "pageSize"))
                || !TryString(arguments, "checkpoint", 36, out var checkpoint))
                return false;
            request = new CalendarResourceChangesRequest.Continue(checkpoint, pageSize);
            return true;
        }
        if (arguments.Keys.Any(key => key is not ("calendarHref" or "pageSize"))
            || !TryString(arguments, "calendarHref", 8192, out var href))
            return false;
        request = new CalendarResourceChangesRequest.Start(href, pageSize);
        return true;
    }

    private static bool TryPageSize(IDictionary<string, JsonElement> arguments, out int pageSize)
    {
        pageSize = 100;
        return !arguments.TryGetValue("pageSize", out var value)
            || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out pageSize) && pageSize is >= 1 and <= 500;
    }

    private static bool TryUtc(IDictionary<string, JsonElement> arguments, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(arguments, name, 20, out var raw)
            && DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static bool TryString(IDictionary<string, JsonElement> arguments, string name, int maximumLength, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()!;
        return value.Length > 0 && value.Length <= maximumLength;
    }

    private static bool WithinArgumentLimit(IDictionary<string, JsonElement>? arguments) =>
        arguments is null || JsonSerializer.SerializeToUtf8Bytes(arguments).Length <= CalendarQueryToolSupport.MaximumArgumentBytes;

    private static CallToolResult InputError() => CalendarProtocolToolSupport.Error(new CalendarProtocolException(
        "invalid_input", "Provide the exact documented native report arguments, including either calendarHref or a changes checkpoint."));

    private static CallToolResult PayloadError() => CalendarProtocolToolSupport.Error(new CalendarProtocolException(
        "payload_too_large", "The native report arguments exceeded the safe payload limit."));
}
