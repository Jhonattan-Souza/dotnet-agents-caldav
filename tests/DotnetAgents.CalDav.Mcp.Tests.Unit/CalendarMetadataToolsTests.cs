using System.Net;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarMetadataToolsTests
{
    private const string Href = "https://cal.example/home/work/";

    [Fact]
    public async Task Inspect_returns_advertised_metadata_with_schema_valid_structured_content()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        module.InspectAsync(Href, Arg.Any<CancellationToken>()).Returns(Snapshot());

        var result = await new CalendarMetadataTools(module).InspectAsync(Href, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        result.StructuredContent.Value.GetProperty("scheduling").GetProperty("state").GetString().ShouldBe("unknown");
        result.StructuredContent.Value.TryGetProperty("changeTag", out _).ShouldBeFalse();
        CalendarOutputSchemaGuard.Validate("calendars.inspect", result);
        await module.Received(1).InspectAsync(Href, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inspect_and_patch_readback_carry_a_reported_change_tag_verbatim()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        var patch = new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Work"));
        var observations = Enumerable.Range(0, 11)
            .Select(index => new CalendarPropertyObservation("urn:ietf:params:xml:ns:caldav", "property-" + index, 200))
            .Append(new CalendarPropertyObservation("http://calendarserver.org/ns/", "getctag", 200)).ToArray();
        var snapshot = Snapshot() with { Properties = observations, ChangeTag = "\"d025819f\"" };
        module.InspectAsync(Href, Arg.Any<CancellationToken>()).Returns(snapshot);
        module.PatchAsync(Href, patch, Arg.Any<CancellationToken>())
            .Returns(new CalendarMetadataPatchResult(CalendarMutationState.Committed, snapshot));
        var tools = new CalendarMetadataTools(module);

        var inspected = await tools.InspectAsync(Href, CancellationToken.None);
        var patched = await tools.PatchAsync(Href, patch, CancellationToken.None);

        inspected.StructuredContent!.Value.GetProperty("changeTag").GetString().ShouldBe("\"d025819f\"");
        patched.StructuredContent!.Value.GetProperty("calendar").GetProperty("changeTag").GetString().ShouldBe("\"d025819f\"");
        CalendarOutputSchemaGuard.Validate("calendars.inspect", inspected);
        CalendarOutputSchemaGuard.Validate("calendars.patch", patched);
    }

    [Fact]
    public async Task Patch_success_declares_unconditional_concurrency_and_committed_state()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        var patch = new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Work"));
        module.PatchAsync(Href, patch, Arg.Any<CancellationToken>())
            .Returns(new CalendarMetadataPatchResult(CalendarMutationState.Committed, Snapshot()));

        var result = await new CalendarMetadataTools(module).PatchAsync(Href, patch, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("concurrency").GetString().ShouldBe("unconditional");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
    }

    [Theory]
    [InlineData("indeterminate", CalendarMutationState.Unknown, "unknown")]
    [InlineData("committed_but_unverified", CalendarMutationState.Committed, "committed")]
    [InlineData("upstream_forbidden", CalendarMutationState.NotCommitted, "not_committed")]
    public async Task Patch_preserves_mutation_truth_in_error_schema(string code, CalendarMutationState state, string expected)
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        var patch = new CalendarMetadataPatch(new CalendarMetadataTextPatch("remove"));
        module.PatchAsync(Href, patch, Arg.Any<CancellationToken>())
            .Returns(new CalendarMetadataPatchResult(state, Error: new CalendarProtocolException(code, "Inspect before another write.")));

        var result = await new CalendarMetadataTools(module).PatchAsync(Href, patch, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(code);
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe(expected);
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
    }

    [Fact]
    public async Task Patch_preflight_exception_is_not_attempted()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        module.PatchAsync(Href, Arg.Any<CalendarMetadataPatch>(), Arg.Any<CancellationToken>())
            .Returns<CalendarMetadataPatchResult>(_ => throw new CalendarProtocolException("outside_scope", "Outside Calendar Scope."));

        var result = await new CalendarMetadataTools(module).PatchAsync(Href, new CalendarMetadataPatch(), CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
    }

    [Theory]
    [InlineData(401, "upstream_unauthorized")]
    [InlineData(403, "upstream_forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(405, "unsupported_capability")]
    [InlineData(413, "payload_too_large")]
    [InlineData(429, "upstream_rate_limited")]
    [InlineData(503, "upstream_unavailable")]
    [InlineData(302, "upstream_protocol_error")]
    public async Task Read_http_failure_is_actionable_and_schema_valid(int status, string code)
    {
        var result = await CalendarProtocolToolSupport.ExecuteReadAsync(
            _ => throw new HttpRequestException("private upstream detail", null, (HttpStatusCode)status), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(code);
        result.StructuredContent.Value.GetRawText().ShouldNotContain("private upstream detail");
        CalendarOutputSchemaGuard.Validate("calendars.inspect", result);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("circuit")]
    [InlineData("limiter")]
    public async Task Inspect_resilience_failure_is_an_actionable_tool_error(string failure)
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        module.InspectAsync(Href, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarMetadataSnapshot>(ResilienceFailure(failure)));

        var result = await new CalendarMetadataTools(module).InspectAsync(Href, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        result.StructuredContent.Value.GetRawText().ShouldNotContain("private");
        result.StructuredContent.Value.TryGetProperty("calendar", out _).ShouldBeFalse();
        CalendarOutputSchemaGuard.Validate("calendars.inspect", result);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("circuit")]
    [InlineData("limiter")]
    public async Task Patch_preflight_resilience_failure_retains_not_attempted(string failure)
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        module.PatchAsync(Href, Arg.Any<CalendarMetadataPatch>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarMetadataPatchResult>(ResilienceFailure(failure)));

        var result = await new CalendarMetadataTools(module).PatchAsync(Href,
            new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Work")), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
    }

    [Fact]
    public async Task Patch_atomic_property_failure_names_each_unapplied_member_as_a_violation()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        var patch = new CalendarMetadataPatch(Color: new("set", "#FF2968"), Order: new CalendarMetadataOrderPatch("set", 1));
        module.PatchAsync(Href, patch, Arg.Any<CancellationToken>())
            .Returns(new CalendarMetadataPatchResult(CalendarMutationState.NotCommitted,
                Error: new CalendarProtocolException("upstream_forbidden", "The Calendar operation was forbidden.")
                {
                    RejectedProperties = [new("order", 403), new("color", 424)]
                }));

        var result = await new CalendarMetadataTools(module).PatchAsync(Href, patch, CancellationToken.None);

        result.IsError.ShouldBe(true);
        var content = result.StructuredContent!.Value;
        content.GetProperty("mutationState").GetString().ShouldBe("not_committed");
        content.GetProperty("violations").EnumerateArray()
            .Select(item => (item.GetProperty("pointer").GetString(), item.GetProperty("code").GetString()))
            .ShouldBe([("/patch/color", "property_not_applied"), ("/patch/order", "property_rejected")]);
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
    }

    [Fact]
    public async Task Inspect_exposes_both_descriptions_color_and_order_in_schema_valid_output()
    {
        var module = Substitute.For<ICalendarMetadataModule>();
        module.InspectAsync(Href, Arg.Any<CancellationToken>()).Returns(Snapshot() with
        {
            Description = "CalDAV text",
            DavDescription = "WebDAV text",
            Color = "#FF2968",
            Order = 0,
            TimeZoneIds = ["Europe/Berlin"]
        });

        var result = await new CalendarMetadataTools(module).InspectAsync(Href, CancellationToken.None);

        var content = result.StructuredContent!.Value;
        content.GetProperty("description").GetString().ShouldBe("CalDAV text");
        content.GetProperty("davDescription").GetString().ShouldBe("WebDAV text");
        content.GetProperty("color").GetString().ShouldBe("#FF2968");
        content.GetProperty("order").GetInt32().ShouldBe(0);
        CalendarOutputSchemaGuard.Validate("calendars.inspect", result);
    }

    private static Exception ResilienceFailure(string failure) => failure switch
    {
        "timeout" => new TimeoutRejectedException("private timeout details"),
        "circuit" => new BrokenCircuitException("private circuit details"),
        _ => new RateLimiterRejectedException("private limiter details")
    };

    [Fact]
    public async Task Caller_cancellation_propagates_without_upstream_failure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => CalendarProtocolToolSupport.ExecuteReadAsync(
            token => Task.FromCanceled<object>(token), cancelled.Token));
    }

    [Theory]
    [InlineData("calendars.inspect", "{\"calendarHref\":\"https://cal.example/home/work/\"}", true)]
    [InlineData("calendars.inspect", "{\"calendarHref\":\"https://cal.example/home/work/\",\"extra\":true}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"description\":{\"operation\":\"set\",\"value\":\"Plans\",\"language\":\"pt-BR\"}}}", true)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"displayName\":null}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"displayName\":{\"operation\":\"remove\",\"value\":\"unexpected\"}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"color\":{\"operation\":\"set\",\"value\":\"#FF2968\"},\"order\":{\"operation\":\"set\",\"value\":0},\"timeZone\":{\"operation\":\"set\",\"value\":\"America/Port-au-Prince\"}}}", true)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"color\":{\"operation\":\"remove\"},\"order\":{\"operation\":\"remove\"},\"timeZone\":{\"operation\":\"remove\"}}}", true)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"color\":{\"operation\":\"set\",\"value\":\"#FF2968FF\"}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"color\":{\"operation\":\"set\",\"value\":\"#FF2968\",\"language\":\"en\"}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"order\":{\"operation\":\"set\",\"value\":-1}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"order\":{\"operation\":\"set\",\"value\":\"1\"}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"order\":{\"operation\":\"set\",\"value\":2147483648}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"timeZone\":{\"operation\":\"set\",\"value\":\"+01:00\"}}}", false)]
    [InlineData("calendars.patch", "{\"calendarHref\":\"https://cal.example/home/work/\",\"patch\":{\"timeZone\":{\"operation\":\"remove\",\"value\":\"UTC\"}}}", false)]
    [InlineData("calendar_resources.changes", "{\"checkpoint\":\"opaque\",\"pageSize\":2}", true)]
    [InlineData("calendar_resources.changes", "{\"checkpoint\":\"opaque\",\"calendarHref\":\"https://cal.example/home/work/\"}", false)]
    public void Native_input_boundary_enforces_closed_schema_before_sdk_deserialization(string tool, string json, bool valid)
    {
        CalendarProtocolInputGuard.Validate(tool, JsonNode.Parse(json)).Count.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData("calendars.inspect")]
    [InlineData("calendars.patch")]
    [InlineData("calendars.free_busy")]
    [InlineData("calendar_resources.changes")]
    public void Native_input_boundary_rejects_duplicate_members_and_oversize_payloads(string tool)
    {
        var duplicate = StrictToolInputGuard.Reject(tool, new StrictToolInputEvidence(100, true));
        var oversized = StrictToolInputGuard.Reject(tool, new StrictToolInputEvidence(262145, false));

        duplicate.ShouldNotBeNull();
        oversized.ShouldNotBeNull();
        duplicate.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        oversized.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        CalendarOutputSchemaGuard.Validate(tool, duplicate);
        CalendarOutputSchemaGuard.Validate(tool, oversized);
    }

    private static CalendarMetadataSnapshot Snapshot() => new(Href, "Work", null, null,
        "unknown", [], "unknown", [], new CalendarAdvertisedLimits(null, null, null, null, null), [], [],
        new CalendarSchedulingObservation("unknown", null));
}
