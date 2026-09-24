using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarQueryTextArgumentsTests
{
    private static readonly QueryFailure ModuleFailure = new(
        QueryFailureCode.UpstreamUnavailable,
        QueryFailureCategory.Upstream,
        "unavailable",
        true,
        QueryFailurePhase.Execution);

    [Fact]
    public async Task EntityStartCarriesTextAndCategoriesVerbatimToCore()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var observed = new List<CalendarEntityQueryRequest>();
        module.QueryEntitiesAsync(Arg.Do<CalendarEntityQueryRequest>(observed.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarEntityQueryItem>.Failure(ModuleFailure));
        var tool = new CalendarEntityTools(module);

        await tool.QueryRawAsync(EntityStart(("text", " Dentist  visit "), ("categories", new[] { "Health", "Work" })),
            CancellationToken.None);
        await tool.QueryRawAsync(EntityStart(("categories", new[] { "Health" })), CancellationToken.None);
        await tool.QueryRawAsync(EntityStart(("text", "dentist")), CancellationToken.None);
        await tool.QueryRawAsync(EntityStart(), CancellationToken.None);

        var filters = observed.Select(request => request.ShouldBeOfType<CalendarEntityQueryRequest.Start>().Query.TextFilter)
            .ToArray();
        filters[0].ShouldNotBeNull().Text.ShouldBe(" Dentist  visit ");
        filters[0]!.Categories.ShouldBe(["Health", "Work"]);
        filters[1].ShouldNotBeNull().Text.ShouldBeNull();
        filters[1]!.Categories.ShouldBe(["Health"]);
        filters[2].ShouldNotBeNull().Categories.ShouldBeNull();
        filters[3].ShouldBeNull();
    }

    [Fact]
    public async Task OccurrenceAndTodoStartsCarryTheTextFilter()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var occurrences = new List<CalendarOccurrenceQueryRequest>();
        var todos = new List<CalendarTodoQueryRequest>();
        module.QueryOccurrencesAsync(Arg.Do<CalendarOccurrenceQueryRequest>(occurrences.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(ModuleFailure));
        module.QueryTodosAsync(Arg.Do<CalendarTodoQueryRequest>(todos.Add), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Failure(ModuleFailure));

        await new CalendarOccurrenceTools(module).QueryRawAsync(
            OccurrenceStart(("text", "planning"), ("categories", new[] { "Work" })),
            CancellationToken.None);
        await new CalendarTodoTools(module).QueryRawAsync(
            TodoStart(("text", "garden"), ("categories", new[] { "Home" })),
            CancellationToken.None);

        var occurrence = occurrences.ShouldHaveSingleItem().ShouldBeOfType<CalendarOccurrenceQueryRequest.Start>();
        occurrence.Query.TextFilter!.Text.ShouldBe("planning");
        occurrence.Query.TextFilter.Categories.ShouldBe(["Work"]);
        var todo = todos.ShouldHaveSingleItem().ShouldBeOfType<CalendarTodoQueryRequest.Start>();
        todo.Query.TextFilter!.Text.ShouldBe("garden");
        todo.Query.TextFilter.Categories.ShouldBe(["Home"]);
    }

    [Fact]
    public async Task LexicallyInvalidOrContinuationFiltersNeverReachCore()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var entity = new CalendarEntityTools(module);
        var occurrence = new CalendarOccurrenceTools(module);
        var todo = new CalendarTodoTools(module);
        (string, object)[][] invalidMembers =
        [
            [("text", 42)],
            [("text", new[] { "dentist" })],
            [("categories", "Health")],
            [("categories", new object[] { "Health", 7 })],
            [("categories", new { name = "Health" })]
        ];

        var results = new List<CallToolResult>();
        foreach (var members in invalidMembers)
        {
            results.Add(await entity.QueryRawAsync(EntityStart(members), CancellationToken.None));
            results.Add(await occurrence.QueryRawAsync(OccurrenceStart(members), CancellationToken.None));
            results.Add(await todo.QueryRawAsync(TodoStart(members), CancellationToken.None));
        }
        results.Add(await entity.QueryRawAsync(Arguments(("cursor", "opaque"), ("text", "dentist")), CancellationToken.None));
        results.Add(await occurrence.QueryRawAsync(
            Arguments(("cursor", "opaque"), ("categories", new[] { "Work" })), CancellationToken.None));
        results.Add(await todo.QueryRawAsync(Arguments(("cursor", "opaque"), ("text", "dentist")), CancellationToken.None));

        results.ShouldAllBe(result => result.IsError == true
            && result.StructuredContent!.Value.GetProperty("code").GetString() == "invalid_input");
        await module.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>());
        await module.DidNotReceive().QueryOccurrencesAsync(
            Arg.Any<CalendarOccurrenceQueryRequest>(), Arg.Any<CancellationToken>());
        await module.DidNotReceive().QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>());
    }

    private static Dictionary<string, JsonElement> EntityStart(params (string Name, object Value)[] members) =>
        Arguments([("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }), .. members]);

    private static Dictionary<string, JsonElement> OccurrenceStart(params (string Name, object Value)[] members) =>
        Arguments([
            ("scope", new { mode = "all" }),
            ("from", new { kind = "utcDateTime", value = "2026-11-01T00:00:00Z" }),
            ("to", new { kind = "utcDateTime", value = "2026-11-30T00:00:00Z" }),
            .. members
        ]);

    private static Dictionary<string, JsonElement> TodoStart(params (string Name, object Value)[] members) =>
        Arguments([("scope", new { mode = "all" }), .. members]);

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object Value)[] members) =>
        members.ToDictionary(member => member.Name, member => JsonSerializer.SerializeToElement(member.Value));
}
