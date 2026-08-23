using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarOccurrenceToolsTests
{
    [Fact]
    public async Task StartIsMechanicalAndPublishesModulePageWithoutReserialization()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        CalendarOccurrenceQueryRequest? observed = null;
        var structured = JsonSerializer.SerializeToElement(new
        {
            outcome = "success",
            items = Array.Empty<object>(),
            diagnostics = Array.Empty<object>(),
            temporalEvaluationContext = new { timeZone = "America/New_York", source = "caller" },
            pagination = new { mode = "query_result_snapshot", nextCursor = (string?)null }
        });
        module.QueryOccurrencesAsync(
                Arg.Do<CalendarOccurrenceQueryRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Page(new QueryPage<CalendarOccurrenceQueryItem>(
                [], [], null, structured, "Occurrence query completed.", 100,
                TemporalEvaluationContext: new TemporalEvaluationContext(
                    "America/New_York", TemporalEvaluationContextSource.Caller))));
        var tool = new CalendarOccurrenceTools(module);

        var result = await tool.QueryRawAsync(StartArguments(), TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(false);
        result.StructuredContent.ShouldBe(structured);
        var start = observed.ShouldBeOfType<CalendarOccurrenceQueryRequest.Start>();
        start.PageSize.ShouldBe(1);
        start.Query.EvaluationTimeZone.ShouldBe("America/New_York");
    }

    [Fact]
    public async Task ContinueAcceptsOnlyCursorAndOptionalPageSize()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        CalendarOccurrenceQueryRequest? observed = null;
        module.QueryOccurrencesAsync(
                Arg.Do<CalendarOccurrenceQueryRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(new QueryFailure(
                QueryFailureCode.CursorExpired,
                QueryFailureCategory.State,
                "expired",
                false,
                QueryFailurePhase.Pagination)));
        var tool = new CalendarOccurrenceTools(module);

        var result = await tool.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["pageSize"] = JsonSerializer.SerializeToElement(200)
        }, TestContext.Current.CancellationToken);

        observed.ShouldBe(new CalendarOccurrenceQueryRequest.Continue("opaque", 200));
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("cursor_expired");
    }

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task InvalidStartOrMixedContinueNeverReachesModule(Dictionary<string, JsonElement> arguments)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var tool = new CalendarOccurrenceTools(module);

        var result = await tool.QueryRawAsync(arguments, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await module.DidNotReceive().QueryOccurrencesAsync(
            Arg.Any<CalendarOccurrenceQueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArgumentBeyondExactLimitFailsBeforeModule()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var tool = new CalendarOccurrenceTools(module);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement(new string('x', CalendarOccurrenceTools.MaximumArgumentBytes))
        };

        var result = await tool.QueryRawAsync(arguments, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await module.DidNotReceive().QueryOccurrencesAsync(
            Arg.Any<CalendarOccurrenceQueryRequest>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<Dictionary<string, JsonElement>> InvalidShapes => new()
    {
        new Dictionary<string, JsonElement>(),
        StartArguments(("unknown", JsonSerializer.SerializeToElement(true))),
        StartArguments(("evaluationTimeZone", JsonSerializer.SerializeToElement<string?>(null))),
        StartArguments(("pageSize", JsonSerializer.SerializeToElement(0))),
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" })
        }
    };

    private static Dictionary<string, JsonElement> StartArguments(
        params (string Name, JsonElement Value)[] replacements)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" }),
            ["from"] = JsonSerializer.SerializeToElement(new
                { kind = "utcDateTime", value = "2026-08-23T00:00:00Z" }),
            ["to"] = JsonSerializer.SerializeToElement(new
                { kind = "utcDateTime", value = "2026-08-24T00:00:00Z" }),
            ["evaluationTimeZone"] = JsonSerializer.SerializeToElement("America/New_York"),
            ["pageSize"] = JsonSerializer.SerializeToElement(1)
        };
        foreach (var replacement in replacements)
            arguments[replacement.Name] = replacement.Value;
        return arguments;
    }
}
