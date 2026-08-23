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
        var tool = new CalendarOccurrenceTools(null!, protector, TimeProvider.System);
        var items = Enumerable.Range(0, PageAssemblyObservationSupport.CorpusCount)
            .Select(CreateOccurrence)
            .ToArray();
        var result = CalendarOccurrenceQueryResult.Success(items);
        var corpusItems = PageAssemblyObservationSupport.SerializeItems(result.Items.Select(CalendarOccurrenceSnapshotResult.FromSnapshot));
        var method = typeof(CalendarOccurrenceTools).GetMethod("CreatePage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Historical Occurrence CreatePage was not found.");
        var createPage = method.CreateDelegate<CreatePageDelegate>();
        var observations = new[] { 1, 50, 200 }
            .Select(size => PageAssemblyObservationSupport.Measure(
                "occurrence",
                "e63ea4d62fa4b4062566a6819127c18a30a1a38d",
                size,
                corpusItems,
                () => createPage(tool, result, null, "page-assembly-observation", size, DateTimeOffset.MaxValue, CancellationToken.None)))
            .ToArray();
        PageAssemblyObservationSupport.Write(observations);
        return 0;
    }

    private static CalendarOccurrenceSnapshot CreateOccurrence(int index)
    {
        var instant = new CalendarTemporalValue(
            CalendarTemporalKind.UtcDateTime,
            $"202601{(index % 28) + 1:D2}T{index % 24:D2}0000Z");
        return new CalendarOccurrenceSnapshot(
            PageAssemblyObservationSupport.Snapshot(index, CalendarResourceProjectionKind.Event),
            instant,
            new CalendarOccurrenceTiming(instant, instant, EvaluatedStartUtc: instant));
    }

    private delegate CallToolResult CreatePageDelegate(
        CalendarOccurrenceTools target,
        CalendarOccurrenceQueryResult result,
        CalendarOccurrenceContinuation? continuation,
        string queryContext,
        int pageSize,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken);
}
