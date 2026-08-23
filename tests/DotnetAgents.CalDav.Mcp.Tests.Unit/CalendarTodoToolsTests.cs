using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarTodoToolsTests
{
    [Fact]
    public async Task StartPassesClosedTypedRequestAndModuleBuiltPageThroughMechanically()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var structured = JsonSerializer.SerializeToElement(new
        {
            outcome = "success",
            items = new[] { new { resultKind = "entity", uid = "todo-1" } },
            diagnostics = Array.Empty<object>(),
            excludedIndeterminateCount = 0,
            pagination = new { mode = "query_result_snapshot", nextCursor = (string?)null }
        });
        module.QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Page(new QueryPage<CalendarTodoQueryPageItem>(
                [new(structured.GetProperty("items")[0])],
                [],
                null,
                structured,
                "module-text",
                321)));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" }),
            ["completionStates"] = JsonSerializer.SerializeToElement(new[] { "open", "completed" }),
            ["projection"] = JsonSerializer.SerializeToElement(new[] { "summary", "due" }),
            ["pageSize"] = JsonSerializer.SerializeToElement(17)
        };

        var result = await new CalendarTodoTools(module).QueryRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent.ShouldBe(structured);
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Text.ShouldBe("module-text");
        await module.Received(1).QueryTodosAsync(
            Arg.Is<CalendarTodoQueryRequest.Start>(start =>
                start.PageSize == 17
                && start.Query.Scope.Mode == CalendarEntityScopeMode.All
                && start.Query.CompletionStates!.SequenceEqual(new[]
                {
                    CalendarTodoCompletionState.Open,
                    CalendarTodoCompletionState.Completed
                })
                && start.Projection.SequenceEqual(new[]
                {
                    CalendarTodoProjectionField.Summary,
                    CalendarTodoProjectionField.Due
                })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinueAcceptsOnlyCursorAndOptionalPageSize()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Failure(new QueryFailure(
                QueryFailureCode.CursorExpired,
                QueryFailureCategory.Input,
                "expired",
                false,
                QueryFailurePhase.Pagination)));
        var sut = new CalendarTodoTools(module);

        var result = await sut.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["pageSize"] = JsonSerializer.SerializeToElement(3)
        }, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("cursor_expired");
        await module.Received(1).QueryTodosAsync(
            Arg.Is<CalendarTodoQueryRequest.Continue>(continuation =>
                continuation.Cursor == "opaque" && continuation.PageSize == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrictUnionRejectsMixedUnknownNullAndOversizedShapesBeforeModule()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var sut = new CalendarTodoTools(module);
        var scope = JsonSerializer.SerializeToElement(new { mode = "all" });
        var cases = new[]
        {
            new Dictionary<string, JsonElement>
            {
                ["scope"] = scope,
                ["cursor"] = JsonSerializer.SerializeToElement("mixed")
            },
            new Dictionary<string, JsonElement>
            {
                ["scope"] = scope,
                ["unknown"] = JsonSerializer.SerializeToElement(true)
            },
            new Dictionary<string, JsonElement>
            {
                ["scope"] = scope,
                ["evaluationTimeZone"] = JsonSerializer.SerializeToElement<string?>(null)
            },
            new Dictionary<string, JsonElement>
            {
                ["cursor"] = JsonSerializer.SerializeToElement(new string('x', 2049))
            }
        };

        foreach (var arguments in cases)
        {
            var result = await sut.QueryRawAsync(arguments, CancellationToken.None);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }
        var oversized = await sut.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["scope"] = scope,
            ["unknown"] = JsonSerializer.SerializeToElement(new string('x', CalendarTodoTools.MaximumArgumentBytes))
        }, CancellationToken.None);
        oversized.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await module.DidNotReceive().QueryTodosAsync(
            Arg.Any<CalendarTodoQueryRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(QueryFailureCode.InvalidInput, "invalid_input")]
    [InlineData(QueryFailureCode.CursorExpired, "cursor_expired")]
    [InlineData(QueryFailureCode.LimitExhausted, "limit_exhausted")]
    [InlineData(QueryFailureCode.Busy, "busy")]
    [InlineData(QueryFailureCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(QueryFailureCode.UpstreamProtocolError, "upstream_protocol_error")]
    [InlineData(QueryFailureCode.UnsupportedCapability, "unsupported_capability")]
    [InlineData(QueryFailureCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(QueryFailureCode.TemporalUnresolved, "temporal_unresolved")]
    [InlineData(QueryFailureCode.RecurrenceUnevaluable, "recurrence_unevaluable")]
    [InlineData(QueryFailureCode.UpstreamUnavailable, "upstream_unavailable")]
    [InlineData(QueryFailureCode.UpstreamUnauthorized, "upstream_unauthorized")]
    [InlineData(QueryFailureCode.UpstreamForbidden, "upstream_forbidden")]
    [InlineData(QueryFailureCode.UpstreamRateLimited, "upstream_rate_limited")]
    [InlineData(QueryFailureCode.NotFound, "not_found")]
    [InlineData(QueryFailureCode.Ambiguous, "ambiguous")]
    [InlineData(QueryFailureCode.OutsideScope, "outside_scope")]
    public async Task MapsEveryClosedModuleFailure(QueryFailureCode code, string expected)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Failure(new QueryFailure(
                code,
                QueryFailureCategory.Input,
                "safe",
                false,
                QueryFailurePhase.Execution)));

        var result = await new CalendarTodoTools(module).QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" })
        }, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expected);
    }
}
