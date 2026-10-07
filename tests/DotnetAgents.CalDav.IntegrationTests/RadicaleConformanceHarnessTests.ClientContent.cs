using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed partial class RadicaleConformanceHarnessTests
{
    private const string Davx5MinifiedZoneTodo = "davx5-4.5.20-ical4j-4.3.0-america-sao-paulo-minified.ics";

    // tasks.org rewrites a To-do through DAVx5 with only America/Sao_Paulo's last STANDARD observance.
    [Fact]
    public async Task Pinned_profile_queries_and_completes_davx5_todos_whose_zone_keeps_only_its_last_observance()
    {
        using var probe = CreateProbeClient();
        var calendar = new Uri(fixture.BaseUrl + "/conformance/client-content/", UriKind.Absolute);
        (await CreateCalendarAsync(probe, calendar, "Client Content", "VTODO")).ShouldBe(HttpStatusCode.Created);
        var completed = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClientContent", Davx5MinifiedZoneTodo));
        var open = completed
            .Replace("UID:davx5-truncated-zone-todo", "UID:davx5-open-todo", StringComparison.Ordinal)
            .Replace("COMPLETED:20261005T111615Z\r\nSTATUS:COMPLETED\r\nPERCENT-COMPLETE:100\r\n", string.Empty,
                StringComparison.Ordinal);
        (await SendProbeAsync(probe, HttpMethod.Put, new Uri(calendar, "completed.ics"), completed,
            ("If-None-Match", "*"))).Status.ShouldBe(HttpStatusCode.Created);
        var openPut = await SendProbeAsync(probe, HttpMethod.Put, new Uri(calendar, "open.ics"), open,
            ("If-None-Match", "*"));
        openPut.Status.ShouldBe(HttpStatusCode.Created);
        await using var provider = CreateTextSearchProvider(calendar.AbsoluteUri, new ConcurrentQueue<string>());
        var queries = provider.GetRequiredService<ICalendarQueryModule>();

        var todos = (await queries.QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    [CalendarTodoCompletionState.Open],
                    EvaluationTimeZone: "America/Sao_Paulo"),
                [CalendarTodoProjectionField.Summary]),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        todos.Value.Items.ShouldHaveSingleItem();
        var occurrences = (await queries.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(
                new CalendarOccurrenceQuery(
                    CalendarEntityScope.All,
                    DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-10-10T00:00:00Z", CultureInfo.InvariantCulture),
                    "America/Sao_Paulo")),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        occurrences.Value.Items
            .Select(item => item.Value.GetProperty("timing").GetProperty("evaluatedStartUtc")
                .GetProperty("value").GetString())
            .ShouldBe(["2026-10-04T12:00:00Z", "2026-10-04T12:00:00Z"]);

        var completion = await provider.GetRequiredService<ICalendarService>().CompleteTodoAsync(
            new CalendarTodoCompletionRequest(new CalendarResourceRevisionReference(
                new Uri(calendar, "open.ics").AbsoluteUri,
                "davx5-open-todo",
                CalendarEntityKind.Todo,
                openPut.EntityTag!)),
            TestContext.Current.CancellationToken);
        completion.Code.ShouldBe(CalendarEntityPatchCode.Success);
        completion.MutationState.ShouldBe(CalendarMutationState.Committed);
    }
}
