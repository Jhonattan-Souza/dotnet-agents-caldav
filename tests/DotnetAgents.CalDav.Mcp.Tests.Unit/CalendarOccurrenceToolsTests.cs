using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarOccurrenceToolsTests
{
    [Fact]
    public async Task QueryRawAsync_OversizedFailurePublishesOnlyTheSelectedPayloadError()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(new QueryFailure(
                QueryFailureCode.UpstreamUnavailable,
                QueryFailureCategory.Upstream,
                new string('x', CalendarQueryToolSupport.MaximumStructuredResultBytes),
                true,
                QueryFailurePhase.Execution)));
        var sut = new CalendarOccurrenceTools(module);

        var (result, operation) = await ToolTelemetryTestScope.CaptureAsync(
            "calendar_occurrences.query",
            () => sut.QueryRawAsync(new Dictionary<string, JsonElement>
            {
                ["cursor"] = JsonSerializer.SerializeToElement("opaque")
            }, CancellationToken.None));

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        operation.ShouldMatchStructuredError(result.StructuredContent.Value);
    }

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

    [Fact]
    public async Task StartMayOmitOptionalContextAndPageSizeForModuleResolutionAndDefaulting()
    {
        var module = Substitute.For<ICalendarQueryModule>();
        CalendarOccurrenceQueryRequest? observed = null;
        module.QueryOccurrencesAsync(
                Arg.Do<CalendarOccurrenceQueryRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(new QueryFailure(
                QueryFailureCode.InvalidInput,
                QueryFailureCategory.Input,
                "missing configured context",
                false,
                QueryFailurePhase.SchemaLexicalDiscriminator)));
        var arguments = StartArguments();
        arguments.Remove("evaluationTimeZone");
        arguments.Remove("pageSize");

        _ = await new CalendarOccurrenceTools(module).QueryRawAsync(
            arguments,
            TestContext.Current.CancellationToken);

        var start = observed.ShouldBeOfType<CalendarOccurrenceQueryRequest.Start>();
        start.PageSize.ShouldBe(50);
        start.Query.EvaluationTimeZone.ShouldBeNull();
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

    [Fact]
    public async Task FailureReplyMechanicallyMapsTheClosedModuleVocabulary()
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
            (new(QueryFailureCode.TemporalUnresolved, QueryFailureCategory.CapabilityAndProjection, "time", false,
                QueryFailurePhase.CompleteResourceSemantics), "temporal_unresolved", "capabilityAndProjection",
                "completeResourceSemantics"),
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
            (new(QueryFailureCode.NotFound, QueryFailureCategory.Selection, "missing", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "not_found", "selection",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.Ambiguous, QueryFailureCategory.Selection, "ambiguous", false,
                QueryFailurePhase.SelectionDiscoveryCapability), "ambiguous", "selection",
                "selectionDiscoveryCapability"),
            (new(QueryFailureCode.OutsideScope, QueryFailureCategory.Selection, "scope", false,
                QueryFailurePhase.OriginScopeAuthorization), "outside_scope", "selection", "originScopeAuthorization")
        };

        foreach (var testCase in cases)
        {
            var result = await InvokeFailureAsync(testCase.Failure);

            result.GetProperty("code").GetString().ShouldBe(testCase.Code);
            result.GetProperty("category").GetString().ShouldBe(testCase.Category);
            result.GetProperty("phase").GetString().ShouldBe(testCase.Phase);
            if (testCase.Failure.RetryAfterMs is not null)
                result.GetProperty("retryAfterMs").GetInt32().ShouldBe(123);
        }
    }

    [Fact]
    public async Task FailureReplyPreservesEveryBoundedEvidenceFieldAndClosedLimitDimension()
    {
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
                "limit",
                false,
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
            if (dimension.Wire is null)
                limits.TryGetProperty("dimension", out _).ShouldBeFalse();
            else
                limits.GetProperty("dimension").GetString().ShouldBe(dimension.Wire);
            limits.GetProperty("observed").GetInt64().ShouldBe(7);
            limits.GetProperty("limit").GetInt64().ShouldBe(8);
            error.GetProperty("authorizedCandidates")[0].GetProperty("calendar").GetProperty("href")
                .GetString().ShouldBe(candidate.CalendarHref);
            error.GetProperty("retryAfterMs").GetInt32().ShouldBe(123);
        }
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
        module.QueryOccurrencesAsync(Arg.Any<CalendarOccurrenceQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(failure));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new CalendarOccurrenceTools(module).QueryRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["cursor"] = JsonSerializer.SerializeToElement("opaque")
            },
            CancellationToken.None));
    }

    public static TheoryData<Dictionary<string, JsonElement>> InvalidShapes => new()
    {
        new Dictionary<string, JsonElement>(),
        StartArguments(("unknown", JsonSerializer.SerializeToElement(true))),
        StartArguments(("evaluationTimeZone", JsonSerializer.SerializeToElement<string?>(null))),
        StartArguments(("pageSize", JsonSerializer.SerializeToElement(0))),
        StartArguments(("pageSize", JsonSerializer.SerializeToElement(201))),
        StartArguments(("pageSize", JsonSerializer.SerializeToElement("1"))),
        StartArguments(("scope", JsonSerializer.SerializeToElement(new { mode = 42 }))),
        StartArguments(("from", JsonSerializer.SerializeToElement(new
            { kind = "floatingDateTime", value = "2026-08-23T00:00:00" }))),
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement(42)
        },
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("")
        },
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement(new string('x', 2049))
        },
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["pageSize"] = JsonSerializer.SerializeToElement("1")
        },
        new Dictionary<string, JsonElement>
        {
            ["cursor"] = JsonSerializer.SerializeToElement("opaque"),
            ["scope"] = JsonSerializer.SerializeToElement(new { mode = "all" })
        }
    };

    private static async Task<JsonElement> InvokeFailureAsync(QueryFailure failure)
    {
        var module = Substitute.For<ICalendarQueryModule>();
        module.QueryOccurrencesAsync(
                Arg.Any<CalendarOccurrenceQueryRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new QueryReply<CalendarOccurrenceQueryItem>.Failure(failure));

        var result = await new CalendarOccurrenceTools(module).QueryRawAsync(
            new Dictionary<string, JsonElement> { ["cursor"] = JsonSerializer.SerializeToElement("opaque") },
            TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true);
        return result.StructuredContent.ShouldNotBeNull();
    }

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
