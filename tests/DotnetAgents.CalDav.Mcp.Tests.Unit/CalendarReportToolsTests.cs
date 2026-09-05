using System.Net;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class CalendarReportToolsTests
{
    private const string CalendarHref = "https://cal.example/cal/";
    private const string Checkpoint = "cs1_0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset From = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreeBusyMapsExactWindowAndPreservesStructuredServerTruth()
    {
        var module = Substitute.For<ICalendarReportModule>();
        module.FreeBusyAsync(Arg.Any<CalendarFreeBusyRequest>(), Arg.Any<CancellationToken>()).Returns(new CalendarFreeBusyResult(
            CalendarHref, "2026-09-05T00:00:00Z", "2026-09-06T00:00:00Z",
            [new("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "X-team-focus")]));

        var result = await new CalendarReportTools(module).FreeBusyRawAsync(Arguments("""
            {"calendarHref":"https://cal.example/cal/","from":"2026-09-05T00:00:00Z","to":"2026-09-06T00:00:00Z"}
            """), CancellationToken.None);

        result.IsError.ShouldBe(false);
        var content = result.StructuredContent!.Value;
        content.GetProperty("outcome").GetString().ShouldBe("success");
        content.GetProperty("complete").GetBoolean().ShouldBeTrue();
        content.GetProperty("temporalAuthority").GetString().ShouldBe("server");
        content.GetProperty("periods")[0].GetProperty("busyType").GetString().ShouldBe("X-team-focus");
        await module.Received(1).FreeBusyAsync(new(CalendarHref, From, From.AddDays(1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangesDistinguishesStartFromContinuationAndOmitsRemovedEtag()
    {
        var module = Substitute.For<ICalendarReportModule>();
        module.ChangesAsync(Arg.Any<CalendarResourceChangesRequest>(), Arg.Any<CancellationToken>()).Returns(new CalendarResourceChangesResult(
            CalendarHref, "initial", [new(CalendarHref + "gone.ics", "removed")], Checkpoint, true));
        var tools = new CalendarReportTools(module);

        var first = await tools.ChangesRawAsync(Arguments("{\"calendarHref\":\"https://cal.example/cal/\"}"), CancellationToken.None);
        var next = await tools.ChangesRawAsync(Arguments(JsonSerializer.Serialize(new { checkpoint = Checkpoint, pageSize = 500 })), CancellationToken.None);

        first.IsError.ShouldBe(false);
        next.IsError.ShouldBe(false);
        var content = first.StructuredContent!.Value;
        content.GetProperty("checkpointLifetime").GetString().ShouldBe("session");
        content.GetProperty("mode").GetString().ShouldBe("initial");
        content.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        content.GetProperty("removalMeaning").GetString().ShouldBe("removed_from_view");
        content.GetProperty("changes")[0].TryGetProperty("etag", out _).ShouldBeFalse();
        await module.Received(1).ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), Arg.Any<CancellationToken>());
        await module.Received(1).ChangesAsync(new CalendarResourceChangesRequest.Continue(Checkpoint, 500), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"calendarHref\":null,\"from\":\"2026-09-05T00:00:00Z\",\"to\":\"2026-09-06T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"\",\"from\":\"2026-09-05T00:00:00Z\",\"to\":\"2026-09-06T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"from\":\"2026-09-05T00:00:00+00:00\",\"to\":\"2026-09-06T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"from\":\"2026-09-05T00:00:00.1Z\",\"to\":\"2026-09-06T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"from\":\"2026-02-30T00:00:00Z\",\"to\":\"2026-09-06T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"from\":\"2026-09-05T00:00:00Z\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"from\":\"2026-09-05T00:00:00Z\",\"to\":\"2026-09-06T00:00:00Z\",\"evaluationTimeZone\":\"UTC\"}")]
    public async Task MalformedFreeBusyArgumentsNeverReachModule(string? json)
    {
        var module = Substitute.For<ICalendarReportModule>();

        var result = await new CalendarReportTools(module).FreeBusyRawAsync(json is null ? null : Arguments(json), CancellationToken.None);

        ErrorCode(result).ShouldBe("invalid_input");
        module.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"checkpoint\":\"ambiguous\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"cursor\":\"query-cursor\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":0}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":501}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":1.5}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":2147483648}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":\"100\"}")]
    [InlineData("{\"calendarHref\":\"https://cal.example/cal/\",\"pageSize\":null}")]
    [InlineData("{\"checkpoint\":null}")]
    [InlineData("{\"checkpoint\":\"\"}")]
    [InlineData("{\"checkpoint\":\"next\",\"from\":\"2026-09-05T00:00:00Z\"}")]
    public async Task AmbiguousOrMalformedChangesArgumentsNeverReachModule(string? json)
    {
        var module = Substitute.For<ICalendarReportModule>();

        var result = await new CalendarReportTools(module).ChangesRawAsync(json is null ? null : Arguments(json), CancellationToken.None);

        ErrorCode(result).ShouldBe("invalid_input");
        module.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ArgumentAndCheckpointLimitsFailBeforeModuleDispatch()
    {
        var module = Substitute.For<ICalendarReportModule>();
        var tools = new CalendarReportTools(module);
        var arguments = new Dictionary<string, JsonElement> { ["checkpoint"] = JsonSerializer.SerializeToElement(new string('a', 37)) };
        ErrorCode(await tools.ChangesRawAsync(arguments, CancellationToken.None)).ShouldBe("invalid_input");
        arguments["checkpoint"] = JsonSerializer.SerializeToElement(new string('a', 256 * 1024));
        ErrorCode(await tools.ChangesRawAsync(arguments, CancellationToken.None)).ShouldBe("payload_too_large");
        ErrorCode(await tools.FreeBusyRawAsync(arguments, CancellationToken.None)).ShouldBe("payload_too_large");
        module.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(401, "upstream_unauthorized")]
    [InlineData(403, "upstream_forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(405, "unsupported_capability")]
    [InlineData(413, "payload_too_large")]
    [InlineData(429, "upstream_rate_limited")]
    [InlineData(500, "upstream_unavailable")]
    [InlineData(501, "unsupported_capability")]
    public async Task UpstreamFailuresNeverBecomeAnEmptyAvailabilityResult(int status, string code)
    {
        var module = Substitute.For<ICalendarReportModule>();
        module.FreeBusyAsync(Arg.Any<CalendarFreeBusyRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarFreeBusyResult>(new HttpRequestException("secret upstream details", null, (HttpStatusCode)status)));

        var result = await new CalendarReportTools(module).FreeBusyRawAsync(Arguments("""
            {"calendarHref":"https://cal.example/cal/","from":"2026-09-05T00:00:00Z","to":"2026-09-06T00:00:00Z"}
            """), CancellationToken.None);

        ErrorCode(result).ShouldBe(code);
        result.StructuredContent!.Value.TryGetProperty("periods", out _).ShouldBeFalse();
        result.StructuredContent.Value.GetRawText().ShouldNotContain("secret upstream details");
    }

    [Fact]
    public async Task ExpiredCheckpointHasActionableResetWithoutAReplacementCheckpoint()
    {
        var module = Substitute.For<ICalendarReportModule>();
        module.ChangesAsync(Arg.Any<CalendarResourceChangesRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarResourceChangesResult>(new CalendarProtocolException(
                "sync_reset_required", "Start again with calendarHref and no checkpoint to rebuild inventory.")));

        var result = await new CalendarReportTools(module).ChangesRawAsync(Arguments("{\"checkpoint\":\"expired\"}"), CancellationToken.None);

        ErrorCode(result).ShouldBe("sync_reset_required");
        result.StructuredContent!.Value.TryGetProperty("checkpoint", out _).ShouldBeFalse();
        result.StructuredContent.Value.GetProperty("message").GetString()!.ShouldContain("rebuild inventory");
    }

    private static Dictionary<string, JsonElement> Arguments(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static string ErrorCode(CallToolResult result)
    {
        result.IsError.ShouldBe(true);
        return result.StructuredContent!.Value.GetProperty("code").GetString()!;
    }
}
