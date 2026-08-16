using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarOccurrenceToolsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly CalendarEntityUtcArgument From = new("utcDateTime", "2026-08-15T12:00:00Z");
    private static readonly CalendarEntityUtcArgument To = new("utcDateTime", "2026-08-16T12:00:00Z");

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task QueryRawAsync_EnforcesExactArgumentByteBoundary(int extraByte, bool rejected)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = ArgumentsWithSerializedSize(CalendarOccurrenceTools.MaximumArgumentBytes + extraByte);

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        JsonSerializer.SerializeToUtf8Bytes(arguments).Length.ShouldBe(CalendarOccurrenceTools.MaximumArgumentBytes + extraByte);
        if (rejected)
        {
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
            await service.DidNotReceive().QueryOccurrencesAsync(
                Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.IsError.ShouldBe(false);
            await service.Received(1).QueryOccurrencesAsync(
                Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task QueryRawAsync_AcceptsExactFrozenShapeWithoutEntityKindsAndDefaultsAbsentPageSize()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarOccurrenceQuery? observed = null;
        service.QueryOccurrencesAsync(Arg.Do<CalendarOccurrenceQuery>(query => observed = query), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"scope\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\"https://cal.example/a/\"}},"
            + "\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"},"
            + "\"to\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-16T12:00:00Z\"},"
            + "\"evaluationTimeZone\":\"America/New_York\"}");

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull();
        observed.Scope.Calendar!.Href.ShouldBe("https://cal.example/a/");
        observed.EvaluationTimeZone.ShouldBe("America/New_York");
    }

    [Fact]
    public async Task QueryCoreAsync_RejectsOversizedRawEvidenceBeforeService()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            From,
            To,
            CancellationToken.None,
            rawArguments: ArgumentsWithSerializedSize(CalendarOccurrenceTools.MaximumArgumentBytes + 1));

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await service.DidNotReceive().QueryOccurrencesAsync(
            Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"scope\":{\"mode\":\"all\"},\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"}}")]
    [InlineData("{\"scope\":{\"mode\":\"all\"},\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"},\"to\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-16T12:00:00Z\"},\"entityKinds\":[\"event\"]}")]
    [InlineData("{\"scope\":{\"mode\":\"all\"},\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"},\"to\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-16T12:00:00Z\"},\"pageSize\":null}")]
    [InlineData("{\"scope\":{\"mode\":\"all\"},\"from\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-15T12:00:00Z\"},\"to\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-16T12:00:00Z\"},\"evaluationTimeZone\":null}")]
    public async Task QueryRawAsync_RejectsMissingNullAndUnknownFrozenShape(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var arguments = json == "null" ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await sut.QueryRawAsync(arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarOccurrenceQueryCode.InvalidInput, "invalid_input")]
    [InlineData(CalendarOccurrenceQueryCode.UnsafeScope, "invalid_input")]
    [InlineData(CalendarOccurrenceQueryCode.NotFound, "not_found")]
    [InlineData(CalendarOccurrenceQueryCode.Ambiguous, "ambiguous")]
    [InlineData(CalendarOccurrenceQueryCode.OutsideScope, "outside_scope")]
    [InlineData(CalendarOccurrenceQueryCode.UnsupportedCapability, "unsupported_capability")]
    [InlineData(CalendarOccurrenceQueryCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(CalendarOccurrenceQueryCode.LimitExhausted, "limit_exhausted")]
    [InlineData(CalendarOccurrenceQueryCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(CalendarOccurrenceQueryCode.TemporalUnresolved, "temporal_unresolved")]
    [InlineData(CalendarOccurrenceQueryCode.RecurrenceUnevaluable, "recurrence_unevaluable")]
    [InlineData(CalendarOccurrenceQueryCode.UpstreamProtocolError, "upstream_protocol_error")]
    public async Task QueryAsync_MapsEveryDomainFailure(CalendarOccurrenceQueryCode code, string expected)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Failure(code));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expected);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "upstream_protocol_error", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.BadGateway, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized", "execution")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden", "execution")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited", "execution")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", "admissionAndPayload")]
    [InlineData(HttpStatusCode.Conflict, "conflict", "execution")]
    [InlineData(HttpStatusCode.PreconditionFailed, "conflict", "execution")]
    [InlineData(HttpStatusCode.RequestTimeout, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.InsufficientStorage, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.InternalServerError, "upstream_unavailable", "execution")]
    [InlineData(HttpStatusCode.BadRequest, "upstream_protocol_error", "execution")]
    [InlineData(null, "upstream_unavailable", "execution")]
    public async Task QueryAsync_MapsHttpFailureWithoutLosingItsEarliestPhase(
        HttpStatusCode? status,
        string code,
        string phase)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarOccurrenceQueryResult>>(_ => throw new HttpRequestException("secret", null, status));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(code);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe(phase);
        result.StructuredContent.Value.ToString().ShouldNotContain("secret");
    }

    [Fact]
    public async Task QueryAsync_MapsObservedDomainLimitsWithoutPartialItems()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Failure(
                CalendarOccurrenceQueryCode.LimitExhausted,
                limits: new CalendarOccurrenceQueryExecutionLimits(17, 2001, 4097)));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        var limits = result.StructuredContent!.Value.GetProperty("limits");
        limits.GetProperty("resourcesInspected").GetInt32().ShouldBe(17);
        limits.GetProperty("occurrenceCount").GetInt32().ShouldBe(2001);
        limits.GetProperty("byteCount").GetInt32().ShouldBe(4097);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_EmitsAuthorizedCandidatesOnlyForSelectionFailures()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Failure(
                CalendarOccurrenceQueryCode.NotFound,
                [new CalendarDescriptor
                {
                    Href = "https://cal.example/a/",
                    DisplayName = "A",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.Advertised
                }]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("authorizedCandidates").GetArrayLength().ShouldBe(1);
    }

    [Theory]
    [InlineData("invalid", "utcDateTime", "utcDateTime", null, 50, 0)]
    [InlineData("all", "date", "utcDateTime", null, 50, 0)]
    [InlineData("all", "utcDateTime", "date", null, 50, 0)]
    [InlineData("all", "utcDateTime", "utcDateTime", "", 50, 0)]
    [InlineData("all", "utcDateTime", "utcDateTime", null, 0, 0)]
    [InlineData("all", "utcDateTime", "utcDateTime", null, 201, 0)]
    [InlineData("all", "utcDateTime", "utcDateTime", null, 50, 2049)]
    public async Task QueryAsync_RejectsEachInvalidDomainAndPaginationBranchBeforeService(
        string scopeMode,
        string fromKind,
        string toKind,
        string? evaluationTimeZone,
        int pageSize,
        int cursorLength)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument(scopeMode),
            From with { Kind = fromKind },
            To with { Kind = toKind },
            CancellationToken.None,
            evaluationTimeZone,
            pageSize,
            cursorLength == 0 ? null : new string('x', cursorLength));

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().QueryOccurrencesAsync(
            Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(DiscoveryProtocolFailures))]
    public async Task QueryAsync_MalformedDiscoveryRetainsSelectionDiscoveryPhase(Exception exception)
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarOccurrenceQueryResult>>(_ => throw exception);
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        result.StructuredContent.Value.ToString().ShouldNotContain("secret");
    }

    public static TheoryData<Exception> DiscoveryProtocolFailures => new()
    {
        new XmlException("secret"),
        new CalendarDiscoveryProtocolException("secret")
    };

    [Fact]
    public async Task QueryAsync_CandidateProtocolFailureAfterSelectionRemainsExecutionPhase()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Failure(CalendarOccurrenceQueryCode.UpstreamProtocolError));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("execution");
    }

    [Fact]
    public async Task QueryAsync_EmitsExactOccurrenceShapeAndPaginatesByFrozenContinuationTuple()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-15T10:00:00Z"),
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-16T10:00:00Z"),
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "z", "2026-08-15T10:00:00Z")
            ]));
        var time = new MutableTimeProvider(Now);
        var sut = CreateTool(service, time);

        var first = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            evaluationTimeZone: "America/New_York", pageSize: 2);
        var cursor = first.StructuredContent!.Value.GetProperty("pagination").GetProperty("nextCursor").GetString();

        first.IsError.ShouldBe(false);
        first.StructuredContent.Value.GetProperty("outcome").GetString().ShouldBe("success");
        first.StructuredContent.Value.GetProperty("items").GetArrayLength().ShouldBe(2);
        var item = first.StructuredContent.Value.GetProperty("items")[0];
        item.EnumerateObject().Select(property => property.Name).ShouldBe(["snapshot", "recurrenceIdentity", "timing"]);
        item.GetProperty("recurrenceIdentity").GetProperty("value").GetProperty("kind").GetString().ShouldBe("utcDateTime");
        cursor.ShouldNotBeNullOrEmpty();
        cursor.Length.ShouldBeLessThanOrEqualTo(CalendarEntityCursorProtector.MaximumCursorCharacters);
        cursor.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').ShouldBeTrue();

        var second = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            evaluationTimeZone: "America/New_York", pageSize: 2, cursor: cursor);

        second.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.StructuredContent.Value.GetProperty("items")[0].GetProperty("snapshot")
            .GetProperty("projection").GetProperty("uid").GetString().ShouldBe("z");
        second.StructuredContent.Value.GetProperty("pagination").GetProperty("nextCursor").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void ContinuationUsesTheDomainTemporalCanonicalSortKey()
    {
        var occurrence = Occurrence(
            "2026-08-15T12:00:00Z",
            "https://cal.example/a/",
            "uid",
            "2026-08-15T10:00:00Z");

        var continuation = CalendarOccurrenceContinuation.FromSnapshot(occurrence);

        continuation.RecurrenceIdentity.ShouldBe(occurrence.RecurrenceIdentity.GetCanonicalSortKey());
    }

    [Fact]
    public async Task QueryAsync_ContinuationComparesEffectiveStartBeforeCalendarHref()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-15T10:00:00Z"),
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/b/", "a", "2026-08-15T10:00:00Z"),
                Occurrence("2026-08-15T13:00:00Z", "https://cal.example/a/", "a", "2026-08-15T13:00:00Z")
            ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        var first = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 2);

        var second = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            pageSize: 2, cursor: GetCursor(first));

        second.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task QueryAsync_RejectsAuthenticatedCursorWithIncompleteContinuationTail()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([]));
        var time = new MutableTimeProvider(Now);
        var protector = CreateProtector(time);
        var sut = new CalendarOccurrenceTools(service, protector, time);
        var context = JsonSerializer.Serialize(new
        {
            ScopeMode = CalendarEntityScopeMode.All,
            SelectorKind = "href",
            SelectorValue = (string?)null,
            From = DateTimeOffset.Parse(From.Value).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            To = DateTimeOffset.Parse(To.Value).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            EvaluationTimeZone = (string?)null,
            PageSize = 1
        });
        var invalidTails = new[]
        {
            "null",
            JsonSerializer.Serialize(new { CalendarHref = "", EntityUid = "uid", RecurrenceIdentity = "identity" }),
            JsonSerializer.Serialize(new { CalendarHref = "calendar", EntityUid = "", RecurrenceIdentity = "identity" }),
            JsonSerializer.Serialize(new { CalendarHref = "calendar", EntityUid = "uid", RecurrenceIdentity = "" })
        };

        foreach (var tail in invalidTails)
        {
            var cursor = protector.Protect(context, "2026-08-15T12:00:00Z", tail);
            await AssertInvalidCursor(sut.QueryCoreAsync(
                new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
                pageSize: 1, cursor: cursor));
        }

        var validTail = JsonSerializer.Serialize(
            new { CalendarHref = "calendar", EntityUid = "uid", RecurrenceIdentity = "identity" });
        var validCursor = protector.Protect(context, "2026-08-15T12:00:00Z", validTail);
        var result = await sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            pageSize: 1, cursor: validCursor);
        result.IsError.ShouldBe(false);
    }

    [Fact]
    public async Task QueryAsync_CursorIsNonceRandomTamperEvidentBoundToQueryCredentialsExpiryAndProcessKey()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-15T10:00:00Z"),
                Occurrence("2026-08-15T13:00:00Z", "https://cal.example/a/", "a", "2026-08-15T13:00:00Z")
            ]));
        var time = new MutableTimeProvider(Now);
        var sut = CreateTool(service, time);
        var first = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1);
        var repeated = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1);
        var cursor = GetCursor(first);
        var otherCursor = GetCursor(repeated);

        cursor.ShouldNotBe(otherCursor);
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 2, cursor: cursor));
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("href", Href: "https://cal.example/a/")),
            From,
            To,
            CancellationToken.None,
            pageSize: 1,
            cursor: cursor));
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-15T12:00:01Z"),
            To,
            CancellationToken.None,
            pageSize: 1,
            cursor: cursor));
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"),
            From,
            new CalendarEntityUtcArgument("utcDateTime", "2026-08-16T11:59:59Z"),
            CancellationToken.None,
            pageSize: 1,
            cursor: cursor));
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            evaluationTimeZone: "UTC", pageSize: 1, cursor: cursor));
        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1, cursor: tampered));
        var restarted = CreateTool(service, time);
        await AssertInvalidCursor(restarted.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1, cursor: cursor));
        time.Advance(TimeSpan.FromMinutes(10));
        await AssertInvalidCursor(sut.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1, cursor: cursor));
    }

    [Fact]
    public async Task QueryAsync_CursorCredentialBindingRejectsSameKeyUnderDifferentCredentials()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-15T10:00:00Z"),
                Occurrence("2026-08-15T13:00:00Z", "https://cal.example/a/", "a", "2026-08-15T13:00:00Z")
            ]));
        var time = new MutableTimeProvider(Now);
        var key = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var source = CreateTool(service, time, key, "password-a");
        var first = await source.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None, pageSize: 1);
        var otherCredential = CreateTool(service, time, key, "password-b");

        await AssertInvalidCursor(otherCredential.QueryCoreAsync(
            new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None,
            pageSize: 1, cursor: GetCursor(first)));
    }

    [Fact]
    public async Task QueryAsync_RejectsSingleOccurrenceThatCannotFitStructuredBudget()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([
                Occurrence("2026-08-15T12:00:00Z", "https://cal.example/a/", "a", "2026-08-15T10:00:00Z", new byte[3_200_000])
            ]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_RejectsHumanAndDiagnosticContentBeyondShared64KiBBudget()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success(
                [],
                [new CalendarResourceDiagnostic("large", new string('x', 70_000), CalendarResourceDiagnosticSeverity.Warning)]));
        var sut = CreateTool(service, new MutableTimeProvider(Now));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_FinalDeadlineReturnsLimitErrorWithoutSuccessItems()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(CalendarOccurrenceQueryResult.Success([]));
        var sut = CreateTool(service, new SequencedTimeProvider(Now, Now.AddSeconds(31)));

        var result = await sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_CallerCancellationPropagatesWithoutReturningAResult()
    {
        var service = Substitute.For<ICalendarService>();
        service.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellation(call.ArgAt<CancellationToken>(1)));
        var sut = CreateTool(service, new MutableTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();

        var pending = sut.QueryCoreAsync(new CalendarEntityScopeArgument("all"), From, To, cancellation.Token);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    private static CalendarOccurrenceTools CreateTool(ICalendarService service, TimeProvider timeProvider) => new(
        service,
        CreateProtector(timeProvider),
        timeProvider);

    private static CalendarEntityCursorProtector CreateProtector(TimeProvider timeProvider) => new(
        timeProvider,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "password"
        }));

    private static CalendarOccurrenceTools CreateTool(
        ICalendarService service,
        TimeProvider timeProvider,
        byte[] key,
        string password) => new(
            service,
            new CalendarEntityCursorProtector(
                timeProvider,
                Options.Create(new CalDavOptions
                {
                    BaseUrl = "https://cal.example",
                    Username = "user",
                    Password = password
                }),
                key),
            timeProvider);

    private static CalendarOccurrenceSnapshot Occurrence(
        string effectiveStart,
        string calendarHref,
        string uid,
        string identity,
        byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var temporalIdentity = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, identity);
        var effective = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, effectiveStart);
        return new CalendarOccurrenceSnapshot(
            new CalendarResourceSnapshot(
                calendarHref,
                $"{calendarHref}{uid}.ics",
                "\"r1\"",
                bytes,
                [],
                new CalendarResourceProjection(CalendarResourceProjectionKind.Event, uid, "summary"),
                []),
            temporalIdentity,
            new CalendarOccurrenceTiming(temporalIdentity, effective, EvaluatedStartUtc: effective));
    }

    private static string GetCursor(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("pagination").GetProperty("nextCursor").GetString()!;

    private static async Task AssertInvalidCursor(Task<ModelContextProtocol.Protocol.CallToolResult> pending)
    {
        var result = await pending;
        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
    }

    private static async Task<CalendarOccurrenceQueryResult> WaitForCancellation(CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await pending.Task.WaitAsync(cancellationToken);
        return CalendarOccurrenceQueryResult.Success([]);
    }

    private static Dictionary<string, JsonElement> ArgumentsWithSerializedSize(int targetBytes)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["scope"] = JsonSerializer.SerializeToElement(
                new { mode = "selected", calendar = new { by = "name", name = string.Empty } }),
            ["from"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = From.Value }),
            ["to"] = JsonSerializer.SerializeToElement(new { kind = "utcDateTime", value = To.Value })
        };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(arguments).Length;
        arguments["scope"] = JsonSerializer.SerializeToElement(
            new { mode = "selected", calendar = new { by = "name", name = new string('x', targetBytes - overhead) } });
        return arguments;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan amount) => utcNow += amount;
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
    }
}
