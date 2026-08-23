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
        var tool = new CalendarTodoTools(null!, protector, TimeProvider.System);
        var result = CalendarTodoQueryResult.Success(
            Enumerable.Range(0, PageAssemblyObservationSupport.CorpusCount)
                .Select(CreateItem)
                .ToArray());
        var projection = new[] { "summary", "due", "priority", "categories" };
        var corpusItems = PageAssemblyObservationSupport.SerializeItems(result.Items.Select(item => CalendarTodoCompactItemResult.FromItem(item, projection)));
        var method = typeof(CalendarTodoTools).GetMethod("CreatePage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Historical To-do CreatePage was not found.");
        var createPage = method.CreateDelegate<CreatePageDelegate>();
        var observations = new[] { 1, 50, 200 }
            .Select(size => PageAssemblyObservationSupport.Measure(
                "todo",
                "8a9d887a0b5e44ffbca3025a41ae7c8f6705dd77",
                size,
                corpusItems,
                () => createPage(tool, result, null, "page-assembly-observation", size, projection, DateTimeOffset.MaxValue, CancellationToken.None)))
            .ToArray();
        PageAssemblyObservationSupport.Write(observations);
        return 0;
    }

    private static CalendarTodoQueryItem CreateItem(int index)
    {
        var snapshot = PageAssemblyObservationSupport.Snapshot(index, CalendarResourceProjectionKind.Todo);
        var due = new CalendarTemporalValue(
            CalendarTemporalKind.UtcDateTime,
            $"202601{(index % 28) + 1:D2}T{index % 24:D2}0000Z");
        var evaluated = new DateTimeOffset(2026, 1, (index % 28) + 1, index % 24, 0, 0, TimeSpan.Zero);
        return new CalendarTodoQueryItem(
            CalendarTodoQueryResultKind.Entity,
            snapshot,
            null,
            new CalendarTodoCompletionClassification(CalendarTodoCompletionState.Open, "NEEDS-ACTION", null, null, []),
            due,
            evaluated,
            due,
            evaluated,
            false);
    }

    private delegate CallToolResult CreatePageDelegate(
        CalendarTodoTools target,
        CalendarTodoQueryResult result,
        CalendarEntityContinuation? continuation,
        string queryContext,
        int pageSize,
        IReadOnlyList<string> projection,
        DateTimeOffset deadlineAt,
        CancellationToken cancellationToken);
}
