using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarResourceMoveToolsTests
{
    [Fact]
    public async Task MoveRawAsync_UsesFrozenRevisionAndSelectedCalendarAndReturnsVerifiedSnapshot()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarResourceMoveRequest? observed = null;
        service.MoveResourceAsync(
                Arg.Do<CalendarResourceMoveRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceMoveResult.Success(Snapshot()));
        var sut = new CalendarResourceMoveTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"revision\":{\"href\":\"https://cal.example/tasks/reviewed.ics\","
            + "\"entityUid\":\"move-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},"
            + "\"destination\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"name\",\"name\":\"Archive\"}}}");

        var result = await sut.MoveRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        observed.ShouldNotBeNull();
        observed.Revision.ShouldBe(new CalendarResourceRevisionReference(
            "https://cal.example/tasks/reviewed.ics",
            "move-1",
            CalendarEntityKind.Todo,
            "\"r1\""));
        observed.Destination.ShouldBe(CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive")));
    }

    [Fact]
    public async Task MoveRawAsync_MapsFrozenDefaultDestinationWithoutMrtrRoundTrip()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarResourceMoveRequest? observed = null;
        service.MoveResourceAsync(
                Arg.Do<CalendarResourceMoveRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceMoveResult.Success(Snapshot()));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull().Destination.ShouldBe(CalendarMoveDestination.Default);
        await service.Received(1).MoveResourceAsync(
            Arg.Any<CalendarResourceMoveRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"revision\":null,\"destination\":{\"mode\":\"default\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"move-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\",\"unknown\":true},\"destination\":{\"mode\":\"default\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"move-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},\"destination\":{\"mode\":\"all\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"move-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},\"destinationHref\":\"https://cal.example/archive/a.ics\"}")]
    public async Task MoveRawAsync_RejectsNonFrozenShapesBeforeService(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await sut.MoveRawAsync(arguments, CancellationToken.None);

        AssertError(result, "invalid_input", "schemaLexicalDiscriminator", "not_attempted");
        await service.DidNotReceive().MoveResourceAsync(
            Arg.Any<CalendarResourceMoveRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveRawAsync_PreservesWeakEntityTagForOrderedServiceValidation()
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveResult(
                CalendarResourceMoveCode.ConcurrencyUnavailable,
                CalendarMutationState.NotAttempted));
        var sut = CreateTool(service);
        var arguments = Arguments("{\"mode\":\"default\"}");
        arguments["revision"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/tasks/reviewed.ics",
            entityUid = "move-1",
            entityKind = "todo",
            entityTag = "W/\"r1\""
        });

        var result = await sut.MoveRawAsync(arguments, CancellationToken.None);

        AssertError(result, "concurrency_unavailable", "targetRevision", "not_attempted");
        await service.Received(1).MoveResourceAsync(
            Arg.Is<CalendarResourceMoveRequest>(request => request.Revision.EntityTag == "W/\"r1\""),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceMoveCode.NotFound, CalendarMutationState.NotAttempted, "not_found", "selection", "selectionDiscoveryCapability")]
    [InlineData(CalendarResourceMoveCode.Ambiguous, CalendarMutationState.NotAttempted, "ambiguous", "selection", "selectionDiscoveryCapability")]
    [InlineData(CalendarResourceMoveCode.InvalidInput, CalendarMutationState.NotAttempted, "invalid_input", "input", "schemaLexicalDiscriminator")]
    [InlineData(CalendarResourceMoveCode.OutsideScope, CalendarMutationState.NotAttempted, "outside_scope", "selection", "originScopeAuthorization")]
    [InlineData(CalendarResourceMoveCode.EntityKindMismatch, CalendarMutationState.NotAttempted, "entity_kind_mismatch", "state", "targetRevision")]
    [InlineData(CalendarResourceMoveCode.UnsupportedCapability, CalendarMutationState.NotAttempted, "unsupported_capability", "capabilityAndProjection", "selectionDiscoveryCapability")]
    [InlineData(CalendarResourceMoveCode.OpaqueResource, CalendarMutationState.NotAttempted, "opaque_resource", "capabilityAndProjection", "completeResourceSemantics")]
    [InlineData(CalendarResourceMoveCode.Conflict, CalendarMutationState.NotAttempted, "conflict", "state", "targetRevision")]
    [InlineData(CalendarResourceMoveCode.DestinationConflict, CalendarMutationState.NotCommitted, "destination_conflict", "state", "execution")]
    [InlineData(CalendarResourceMoveCode.ConcurrencyUnavailable, CalendarMutationState.NotAttempted, "concurrency_unavailable", "state", "targetRevision")]
    [InlineData(CalendarResourceMoveCode.LimitExhausted, CalendarMutationState.NotAttempted, "limit_exhausted", "limitsAndAdmission", "execution")]
    [InlineData(CalendarResourceMoveCode.PayloadTooLarge, CalendarMutationState.NotAttempted, "payload_too_large", "limitsAndAdmission", "admissionAndPayload")]
    [InlineData(CalendarResourceMoveCode.UpstreamUnauthorized, CalendarMutationState.NotCommitted, "upstream_unauthorized", "upstream", "execution")]
    [InlineData(CalendarResourceMoveCode.UpstreamForbidden, CalendarMutationState.NotCommitted, "upstream_forbidden", "upstream", "execution")]
    [InlineData(CalendarResourceMoveCode.UpstreamRateLimited, CalendarMutationState.NotCommitted, "upstream_rate_limited", "upstream", "execution")]
    [InlineData(CalendarResourceMoveCode.UpstreamUnavailable, CalendarMutationState.NotCommitted, "upstream_unavailable", "upstream", "execution")]
    [InlineData(CalendarResourceMoveCode.UpstreamProtocolError, CalendarMutationState.NotCommitted, "upstream_protocol_error", "upstream", "execution")]
    [InlineData(CalendarResourceMoveCode.FidelityFailure, CalendarMutationState.Committed, "fidelity_failure", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarResourceMoveCode.CommittedButUnverified, CalendarMutationState.Committed, "committed_but_unverified", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarResourceMoveCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed, "committed_but_concurrency_unavailable", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown, "indeterminate", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    public async Task MoveRawAsync_MapsTruthfulFrozenErrorOutcome(
        CalendarResourceMoveCode serviceCode,
        CalendarMutationState mutationState,
        string expectedCode,
        string expectedCategory,
        string expectedPhase)
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveResult(serviceCode, mutationState));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        AssertError(result, expectedCode, expectedPhase, MutationStateText(mutationState));
        result.StructuredContent!.Value.GetProperty("category").GetString().ShouldBe(expectedCategory);
    }

    [Fact]
    public async Task MoveRawAsync_UnexpectedServiceFailureIsIndeterminateAndRedacted()
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceMoveResult>(_ => throw new InvalidOperationException("private server detail"));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        AssertError(result, "upstream_protocol_error", "postWriteVerificationOrReconciliation", "unknown");
        JsonSerializer.Serialize(result).ShouldNotContain("private server detail");
    }

    [Fact]
    public async Task MoveRawAsync_EmitsFrozenElapsedTimeLimitDimension()
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveResult(
                CalendarResourceMoveCode.LimitExhausted,
                CalendarMutationState.NotAttempted,
                LimitDimension: CalendarResourceMoveLimitDimension.ElapsedTime));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        AssertError(result, "limit_exhausted", "execution", "not_attempted");
        result.StructuredContent!.Value.GetProperty("limits").GetProperty("dimension").GetString()
            .ShouldBe("elapsed_time");
    }

    [Fact]
    public async Task MoveRawAsync_EmitsFrozenCalendarCountWithoutScanEvidence()
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveResult(
                CalendarResourceMoveCode.LimitExhausted,
                CalendarMutationState.NotAttempted,
                CalendarCount: 257));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        var limits = result.StructuredContent!.Value.GetProperty("limits");
        limits.TryGetProperty("resourcesInspected", out _).ShouldBeFalse();
        limits.GetProperty("calendarCount").GetInt32().ShouldBe(257);
        limits.TryGetProperty("dimension", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(CalendarResourceMovePhase.SchemaLexicalDiscriminator, "schemaLexicalDiscriminator")]
    [InlineData(CalendarResourceMovePhase.OriginScopeAuthorization, "originScopeAuthorization")]
    [InlineData(CalendarResourceMovePhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability")]
    [InlineData(CalendarResourceMovePhase.TargetRevision, "targetRevision")]
    [InlineData(CalendarResourceMovePhase.CompleteResourceSemantics, "completeResourceSemantics")]
    [InlineData(CalendarResourceMovePhase.AdmissionAndPayload, "admissionAndPayload")]
    [InlineData(CalendarResourceMovePhase.Execution, "execution")]
    [InlineData(CalendarResourceMovePhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation")]
    public async Task MoveRawAsync_PreservesExplicitFrozenFailurePhase(
        CalendarResourceMovePhase phase,
        string expectedPhase)
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveResult(
                CalendarResourceMoveCode.UpstreamProtocolError,
                CalendarMutationState.NotAttempted,
                Phase: phase));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        AssertError(result, "upstream_protocol_error", expectedPhase, "not_attempted");
    }

    [Theory]
    [InlineData("{\"mode\":\"selected\"}")]
    [InlineData("{\"mode\":\"selected\",\"calendar\":null}")]
    [InlineData("{\"mode\":\"selected\",\"calendar\":{\"by\":\"all\",\"name\":\"Archive\"}}")]
    [InlineData("{\"mode\":\"selected\",\"calendar\":{\"by\":\"name\",\"name\":\" Archive \"}}")]
    [InlineData("{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\"relative/\"}}")]
    [InlineData("{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\"https://cal.example/archive/\",\"name\":\"Archive\"}}")]
    public async Task MoveRawAsync_RejectsInvalidSelectedCalendarUnion(string destination)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments(destination), CancellationToken.None);

        AssertError(result, "invalid_input", "schemaLexicalDiscriminator", "not_attempted");
        await service.DidNotReceive().MoveResourceAsync(
            Arg.Any<CalendarResourceMoveRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveRawAsync_MapsSelectedCalendarByHref()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarResourceMoveRequest? observed = null;
        service.MoveResourceAsync(
                Arg.Do<CalendarResourceMoveRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceMoveResult.Success(Snapshot()));
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(
            Arguments("{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\"https://cal.example/archive/\"}}"),
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull().Destination.Calendar!.Href.ShouldBe("https://cal.example/archive/");
    }

    [Fact]
    public async Task MoveRawAsync_MapsUpstreamCancellationBeforeDispatchAsUnavailable()
    {
        var service = Substitute.For<ICalendarService>();
        service.MoveResourceAsync(Arg.Any<CalendarResourceMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceMoveResult>(_ => throw new OperationCanceledException());
        var sut = CreateTool(service);

        var result = await sut.MoveRawAsync(Arguments("{\"mode\":\"default\"}"), CancellationToken.None);

        AssertError(result, "upstream_unavailable", "selectionDiscoveryCapability", "not_attempted");
        result.StructuredContent!.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
    }

    private static CalendarResourceMoveTools CreateTool(ICalendarService service) => new(
        service,
        TimeProvider.System);

    private static Dictionary<string, JsonElement> Arguments(string destination) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"revision\":{\"href\":\"https://cal.example/tasks/reviewed.ics\","
            + "\"entityUid\":\"move-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},"
            + "\"destination\":" + destination + "}")!;

    private static void AssertError(
        ModelContextProtocol.Protocol.CallToolResult result,
        string code,
        string phase,
        string mutationState)
    {
        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("phase").GetString().ShouldBe(phase);
        structured.GetProperty("mutationState").GetString().ShouldBe(mutationState);
    }

    private static string MutationStateText(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };

    private static CalendarResourceSnapshot Snapshot()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VTODO\r\nUID:move-1\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/archive/",
            "https://cal.example/archive/move-1.ics",
            "\"r2\"",
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "move-1", null),
            []);
    }
}
