using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>
/// Advertises the catalog's caching hints on <c>tools/list</c>. The tool set is fixed for the
/// process configuration and never emits list-changed notifications.
/// </summary>
internal static class CalendarToolListCache
{
    private static readonly (TimeSpan TimeToLive, CacheScope Scope) Cache = CalendarToolContract.GetToolsListCache();

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListTools => next =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            result.TimeToLive = Cache.TimeToLive;
            result.CacheScope = Cache.Scope;
            return result;
        };
}
