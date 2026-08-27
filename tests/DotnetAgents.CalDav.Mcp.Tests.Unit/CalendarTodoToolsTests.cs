using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarTodoToolsTests
{
    [Fact]
    public async Task QueryRawAsync_OversizedFailurePublishesOnlyTheSelectedPayloadError()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Failure(new QueryFailure(
                QueryFailureCode.UpstreamUnavailable,
                QueryFailureCategory.Upstream,
                new string('x', CalendarQueryToolSupport.MaximumStructuredResultBytes),
                true,
                QueryFailurePhase.Execution)));
        var sut = new CalendarTodoTools(module);

        var (result, operation) = await ToolTelemetryTestScope.CaptureAsync(
            "todos.query",
            () => sut.QueryRawAsync(new Dictionary<string, JsonElement>
            {
                ["cursor"] = JsonSerializer.SerializeToElement("opaque")
            }, CancellationToken.None));

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        operation.ShouldMatchStructuredError(result.StructuredContent.Value);
    }

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
    public async Task StartAcceptsEveryClosedStateProjectionAndSelectedScopeShape()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var observed = new List<CalendarTodoQueryRequest.Start>();
        module.QueryTodosAsync(
                Arg.Do<CalendarTodoQueryRequest>(request =>
                    observed.Add(request.ShouldBeOfType<CalendarTodoQueryRequest.Start>())),
                Arg.Any<CancellationToken>())
            .Returns(FailureReply());
        var tool = new CalendarTodoTools(module);
        var allStates = new[] { "open", "completed", "cancelled", "indeterminate" };
        var allProjection = new[]
        {
            "summary", "status", "completedAt", "percentComplete", "due",
            "priority", "categories", "start", "description", "recurrence"
        };
        var scopes = new[]
        {
            JsonSerializer.SerializeToElement(new
                { mode = "selected", calendar = new { by = "name", name = "Work" } }),
            JsonSerializer.SerializeToElement(new
                { mode = "selected", calendar = new { by = "href", href = "https://cal.example/work/" } })
        };

        foreach (var scope in scopes)
        {
            _ = await tool.QueryRawAsync(new Dictionary<string, JsonElement>
            {
                ["scope"] = scope,
                ["completionStates"] = JsonSerializer.SerializeToElement(allStates),
                ["from"] = Utc("2026-08-23T00:00:00.123456789Z"),
                ["to"] = Utc("2026-08-24T00:00:00Z"),
                ["dueFrom"] = Utc("2026-08-23T00:00:00Z"),
                ["dueTo"] = Utc("2026-08-25T00:00:00Z"),
                ["evaluationTimeZone"] = JsonSerializer.SerializeToElement("America/Sao_Paulo"),
                ["projection"] = JsonSerializer.SerializeToElement(allProjection),
                ["pageSize"] = JsonSerializer.SerializeToElement(200)
            }, CancellationToken.None);
        }

        observed.Count.ShouldBe(2);
        observed.ShouldAllBe(request =>
            request.Query.CompletionStates!.SequenceEqual(Enum.GetValues<CalendarTodoCompletionState>())
            && request.Projection.SequenceEqual(Enum.GetValues<CalendarTodoProjectionField>())
            && request.PageSize == 200
            && request.Query.From == new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_568));
        observed[0].Query.Scope.Calendar!.Name.ShouldBe("Work");
        observed[1].Query.Scope.Calendar!.Href.ShouldBe("https://cal.example/work/");
    }

    [Fact]
    public async Task MalformedClosedUnionShapesAreRejectedBeforeModule()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        var tool = new CalendarTodoTools(module);
        var all = JsonSerializer.SerializeToElement(new { mode = "all" });
        var malformed = new IDictionary<string, JsonElement>?[]
        {
            null,
            new Dictionary<string, JsonElement>(),
            Arguments(("cursor", 42)),
            Arguments(("cursor", "")),
            Arguments(("cursor", new string('x', 2049))),
            Arguments(("cursor", "opaque"), ("scope", new { mode = "all" })),
            Arguments(("cursor", "opaque"), ("pageSize", "one")),
            Arguments(("cursor", "opaque"), ("pageSize", 0)),
            Arguments(("cursor", "opaque"), ("pageSize", 201)),
            StartArguments(("unknown", true)),
            StartArguments(("scope", "all")),
            StartArguments(("scope", new { mode = "default" })),
            StartArguments(("scope", new { mode = "all", calendar = new { by = "name", name = "Work" } })),
            StartArguments(("scope", JsonDocument.Parse("{\"mode\":\"all\",\"mode\":\"all\"}").RootElement.Clone())),
            StartArguments(("scope", new { mode = "selected" })),
            StartArguments(("scope", new { mode = "selected", calendar = new { by = "private", name = "Work" } })),
            StartArguments(("scope", new { mode = "selected", calendar = new { by = "name", name = " Work" } })),
            StartArguments(("scope", new { mode = "selected", calendar = new { by = "href", href = "", name = "Work" } })),
            StartArguments(("completionStates", Array.Empty<string>())),
            StartArguments(("completionStates", new[] { "open", "open" })),
            StartArguments(("completionStates", new[] { "private" })),
            StartArguments(("completionStates", 42)),
            StartArguments(("projection", Array.Empty<string>())),
            StartArguments(("projection", new[] { "summary", "summary" })),
            StartArguments(("projection", new[] { "private" })),
            StartArguments(("projection", 42)),
            Without(StartArguments(), "to"),
            Without(StartArguments(), "from"),
            StartArguments(("from", new { kind = "private", value = "2026-08-23T00:00:00Z" })),
            StartArguments(("from", new { kind = "utcDateTime" })),
            StartArguments(("from", new { kind = "utcDateTime", value = "2026-08-23T00:00:00+01:00" })),
            StartArguments(("from", new { kind = "utcDateTime", value = "2026-08-23T00:00:00.Z" })),
            StartArguments(("from", new { kind = "utcDateTime", value = "2026-08-23T00:00:00.aZ" })),
            Without(StartArguments(
                ("dueFrom", new { kind = "utcDateTime", value = "2026-08-23T00:00:00Z" }),
                ("dueTo", new { kind = "utcDateTime", value = "2026-08-24T00:00:00Z" })), "dueTo"),
            StartArguments(("evaluationTimeZone", JsonSerializer.SerializeToElement<string?>(null))),
            StartArguments(("evaluationTimeZone", 42)),
            StartArguments(("pageSize", "one")),
            StartArguments(("pageSize", 0)),
            StartArguments(("pageSize", 201))
        };

        for (var index = 0; index < malformed.Length; index++)
        {
            var result = await tool.QueryRawAsync(malformed[index], CancellationToken.None);
            result.StructuredContent!.Value.GetProperty("code").GetString()
                .ShouldBe("invalid_input", $"malformed case {index}");
        }
        await module.DidNotReceive().QueryTodosAsync(
            Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>());
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

    [Fact]
    public async Task FailureReplyPreservesEveryClosedEvidenceVocabulary()
    {
        var categories = new (QueryFailureCategory Value, string Wire)[]
        {
            (QueryFailureCategory.Input, "input"),
            (QueryFailureCategory.State, "state"),
            (QueryFailureCategory.LimitsAndAdmission, "limitsAndAdmission"),
            (QueryFailureCategory.Upstream, "upstream"),
            (QueryFailureCategory.CapabilityAndProjection, "capabilityAndProjection"),
            (QueryFailureCategory.Selection, "selection")
        };
        var phases = new (QueryFailurePhase Value, string Wire)[]
        {
            (QueryFailurePhase.SchemaLexicalDiscriminator, "schemaLexicalDiscriminator"),
            (QueryFailurePhase.Pagination, "pagination"),
            (QueryFailurePhase.Execution, "execution"),
            (QueryFailurePhase.AdmissionAndPayload, "admissionAndPayload"),
            (QueryFailurePhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability"),
            (QueryFailurePhase.TargetRevision, "targetRevision"),
            (QueryFailurePhase.CompleteResourceSemantics, "completeResourceSemantics"),
            (QueryFailurePhase.OriginScopeAuthorization, "originScopeAuthorization")
        };
        for (var index = 0; index < phases.Length; index++)
        {
            var category = categories[index % categories.Length];
            var error = await InvokeFailureAsync(new QueryFailure(
                QueryFailureCode.LimitExhausted,
                category.Value,
                "safe",
                false,
                phases[index].Value));
            error.GetProperty("category").GetString().ShouldBe(category.Wire);
            error.GetProperty("phase").GetString().ShouldBe(phases[index].Wire);
        }

        var candidate = new QueryAuthorizedCandidate(
            "https://cal.example/calendars/work/",
            "Work",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised,
            [new CapabilityEvidence("probe", "event")],
            []);
        var dimensions = new (QueryLimitDimension? Value, string? Wire)[]
        {
            (QueryLimitDimension.ResourceCount, "resource_count"),
            (QueryLimitDimension.AttemptCount, "attempt_count"),
            (QueryLimitDimension.ByteCount, "byte_count"),
            (QueryLimitDimension.ElapsedTime, "elapsed_time"),
            (null, null)
        };
        foreach (var dimension in dimensions)
        {
            var error = await InvokeFailureAsync(new QueryFailure(
                QueryFailureCode.LimitExhausted,
                QueryFailureCategory.LimitsAndAdmission,
                "safe",
                true,
                QueryFailurePhase.Execution,
                new QueryExecutionLimits(1, 2, 3, 4, 5, 6, dimension.Value, 7, 8),
                [candidate],
                123));
            var limits = error.GetProperty("limits");
            limits.GetProperty("resourcesInspected").GetInt32().ShouldBe(1);
            limits.GetProperty("calendarCount").GetInt32().ShouldBe(2);
            limits.GetProperty("occurrenceCount").GetInt32().ShouldBe(3);
            limits.GetProperty("byteCount").GetInt32().ShouldBe(4);
            limits.GetProperty("itemCount").GetInt32().ShouldBe(5);
            limits.GetProperty("snapshotCount").GetInt32().ShouldBe(6);
            limits.GetProperty("observed").GetInt64().ShouldBe(7);
            limits.GetProperty("limit").GetInt64().ShouldBe(8);
            if (dimension.Wire is null)
                limits.TryGetProperty("dimension", out _).ShouldBeFalse();
            else
                limits.GetProperty("dimension").GetString().ShouldBe(dimension.Wire);
            error.GetProperty("authorizedCandidates")[0].GetProperty("calendar").GetProperty("href")
                .GetString().ShouldBe(candidate.CalendarHref);
            error.GetProperty("retryAfterMs").GetInt32().ShouldBe(123);
        }
    }

    private static QueryReply<CalendarTodoQueryPageItem>.Failure FailureReply() => new(new QueryFailure(
        QueryFailureCode.InvalidInput,
        QueryFailureCategory.Input,
        "safe",
        false,
        QueryFailurePhase.SchemaLexicalDiscriminator));

    private static async Task<JsonElement> InvokeFailureAsync(QueryFailure failure)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryTodosAsync(Arg.Any<CalendarTodoQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarTodoQueryPageItem>.Failure(failure));
        var result = await new CalendarTodoTools(module).QueryRawAsync(
            Arguments(("cursor", "opaque")),
            CancellationToken.None);
        result.IsError.ShouldBe(true);
        return result.StructuredContent.ShouldNotBeNull();
    }

    private static Dictionary<string, JsonElement> StartArguments(
        params (string Name, object? Value)[] replacements)
    {
        var arguments = Arguments(
            ("scope", new { mode = "all" }),
            ("from", new { kind = "utcDateTime", value = "2026-08-23T00:00:00Z" }),
            ("to", new { kind = "utcDateTime", value = "2026-08-24T00:00:00Z" }));
        foreach (var (name, value) in replacements)
            arguments[name] = Element(value);
        return arguments;
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => Element(value.Value), StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Without(
        Dictionary<string, JsonElement> arguments,
        string name)
    {
        arguments.Remove(name);
        return arguments;
    }

    private static JsonElement Utc(string value) => JsonSerializer.SerializeToElement(new
        { kind = "utcDateTime", value });

    private static JsonElement Element(object? value) => value is JsonElement element
        ? element
        : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
}
