using System.Net;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarEntityToolsTests
{
    [Fact]
    public async Task QueryRawAsync_OversizedFailurePublishesOnlyTheSelectedPayloadError()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarEntityQueryItem>.Failure(new QueryFailure(
                QueryFailureCode.UpstreamUnavailable,
                QueryFailureCategory.Upstream,
                new string('x', CalendarEntityTools.MaximumStructuredResultBytes),
                true,
                QueryFailurePhase.Execution)));
        var sut = new CalendarEntityTools(module);

        var (result, operation) = await ToolTelemetryTestScope.CaptureAsync(
            "calendar_entities.query",
            () => sut.QueryRawAsync(Arguments(("cursor", "opaque")), CancellationToken.None));

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        operation.ShouldMatchStructuredError(result.StructuredContent.Value);
    }

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
            ["evaluationTimeZone"] = JsonSerializer.SerializeToElement("America/Sao_Paulo"),
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
        typedStart.Query.EvaluationTimeZone.ShouldBe("America/Sao_Paulo");
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
            Arguments(("cursor", 42)),
            Arguments(("cursor", "opaque"), ("pageSize", 0)),
            Arguments(("cursor", "opaque"), ("pageSize", 201)),
            Arguments(("cursor", "opaque"), ("pageSize", "one")),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }),
                ("from", new { kind = "utcDateTime", value = "2026-08-23T12:00:00Z" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }), ("unknown", true)),
            Arguments(("scope", "all"), ("entityKinds", new[] { "event" })),
            Arguments(("scope", new { mode = "private" }), ("entityKinds", new[] { "event" })),
            Arguments(("scope", new { mode = "selected" }), ("entityKinds", new[] { "event" })),
            Arguments(("scope", new { mode = "selected", calendar = new { by = "name", name = "Work", href = "https://cal.example/work/" } }),
                ("entityKinds", new[] { "event" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", Array.Empty<string>())),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event", "event" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "private" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", 42)),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }),
                ("from", new { kind = "private", value = "2026-08-23T12:00:00Z" }),
                ("to", new { kind = "utcDateTime", value = "2026-08-24T12:00:00Z" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }),
                ("from", new { kind = "utcDateTime", value = "2026-08-23T12:00:00+01:00" }),
                ("to", new { kind = "utcDateTime", value = "2026-08-24T12:00:00Z" })),
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }), ("pageSize", 201))
            ,Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" }),
                ("evaluationTimeZone", JsonSerializer.SerializeToElement<string?>(null)))
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

    [Fact]
    public async Task QueryRawAsync_AcceptsEveryStrictScopeSelectorAndOptionalDefault()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var observed = new List<CalendarEntityQueryRequest.Start>();
        module.QueryEntitiesAsync(
                Arg.Do<CalendarEntityQueryRequest>(request => observed.Add(request.ShouldBeOfType<CalendarEntityQueryRequest.Start>())),
                Arg.Any<CancellationToken>())
            .Returns(PageReply());
        var tool = new CalendarEntityTools(module);
        var inputs = new[]
        {
            Arguments(("scope", new { mode = "default" }), ("entityKinds", new[] { "todo" })),
            Arguments(("scope", new { mode = "selected", calendar = new { by = "name", name = "Work" } }),
                ("entityKinds", new[] { "event" })),
            Arguments(("scope", new { mode = "selected", calendar = new { by = "href", href = "https://cal.example/work/" } }),
                ("entityKinds", new[] { "todo" }), ("pageSize", 50))
        };

        foreach (var input in inputs)
            (await tool.QueryRawAsync(input, CancellationToken.None)).IsError.ShouldBe(false);

        observed[0].Query.Scope.ShouldBe(CalendarEntityScope.Default);
        observed[0].Query.EntityKinds.ShouldBe([CalendarEntityKind.Todo]);
        observed[1].Query.Scope.Calendar!.Name.ShouldBe("Work");
        observed[2].Query.Scope.Calendar!.Href.ShouldBe("https://cal.example/work/");
        observed[2].PageSize.ShouldBe(50);
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
            (new(QueryFailureCode.InvalidInput, QueryFailureCategory.Input, "input", false,
                QueryFailurePhase.SchemaLexicalDiscriminator), "invalid_input", "input", "schemaLexicalDiscriminator"),
            (new(QueryFailureCode.CursorExpired, QueryFailureCategory.State, "expired", false,
                QueryFailurePhase.Pagination), "cursor_expired", "state", "pagination"),
            (new(QueryFailureCode.LimitExhausted, QueryFailureCategory.LimitsAndAdmission, "limit", false,
                QueryFailurePhase.Execution), "limit_exhausted", "limitsAndAdmission", "execution"),
            (new(QueryFailureCode.Busy, QueryFailureCategory.LimitsAndAdmission, "busy", true,
                QueryFailurePhase.Pagination, RetryAfterMs: 123), "busy", "limitsAndAdmission", "pagination"),
            (new(QueryFailureCode.PayloadTooLarge, QueryFailureCategory.LimitsAndAdmission, "large", false,
                QueryFailurePhase.AdmissionAndPayload), "payload_too_large", "limitsAndAdmission", "admissionAndPayload"),
            (new(QueryFailureCode.UpstreamProtocolError, QueryFailureCategory.Upstream, "protocol", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "upstream_protocol_error", "upstream",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.UnsupportedCapability, QueryFailureCategory.CapabilityAndProjection, "unsupported", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "unsupported_capability", "capabilityAndProjection",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.ConcurrencyUnavailable, QueryFailureCategory.State, "etag", false,
                QueryFailurePhase.TargetRevision), "concurrency_unavailable", "state", "targetRevision"),
            (new(QueryFailureCode.RecurrenceUnevaluable, QueryFailureCategory.CapabilityAndProjection, "recurrence", false,
                QueryFailurePhase.CompleteResourceSemantics), "recurrence_unevaluable", "capabilityAndProjection",
                "completeResourceSemantics"),
            (new(QueryFailureCode.UpstreamUnavailable, QueryFailureCategory.Upstream, "unavailable", true,
                QueryFailurePhase.Execution), "upstream_unavailable", "upstream", "execution"),
            (new(QueryFailureCode.UpstreamUnauthorized, QueryFailureCategory.Upstream, "unauthorized", false,
                QueryFailurePhase.Execution), "upstream_unauthorized", "upstream", "execution"),
            (new(QueryFailureCode.UpstreamForbidden, QueryFailureCategory.Upstream, "forbidden", false,
                QueryFailurePhase.Execution), "upstream_forbidden", "upstream", "execution"),
            (new(QueryFailureCode.UpstreamRateLimited, QueryFailureCategory.Upstream, "rate", true,
                QueryFailurePhase.Execution), "upstream_rate_limited", "upstream", "execution"),
            (new(QueryFailureCode.TemporalUnresolved, QueryFailureCategory.CapabilityAndProjection, "time", false,
                QueryFailurePhase.CompleteResourceSemantics), "temporal_unresolved", "capabilityAndProjection",
                "completeResourceSemantics"),
            (new(QueryFailureCode.NotFound, QueryFailureCategory.Selection, "missing", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "not_found", "selection",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.Ambiguous, QueryFailureCategory.Selection, "ambiguous", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "ambiguous", "selection",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.OutsideScope, QueryFailureCategory.Selection, "scope", false,
                QueryFailurePhase.OriginScopeAuthorization), "outside_scope", "selection",
                "originScopeAuthorization")
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
    public async Task QueryRawAsync_MapsEveryOptionalFailureEvidenceField()
    {
        var candidate = new QueryAuthorizedCandidate(
            "https://cal.example/calendars/work/",
            "Work",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised,
            [new CapabilityEvidence("probe", "event")],
            []);
        var failure = new QueryFailure(
            QueryFailureCode.LimitExhausted,
            QueryFailureCategory.LimitsAndAdmission,
            "limit",
            false,
            QueryFailurePhase.Execution,
            new QueryExecutionLimits(1, 2, 3, 4, 5, 6),
            [candidate],
            123);
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarEntityQueryItem>.Failure(failure));

        var result = await new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("cursor", "opaque")),
            CancellationToken.None);

        var error = result.StructuredContent!.Value;
        error.GetProperty("limits").GetProperty("resourcesInspected").GetInt32().ShouldBe(1);
        error.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(2);
        error.GetProperty("limits").GetProperty("occurrenceCount").GetInt32().ShouldBe(3);
        error.GetProperty("limits").GetProperty("byteCount").GetInt32().ShouldBe(4);
        error.GetProperty("limits").GetProperty("itemCount").GetInt32().ShouldBe(5);
        error.GetProperty("limits").GetProperty("snapshotCount").GetInt32().ShouldBe(6);
        error.GetProperty("authorizedCandidates")[0].GetProperty("calendar").GetProperty("href").GetString()
            .ShouldBe(candidate.CalendarHref);
        error.GetProperty("retryAfterMs").GetInt32().ShouldBe(123);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QueryRawAsync_RejectsUndefinedTypedFailureVocabulary(int invalidField)
    {
        var failure = new QueryFailure(
            invalidField == 0 ? (QueryFailureCode)99 : QueryFailureCode.InvalidInput,
            invalidField == 1 ? (QueryFailureCategory)99 : QueryFailureCategory.Input,
            "invalid internal vocabulary",
            false,
            invalidField == 2 ? (QueryFailurePhase)99 : QueryFailurePhase.Execution);
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarEntityQueryItem>.Failure(failure));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("cursor", "opaque")),
            CancellationToken.None));
    }

    [Fact]
    public async Task QueryRawAsync_PropagatesCallerCancellationToTheModule()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var moduleEntered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModule = new TaskCompletionSource<QueryReply<CalendarEntityQueryItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.ArgAt<CancellationToken>(1);
                moduleEntered.TrySetResult(token);
                return releaseModule.Task.WaitAsync(token);
            });
        using var cancellation = new CancellationTokenSource();
        var pending = new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("cursor", "opaque")),
            cancellation.Token);

        var observedToken = await moduleEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        observedToken.ShouldBe(cancellation.Token);
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
    public async Task ActualSdkEnvelopeMatchesTheModuleAccountantWithTemporalContext()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
            new PrimaryHandlerFilter(new QueryEnvelopeHandler(calendarHref)));
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example/calendars/";
            options.Username = "user";
            options.Password = "password";
            options.CalendarHrefs = calendarHref;
            options.EvaluationTimeZone = "Europe/London";
        });
        await using var provider = services.BuildServiceProvider();
        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(
                new CalendarEntityQuery(
                    CalendarEntityScope.All,
                    [CalendarEntityKind.Event],
                    new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero),
                    "America/Sao_Paulo")),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryEntitiesAsync(Arg.Any<CalendarEntityQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var actual = await new CalendarEntityTools(module).QueryRawAsync(
            Arguments(("scope", new { mode = "all" }), ("entityKinds", new[] { "event" })),
            CancellationToken.None);

        CalendarEntityTools.MeasureResult(actual).ShouldBe(page.Value.MeasuredCallToolResultBytes);
        actual.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);
        actual.StructuredContent!.Value.GetProperty("temporalEvaluationContext")
            .GetProperty("timeZone").GetString().ShouldBe("America/Sao_Paulo");
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
                temporalEvaluationContext = new { timeZone = "America/Sao_Paulo", source = "caller" },
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

    private sealed class PrimaryHandlerFilter(HttpMessageHandler handler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = handler;
        };
    }

    private sealed class QueryEnvelopeHandler(string calendarHref) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var xml = request.Method.Method switch
            {
                "PROPFIND" when request.Headers.GetValues("Depth").Single() == "0" => $"""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>https://cal.example/calendars/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>https://cal.example/calendars/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """,
                "PROPFIND" => $"""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>{calendarHref}</d:href><d:propstat><d:prop><d:displayname>Work</d:displayname><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """,
                "REPORT" when body.Contains("calendar-query", StringComparison.Ordinal) => $"""
                    <d:multistatus xmlns:d="DAV:"><d:response><d:href>{calendarHref}a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>
                    """,
                "REPORT" => $"""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>{calendarHref}a.ics</d:href><d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag><c:calendar-data>BEGIN:VCALENDAR&#13;
                    VERSION:2.0&#13;
                    BEGIN:VEVENT&#13;
                    UID:a&#13;
                    DTSTAMP:20260823T120000Z&#13;
                    DTSTART:20260824T120000Z&#13;
                    END:VEVENT&#13;
                    END:VCALENDAR&#13;
                    </c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
                    """,
                _ => throw new InvalidOperationException("Unexpected deterministic query request.")
            };
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                RequestMessage = request
            };
        }
    }
}
