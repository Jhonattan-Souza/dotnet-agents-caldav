using System.Text;
using System.Net;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarEntityToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "invalid_input")]
    [InlineData(1, "payload_too_large")]
    public async Task QueryRawAsync_EnforcesExactArgumentByteBoundary(int extraByte, string expectedCode)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = ArgumentsWithSerializedSize(CalendarEntityTools.MaximumArgumentBytes + extraByte);

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        JsonSerializer.SerializeToUtf8Bytes(arguments).Length.ShouldBe(CalendarEntityTools.MaximumArgumentBytes + extraByte);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void EnsureBoundedResult_EnforcesExactHumanReadableByteBoundary(int extraByte, bool rejected)
    {
        var result = ResultWithHumanReadableSize(CalendarEntityTools.MaximumHumanReadableBytes + extraByte);

        var bounded = CalendarEntityTools.EnsureBoundedResult(result);

        CalendarEntityTools.MeasureHumanReadableResult(result)
            .ShouldBe(CalendarEntityTools.MaximumHumanReadableBytes + extraByte);
        bounded.IsError.ShouldBe(rejected);
        if (rejected)
            bounded.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void EnsureBoundedResult_EnforcesExactStructuredByteBoundary(int extraByte, bool rejected)
    {
        var result = ResultWithStructuredSize(CalendarEntityTools.MaximumStructuredResultBytes + extraByte);

        var bounded = CalendarEntityTools.EnsureBoundedResult(result);

        CalendarEntityTools.MeasureResult(result)
            .ShouldBe(CalendarEntityTools.MaximumStructuredResultBytes + extraByte);
        bounded.IsError.ShouldBe(rejected);
        if (rejected)
            bounded.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Theory]
    [InlineData(CalendarEntityQueryCode.InvalidInput, "invalid_input")]
    [InlineData(CalendarEntityQueryCode.UnsafeScope, "invalid_input")]
    [InlineData(CalendarEntityQueryCode.NotFound, "not_found")]
    [InlineData(CalendarEntityQueryCode.Ambiguous, "ambiguous")]
    [InlineData(CalendarEntityQueryCode.OutsideScope, "outside_scope")]
    [InlineData(CalendarEntityQueryCode.EntityKindMismatch, "entity_kind_mismatch")]
    [InlineData(CalendarEntityQueryCode.UnsupportedCapability, "unsupported_capability")]
    [InlineData(CalendarEntityQueryCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(CalendarEntityQueryCode.LimitExhausted, "limit_exhausted")]
    [InlineData(CalendarEntityQueryCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(CalendarEntityQueryCode.UpstreamProtocolError, "upstream_protocol_error")]
    [InlineData(CalendarEntityQueryCode.TemporalUnresolved, "temporal_unresolved")]
    [InlineData(CalendarEntityQueryCode.RecurrenceUnevaluable, "recurrence_unevaluable")]
    public async Task QueryAsync_MapsEveryDomainFailure(CalendarEntityQueryCode domainCode, string expectedCode)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Failure(domainCode));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_RejectsInvalidTypedShapeVariantsBeforeService()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var validFrom = new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:00Z");
        var validTo = new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T13:00:00Z");
        var calls = new Task<ModelContextProtocol.Protocol.CallToolResult>[]
        {
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("default", new CalendarEntityReferenceArgument("name", "x")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all", new CalendarEntityReferenceArgument("name", "x")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("bad"), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected"), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("bad", "x")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name", " x ")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name", "x", "https://cal.example/x/")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("href")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("href", "x", "https://cal.example/x/")), ["event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), [], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event", "todo", "event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event", "event"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["journal"], CancellationToken.None),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, from: validFrom),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, to: validTo),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, new CalendarEntityUtcArgument("date", validFrom.Value), validTo),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, new CalendarEntityUtcArgument("utcDateTime", "bad"), validTo),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:00.xZ"), validTo),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 0),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 201),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, cursor: new string('a', 2049))
        };

        foreach (var call in calls)
        {
            var result = await call;
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }
        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"scope\":null,\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":1},\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":\"selected\",\"calendar\":1},\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":\"selected\",\"calendar\":{\"by\":1,\"name\":\"x\"}},\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"name\",\"href\":\"x\"}},\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":[\"event\"],\"from\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":[\"event\"],\"to\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":[\"event\"],\"pageSize\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":[\"event\"],\"cursor\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"default\"},\"entityKinds\":[\"event\"],\"from\":{\"kind\":\"utcDateTime\"}}")]
    public async Task QueryRawAsync_RejectsInvalidRawShapes(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = json == "null"
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryRawAsync_AcceptsCompleteFrozenShapeAndDeserializesOptionalValues()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEntityQuery? observed = null;
        service.QueryEntitiesAsync(Arg.Do<CalendarEntityQuery>(query => observed = query), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"scope\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\"https://cal.example/a/\"}},"
            + "\"entityKinds\":[\"event\",\"todo\"],"
            + "\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"},"
            + "\"to\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T13:00:00Z\"},"
            + "\"pageSize\":25}");

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull();
        observed.EntityKinds.ShouldBe([CalendarEntityKind.Event, CalendarEntityKind.Todo]);
        observed.Scope.Calendar!.Href.ShouldBe("https://cal.example/a/");
    }

    [Fact]
    public async Task QueryRawAsync_AcceptsSelectedNameWithInternalSpaces()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEntityQuery? observed = null;
        service.QueryEntitiesAsync(Arg.Do<CalendarEntityQuery>(query => observed = query), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Failure(CalendarEntityQueryCode.NotFound));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"scope\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"name\",\"name\":\"No such authorized calendar\"}},"
            + "\"entityKinds\":[\"todo\"]}");

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("not_found");
        observed.ShouldNotBeNull();
        observed.Scope.Calendar!.Name.ShouldBe("No such authorized calendar");
    }

    [Fact]
    public async Task QueryRawAsync_RejectsOversizedRawBodyBeforeShapeOrService()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["oversized"] = JsonSerializer.SerializeToElement(new string('x', 270_000))
        };

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_PaginatesByCanonicalCalendarAndResourceKey()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics"),
                Snapshot("https://cal.example/a/", "https://cal.example/a/2.ics"),
                Snapshot("https://cal.example/b/", "https://cal.example/b/1.ics")
            ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var first = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            ["event"],
            CancellationToken.None,
            pageSize: 2);

        first.IsError.ShouldBe(false);
        var firstItems = first.StructuredContent!.Value.GetProperty("items");
        firstItems.GetArrayLength().ShouldBe(2);
        var cursor = first.StructuredContent.Value.GetProperty("pagination").GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();
        cursor.Length.ShouldBeLessThanOrEqualTo(CalendarEntityCursorProtector.MaximumCursorCharacters);

        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot("https://cal.example/0/", "https://cal.example/0/new.ics"),
                Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics"),
                Snapshot("https://cal.example/a/", "https://cal.example/a/2.ics"),
                Snapshot("https://cal.example/b/", "https://cal.example/b/1.ics")
            ]));

        var second = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            ["event"],
            CancellationToken.None,
            pageSize: 2,
            cursor: cursor);

        var secondItems = second.StructuredContent!.Value.GetProperty("items");
        secondItems.GetArrayLength().ShouldBe(1);
        secondItems[0].GetProperty("resourceRevision").GetProperty("href").GetString()
            .ShouldBe("https://cal.example/b/1.ics");
        second.StructuredContent.Value.GetProperty("pagination").GetProperty("nextCursor").ValueKind
            .ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task QueryAsync_ProjectsEverySafeDiagnosticSeverity()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success(
                [Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics")],
                [
                    new CalendarResourceDiagnostic("info", "Info", CalendarResourceDiagnosticSeverity.Info),
                    new CalendarResourceDiagnostic("warning", "Warning", CalendarResourceDiagnosticSeverity.Warning),
                    new CalendarResourceDiagnostic("error", "Error", CalendarResourceDiagnosticSeverity.Error)
                ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument(
                "selected",
                new CalendarEntityReferenceArgument("href", Href: "https://cal.example/a/")),
            ["event", "todo"],
            CancellationToken.None,
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:00Z"),
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T13:00:00Z"));

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("diagnostics")
            .EnumerateArray().Select(item => item.GetProperty("severity").GetString())
            .ShouldBe(["info", "warning", "error"]);
    }

    [Fact]
    public async Task QueryAsync_ProjectsAllAuthorizedCapabilityStates()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Failure(
                CalendarEntityQueryCode.NotFound,
                [new CalendarDescriptor
                {
                    Href = "https://cal.example/a/",
                    DisplayNameProvenance = DisplayNameProvenance.Missing,
                    EventSupport = EntityKindSupport.NotAdvertised,
                    TodoSupport = EntityKindSupport.Advertised,
                    TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VTODO")]
                }]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["todo"], CancellationToken.None);

        var kinds = result.StructuredContent!.Value.GetProperty("authorizedCandidates")[0].GetProperty("entityKinds");
        kinds.GetProperty("event").GetProperty("state").GetString().ShouldBe("not_advertised");
        kinds.GetProperty("todo").GetProperty("state").GetString().ShouldBe("advertised");
    }

    [Fact]
    public async Task QueryAsync_RejectsTamperedRestartedMismatchedExpiredAndNonCanonicalCursorsBeforeService()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics"),
                Snapshot("https://cal.example/a/", "https://cal.example/a/2.ics")
            ]));
        var time = new MutableTimeProvider(Now);
        var sut = CreateTool(service, time);
        var first = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1);
        var cursor = first.StructuredContent!.Value.GetProperty("pagination").GetProperty("nextCursor").GetString()!;
        service.ClearReceivedCalls();

        var invalidCalls = new[]
        {
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1,
                cursor: cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A")),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["todo"], CancellationToken.None, pageSize: 1, cursor: cursor),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 2, cursor: cursor),
            sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1, cursor: cursor + "=")
        };
        foreach (var call in invalidCalls)
            AssertInvalidCursor(await call);

        var restarted = CreateTool(service, time);
        AssertInvalidCursor(await restarted.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1, cursor: cursor));

        time.Advance(TimeSpan.FromMinutes(10));
        AssertInvalidCursor(await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1, cursor: cursor));

        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CursorProtector_UsesUniqueBoundedNonCredentialBearingTokens()
    {
        var options = CreateOptions();
        var protector = new CalendarEntityCursorProtector(
            new MutableTimeProvider(Now),
            options,
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());

        var first = protector.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");
        var second = protector.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");

        first.ShouldNotBe(second);
        first.Length.ShouldBeLessThanOrEqualTo(2048);
        first.ShouldNotContain("user");
        first.ShouldNotContain("password");
        first.ShouldNotContain("cal.example");
        protector.TryUnprotect(first, "query", out var continuation, out var expired).ShouldBeTrue();
        expired.ShouldBeFalse();
        continuation.ShouldBe(new CalendarEntityContinuation(
            "https://cal.example/a/", "https://cal.example/a/1.ics"));
    }

    [Fact]
    public void CursorProtector_BindsCredentialFieldsWithoutDelimiterCollisions()
    {
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var first = new CalendarEntityCursorProtector(
            new MutableTimeProvider(Now),
            CreateOptions(username: "alpha\u001fbeta", password: "gamma"),
            key);
        var redistributed = new CalendarEntityCursorProtector(
            new MutableTimeProvider(Now),
            CreateOptions(username: "alpha", password: "beta\u001fgamma"),
            key);
        var cursor = first.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");

        redistributed.TryUnprotect(cursor, "query", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void CursorProtector_RejectsNonCanonicalAlternatePadBitsForSameBytes()
    {
        var protector = new CalendarEntityCursorProtector(
            new MutableTimeProvider(Now),
            CreateOptions(),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var query = "query";
        var canonical = protector.Protect(query, "https://cal.example/a/", "https://cal.example/a/1.ics");
        while (canonical.Length % 4 == 0)
        {
            query += "x";
            canonical = protector.Protect(query, "https://cal.example/a/", "https://cal.example/a/1.ics");
        }
        var alternate = WithAlternatePadBits(canonical);

        Convert.FromBase64String(ToPaddedBase64(canonical))
            .ShouldBe(Convert.FromBase64String(ToPaddedBase64(alternate)));
        protector.TryUnprotect(alternate, query, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_RejectsOversizedNormalArgumentsBeforeService()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name", new string('a', 270_000))),
            ["event"],
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await service.DidNotReceive().QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_PreservesHigherPrecisionUtcBoundsForSecondResolutionTruth()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEntityQuery? observed = null;
        service.QueryEntitiesAsync(Arg.Do<CalendarEntityQuery>(query => observed = query), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            ["event"],
            CancellationToken.None,
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:00.0000000001Z"),
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:01.123456789Z"));

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull();
        observed.From!.Value.Ticks.ShouldBe(DateTimeOffset.Parse("2026-08-15T12:00:00Z").Ticks + 1);
        observed.To!.Value.Ticks.ShouldBe(DateTimeOffset.Parse("2026-08-15T12:00:01Z").Ticks + 1_234_568);
    }

    [Fact]
    public async Task QueryAsync_RejectsSelectedNameRequiringSilentTrimmingBeforeService()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEntityQuery? observed = null;
        service.QueryEntitiesAsync(Arg.Do<CalendarEntityQuery>(query => observed = query), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument(
                "selected",
                new CalendarEntityReferenceArgument("name", Name: "  Work  ")),
            ["event"],
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        observed.ShouldBeNull();
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59.99999999Z")]
    public async Task QueryAsync_HandlesExtremeArbitraryPrecisionBoundsWithoutException(string lexical)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            ["event"],
            CancellationToken.None,
            new CalendarEntityUtcArgument("utcDateTime", lexical),
            new CalendarEntityUtcArgument("utcDateTime", "9999-12-31T23:59:59Z"));

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
    }

    [Fact]
    public async Task QueryAsync_ReturnsNoItemsWhenOneSnapshotCannotFitPage()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics", new byte[3_200_000])
            ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_MapsObservedResourceByteLimitExactly()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Failure(
                CalendarEntityQueryCode.PayloadTooLarge,
                limits: new CalendarEntityQueryExecutionLimits(ByteCount: 4_194_305)));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        var limits = result.StructuredContent.Value.GetProperty("limits");
        limits.EnumerateObject().Select(property => property.Name).ShouldBe(["byteCount"]);
        limits.GetProperty("byteCount").GetInt32().ShouldBe(4_194_305);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_BoundsHugeAuthorizedCandidateFailureWithoutLeakingValue()
    {
        var service = Substitute.For<ICalendarService>();
        var unsafeValue = new string('x', 4_300_000);
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Failure(
                CalendarEntityQueryCode.Ambiguous,
                [new CalendarDescriptor
                {
                    Href = "https://cal.example/a/",
                    DisplayName = unsafeValue,
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.Unknown,
                    EventEvidence = [new CapabilityEvidence("server", unsafeValue)]
                }]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.TryGetProperty("authorizedCandidates", out _).ShouldBeFalse();
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
        result.StructuredContent.Value.GetProperty("limits").GetProperty("byteCount").GetInt32()
            .ShouldBeGreaterThan(4 * 1024 * 1024);
        result.StructuredContent.Value.ToString().ShouldNotContain(unsafeValue[..100]);
    }

    [Fact]
    public async Task QueryAsync_ReturnsTypedZeroItemFailureWhenContinuationCannotFit()
    {
        var service = Substitute.For<ICalendarService>();
        var longCalendar = "https://cal.example/" + new string('a', 1800) + "/";
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot(longCalendar, longCalendar + "1.ics"),
                Snapshot(longCalendar, longCalendar + "2.ics")
            ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None, pageSize: 1);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized", "upstream", false, "execution")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden", "upstream", false, "execution")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited", "upstream", true, "execution")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability", "capabilityAndProjection", false, "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability", "capabilityAndProjection", false, "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "limitsAndAdmission", false, "admissionAndPayload")]
    [InlineData(HttpStatusCode.Conflict, "conflict", "state", false, "execution")]
    [InlineData(HttpStatusCode.PreconditionFailed, "conflict", "state", false, "execution")]
    [InlineData(HttpStatusCode.RequestTimeout, "upstream_unavailable", "upstream", true, "execution")]
    [InlineData(HttpStatusCode.InsufficientStorage, "upstream_unavailable", "upstream", false, "execution")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream_unavailable", "upstream", true, "execution")]
    [InlineData(HttpStatusCode.BadRequest, "upstream_protocol_error", "upstream", false, "execution")]
    public async Task QueryAsync_MapsExpectedUpstreamStatusesWithoutPartialItems(
        HttpStatusCode status,
        string code,
        string category,
        bool retryable,
        string phase)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarEntityQueryResult>>(_ => throw new HttpRequestException("secret", null, status));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(code);
        result.StructuredContent.Value.GetProperty("category").GetString().ShouldBe(category);
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBe(retryable);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe(phase);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
        var message = result.StructuredContent.Value.GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldNotContain("secret");
    }

    [Fact]
    public async Task QueryAsync_MapsExpectedExecutionExceptions()
    {
        var cases = new (Exception Exception, string Code)[]
        {
            (new HttpRequestException("secret"), "upstream_unavailable"),
            (new TimeoutException("secret"), "upstream_unavailable"),
            (new XmlException("secret"), "upstream_protocol_error"),
            (new CalendarDiscoveryProtocolException("secret"), "upstream_protocol_error"),
            (new CalendarDiscoveryUnsupportedCapabilityException("secret"), "unsupported_capability"),
            (new CalendarDiscoveryLimitException(257), "limit_exhausted")
        };
        foreach (var testCase in cases)
        {
            var service = Substitute.For<ICalendarService>();
            service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
                .Returns<Task<CalendarEntityQueryResult>>(_ => throw testCase.Exception);
            var sut = CreateTool(service, new MutableTimeProvider(Now));

            var result = await sut.QueryCoreAsync(
                new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(testCase.Code);
            result.StructuredContent.Value.ToString().ShouldNotContain("secret");
        }
    }

    [Fact]
    public async Task QueryAsync_CompleteReadDeadlineCancelsServiceAndMapsSafeRetryableFailure()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellation(call.ArgAt<CancellationToken>(1)));
        var time = new MutableTimeProvider(Now);
        var sut = CreateTool(service, time);

        var pending = sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        var result = await pending;

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("category").GetString().ShouldBe("limitsAndAdmission");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
        var message = result.StructuredContent.Value.GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldNotContain("OperationCanceledException");
        await service.Received(1).QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_DeadlineBeforeRecurrenceFilteringReturnsTypedZeroItemLimit()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/recurring.ics";
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<ICalendarClient>();
        var service = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredRead.TrySetResult();
            await releaseRead.Task;
            return CalendarResourceRead.Success(
                resourceHref,
                "\"r1\"",
                Encoding.UTF8.GetBytes(
                    "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
                    + "BEGIN:VEVENT\r\nUID:recurring\r\nDTSTAMP:20260815T120000Z\r\n"
                    + "DTSTART:20260815T120000Z\r\nDURATION:PT1S\r\nRRULE:FREQ=SECONDLY;COUNT=2000\r\n"
                    + "END:VEVENT\r\nEND:VCALENDAR\r\n"));
        });
        var time = new MutableTimeProvider(Now);
        var sut = CreateTool(service, time);

        var pending = sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            ["event"],
            CancellationToken.None,
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:00Z"),
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T13:00:00Z"));
        await enteredRead.Task;
        time.Advance(TimeSpan.FromSeconds(30));
        releaseRead.TrySetResult();
        var result = await pending;

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("category").GetString().ShouldBe("limitsAndAdmission");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_DeadlineDuringPageConstructionReturnsNoItems()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityQueryResult.Success([
                Snapshot("https://cal.example/a/", "https://cal.example/a/1.ics")
            ]));
        var time = new SequencedTimeProvider(Now, Now, Now.AddSeconds(30));
        var sut = CreateTool(service, time);

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("category").GetString().ShouldBe("limitsAndAdmission");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_CallerCancellationPropagatesWithoutTypedDeadlineMapping()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellation(call.ArgAt<CancellationToken>(1)));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();

        var pending = sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), ["event"], cancellation.Token);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        await service.Received(1).QueryEntitiesAsync(Arg.Any<CalendarEntityQuery>(), Arg.Any<CancellationToken>());
    }

    private static CalendarEntityTools CreateTool(ICalendarService service, TimeProvider timeProvider) => new(
        service,
        new CalendarEntityCursorProtector(timeProvider, CreateOptions()),
        timeProvider);

    private static IOptions<CalDavOptions> CreateOptions(
        string username = "user",
        string password = "password") => Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = username,
            Password = password
        });

    private static CalendarResourceSnapshot Snapshot(
        string calendarHref,
        string resourceHref,
        byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var properties = bytes.Length >= 3_000_000
            ? LargeCalendarProperties(bytes.Length)
            : [];
        return new CalendarResourceSnapshot(
            calendarHref,
            resourceHref,
            "\"r1\"",
            bytes,
            properties,
            new CalendarResourceProjection(CalendarResourceProjectionKind.Event, "u1", "summary"),
            []);
    }

    private static IReadOnlyList<CalendarProperty> LargeCalendarProperties(int length)
    {
        var value = new string('x', checked(length * 2));
        return
        [
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0), new CalendarComponentPathSegment("VEVENT", 0)],
                "X-LARGE", [], CalendarPropertyValueType.Unknown, value, $"X-LARGE:{value}\r\n")
        ];
    }

    private static void AssertInvalidCursor(ModelContextProtocol.Protocol.CallToolResult result)
    {
        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        var message = result.StructuredContent.Value.GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldNotContain("Exception");
    }

    private static Dictionary<string, JsonElement> ArgumentsWithSerializedSize(int targetBytes)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "default" }),
            ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" }),
            ["unknown"] = JsonSerializer.SerializeToElement(string.Empty)
        };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(arguments).Length;
        arguments["unknown"] = JsonSerializer.SerializeToElement(new string('x', targetBytes - overhead));
        return arguments;
    }

    private static CallToolResult ResultWithHumanReadableSize(int targetBytes)
    {
        var result = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { diagnostics = Array.Empty<object>() }),
            Content = [new TextContentBlock { Text = string.Empty }]
        };
        var overhead = CalendarEntityTools.MeasureHumanReadableResult(result);
        result.Content = [new TextContentBlock { Text = new string('x', targetBytes - overhead) }];
        return result;
    }

    private static CallToolResult ResultWithStructuredSize(int targetBytes)
    {
        var result = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { padding = string.Empty }),
            Content = [new TextContentBlock { Text = "ok" }]
        };
        var overhead = CalendarEntityTools.MeasureResult(result);
        result.StructuredContent = JsonSerializer.SerializeToElement(
            new { padding = new string('x', targetBytes - overhead) });
        return result;
    }

    private static async Task<CalendarEntityQueryResult> WaitForCancellation(CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await pending.Task.WaitAsync(cancellationToken);
        return CalendarEntityQueryResult.Success([]);
    }

    private static string WithAlternatePadBits(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var index = alphabet.IndexOf(value[^1], StringComparison.Ordinal);
        var alternateIndex = value.Length % 4 == 2 ? index ^ 1 : index ^ 1;
        return value[..^1] + alphabet[alternateIndex];
    }

    private static string ToPaddedBase64(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        return standard + new string('=', (4 - standard.Length % 4) % 4);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            MutableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }

    private sealed class SequencedTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Math.Min(_index, values.Length - 1);
            _index++;
            return values[index];
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new InertTimer();

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
