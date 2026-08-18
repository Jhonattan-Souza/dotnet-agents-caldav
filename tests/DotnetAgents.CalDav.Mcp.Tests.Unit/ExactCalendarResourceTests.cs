using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class ExactCalendarResourceTests
{
    [Fact]
    public async Task ExactCreateRawAsync_ReviewsWithoutWritingThenRequiresMrtrInput()
    {
        const string destinationHref = "https://cal.example/events/exact.ics";
        var utf8 = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:exact-create-1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var bindingRevision = new CalendarResourceRevisionReference(
            destinationHref,
            "exact-create-1",
            CalendarEntityKind.Event,
            "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, bindingRevision, new byte[32]));
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "user",
                Password = "secret"
            }),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var sut = new ExactCalendarResourceWriteTools(
            service,
            protector,
            timeProvider,
            new CalendarMutationAdmission(timeProvider));
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["destinationHref"] = System.Text.Json.JsonSerializer.SerializeToElement(destinationHref),
            ["utf8Resource"] = System.Text.Json.JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(utf8))
        };

        await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments,
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None));

        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Is<CalendarExactCreateRequest>(request =>
                request.DestinationHref == destinationHref
                && request.AuthoritativeUtf8.ToArray().SequenceEqual(utf8)),
            Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_AcceptedBoundContinuationExecutesOnce()
    {
        const string destinationHref = "https://cal.example/events/exact.ics";
        var utf8 = ExactEvent("exact-create-2");
        var revision = new CalendarResourceRevisionReference(
            destinationHref, "exact-create-2", CalendarEntityKind.Event, "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        service.ExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarExactResourceResult.Success(CreateSnapshot("\"r1\"")));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, utf8);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(2).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
        await service.Received(1).ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceRawAsync_RequiresReviewBeforeExecution()
    {
        var service = Substitute.For<ICalendarService>();
        var revision = ExactRevision();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);

        await Should.ThrowAsync<InputRequiredException>(() => sut.ReplaceRawAsync(
            ReplaceArguments(revision, ExactEvent(revision.EntityUid)), null, null, true, CancellationToken.None));

        await service.Received(1).ReviewExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_RequiresReviewBeforeExecution()
    {
        var service = Substitute.For<ICalendarService>();
        var revision = ExactRevision();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);

        await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            MoveArguments(revision), null, null, true, CancellationToken.None));

        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("move")]
    public async Task ExactRevisionWriteRawAsync_MapsCanonicalWeakTagToTypedConcurrencyFailure(string operation)
    {
        var weakRevision = ExactRevision() with { EntityTag = "W/\"r1\"" };
        var failure = new CalendarExactResourceReviewResult(
            new CalendarExactResourceResult(
                CalendarExactResourceCode.ConcurrencyUnavailable,
                CalendarMutationState.NotAttempted,
                Phase: CalendarExactResourcePhase.TargetRevision),
            null,
            default);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        var sut = CreateWriteTools(service);

        var result = operation == "replace"
            ? await sut.ReplaceRawAsync(
                ReplaceArguments(weakRevision, ExactEvent(weakRevision.EntityUid)),
                null,
                null,
                true,
                TestContext.Current.CancellationToken)
            : await sut.MoveRawAsync(
                MoveArguments(weakRevision),
                null,
                null,
                true,
                TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("concurrency_unavailable");
        await service.DidNotReceive().ExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarExactResourceCode.InvalidInput, "invalid_input")]
    [InlineData(CalendarExactResourceCode.InvalidCalendarData, "invalid_calendar_data")]
    [InlineData(CalendarExactResourceCode.NotFound, "not_found")]
    [InlineData(CalendarExactResourceCode.OutsideScope, "outside_scope")]
    [InlineData(CalendarExactResourceCode.EntityKindMismatch, "entity_kind_mismatch")]
    [InlineData(CalendarExactResourceCode.UnsupportedCapability, "unsupported_capability")]
    [InlineData(CalendarExactResourceCode.Conflict, "conflict")]
    [InlineData(CalendarExactResourceCode.DestinationConflict, "destination_conflict")]
    [InlineData(CalendarExactResourceCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(CalendarExactResourceCode.LimitExhausted, "limit_exhausted")]
    [InlineData(CalendarExactResourceCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(CalendarExactResourceCode.UpstreamUnauthorized, "upstream_unauthorized")]
    [InlineData(CalendarExactResourceCode.UpstreamForbidden, "upstream_forbidden")]
    [InlineData(CalendarExactResourceCode.UpstreamRateLimited, "upstream_rate_limited")]
    [InlineData(CalendarExactResourceCode.UpstreamUnavailable, "upstream_unavailable")]
    [InlineData(CalendarExactResourceCode.UpstreamProtocolError, "upstream_protocol_error")]
    [InlineData(CalendarExactResourceCode.FidelityFailure, "fidelity_failure")]
    [InlineData(CalendarExactResourceCode.CommittedButUnverified, "committed_but_unverified")]
    [InlineData(CalendarExactResourceCode.CommittedButConcurrencyUnavailable, "committed_but_concurrency_unavailable")]
    [InlineData(CalendarExactResourceCode.Indeterminate, "indeterminate")]
    public async Task ExactCreateRawAsync_MapsEveryFrozenFailureCode(
        CalendarExactResourceCode code,
        string expectedCode)
    {
        const string destinationHref = "https://cal.example/events/failure.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(new CalendarExactResourceResult(
                code,
                CalendarMutationState.NotAttempted,
                Phase: CalendarExactResourcePhase.CompleteResourceSemantics), null, default));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("failure")),
            null,
            null,
            false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("completeResourceSemantics");
    }

    [Fact]
    public async Task ExactWriteError_RedactsAuthoritativeAndRawSnapshotContent()
    {
        const string destinationHref = "https://cal.example/events/redacted.ics";
        var snapshot = CreateSnapshot("\"secret-etag\"");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(
                new CalendarExactResourceResult(
                    CalendarExactResourceCode.Conflict,
                    CalendarMutationState.NotCommitted,
                    snapshot,
                    Phase: CalendarExactResourcePhase.TargetRevision),
                null,
                default));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("redacted")),
            null,
            null,
            true,
            TestContext.Current.CancellationToken);

        var current = result.StructuredContent!.Value.GetProperty("currentSnapshot");
        current.TryGetProperty("calendar", out _).ShouldBeTrue();
        current.TryGetProperty("resourceRevision", out _).ShouldBeTrue();
        current.TryGetProperty("authoritativePayload", out _).ShouldBeFalse();
        current.TryGetProperty("calendarProperties", out _).ShouldBeFalse();
        current.TryGetProperty("projection", out _).ShouldBeFalse();
        current.TryGetProperty("diagnostics", out _).ShouldBeFalse();
        result.StructuredContent.Value.GetRawText().ShouldNotContain("BEGIN:VCALENDAR");
    }

    [Theory]
    [InlineData(CalendarExactResourcePhase.SchemaLexicalDiscriminator, "schemaLexicalDiscriminator")]
    [InlineData(CalendarExactResourcePhase.OriginScopeAuthorization, "originScopeAuthorization")]
    [InlineData(CalendarExactResourcePhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability")]
    [InlineData(CalendarExactResourcePhase.TargetRevision, "targetRevision")]
    [InlineData(CalendarExactResourcePhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarExactResourcePhase.Execution, "execution")]
    public async Task ExactCreateRawAsync_MapsEveryFrozenFailurePhase(
        CalendarExactResourcePhase phase,
        string expectedPhase)
    {
        const string destinationHref = "https://cal.example/events/phase.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.Unknown,
                Phase: phase), null, default));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("phase")), null, null, false, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("unknown");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("move")]
    public async Task ExactWriteRawAsync_RejectsMissingArgumentsBeforeReview(string operation)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateWriteTools(service);

        var result = operation switch
        {
            "create" => await sut.CreateRawAsync(null, null, null, true, CancellationToken.None),
            "replace" => await sut.ReplaceRawAsync(null, null, null, true, CancellationToken.None),
            _ => await sut.MoveRawAsync(null, null, null, true, CancellationToken.None)
        };

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
    }

    [Theory]
    [InlineData("decline", null, "confirmation_declined")]
    [InlineData("cancel", null, "confirmation_declined")]
    [InlineData("accept", false, "confirmation_declined")]
    [InlineData("accept", null, "confirmation_mismatch")]
    [InlineData("other", true, "confirmation_mismatch")]
    public async Task ExactCreateRawAsync_NegativeOrMalformedConfirmationNeverWrites(
        string action,
        bool? confirmed,
        string expectedOutcome)
    {
        const string destinationHref = "https://cal.example/events/confirm.ics";
        var revision = new CalendarResourceRevisionReference(
            destinationHref, "confirm", CalendarEntityKind.Event, "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("confirm"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments,
            first.Result.RequestState,
            Confirmation(action, confirmed),
            true,
            CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        if (expectedOutcome == "confirmation_declined")
            structured.GetProperty("outcome").GetString().ShouldBe(expectedOutcome);
        else
            structured.GetProperty("code").GetString().ShouldBe(expectedOutcome);
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("empty-state")]
    [InlineData("null-responses")]
    [InlineData("two-responses")]
    [InlineData("wrong-key")]
    [InlineData("null-response")]
    [InlineData("two-content")]
    [InlineData("wrong-content-key")]
    [InlineData("non-boolean")]
    public async Task ExactCreateRawAsync_MalformedConfirmationEnvelopeNeverWrites(string scenario)
    {
        const string destinationHref = "https://cal.example/events/envelope.ics";
        var revision = new CalendarResourceRevisionReference(
            destinationHref, "envelope", CalendarEntityKind.Event, "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("envelope"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));
        var responses = MalformedConfirmationEnvelope(scenario);

        var result = await sut.CreateRawAsync(
            arguments,
            scenario == "empty-state" ? string.Empty : first.Result.RequestState,
            responses,
            true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_ChangedArgumentsInvalidateProtectedStateBeforeReview()
    {
        const string destinationHref = "https://cal.example/events/bound.ics";
        var revision = new CalendarResourceRevisionReference(
            destinationHref, "bound", CalendarEntityKind.Event, "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("bound"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));
        arguments["utf8Resource"] = JsonSerializer.SerializeToElement(
            Encoding.UTF8.GetString(ExactEvent("changed")));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_ChangedReviewIntentInvalidatesConfirmation()
    {
        const string destinationHref = "https://cal.example/events/intent.ics";
        var revision = new CalendarResourceRevisionReference(
            destinationHref, "intent", CalendarEntityKind.Event, "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new CalendarExactResourceReviewResult(null, revision, new byte[32]),
                new CalendarExactResourceReviewResult(null, revision, Enumerable.Repeat((byte)1, 32).ToArray()));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("intent"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_ValidReviewWithoutMrtrIsUnsupportedAndDoesNotWrite()
    {
        const string destinationHref = "https://cal.example/events/no-mrtr.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, new CalendarResourceRevisionReference(
                destinationHref, "no-mrtr", CalendarEntityKind.Event, "*"), new byte[32]));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("no-mrtr")), null, null, false, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("move")]
    public async Task ExactWriteRawAsync_RejectsOversizeArgumentsBeforeAdmission(string operation)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = CreateWriteTools(service);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["padding"] = JsonSerializer.SerializeToElement(new string('x', ExactCalendarResourceWriteTools.MaximumArgumentBytes))
        };

        var result = operation switch
        {
            "create" => await sut.CreateRawAsync(arguments, null, null, true, CancellationToken.None),
            "replace" => await sut.ReplaceRawAsync(arguments, null, null, true, CancellationToken.None),
            _ => await sut.MoveRawAsync(arguments, null, null, true, CancellationToken.None)
        };

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Fact]
    public async Task ExactCreateRawAsync_AllowsEscapeExpandedResourceAtDecodedLimit()
    {
        const string destinationHref = "https://cal.example/events/escaped.ics";
        var resource = EscapeExpandedExactEvent(4 * 1024 * 1024);
        var bindingRevision = new CalendarResourceRevisionReference(
            destinationHref,
            "escaped",
            CalendarEntityKind.Event,
            "*");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, bindingRevision, new byte[32]));

        await Should.ThrowAsync<InputRequiredException>(() => CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, resource),
            null,
            null,
            true,
            TestContext.Current.CancellationToken));

        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Is<CalendarExactCreateRequest>(request => request.AuthoritativeUtf8.Length == resource.Length),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_MapsDecodedResourceAboveLimitAfterTransportAdmission()
    {
        const string destinationHref = "https://cal.example/events/escaped-too-large.ics";
        var resource = EscapeExpandedExactEvent((4 * 1024 * 1024) + 1);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(
                new CalendarExactResourceResult(
                    CalendarExactResourceCode.PayloadTooLarge,
                    CalendarMutationState.NotAttempted,
                    Phase: CalendarExactResourcePhase.SchemaLexicalDiscriminator),
                null,
                default));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, resource),
            null,
            null,
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await service.DidNotReceive().ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    public async Task ExactWriteRawAsync_RejectsOversizeDecodedResourceBeforeMalformedShape(string operation)
    {
        var oversized = new string('x', ExactCalendarResourceWriteTools.MaximumDecodedResourceBytes + 1);
        var arguments = operation == "create"
            ? new Dictionary<string, JsonElement>
            {
                ["destinationHref"] = JsonSerializer.SerializeToElement("relative.ics"),
                ["utf8Resource"] = JsonSerializer.SerializeToElement(oversized)
            }
            : new Dictionary<string, JsonElement>
            {
                ["revision"] = JsonSerializer.SerializeToElement("malformed"),
                ["utf8Resource"] = JsonSerializer.SerializeToElement(oversized)
            };
        var service = Substitute.For<ICalendarService>();
        var sut = CreateWriteTools(service);

        var result = operation == "create"
            ? await sut.CreateRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken)
            : await sut.ReplaceRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("admissionAndPayload");
        service.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create", 1)]
    [InlineData("create", 2)]
    [InlineData("replace", 1)]
    [InlineData("replace", 2)]
    public async Task ExactWriteRawAsync_RejectsBase64DecodedResourceAboveLimitBeforeMalformedShape(
        string operation,
        int extraBytes)
    {
        var encoded = Convert.ToBase64String(
            new byte[ExactCalendarResourceWriteTools.MaximumDecodedResourceBytes + extraBytes]);
        var arguments = operation == "create"
            ? new Dictionary<string, JsonElement>
            {
                ["destinationHref"] = JsonSerializer.SerializeToElement("relative.ics"),
                ["base64Utf8Resource"] = JsonSerializer.SerializeToElement(encoded)
            }
            : new Dictionary<string, JsonElement>
            {
                ["revision"] = JsonSerializer.SerializeToElement("malformed"),
                ["base64Utf8Resource"] = JsonSerializer.SerializeToElement(encoded)
            };
        var service = Substitute.For<ICalendarService>();
        var sut = CreateWriteTools(service);

        var result = operation == "create"
            ? await sut.CreateRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken)
            : await sut.ReplaceRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("admissionAndPayload");
        service.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactCreateRawAsync_RejectsNonCanonicalBase64AtDecodedLimitAsInvalidInput()
    {
        var encoded = Convert.ToBase64String(
            new byte[ExactCalendarResourceWriteTools.MaximumDecodedResourceBytes]) + " ";
        var arguments = new Dictionary<string, JsonElement>
        {
            ["destinationHref"] = JsonSerializer.SerializeToElement("relative.ics"),
            ["base64Utf8Resource"] = JsonSerializer.SerializeToElement(encoded)
        };
        var service = Substitute.For<ICalendarService>();

        var result = await CreateWriteTools(service).CreateRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("schemaLexicalDiscriminator");
        service.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    public async Task ExactWriteRawAsync_RejectsOversizeMetadataBeforeUnknownPropertyShape(string operation)
    {
        var arguments = operation == "create"
            ? CreateArguments("https://cal.example/events/metadata-shape.ics", ExactEvent("metadata-shape"))
            : ReplaceArguments(ExactRevision(), ExactEvent("exact-write-1"));
        arguments["unknown"] = JsonSerializer.SerializeToElement(
            new string('x', ExactCalendarResourceWriteTools.MaximumMetadataArgumentBytes));
        var service = Substitute.For<ICalendarService>();
        var sut = CreateWriteTools(service);

        var result = operation == "create"
            ? await sut.CreateRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken)
            : await sut.ReplaceRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("admissionAndPayload");
        service.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("move")]
    public async Task ExactWriteRawAsync_RejectsOversizeMetadataBeforeReview(string operation)
    {
        var service = Substitute.For<ICalendarService>();
        var large = new string('a', 70_000);
        var revision = ExactRevision() with { EntityUid = large };
        var arguments = operation switch
        {
            "create" => CreateArguments($"https://cal.example/events/{large}.ics", ExactEvent("metadata")),
            "replace" => ReplaceArguments(revision, ExactEvent("metadata")),
            _ => MoveArguments(revision)
        };
        var sut = CreateWriteTools(service);

        var result = operation switch
        {
            "create" => await sut.CreateRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken),
            "replace" => await sut.ReplaceRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken),
            _ => await sut.MoveRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken)
        };

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await service.DidNotReceive().ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ReviewExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create", -1)]
    [InlineData("create", 0)]
    [InlineData("create", 1)]
    [InlineData("replace", -1)]
    [InlineData("replace", 0)]
    [InlineData("replace", 1)]
    [InlineData("move", -1)]
    [InlineData("move", 0)]
    [InlineData("move", 1)]
    public async Task ExactWriteRawAsync_EnforcesExactMetadataBoundaryBeforeReview(
        string operation,
        int extraByte)
    {
        Dictionary<string, JsonElement> ArgumentsWithVariableLength(int length)
        {
            var variable = new string('a', length);
            var revision = ExactRevision() with { EntityUid = variable };
            return operation switch
            {
                "create" => CreateArguments(
                    $"https://cal.example/events/{variable}.ics",
                    ExactEvent("metadata-boundary")),
                "replace" => ReplaceArguments(revision, ExactEvent("metadata-boundary")),
                _ => MoveArguments(revision)
            };
        }
        var oneByte = ArgumentsWithVariableLength(1);
        var fixedBytes = ExactCalendarResourceWriteTools.MeasureMetadataArguments(oneByte) - 1;
        var arguments = ArgumentsWithVariableLength(
            ExactCalendarResourceWriteTools.MaximumMetadataArgumentBytes - fixedBytes + extraByte);
        ExactCalendarResourceWriteTools.MeasureMetadataArguments(arguments).ShouldBe(
            ExactCalendarResourceWriteTools.MaximumMetadataArgumentBytes + extraByte);
        var failure = new CalendarExactResourceReviewResult(
            new CalendarExactResourceResult(
                CalendarExactResourceCode.InvalidInput,
                CalendarMutationState.NotAttempted,
                Phase: CalendarExactResourcePhase.SchemaLexicalDiscriminator),
            null,
            default);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        var sut = CreateWriteTools(service);

        var result = operation switch
        {
            "create" => await sut.CreateRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken),
            "replace" => await sut.ReplaceRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken),
            _ => await sut.MoveRawAsync(arguments, null, null, true, TestContext.Current.CancellationToken)
        };

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(
            extraByte <= 0 ? "invalid_input" : "payload_too_large");
        var expectedReviews = extraByte <= 0 ? 1 : 0;
        service.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith(
            "ReviewExact",
            StringComparison.Ordinal)).ShouldBe(expectedReviews);
    }

    [Theory]
    [InlineData("create", 0)]
    [InlineData("create", 1)]
    [InlineData("replace", 0)]
    [InlineData("replace", 1)]
    [InlineData("move", 0)]
    [InlineData("move", 1)]
    public async Task ExactWriteRawAsync_EnforcesExactConfirmationPreviewBudget(string operation, int extraByte)
    {
        const string destinationHref = "https://cal.example/events/preview-budget-destination.ics";
        var inputRevision = ExactRevision();
        var previewDestination = operation is "create" or "move" ? destinationHref : null;
        var emptyUidRevision = inputRevision with { EntityUid = string.Empty };
        var fixedBytes = ConfirmationPreviewByteCount(operation, emptyUidRevision, previewDestination);
        var reviewedRevision = inputRevision with
        {
            EntityUid = new string('u', CalendarQueryToolSupport.MaximumHumanReadableBytes - fixedBytes + extraByte)
        };
        var review = new CalendarExactResourceReviewResult(null, reviewedRevision, new byte[32]);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(review);
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(review);
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(review);
        var sut = CreateWriteTools(service);

        Task<CallToolResult> Invoke() => operation switch
        {
            "create" => sut.CreateRawAsync(
                CreateArguments(destinationHref, ExactEvent("preview-budget")),
                null, null, true, TestContext.Current.CancellationToken),
            "replace" => sut.ReplaceRawAsync(
                ReplaceArguments(inputRevision, ExactEvent(inputRevision.EntityUid)),
                null, null, true, TestContext.Current.CancellationToken),
            _ => sut.MoveRawAsync(
                MoveArguments(inputRevision, destinationHref),
                null, null, true, TestContext.Current.CancellationToken)
        };

        if (extraByte == 0)
        {
            await Should.ThrowAsync<InputRequiredException>(Invoke);
            ConfirmationPreviewByteCount(operation, reviewedRevision, previewDestination)
                .ShouldBe(CalendarQueryToolSupport.MaximumHumanReadableBytes);
            return;
        }

        var result = await Invoke();
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("move")]
    public async Task ExactWriteRawAsync_BusyAdmissionDoesNotReview(string operation)
    {
        var service = Substitute.For<ICalendarService>();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var admission = new CalendarMutationAdmission(time);
        var held = await admission.AcquireAsync(CancellationToken.None);
        held.ShouldNotBeNull();
        var sut = CreateWriteTools(service, time, admission);
        var revision = ExactRevision();

        var result = operation switch
        {
            "create" => await sut.CreateRawAsync(
                CreateArguments("https://cal.example/events/busy.ics", ExactEvent("busy")), null, null, true, CancellationToken.None),
            "replace" => await sut.ReplaceRawAsync(
                ReplaceArguments(revision, ExactEvent(revision.EntityUid)), null, null, true, CancellationToken.None),
            _ => await sut.MoveRawAsync(MoveArguments(revision), null, null, true, CancellationToken.None)
        };

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        result.StructuredContent.Value.GetProperty("retryAfterMs").GetInt32()
            .ShouldBe(CalendarMutationAdmission.RetryAfterMilliseconds);
        held.Dispose();
    }

    [Theory]
    [InlineData("missing-revision")]
    [InlineData("short-digest")]
    public async Task ExactCreateRawAsync_RejectsMalformedReviewEvidence(string scenario)
    {
        const string destinationHref = "https://cal.example/events/evidence.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(
                null,
                scenario == "missing-revision" ? null : new CalendarResourceRevisionReference(
                    destinationHref, "evidence", CalendarEntityKind.Event, "*"),
                scenario == "short-digest" ? new byte[31] : new byte[32]));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("evidence")), null, null, true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public async Task ExactCreateRawAsync_ExpiredProtectedStateNeverRevalidatesOrWrites()
    {
        const string destinationHref = "https://cal.example/events/expired.ics";
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, new CalendarResourceRevisionReference(
                destinationHref, "expired", CalendarEntityKind.Event, "*"), new byte[32]));
        var sut = CreateWriteTools(service, time);
        var arguments = CreateArguments(destinationHref, ExactEvent("expired"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(11));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_expired");
        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_UnexpectedReviewFailureIsSanitized()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarExactResourceReviewResult>>(_ => throw new InvalidOperationException("secret"));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments("https://cal.example/events/failure.ics", ExactEvent("failure")),
            null,
            null,
            true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Theory]
    [InlineData(CalendarExactResourceCode.NoChange, false, "no_change")]
    [InlineData(CalendarExactResourceCode.Success, false, "success")]
    [InlineData(CalendarExactResourceCode.Conflict, true, "conflict")]
    public async Task ExactCreateRawAsync_MapsReviewTerminalOutcome(
        CalendarExactResourceCode code,
        bool expectedError,
        string expectedOutcome)
    {
        const string destinationHref = "https://cal.example/events/outcome.ics";
        var snapshot = CreateSnapshot("\"r2\"");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(new CalendarExactResourceResult(
                code,
                code == CalendarExactResourceCode.Conflict
                    ? CalendarMutationState.NotCommitted
                    : code == CalendarExactResourceCode.Success
                        ? CalendarMutationState.Committed
                        : CalendarMutationState.NotAttempted,
                code == CalendarExactResourceCode.NoChange ? null : snapshot), null, default));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("outcome")), null, null, true, CancellationToken.None);

        result.IsError.ShouldBe(expectedError);
        var property = expectedError ? "code" : "outcome";
        result.StructuredContent!.Value.GetProperty(property).GetString().ShouldBe(expectedOutcome);
    }

    [Fact]
    public async Task ExactReplaceRawAsync_TodoPreviewUsesTodoRevision()
    {
        var revision = new CalendarResourceRevisionReference(
            "https://cal.example/tasks/todo.ics", "todo", CalendarEntityKind.Todo, "\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var arguments = ReplaceArguments(revision, Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"));

        var required = await Should.ThrowAsync<InputRequiredException>(() => CreateWriteTools(service).ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var request = required.Result.InputRequests.ShouldNotBeNull()["confirm_exact_write"];
        var message = request.ElicitationParams.ShouldNotBeNull().Message;
        message.ShouldContain("todo");
        message.ShouldContain(revision.Href);
        message.ShouldContain(revision.EntityTag);
    }

    [Fact]
    public async Task ExactMoveRawAsync_PreviewNamesDestinationAndExpectedEntityTag()
    {
        var revision = new CalendarResourceRevisionReference(
            "https://cal.example/events/preview-source.ics",
            "preview-move",
            CalendarEntityKind.Event,
            "\"r1\"");
        const string destinationHref = "https://cal.example/events/preview-destination.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));

        var required = await Should.ThrowAsync<InputRequiredException>(() => CreateWriteTools(service).MoveRawAsync(
            MoveArguments(revision, destinationHref), null, null, true, CancellationToken.None));

        var message = required.Result.InputRequests.ShouldNotBeNull()["confirm_exact_write"]
            .ElicitationParams.ShouldNotBeNull().Message;
        message.ShouldContain(revision.Href);
        message.ShouldContain(destinationHref);
        message.ShouldContain(revision.EntityTag);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void EnsureBoundedResult_EnforcesExactStructuredByteBoundary(int extraByte, bool rejected)
    {
        var result = ResultWithStructuredSize(
            ExactCalendarResourceWriteTools.MaximumStructuredResultBytes + extraByte);

        var bounded = ExactCalendarResourceWriteTools.EnsureBoundedResult(
            result,
            CalendarMutationState.Committed);

        ExactCalendarResourceWriteTools.MeasureResult(result)
            .ShouldBe(ExactCalendarResourceWriteTools.MaximumStructuredResultBytes + extraByte);
        bounded.IsError.ShouldBe(rejected);
        if (rejected)
        {
            bounded.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
            bounded.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        }
    }

    [Fact]
    public async Task ExactCreateRawAsync_ContinuationWithoutMrtrDoesNotRevalidate()
    {
        const string destinationHref = "https://cal.example/events/continuation.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, new CalendarResourceRevisionReference(
                destinationHref, "continuation", CalendarEntityKind.Event, "*"), new byte[32]));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("continuation"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), false, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_UnexpectedExecutionFailureIsIndeterminate()
    {
        const string destinationHref = "https://cal.example/events/indeterminate.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, new CalendarResourceRevisionReference(
                destinationHref, "indeterminate", CalendarEntityKind.Event, "*"), new byte[32]));
        service.ExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarExactResourceResult>>(_ => throw new InvalidOperationException("after dispatch"));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("indeterminate"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("indeterminate");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task ExactGetAsync_ReturnsNativeResourceLinkWithoutRawContent()
    {
        var snapshot = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot });
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync(snapshot.ResourceHref, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.Content.OfType<ResourceLinkBlock>().ShouldHaveSingleItem();
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("BEGIN:VCALENDAR");
        result.StructuredContent!.Value.GetProperty("resourceLink").GetProperty("type").GetString().ShouldBe("resource_link");
    }

    [Fact]
    public async Task ReadAsync_ReturnsOnlyTheRevisionBoundByTheProtectedLink()
    {
        var snapshot = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot });
        var link = ExactCalendarResourceLink.Create(snapshot);

        var result = await ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None);

        result.CacheScope.ShouldBe(CacheScope.Private);
        result.TimeToLive.ShouldBe(TimeSpan.Zero);
        result.Contents.ShouldHaveSingleItem().ShouldBeOfType<BlobResourceContents>().DecodedData
            .ToArray().ShouldBe(snapshot.AuthoritativeUtf8.ToArray());
        ExactCalendarResourceHandler.List().Resources.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactGetBlobCanBeSubmittedToExactReplaceWithoutChangingSplitUtf8FoldBytes()
    {
        var prefix = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:roundtrip\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\n"
            + "RRULE:FREQ=YEARLY;RSCALE=CHINESE;SKIP=BACKWARD\r\nSUMMARY:Caf");
        var suffix = Encoding.UTF8.GetBytes("\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        byte[] bytes = [.. prefix, 0xc3, (byte)'\r', (byte)'\n', (byte)' ', 0xa9, .. suffix];
        var snapshot = CreateSnapshot("\"r1\"") with { AuthoritativeUtf8 = bytes };
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, new CalendarResourceRevisionReference(
                snapshot.ResourceHref, "roundtrip", CalendarEntityKind.Event, snapshot.EntityTag), new byte[32]));
        var read = await ExactCalendarResourceHandler.ReadAsync(
            ExactCalendarResourceLink.Create(snapshot).Uri, service, CancellationToken.None);
        var blob = read.Contents.ShouldHaveSingleItem().ShouldBeOfType<BlobResourceContents>();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["revision"] = RevisionElement(new CalendarResourceRevisionReference(
                snapshot.ResourceHref, "roundtrip", CalendarEntityKind.Event, snapshot.EntityTag)),
            ["base64Utf8Resource"] = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(blob.Blob.Span))
        };

        await Should.ThrowAsync<InputRequiredException>(() => CreateWriteTools(service).ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var reviewed = service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ICalendarService.ReviewExactReplaceResourceAsync))
            .GetArguments()[0].ShouldBeOfType<CalendarExactReplaceRequest>();
        reviewed.AuthoritativeUtf8.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task ReadAsync_RejectsAChangedRevision()
    {
        var linked = CreateSnapshot("\"r1\"");
        var changed = CreateSnapshot("\"r2\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(linked.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(changed.ResourceHref, changed.EntityTag, changed.AuthoritativeUtf8) with { Snapshot = changed });
        var link = ExactCalendarResourceLink.Create(linked);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None));
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.NotFound)]
    [InlineData(CalendarResourceReadCode.Success)]
    public async Task ReadAsync_RejectsUnavailableTypedRead(CalendarResourceReadCode readCode)
    {
        var linked = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(linked.ResourceHref, Arg.Any<CancellationToken>()).Returns(new CalendarResourceRead(readCode));
        var link = ExactCalendarResourceLink.Create(linked);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://snapshot/abc?etag=abc")]
    [InlineData("caldav-exact://other/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot/?etag=abc")]
    [InlineData("caldav-exact://snapshot/abc?other=abc")]
    [InlineData("caldav-exact://user@snapshot/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot:123/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot/abc?etag=abc#fragment")]
    [InlineData("caldav-exact://snapshot/abc?etag=")]
    [InlineData("caldav-exact://snapshot/abc?etag=abc&extra=1")]
    [InlineData("caldav-exact://snapshot/a/b?etag=abc")]
    [InlineData("caldav-exact://snapshot/aA==?etag=abc")]
    [InlineData("caldav-exact://snapshot/aB?etag=abc")]
    [InlineData("caldav-exact://snapshot/wyg?etag=InIxIg")]
    public async Task ReadAsync_RejectsForgedLinkWithoutCallingService(string uri)
    {
        var service = Substitute.For<ICalendarService>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(uri, service, CancellationToken.None));

        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative", "\"r1\"")]
    [InlineData("ftp://cal.example/events/a.ics", "\"r1\"")]
    [InlineData("https://user@cal.example/events/a.ics", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics?query=1", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics#fragment", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics", "W/\"weak\"")]
    [InlineData("https://cal.example/events/a.ics", "not-an-etag")]
    public async Task ReadAsync_RejectsInvalidDecodedBindingsWithoutCallingService(string href, string entityTag)
    {
        var uri = $"caldav-exact://snapshot/{Encode(href)}?etag={Encode(entityTag)}";
        var service = Substitute.For<ICalendarService>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(uri, service, CancellationToken.None));

        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactGetAsync_UsesSharedSafeUpstreamFailureMapping()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new OperationCanceledException());
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
    }

    [Fact]
    public async Task ExactGetAsync_UsesSharedDiscoveryLimitMapping()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new CalendarDiscoveryLimitException(300));
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(300);
    }

    private static CalendarResourceSnapshot CreateSnapshot(string entityTag)
    {
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/events/",
            "https://cal.example/events/a.ics",
            entityTag,
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
            []);
    }

    private static ExactCalendarResourceWriteTools CreateWriteTools(
        ICalendarService service,
        TimeProvider? suppliedTime = null,
        CalendarMutationAdmission? suppliedAdmission = null)
    {
        var timeProvider = suppliedTime
            ?? new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "user",
                Password = "secret"
            }),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new ExactCalendarResourceWriteTools(
            service,
            protector,
            timeProvider,
            suppliedAdmission ?? new CalendarMutationAdmission(timeProvider));
    }

    private static CalendarResourceRevisionReference ExactRevision() => new(
        "https://cal.example/events/source.ics",
        "exact-write-1",
        CalendarEntityKind.Event,
        "\"r1\"");

    private static Dictionary<string, JsonElement> CreateArguments(string destinationHref, byte[] utf8) => new()
    {
        ["destinationHref"] = JsonSerializer.SerializeToElement(destinationHref),
        ["utf8Resource"] = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(utf8))
    };

    private static Dictionary<string, JsonElement> ReplaceArguments(
        CalendarResourceRevisionReference revision,
        byte[] utf8) => new()
    {
        ["revision"] = RevisionElement(revision),
        ["utf8Resource"] = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(utf8))
    };

    private static Dictionary<string, JsonElement> MoveArguments(
        CalendarResourceRevisionReference revision,
        string destinationHref = "https://cal.example/events/destination.ics") => new()
    {
        ["revision"] = RevisionElement(revision),
        ["destinationHref"] = JsonSerializer.SerializeToElement(destinationHref)
    };

    private static CallToolResult ResultWithStructuredSize(int targetBytes)
    {
        var result = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { padding = string.Empty }),
            Content = [new TextContentBlock { Text = "ok" }]
        };
        var overhead = ExactCalendarResourceWriteTools.MeasureResult(result);
        result.StructuredContent = JsonSerializer.SerializeToElement(
            new { padding = new string('x', targetBytes - overhead) });
        return result;
    }

    private static JsonElement RevisionElement(CalendarResourceRevisionReference revision) =>
        JsonSerializer.SerializeToElement(new
        {
            href = revision.Href,
            entityUid = revision.EntityUid,
            entityKind = "event",
            entityTag = revision.EntityTag
        });

    private static Dictionary<string, InputResponse> AcceptedConfirmation() => new()
    {
        ["confirm_exact_write"] = InputResponse.FromElicitResult(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(true)
            }
        })
    };

    private static Dictionary<string, InputResponse> Confirmation(string action, bool? confirmed)
    {
        var content = confirmed is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(confirmed.Value)
            };
        return new Dictionary<string, InputResponse>
        {
            ["confirm_exact_write"] = InputResponse.FromElicitResult(new ElicitResult
            {
                Action = action,
                Content = content
            })
        };
    }

    private static Dictionary<string, InputResponse>? MalformedConfirmationEnvelope(string scenario)
    {
        if (scenario == "null-responses")
            return null;
        if (scenario == "two-responses")
        {
            var responses = AcceptedConfirmation();
            responses["unexpected"] = InputResponse.FromElicitResult(new ElicitResult { Action = "decline" });
            return responses;
        }
        if (scenario == "wrong-key")
        {
            return new Dictionary<string, InputResponse>
            {
                ["unexpected"] = InputResponse.FromElicitResult(new ElicitResult { Action = "decline" })
            };
        }
        if (scenario == "null-response")
            return new Dictionary<string, InputResponse> { ["confirm_exact_write"] = null! };
        var content = scenario switch
        {
            "two-content" => new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(true),
                ["unexpected"] = JsonSerializer.SerializeToElement(true)
            },
            "wrong-content-key" => new Dictionary<string, JsonElement>
            {
                ["unexpected"] = JsonSerializer.SerializeToElement(true)
            },
            "non-boolean" => new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement("yes")
            },
            _ => new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(true)
            }
        };
        return new Dictionary<string, InputResponse>
        {
            ["confirm_exact_write"] = InputResponse.FromElicitResult(new ElicitResult
            {
                Action = "accept",
                Content = content
            })
        };
    }

    private static byte[] ExactEvent(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] EscapeExpandedExactEvent(int targetBytes)
    {
        const string prefix = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:escaped\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nSUMMARY:";
        const string suffix = "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return Encoding.UTF8.GetBytes(prefix + new string('<', targetBytes - prefix.Length - suffix.Length) + suffix);
    }

    private static int ConfirmationPreviewByteCount(
        string operation,
        CalendarResourceRevisionReference revision,
        string? destinationHref)
    {
        var destination = destinationHref is null ? string.Empty : $", destination {destinationHref}";
        var kind = revision.EntityKind == CalendarEntityKind.Event ? "event" : "todo";
        var message = $"Confirm calendar_resources.exact_{operation} for href {revision.Href}{destination}, "
            + $"UID {revision.EntityUid}, kind {kind}, and expected ETag {revision.EntityTag}.";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            Message = message,
            Title = "Confirm exact write",
            Description = "Apply exactly the reviewed complete Calendar Object Resource write."
        }).Length;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
