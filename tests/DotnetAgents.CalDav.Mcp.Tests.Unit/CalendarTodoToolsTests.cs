using System.Text;
using System.Text.Json;
using System.Net;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using NSubstitute;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarTodoToolsTests
{
    [Fact]
    public async Task QueryCoreAsync_ReturnsCompactOpenTodoWithRevisionTarget()
    {
        var service = Substitute.For<ICalendarService>();
        var snapshot = Snapshot("todo-1", "STATUS:NEEDS-ACTION\r\nSUMMARY:Buy milk\r\n");
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success([
                new CalendarTodoQueryItem(
                    CalendarTodoQueryResultKind.Entity,
                    snapshot,
                    null,
                    new(CalendarTodoCompletionState.Open, "NEEDS-ACTION", null, null, []),
                    null,
                    null,
                    null,
                    null,
                    false)
            ]));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cursor = new CalendarEntityCursorProtector(
            time,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var sut = new CalendarTodoTools(service, cursor, time);

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            null,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("items")[0].GetProperty("completionState").GetString()
            .ShouldBe("open");
        result.StructuredContent.Value.GetProperty("items")[0].TryGetProperty("calendarProperties", out _)
            .ShouldBeFalse();
        result.StructuredContent.Value.GetProperty("items")[0].GetProperty("summary").GetString()
            .ShouldBe("Buy milk");
        result.StructuredContent.Value.GetProperty("items")[0].GetProperty("completionTarget")
            .GetProperty("entityRevision").GetProperty("entityUid").GetString().ShouldBe("todo-1");
        await service.Received(1).QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryCoreAsync_RejectsDefaultScopeAndInvalidBoundsProjectionAndCursor()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateSut(service);
        var cases = new Func<Task<CallToolResult>>[]
        {
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("default"), null, CancellationToken.None),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None, pageSize: 0),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["unknown"], CancellationToken.None),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                from: new CalendarEntityUtcArgument("utcDateTime", "2026-08-19T00:00:00Z")),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                projection: ["unknown"]),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["open", "open"], CancellationToken.None),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                dueFrom: new CalendarEntityUtcArgument("utcDateTime", "2026-08-19T00:00:00Z")),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("invalid")), null, CancellationToken.None),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                from: new CalendarEntityUtcArgument("utcDateTime", "not-a-time"),
                to: new CalendarEntityUtcArgument("utcDateTime", "2026-08-20T00:00:00Z")),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                from: new CalendarEntityUtcArgument("utcDateTime", "2026-08-19T00:00:00.123X"),
                to: new CalendarEntityUtcArgument("utcDateTime", "2026-08-20T00:00:00Z")),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                projection: ["summary", "summary"]),
            () => sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
                cursor: new string('x', CalendarEntityCursorProtector.MaximumCursorCharacters + 1))
        };

        foreach (var testCase in cases)
        {
            var result = await testCase();
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }
        await service.DidNotReceive().QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryRawAsync_RejectsUnknownShapeAndOversizedArguments()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateSut(service);
        var scope = JsonSerializer.SerializeToElement(new { mode = "all" });
        var unknown = await sut.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["scope"] = scope,
            ["unknown"] = JsonSerializer.SerializeToElement(true)
        }, CancellationToken.None);
        var oversized = await sut.QueryRawAsync(new Dictionary<string, JsonElement>
        {
            ["scope"] = scope,
            ["padding"] = JsonSerializer.SerializeToElement(new string('x', CalendarTodoTools.MaximumArgumentBytes))
        }, CancellationToken.None);

        unknown.IsError.ShouldBe(true);
        unknown.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        oversized.IsError.ShouldBe(true);
        oversized.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Theory]
    [InlineData(CalendarTodoQueryCode.InvalidInput)]
    [InlineData(CalendarTodoQueryCode.UnsafeScope)]
    [InlineData(CalendarTodoQueryCode.NotFound)]
    [InlineData(CalendarTodoQueryCode.Ambiguous)]
    [InlineData(CalendarTodoQueryCode.OutsideScope)]
    [InlineData(CalendarTodoQueryCode.UnsupportedCapability)]
    [InlineData(CalendarTodoQueryCode.ConcurrencyUnavailable)]
    [InlineData(CalendarTodoQueryCode.LimitExhausted)]
    [InlineData(CalendarTodoQueryCode.PayloadTooLarge)]
    [InlineData(CalendarTodoQueryCode.TemporalUnresolved)]
    [InlineData(CalendarTodoQueryCode.RecurrenceUnevaluable)]
    public async Task QueryCoreAsync_MapsClosedServiceFailures(CalendarTodoQueryCode code)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Failure(code));
        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "upstream_protocol_error", "execution")]
    [InlineData(HttpStatusCode.BadGateway, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized", "execution")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden", "execution")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited", "execution")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "admissionAndPayload")]
    [InlineData(HttpStatusCode.Conflict, "conflict", "execution")]
    [InlineData(HttpStatusCode.PreconditionFailed, "conflict", "execution")]
    [InlineData(HttpStatusCode.RequestTimeout, "upstream_protocol_error", "execution")]
    [InlineData(HttpStatusCode.InsufficientStorage, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.InternalServerError, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.BadRequest, "upstream_protocol_error", "execution")]
    [InlineData(null, "upstream_unavailable", "execution")]
    public async Task QueryCoreAsync_MapsHttpFailuresWithoutLeakingUpstreamDetails(
        HttpStatusCode? status,
        string expectedCode,
        string expectedPhase)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarTodoQueryResult>>(_ => throw new HttpRequestException("secret", null, status));

        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        result.StructuredContent.Value.ToString().ShouldNotContain("secret");
    }

    [Fact]
    public async Task QueryCoreAsync_MapsObservedLimitsAndCandidatesWithoutPartialItems()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Failure(
                CalendarTodoQueryCode.NotFound,
                [new CalendarDescriptor
                {
                    Href = "https://cal.example/todos/",
                    DisplayName = "Tasks",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.NotAdvertised,
                    TodoSupport = EntityKindSupport.Advertised
                }]));

        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("authorizedCandidates").GetArrayLength().ShouldBe(1);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBe(false);

        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Failure(
                CalendarTodoQueryCode.LimitExhausted,
                limits: new CalendarEntityQueryExecutionLimits(ResourcesInspected: 2, OccurrenceCount: 3, ByteCount: 4)));
        result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);
        result.StructuredContent!.Value.GetProperty("limits").GetProperty("resourcesInspected").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task QueryRawAsync_DeserializesAllOptionalArgumentsAndRejectsPresentNull()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success([]));
        var sut = CreateSut(service);
        var utc = new { kind = "utcDateTime", value = "2026-08-19T00:00:00Z" };
        var valid = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" }),
            ["completionStates"] = JsonSerializer.SerializeToElement(new[] { "open", "completed", "cancelled", "indeterminate" }),
            ["from"] = JsonSerializer.SerializeToElement(utc),
            ["to"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = "2026-08-20T00:00:00Z" }),
            ["evaluationTimeZone"] = JsonSerializer.SerializeToElement("America/Sao_Paulo"),
            ["dueFrom"] = JsonSerializer.SerializeToElement(utc),
            ["dueTo"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = "2026-08-20T00:00:00Z" }),
            ["projection"] = JsonSerializer.SerializeToElement(new[] { "summary", "due" }),
            ["pageSize"] = JsonSerializer.SerializeToElement(10)
        };

        var result = await sut.QueryRawAsync(valid, CancellationToken.None);
        result.IsError.ShouldBe(false);
        await service.Received(1).QueryTodosAsync(Arg.Is<CalendarTodoQuery>(query =>
            query.Scope.Mode == CalendarEntityScopeMode.All
            && query.CompletionStates!.Count == 4
            && query.From.HasValue
            && query.DueTo.HasValue), Arg.Any<CancellationToken>());

        var withNull = new Dictionary<string, JsonElement>(valid)
        {
            ["evaluationTimeZone"] = JsonSerializer.SerializeToElement<string?>(null)
        };
        var invalid = await sut.QueryRawAsync(withNull, CancellationToken.None);
        invalid.IsError.ShouldBe(true);
        invalid.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");

        var scopeOnly = await sut.QueryRawAsync(
            new Dictionary<string, JsonElement> { ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" }) },
            CancellationToken.None);
        scopeOnly.IsError.ShouldBe(false);

        var nullScope = await sut.QueryRawAsync(
            new Dictionary<string, JsonElement> { ["scope"] = JsonDocument.Parse("null").RootElement },
            CancellationToken.None);
        nullScope.IsError.ShouldBe(true);
    }

    [Fact]
    public async Task QueryCoreAsync_RejectsOneCompactItemThatExceedsResultBudget()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success([
                new(CalendarTodoQueryResultKind.Entity,
                    Snapshot("huge", $"SUMMARY:{new string('x', 70_000)}\r\n"),
                    null,
                    new(CalendarTodoCompletionState.Open, "NEEDS-ACTION", null, null, []),
                    null, null, null, null, false)
            ]));

        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Fact]
    public async Task QueryCoreAsync_RejectsOversizedDiagnosticsBeforeReturningSuccess()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success([], Enumerable.Range(0, 5_000)
                .Select(index => new CalendarResourceDiagnostic("diagnostic", new string('x', 20), CalendarResourceDiagnosticSeverity.Warning))
                .ToArray()));

        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Fact]
    public async Task QueryCoreAsync_PaginatesWithBoundCursorAndProjectsAllFields()
    {
        var service = Substitute.For<ICalendarService>();
        var first = Snapshot("todo-z", "SUMMARY:First\r\nSTATUS:NEEDS-ACTION\r\nDTSTART:20260819T090000Z\r\nDUE:20260819T100000Z\r\nPRIORITY:3\r\nCATEGORIES:work,home\r\nDESCRIPTION:Details\r\nRRULE:FREQ=DAILY;COUNT=2\r\n");
        var second = Snapshot("todo-a", "SUMMARY:Second\r\nDUE:20260819T110000Z\r\n");
        var completion = new CalendarTodoCompletionClassification(CalendarTodoCompletionState.Open, "NEEDS-ACTION", null, null, []);
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>()).Returns(
            CalendarTodoQueryResult.Success([
                new(CalendarTodoQueryResultKind.Entity, first, null, completion,
                    new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T100000Z"),
                    new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero), null, null, true),
                new(CalendarTodoQueryResultKind.Entity, second, null, completion,
                    new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T110000Z"),
                    new DateTimeOffset(2026, 8, 19, 11, 0, 0, TimeSpan.Zero), null, null, false)
            ]));
        var sut = CreateSut(service);
        var projection = new[] { "summary", "status", "completedAt", "percentComplete", "due", "priority", "categories", "start", "description", "recurrence" };

        var page = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
            projection: projection, pageSize: 1);
        var cursor = page.StructuredContent!.Value.GetProperty("pagination").GetProperty("nextCursor").GetString();
        var next = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None,
            projection: projection, pageSize: 1, cursor: cursor);

        page.IsError.ShouldBe(false);
        page.StructuredContent.Value.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        page.StructuredContent.Value.GetProperty("items")[0].GetProperty("recurrence").ValueKind
            .ShouldBe(JsonValueKind.Object);
        cursor.ShouldNotBeNullOrWhiteSpace();
        next.IsError.ShouldBe(false);
        next.StructuredContent!.Value.GetProperty("items").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("uid").GetString().ShouldBe("todo-a");
    }

    [Fact]
    public async Task QueryCoreAsync_ProjectsTemporalOccurrenceAndUnresolvedTargets()
    {
        var service = Substitute.For<ICalendarService>();
        var completed = Snapshot("completed", "SUMMARY:Done\r\nDESCRIPTION:Details\r\nSTATUS:COMPLETED\r\nCOMPLETED:20260819T110000Z\r\nPERCENT-COMPLETE:100\r\nDUE;VALUE=DATE:20260820\r\nDTSTART:20260819T090000Z\r\nPRIORITY:1\r\nCATEGORIES:work,home\r\nRRULE:FREQ=DAILY;COUNT=2\r\n");
        var completedDue = new CalendarTemporalValue(CalendarTemporalKind.Date, "20260820");
        var completedStart = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T090000Z");
        var occurrence = new CalendarOccurrenceSnapshot(
            completed,
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T090000Z"),
            new CalendarOccurrenceTiming(completedStart, completedStart, completedDue, completedDue));
        var classification = new CalendarTodoCompletionClassification(
            CalendarTodoCompletionState.Completed, "COMPLETED",
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T110000Z"), 100, []);
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success([
                new(CalendarTodoQueryResultKind.Entity, completed, null, classification, completedDue,
                    new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero), completedStart,
                    new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero), true),
                new(CalendarTodoQueryResultKind.Occurrence, completed, occurrence, classification, completedDue,
                    new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero), completedStart,
                    new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero), false),
                new(CalendarTodoQueryResultKind.Unresolved, completed, null,
                    new(CalendarTodoCompletionState.Indeterminate, null, null, null,
                        [new CalendarResourceDiagnostic("temporal_unresolved", "timing", CalendarResourceDiagnosticSeverity.Warning)]),
                    null, null, null, null, false)
            ]));

        var projection = new[] { "summary", "status", "completedAt", "percentComplete", "due", "priority", "categories", "start", "description", "recurrence" };
        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None, projection: projection, pageSize: 3);

        result.IsError.ShouldBe(false);
        var items = result.StructuredContent!.Value.GetProperty("items").EnumerateArray().ToArray();
        items.Length.ShouldBe(3);
        items[0].GetProperty("completionTarget").GetProperty("kind").GetString().ShouldBe("occurrence_required");
        items[1].GetProperty("completionTarget").GetProperty("kind").GetString().ShouldBe("direct");
        items[1].GetProperty("completionTarget").TryGetProperty("recurrenceIdentity", out _).ShouldBe(false);
        items[2].GetProperty("completionTarget").GetProperty("kind").GetString().ShouldBe("unavailable");
        items[0].GetProperty("due").GetProperty("evaluatedUtc").GetProperty("value").GetString()
            .ShouldBe("2026-08-20T00:00:00Z");
        items[0].GetProperty("categories").GetArrayLength().ShouldBe(2);
        items[0].GetProperty("completedAt").GetProperty("value").GetString().ShouldBe("2026-08-19T11:00:00Z");
    }

    [Fact]
    public async Task QueryCoreAsync_UsesAllRecurrenceIdentityKindsAndHonorsMinimalProjection()
    {
        var service = Substitute.For<ICalendarService>();
        var snapshot = Snapshot("identity", "SUMMARY:Identity\r\nSTATUS:NEEDS-ACTION\r\n");
        var timing = new CalendarOccurrenceTiming(
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T090000Z"),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "20260819T090000Z"));
        var completion = new CalendarTodoCompletionClassification(CalendarTodoCompletionState.Open, "NEEDS-ACTION", null, null, []);
        var rows = new[]
        {
            (CalendarTemporalKind.Date, "20260819"),
            (CalendarTemporalKind.FloatingDateTime, "20260819T090000"),
            (CalendarTemporalKind.ZonedDateTime, "20260819T090000")
        }.Select((identity, index) => new CalendarTodoQueryItem(
            CalendarTodoQueryResultKind.Occurrence,
            snapshot with { ResourceHref = $"https://cal.example/todos/identity-{index}.ics" },
            new CalendarOccurrenceSnapshot(
                snapshot,
                new CalendarTemporalValue(identity.Item1, identity.Item2, identity.Item1 == CalendarTemporalKind.ZonedDateTime ? "America/Sao_Paulo" : null),
                timing),
            completion,
            null, null, null, null, true)).ToArray();
        service.QueryTodosAsync(Arg.Any<CalendarTodoQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarTodoQueryResult.Success(rows));

        var result = await CreateSut(service).QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), null, CancellationToken.None, projection: ["summary"], pageSize: 3);

        result.IsError.ShouldBe(false);
        var items = result.StructuredContent!.Value.GetProperty("items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("completionTarget").GetProperty("recurrenceIdentity").GetProperty("kind").GetString())
            .ShouldBe(["date", "floatingDateTime", "zonedDateTime"]);
        items.All(item => !item.TryGetProperty("priority", out var ignored)).ShouldBe(true);
    }

    private static CalendarTodoTools CreateSut(ICalendarService service)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cursor = new CalendarEntityCursorProtector(
            time,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new CalendarTodoTools(service, cursor, time);
    }

    private static CalendarResourceSnapshot Snapshot(string uid, string properties)
    {
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//tests//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260819T100000Z\r\n{properties}END:VTODO\r\nEND:VCALENDAR\r\n";
        return new(
            "https://cal.example/todos/",
            $"https://cal.example/todos/{uid}.ics",
            "\"1\"",
            Encoding.UTF8.GetBytes(ics),
            [],
            new(CalendarResourceProjectionKind.Todo, uid, null),
            []);
    }
}
