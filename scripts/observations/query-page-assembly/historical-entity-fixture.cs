using System.Reflection;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class QueryPageAssemblyObservation
{
    private static int Main()
    {
        var protector = new CalendarEntityCursorProtector(
            TimeProvider.System,
            Options.Create(new CalDavOptions()),
            new byte[64]);
        var tool = new CalendarEntityTools(null!, protector, TimeProvider.System);
        var result = CalendarEntityQueryResult.Success(
            Enumerable.Range(0, PageAssemblyObservationSupport.CorpusCount)
                .Select(index => PageAssemblyObservationSupport.Snapshot(index, CalendarResourceProjectionKind.Event))
                .ToArray());
        var corpusItems = PageAssemblyObservationSupport.SerializeItems(result.Items.Select(CalendarSnapshotResult.FromSnapshot));
        var method = typeof(CalendarEntityTools).GetMethod("CreatePage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Historical Calendar Entity CreatePage was not found.");
        var createPage = method.CreateDelegate<CreatePageDelegate>();
        var observations = new[] { 1, 50, 200 }
            .Select(size => PageAssemblyObservationSupport.Measure(
                "entity",
                "4df75347477ca6dae463d60b938c7d28ab9b6ea6",
                size,
                corpusItems,
                () => createPage(tool, result, null, "page-assembly-observation", size, DateTimeOffset.MaxValue, CancellationToken.None)))
            .ToArray();
        PageAssemblyObservationSupport.Write(observations);
        return 0;
    }

    private delegate CallToolResult CreatePageDelegate(
        CalendarEntityTools target,
        CalendarEntityQueryResult result,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int pageSize,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken);
}
