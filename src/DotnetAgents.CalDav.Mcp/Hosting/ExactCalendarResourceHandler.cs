using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class ExactCalendarResourceHandler
{
    public static ListResourcesResult List() => new()
    {
        Resources = [],
        TimeToLive = TimeSpan.Zero,
        CacheScope = CacheScope.Private
    };

    public static async Task<ReadResourceResult> ReadAsync(
        string uri,
        ICalendarService calendarService,
        CancellationToken cancellationToken)
    {
        if (!ExactCalendarResourceLink.TryParse(uri, out var href, out var expectedEntityTag))
            throw ResourceNotFound("The protected Calendar resource link is invalid.");

        var read = await calendarService.GetResourceAsync(href, cancellationToken);
        if (read.Code != CalendarResourceReadCode.Success || read.Snapshot is null)
            throw Unavailable(read.Code);
        if (!string.Equals(read.Snapshot.EntityTag, expectedEntityTag, StringComparison.Ordinal))
            throw ResourceNotFound("The protected Calendar resource revision has changed.");

        return new ReadResourceResult
        {
            Contents =
            [
                BlobResourceContents.FromBytes(
                    read.Snapshot.AuthoritativeUtf8,
                    uri,
                    "text/calendar; charset=utf-8")
            ],
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }

    // Protocol revision 2026-07-28 reports an unresolvable resource URI as Invalid Params (-32602)
    // and other failures as Internal Error (-32603).
    private static McpProtocolException Unavailable(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.NotFound
            or CalendarResourceReadCode.InvalidInput
            or CalendarResourceReadCode.OutsideScope => ResourceNotFound("The protected Calendar resource is unavailable."),
        _ => new McpProtocolException("The protected Calendar resource could not be read.", McpErrorCode.InternalError)
    };

    private static McpProtocolException ResourceNotFound(string message) => new(message, McpErrorCode.InvalidParams);
}
