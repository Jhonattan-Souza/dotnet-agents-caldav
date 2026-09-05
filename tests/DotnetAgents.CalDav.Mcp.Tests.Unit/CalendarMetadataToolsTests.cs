using System.Net;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
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
        CalendarOutputSchemaGuard.Validate("calendars.inspect", result);
        await module.Received(1).InspectAsync(Href, Arg.Any<CancellationToken>());
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
