using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarEntityToolsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task QueryRawAsync_EnforcesExactRawArgumentBoundary(int extraByte)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var arguments = ArgumentsWithSerializedSize(CalendarEntityTools.MaximumArgumentBytes + extraByte);

        var result = await new CalendarEntityTools(module).QueryRawAsync(arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(
            extraByte == 0 ? "invalid_input" : "payload_too_large");
        await module.DidNotReceive().QueryEntitiesAsync(
            Arg.Any<CalendarEntityQueryRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryRawAsync_ConvertsStrictStartAndContinueFormsOnly()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var observed = new List<CalendarEntityQueryRequest>();
        module.QueryEntitiesAsync(Arg.Do<CalendarEntityQueryRequest>(observed.Add), Arg.Any<CancellationToken>())
            .Returns(PageReply());
        var tool = new CalendarEntityTools(module);

        var start = await tool.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" }),
            ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event", "todo" }),
            ["from"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = "2026-08-23T12:00:00Z" }),
            ["to"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = "2026-08-24T12:00:00Z" }),
            ["pageSize"] = JsonSerializer.SerializeToElement(1)
        }, CancellationToken.None);
        var continuation = await tool.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["pageSize"] = JsonSerializer.SerializeToElement(200)
        }, CancellationToken.None);

        start.IsError.ShouldBe(false);
        continuation.IsError.ShouldBe(false);
        var typedStart = observed[0].ShouldBeOfType<CalendarEntityQueryRequest.Start>();
        typedStart.PageSize.ShouldBe(1);
        typedStart.Query.Scope.ShouldBe(CalendarEntityScope.All);
        typedStart.Query.EntityKinds.ShouldBe([CalendarEntityKind.Event, CalendarEntityKind.Todo]);
        typedStart.Query.From.ShouldBe(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        observed[1].ShouldBe(new CalendarEntityQueryRequest.Continue("opaque", 200));
    }

    [Fact]
    public async Task QueryRawAsync_RejectsMixedOrIncompleteUnionBeforeTheModule()
    {
        var invalid = new IDictionary<string, JsonElement>?[]
        {
            null,
            new Dictionary<string, JsonElement>(),
            Arguments(("cursor", "opaque"), ("scope", new { mode = "all" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }), ("cursor", "opaque")),
            Arguments(("cursor", "opaque"), ("entityKinds", new[] { "event" })),
            Arguments(("cursor", "")),
            Arguments(("cursor", "opaque"), ("pageSize", 0)),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }),
                ("from", new { kind = "utcDateTime", value = "2026-08-23T12:00:00Z" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }), ("unknown", true))
        };
        var module = Substitute.For<ICalendarQueryModule>();
        var tool = new CalendarEntityTools(module);

        foreach (var arguments in invalid)
        {
            var result = await tool.QueryRawAsync(arguments, CancellationToken.None);
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }
        await module.DidNotReceive().QueryEntitiesAsync(
            Arg.Any<CalendarEntityQueryRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public async Task QueryRawAsync_EnforcesTheExactCursorCharacterBoundary(int length, bool accepted)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(PageReply());

        var result = await new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("cursor", new string('A', length))),
            CancellationToken.None);

        result.IsError.ShouldBe(!accepted);
        if (accepted)
        {
            await module.Received(1).QueryEntitiesAsync(
                Arg.Is<CalendarEntityQueryRequest.Continue>(request => request.Cursor.Length == 2048),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await module.DidNotReceive().QueryEntitiesAsync(
                Arg.Any<CalendarEntityQueryRequest>(),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task QueryRawAsync_PassesThroughTheModuleBuiltStructuredPageWithoutReprojection()
    {
        var structured = JsonSerializer.SerializeToElement(new
        {
            outcome = "success",
            items = new[] { new { marker = "module-built" } },
            diagnostics = Array.Empty<object>(),
            pagination = new { mode = "query_result_snapshot", nextCursor = (string?)null }
        });
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarEntityQueryItem>.Page(new QueryPage<CalendarEntityQueryItem>(
                [],
                [],
                null,
                structured,
                "Calendar Entity query completed.",
                0)));

        var result = await new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" })),
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetRawText().ShouldBe(structured.GetRawText());
    }

    [Fact]
    public async Task QueryRawAsync_MechanicallyMapsTheClosedFailureVocabulary()
    {
        var cases = new (QueryFailure Failure, string Code, string Category, string Phase)[]
        {
            (new(QueryFailureCode.CursorExpired, QueryFailureCategory.State, "expired", false,
                QueryFailurePhase.Pagination), "cursor_expired", "state", "pagination"),
            (new(QueryFailureCode.Busy, QueryFailureCategory.LimitsAndAdmission, "busy", true,
                QueryFailurePhase.Pagination, RetryAfterMs: 123), "busy", "limitsAndAdmission", "pagination"),
            (new(QueryFailureCode.ConcurrencyUnavailable, QueryFailureCategory.State, "etag", false,
                QueryFailurePhase.TargetRevision), "concurrency_unavailable", "state", "targetRevision"),
            (new(QueryFailureCode.UpstreamRateLimited, QueryFailureCategory.Upstream, "rate", true,
                QueryFailurePhase.Execution), "upstream_rate_limited", "upstream", "execution"),
            (new(QueryFailureCode.TemporalUnresolved, QueryFailureCategory.CapabilityAndProjection, "time", false,
                QueryFailurePhase.CompleteResourceSemantics), "temporal_unresolved", "capabilityAndProjection",
                "completeResourceSemantics")
        };
        foreach (var testCase in cases)
        {
            var module = Substitute.For<ICalendarQueryModule>();
            module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
                .Returns(new QueryReply<CalendarEntityQueryItem>.Failure(testCase.Failure));

            var result = await new CalendarEntityTools(module).QueryRawAsync(
                Arguments(("cursor", "opaque")),
                CancellationToken.None);

            result.IsError.ShouldBe(true);
            var error = result.StructuredContent!.Value;
            error.GetProperty("code").GetString().ShouldBe(testCase.Code);
            error.GetProperty("category").GetString().ShouldBe(testCase.Category);
            error.GetProperty("phase").GetString().ShouldBe(testCase.Phase);
            if (testCase.Failure.RetryAfterMs is not null)
                error.GetProperty("retryAfterMs").GetInt32().ShouldBe(123);
        }
    }

    [Fact]
    public async Task QueryRawAsync_PropagatesCallerCancellationToTheModule()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellation(call.ArgAt<CancellationToken>(1)));
        using var cancellation = new CancellationTokenSource();
        var pending = new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("cursor", "opaque")),
            cancellation.Token);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void EnsureBoundedResult_ProtectsHumanAndStructuredBudgets()
    {
        var human = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { diagnostics = Array.Empty<object>() }),
            Content = [new TextContentBlock { Text = new string('x', CalendarEntityTools.MaximumHumanReadableBytes) }]
        };
        CalendarEntityTools.EnsureBoundedResult(human).IsError.ShouldBe(true);

        var structured = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                padding = new string('x', CalendarEntityTools.MaximumStructuredResultBytes)
            }),
            Content = [new TextContentBlock { Text = "ok" }]
        };
        CalendarEntityTools.EnsureBoundedResult(structured).IsError.ShouldBe(true);
    }

    [Fact]
    public async Task ActualSdkEnvelopeMatchesTheModuleAccountant()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([calendarHref + "a.ics"]);
        client.GetCalendarResourcesForQueryAsync(
                calendarHref,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns([CalendarResourceRead.Success(
                calendarHref + "a.ics",
                "\"r1\"",
                Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTAMP:20260823T120000Z\r\nDTSTART:20260824T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"))]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        services.AddSingleton(client);
        await using var provider = services.BuildServiceProvider();
        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(
                new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var actual = await new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" })),
            CancellationToken.None);

        CalendarEntityTools.MeasureResult(actual).ShouldBe(page.Value.MeasuredCallToolResultBytes);
        actual.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text.ShouldBe(page.Value.HumanText);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ActualSdkEnvelopeProvesFourMiBBelowAtAndAboveWithSeparatorAndCursor(int delta)
    {
        static CallToolResult Result(int padding) => new()
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                outcome = "success",
                items = new object[] { new { marker = 1 }, new { padding = new string('x', padding) } },
                diagnostics = Array.Empty<object>(),
                pagination = new { mode = "query_result_snapshot", nextCursor = "opaque-cursor" }
            }),
            Content = [new TextContentBlock { Text = "Calendar Entity query completed." }]
        };
        var baseline = CalendarEntityTools.MeasureResult(Result(0));
        var actual = Result(CalendarEntityTools.MaximumStructuredResultBytes - baseline + delta);

        CalendarEntityTools.MeasureResult(actual).ShouldBe(CalendarEntityTools.MaximumStructuredResultBytes + delta);
        var bounded = CalendarEntityTools.EnsureBoundedResult(actual);
        bounded.IsError.ShouldBe(delta > 0);
    }

    private static QueryReply<CalendarEntityQueryItem> PageReply()
    {
        var structured = JsonSerializer.SerializeToElement(new
        {
            outcome = "success",
            items = Array.Empty<object>(),
            diagnostics = Array.Empty<object>(),
            pagination = new { mode = "query_result_snapshot", nextCursor = (string?)null }
        });
        return new QueryReply<CalendarEntityQueryItem>.Page(new QueryPage<CalendarEntityQueryItem>(
            [],
            [],
            null,
            structured,
            "Calendar Entity query completed.",
            0));
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object Value)[] values) =>
        values.ToDictionary(
            item => item.Name,
            item => JsonSerializer.SerializeToElement(item.Value),
            StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> ArgumentsWithSerializedSize(int targetBytes)
    {
        var arguments = Arguments(
            ("scope", new { mode = "default" }),
            ("entityKinds", new[] { "event" }),
            ("unknown", string.Empty));
        var overhead = JsonSerializer.SerializeToUtf8Bytes(arguments).Length;
        arguments["unknown"] = JsonSerializer.SerializeToElement(new string('x', targetBytes - overhead));
        return arguments;
    }

    private static async Task<QueryReply<CalendarEntityQueryItem>> WaitForCancellation(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return PageReply();
    }
}
