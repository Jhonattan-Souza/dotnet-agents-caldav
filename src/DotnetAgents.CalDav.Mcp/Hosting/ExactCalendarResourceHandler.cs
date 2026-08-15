using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
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
            throw new InvalidOperationException("The protected Calendar resource link is invalid.");

        var read = await calendarService.GetResourceAsync(href, cancellationToken);
        if (read.Code != CalendarResourceReadCode.Success || read.Snapshot is null)
            throw new InvalidOperationException("The protected Calendar resource is unavailable.");
        if (!string.Equals(read.Snapshot.EntityTag, expectedEntityTag, StringComparison.Ordinal))
            throw new InvalidOperationException("The protected Calendar resource revision has changed.");

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "text/calendar; charset=utf-8",
                    Text = new UTF8Encoding(false, true).GetString(read.Snapshot.AuthoritativeUtf8.Span)
                }
            ],
            TimeToLive = TimeSpan.Zero,
            CacheScope = CacheScope.Private
        };
    }
}
