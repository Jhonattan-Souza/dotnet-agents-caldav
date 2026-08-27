using System.Text;
using System.Text.Json;
using System.Reflection;
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
        var binding = new CalendarExactCreateReviewBinding(
            destinationHref,
            "exact-create-1",
            CalendarEntityKind.Event,
            new byte[32],
            "1");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarExactCreateReviewResult(
                null,
                binding,
                CreateReviewedExactCreate(binding, utf8)));
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
        var sut = new ExactCalendarResourceWriteTools(service, protector, timeProvider);
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
            Arg.Any<CalendarReviewedExactCreate>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_AcceptedBoundContinuationExecutesOnce()
    {
        const string destinationHref = "https://cal.example/events/exact.ics";
        var utf8 = ExactEvent("exact-create-2");
        var binding = new CalendarExactCreateReviewBinding(
            destinationHref, "exact-create-2", CalendarEntityKind.Event, new byte[32], "1");
        var reviewed = CreateReviewedExactCreate(binding, utf8);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactCreateReviewResult(null, binding, reviewed));
        service.ExactCreateResourceAsync(
                Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>())
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
            Arg.Is<CalendarReviewedExactCreate>(value => ReferenceEquals(value, reviewed)),
            Arg.Any<CancellationToken>());
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
    public async Task ExactReplaceRawAsync_AcceptedBoundContinuationExecutesOnce()
    {
        var revision = ExactRevision();
        var review = new CalendarExactResourceReviewResult(null, revision, new byte[32]);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(review);
        service.ExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarExactResourceResult.Success(CreateSnapshot("\"r2\"")));
        var sut = CreateWriteTools(service);
        var arguments = ReplaceArguments(revision, ExactEvent(revision.EntityUid));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.ReplaceRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(2).ReviewExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
        await service.Received(1).ExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceRawAsync_ContinuationTerminalOutcomeSkipsExecution()
    {
        var revision = ExactRevision();
        var initialReview = new CalendarExactResourceReviewResult(null, revision, new byte[32]);
        var terminalReview = new CalendarExactResourceReviewResult(
            new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotCommitted),
            null,
            default);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(initialReview, terminalReview);
        var sut = CreateWriteTools(service);
        var arguments = ReplaceArguments(revision, ExactEvent(revision.EntityUid));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.ReplaceRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("conflict");
        await service.DidNotReceive().ExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("decline", "confirmation_declined")]
    [InlineData("missing-response", "confirmation_mismatch")]
    [InlineData("malformed", "confirmation_mismatch")]
    [InlineData("no-mrtr", "unsupported_capability")]
    public async Task ExactReplaceRawAsync_NonExecutableContinuationsRemainReadOnly(
        string scenario,
        string expectedCode)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service);
        var arguments = ReplaceArguments(revision, ExactEvent(revision.EntityUid));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));
        var responses = scenario switch
        {
            "decline" => Confirmation("decline", null),
            "missing-response" => null,
            "malformed" => Confirmation("accept", null),
            _ => AcceptedConfirmation()
        };

        var result = await sut.ReplaceRawAsync(
            arguments,
            first.Result.RequestState,
            responses,
            scenario != "no-mrtr",
            CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        (expectedCode == "confirmation_declined"
                ? structured.GetProperty("outcome")
                : structured.GetProperty("code"))
            .GetString().ShouldBe(expectedCode);
        await service.DidNotReceive().ExactReplaceResourceAsync(
            Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceRawAsync_ExpiredProtectedStateNeverRevalidatesOrWrites()
    {
        var revision = ExactRevision();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceReviewResult(null, revision, new byte[32]));
        var sut = CreateWriteTools(service, time);
        var arguments = ReplaceArguments(revision, ExactEvent(revision.EntityUid));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.ReplaceRawAsync(
            arguments, null, null, true, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(11));

        var result = await sut.ReplaceRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_expired");
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
            .Returns(SuccessfulMoveReview(revision));
        var sut = CreateWriteTools(service);

        await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            MoveArguments(revision), null, null, true, CancellationToken.None));

        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing-binding")]
    [InlineData("revision")]
    [InlineData("destination")]
    [InlineData("digest")]
    [InlineData("policy")]
    public async Task ExactMoveRawAsync_RejectsMalformedCoreReviewEvidence(string scenario)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(InvalidMoveReview(scenario, revision));

        var result = await CreateWriteTools(service).MoveRawAsync(
            MoveArguments(revision),
            null,
            null,
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_AcceptedStateCallsOneConfirmedCoreBoundary()
    {
        var revision = ExactRevision();
        var binding = SuccessfulMoveReview(revision).Binding!;
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactMoveReviewResult(null, binding));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(CalendarExactResourceResult.Success(CreateSnapshot("\"moved\"")));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(false);
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.Received(1).ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Is<CalendarExactMoveReviewBinding>(value =>
                value.Revision == binding.Revision
                && value.DestinationHref == binding.DestinationHref
                && value.PolicyVersion == binding.PolicyVersion
                && value.SourceIntentDigest.ToArray().SequenceEqual(binding.SourceIntentDigest.ToArray())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_UnsupportedMrtrPerformsNoCoreIo()
    {
        var service = Substitute.For<ICalendarService>();

        var result = await CreateWriteTools(service).MoveRawAsync(
            MoveArguments(ExactRevision()),
            null,
            null,
            mrtrSupported: false,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await service.DidNotReceive().ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_ContinuationWithoutMrtrPerformsNoFreshCoreIo()
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: false,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("mrtr");
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("tamper", "confirmation_mismatch")]
    [InlineData("key", "confirmation_mismatch")]
    [InlineData("request", "confirmation_mismatch")]
    [InlineData("expired", "confirmation_expired")]
    public async Task ExactMoveRawAsync_ProtectedStateMismatchNeverFreshReviews(
        string mismatch,
        string expectedCode)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-23T12:00:00Z"));
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var sut = CreateWriteTools(service, time, key, MoveStateOptions());
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));
        var state = first.Result.RequestState!;
        if (mismatch == "tamper")
            state = state[..^1] + (state[^1] == 'A' ? 'B' : 'A');
        if (mismatch == "key")
        {
            var rotated = key.ToArray();
            rotated[0] ^= 0xff;
            sut = CreateWriteTools(service, time, rotated, MoveStateOptions());
        }
        if (mismatch == "request")
            arguments = MoveArguments(revision, "https://cal.example/events/changed.ics");
        if (mismatch == "expired")
            time.Advance(TimeSpan.FromMinutes(10));

        var result = await sut.MoveRawAsync(
            arguments,
            state,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("mrtr");
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_DeclinedConfirmationPerformsNoFreshCoreIo()
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            Confirmation("decline", confirmed: null),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("confirmation_declined");
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("cancelled", "upstream_unavailable", "not_attempted", true)]
    [InlineData("fault", "indeterminate", "unknown", false)]
    public async Task ExactMoveRawAsync_MapsConfirmedBoundaryFaultsWithoutAnotherReview(
        string failure,
        string expectedCode,
        string expectedMutationState,
        bool retryable)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CalendarExactResourceResult>>(_ => failure == "cancelled"
                ? throw new OperationCanceledException()
                : throw new IOException("confirmed boundary fault"));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe(expectedMutationState);
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBe(retryable);
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("success-without-snapshot", "upstream_protocol_error")]
    [InlineData("confirmation-mismatch", "confirmation_mismatch")]
    public async Task ExactMoveRawAsync_MapsConfirmedCoreEvidenceWithoutAnotherReview(
        string scenario,
        string expectedCode)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(scenario == "confirmation-mismatch"
                ? new CalendarExactResourceResult(
                    CalendarExactResourceCode.ConfirmationMismatch,
                    CalendarMutationState.NotAttempted,
                    Phase: CalendarExactResourcePhase.Mrtr)
                : new CalendarExactResourceResult(
                    CalendarExactResourceCode.Success,
                    CalendarMutationState.Committed));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, CalendarResourceProjectionKind.Event, "event")]
    [InlineData(CalendarEntityKind.Todo, CalendarResourceProjectionKind.Todo, "todo")]
    public async Task ExactMoveRawAsync_ConflictSnapshotCarriesOnlyTypedRevisionEvidence(
        CalendarEntityKind entityKind,
        CalendarResourceProjectionKind projectionKind,
        string expectedKind)
    {
        var revision = ExactRevision() with { EntityKind = entityKind };
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotCommitted,
                CreateProjectedSnapshot(projectionKind, revision.EntityUid),
                Phase: CalendarExactResourcePhase.TargetRevision));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        var current = result.StructuredContent!.Value.GetProperty("currentSnapshot");
        current.GetProperty("entityRevision").GetProperty("entityKind").GetString().ShouldBe(expectedKind);
        current.TryGetProperty("authoritativePayload", out _).ShouldBeFalse();
        current.TryGetProperty("projection", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExactMoveRawAsync_PropagatesCallerCancellationFromConfirmedCoreBoundary()
    {
        var revision = ExactRevision();
        using var callerCancellation = new CancellationTokenSource();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CalendarExactResourceResult>>(_ =>
            {
                callerCancellation.Cancel();
                throw new OperationCanceledException(callerCancellation.Token);
            });
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        await Should.ThrowAsync<OperationCanceledException>(() => sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            callerCancellation.Token));

        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactMoveRawAsync_ConfirmedBoundaryDeadlineMapsLimitExhausted()
    {
        var revision = ExactRevision();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-23T12:00:00Z"));
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellationAsync(call.ArgAt<CancellationToken>(2), executionStarted));
        var sut = CreateWriteTools(service, time);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));
        var pending = sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);
        await executionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Fact]
    public async Task ExactMoveRawAsync_CallerCancellationWinsWhenDeadlineAlsoElapses()
    {
        var revision = ExactRevision();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-23T12:00:00Z"));
        using var callerCancellation = new CancellationTokenSource();
        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        service.ExecuteConfirmedExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(),
                Arg.Any<CalendarExactMoveReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellationAsync(call.ArgAt<CancellationToken>(2), executionStarted));
        var sut = CreateWriteTools(service, time);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));
        var pending = sut.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            callerCancellation.Token);
        await executionStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        callerCancellation.Cancel();
        time.Advance(TimeSpan.FromSeconds(30));

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
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
    public async Task ExactMoveRawAsync_MalformedConfirmationEnvelopeNeverExecutes(string scenario)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        var sut = CreateWriteTools(service);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));

        var result = await sut.MoveRawAsync(
            arguments,
            scenario == "empty-state" ? string.Empty : first.Result.RequestState,
            MalformedConfirmationEnvelope(scenario),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("credential")]
    [InlineData("origin")]
    [InlineData("endpoint")]
    [InlineData("scope")]
    [InlineData("profile")]
    [InlineData("timeout")]
    public async Task ExactMoveRawAsync_ConfigurationBoundStateMismatchPerformsNoFreshReview(string changed)
    {
        var revision = ExactRevision();
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(revision));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var original = MoveStateOptions();
        var firstTools = CreateWriteTools(service, time, key, original);
        var arguments = MoveArguments(revision);
        var first = await Should.ThrowAsync<InputRequiredException>(() => firstTools.MoveRawAsync(
            arguments, null, null, true, TestContext.Current.CancellationToken));
        var current = MoveStateOptions();
        switch (changed)
        {
            case "credential": current.Password = "changed"; break;
            case "origin": current.BaseUrl = "https://other.example"; break;
            case "endpoint": current.BaseUrl = "https://cal.example/other-caldav"; break;
            case "scope": current.CalendarHrefs = "https://cal.example/archive/"; break;
            case "profile": current.InteroperabilityProfile = null; break;
            default: current.RequestTimeout = TimeSpan.FromSeconds(20); break;
        }
        var secondTools = CreateWriteTools(service, time, key, current);

        var result = await secondTools.MoveRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            TestContext.Current.CancellationToken);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.Received(1).ReviewExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExactMoveState_StaysBoundedAndPrivateForMaximumValidRequestMetadata()
    {
        var uid = new string('u', (64 * 1024) - 512);
        var revision = ExactRevision() with { EntityUid = uid };
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var binding = new CalendarExactMoveReviewBinding(
            revision,
            request.DestinationHref,
            Enumerable.Repeat((byte)0xab, 32).ToArray(),
            "server-authoritative-exact-move/1");
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var protector = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(MoveStateOptions()),
            key);
        byte[] requestBinding = [1, 2, 3, 4];

        var state = protector.ProtectExactMove("calendar_resources.exact_move", requestBinding, binding);

        state.Length.ShouldBeLessThanOrEqualTo(CalendarMutationRequestStateProtector.MaximumRequestStateCharacters);
        state.ShouldNotContain(uid);
        state.ShouldNotContain(revision.Href);
        state.ShouldNotContain(revision.EntityTag);
        state.ShouldNotContain(request.DestinationHref);
        state.ShouldNotContain("secret");
        protector.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            requestBinding,
            out var restored,
            out var expired).ShouldBeTrue();
        expired.ShouldBeFalse();
        restored.Revision.ShouldBe(binding.Revision);
        restored.DestinationHref.ShouldBe(binding.DestinationHref);
        restored.PolicyVersion.ShouldBe(binding.PolicyVersion);
        restored.SourceIntentDigest.Span.SequenceEqual(binding.SourceIntentDigest.Span).ShouldBeTrue();
    }

    [Fact]
    public void ExactMoveState_RejectsLegacyProtectedStateWithoutTypedReviewBinding()
    {
        var revision = ExactRevision();
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var protector = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(MoveStateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        byte[] requestBinding = [1, 2, 3, 4];
        var state = protector.Protect(
            "calendar_resources.exact_move",
            revision,
            requestBinding,
            [5, 6, 7, 8]);

        protector.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            requestBinding,
            out _,
            out var expired).ShouldBeFalse();
        expired.ShouldBeFalse();
    }

    [Fact]
    public void ExactMoveState_RejectsProtectedBindingWithInvalidDigestLength()
    {
        var revision = ExactRevision();
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var invalidBinding = new CalendarExactMoveReviewBinding(
            revision,
            request.DestinationHref,
            new byte[31],
            "server-authoritative-exact-move/1");
        var protector = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(MoveStateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        byte[] requestBinding = [1, 2, 3, 4];
        var state = protector.ProtectExactMove(
            "calendar_resources.exact_move",
            requestBinding,
            invalidBinding);

        protector.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            requestBinding,
            out _,
            out var expired).ShouldBeFalse();
        expired.ShouldBeFalse();
    }

    [Theory]
    [InlineData("AA")]
    [InlineData("A")]
    public void ExactMoveState_RejectsShortOrMalformedCiphertextBeforeDecryption(string state)
    {
        var revision = ExactRevision();
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var protector = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(MoveStateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());

        protector.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            [1, 2, 3, 4],
            out _,
            out var expired).ShouldBeFalse();
        expired.ShouldBeFalse();
    }

    [Fact]
    public void ExactMoveState_RejectsOversizedProtectedPolicyMetadata()
    {
        var revision = ExactRevision();
        var binding = new CalendarExactMoveReviewBinding(
            revision,
            "https://cal.example/events/destination.ics",
            new byte[32],
            new string('p', CalendarMutationRequestStateProtector.MaximumRequestStateCharacters));
        var protector = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(MoveStateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());

        Should.Throw<InvalidOperationException>(() => protector.ProtectExactMove(
            "calendar_resources.exact_move",
            [1, 2, 3, 4],
            binding));
    }

    [Fact]
    public void ExactMoveState_TreatsReorderedDuplicateScopeAsEquivalent()
    {
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var originalOptions = MoveStateOptions();
        originalOptions.CalendarHrefs = "https://cal.example/events/,https://cal.example/tasks/";
        var equivalentOptions = MoveStateOptions();
        equivalentOptions.CalendarHrefs =
            "https://cal.example/tasks/, https://cal.example/events/, https://cal.example/tasks/";
        var original = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(originalOptions),
            key);
        var equivalent = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(equivalentOptions),
            key);
        var revision = ExactRevision();
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var binding = SuccessfulMoveReview(revision).Binding!;
        byte[] requestBinding = [1, 2, 3, 4];
        var state = original.ProtectExactMove("calendar_resources.exact_move", requestBinding, binding);

        equivalent.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            requestBinding,
            out var restored,
            out var expired).ShouldBeTrue();
        expired.ShouldBeFalse();
        restored.Revision.ShouldBe(binding.Revision);
        restored.DestinationHref.ShouldBe(binding.DestinationHref);
        restored.PolicyVersion.ShouldBe(binding.PolicyVersion);
        restored.SourceIntentDigest.Span.SequenceEqual(binding.SourceIntentDigest.Span).ShouldBeTrue();
    }

    [Fact]
    public void ExactMoveState_DistinguishesMeaningfulEndpointPathChange()
    {
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var originalOptions = MoveStateOptions();
        originalOptions.BaseUrl = "https://cal.example/caldav//";
        var changedOptions = MoveStateOptions();
        changedOptions.BaseUrl = "https://cal.example/caldav/";
        var original = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(originalOptions),
            key);
        var changed = new CalendarMutationRequestStateProtector(
            TimeProvider.System,
            Options.Create(changedOptions),
            key);
        var revision = ExactRevision();
        var request = new CalendarExactMoveRequest(revision, "https://cal.example/events/destination.ics");
        var binding = SuccessfulMoveReview(revision).Binding!;
        byte[] requestBinding = [1, 2, 3, 4];
        var state = original.ProtectExactMove("calendar_resources.exact_move", requestBinding, binding);

        changed.TryUnprotectExactMove(
            state,
            "calendar_resources.exact_move",
            request,
            requestBinding,
            out _,
            out var expired).ShouldBeFalse();
        expired.ShouldBeFalse();
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
            .Returns(new CalendarExactMoveReviewResult(failure.Outcome, null));
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
        await service.DidNotReceive().ExecuteConfirmedExactMoveResourceAsync(
            Arg.Any<CalendarExactMoveRequest>(),
            Arg.Any<CalendarExactMoveReviewBinding>(),
            Arg.Any<CancellationToken>());
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
            .Returns(new CalendarExactCreateReviewResult(new CalendarExactResourceResult(
                code,
                CalendarMutationState.NotAttempted,
                Phase: CalendarExactResourcePhase.CompleteResourceSemantics), null, null));

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
    public async Task ExactCreateRawAsync_MapsTypedElapsedTimeLimitEvidence()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactCreateReviewResult(
                new CalendarExactResourceResult(
                    CalendarExactResourceCode.LimitExhausted,
                    CalendarMutationState.NotAttempted,
                    Limits: new CalendarEntityCreateExecutionLimits(
                        Dimension: CalendarEntityCreateLimitDimension.ElapsedTime)),
                null,
                null));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments("https://cal.example/events/deadline.ics", ExactEvent("deadline")),
            null,
            null,
            false,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("limits").GetProperty("dimension")
            .GetString().ShouldBe("elapsed_time");
    }

    [Fact]
    public async Task ExactWriteError_RedactsAuthoritativeAndRawSnapshotContent()
    {
        const string destinationHref = "https://cal.example/events/redacted.ics";
        var snapshot = CreateSnapshot("\"secret-etag\"");
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactCreateReviewResult(
                new CalendarExactResourceResult(
                    CalendarExactResourceCode.Conflict,
                    CalendarMutationState.NotCommitted,
                    snapshot,
                    Phase: CalendarExactResourcePhase.TargetRevision),
                null,
                null));

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
    [InlineData(CalendarExactResourcePhase.Mrtr, "mrtr")]
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
            .Returns(new CalendarExactCreateReviewResult(new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.Unknown,
                Phase: phase), null, null));

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
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "confirm"));
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
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
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
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "envelope"));
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
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_ChangedArgumentsInvalidateProtectedStateBeforeReview()
    {
        const string destinationHref = "https://cal.example/events/bound.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "bound"));
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
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessfulCreateReview(destinationHref, "intent"),
                SuccessfulCreateReview(destinationHref, "intent", Enumerable.Repeat((byte)1, 32).ToArray()));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("intent"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments, first.Result.RequestState, AcceptedConfirmation(), true, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_CredentialChangeInvalidatesProtectedStateBeforeFreshReview()
    {
        const string destinationHref = "https://cal.example/events/credential-bound.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "credential-bound"));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var keyMaterial = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var original = CreateWriteTools(service, time, keyMaterial, "secret");
        var changedCredentials = CreateWriteTools(service, time, keyMaterial, "different-secret");
        var arguments = CreateArguments(destinationHref, ExactEvent("credential-bound"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => original.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await changedCredentials.CreateRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.Received(1).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_FreshReviewFindsOccupiedDestinationWithoutPut()
    {
        const string destinationHref = "https://cal.example/events/fresh-conflict.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessfulCreateReview(destinationHref, "fresh-conflict"),
                new CalendarExactCreateReviewResult(
                    new CalendarExactResourceResult(
                        CalendarExactResourceCode.DestinationConflict,
                        CalendarMutationState.NotAttempted,
                        Phase: CalendarExactResourcePhase.TargetRevision),
                    null,
                    null));
        var sut = CreateWriteTools(service);
        var arguments = CreateArguments(destinationHref, ExactEvent("fresh-conflict"));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.CreateRawAsync(
            arguments, null, null, true, CancellationToken.None));

        var result = await sut.CreateRawAsync(
            arguments,
            first.Result.RequestState,
            AcceptedConfirmation(),
            true,
            CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("destination_conflict");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.Received(2).ReviewExactCreateResourceAsync(
            Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateRawAsync_ValidReviewWithoutMrtrIsUnsupportedAndDoesNotWrite()
    {
        const string destinationHref = "https://cal.example/events/no-mrtr.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "no-mrtr"));

        var result = await CreateWriteTools(service).CreateRawAsync(
            CreateArguments(destinationHref, ExactEvent("no-mrtr")), null, null, false, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await service.DidNotReceive().ExactCreateResourceAsync(
            Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>());
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
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulCreateReview(destinationHref, "escaped"));

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
            .Returns(new CalendarExactCreateReviewResult(
                new CalendarExactResourceResult(
                    CalendarExactResourceCode.PayloadTooLarge,
                    CalendarMutationState.NotAttempted,
                    Phase: CalendarExactResourcePhase.SchemaLexicalDiscriminator),
                null,
                null));

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
            .Returns(new CalendarExactCreateReviewResult(failure.Outcome, null, null));
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(failure);
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarExactMoveReviewResult(failure.Outcome, null));
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
            .Returns(SuccessfulCreateReview(
                destinationHref,
                reviewedRevision.EntityUid));
        service.ReviewExactReplaceResourceAsync(
                Arg.Any<CalendarExactReplaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(review);
        service.ReviewExactMoveResourceAsync(
                Arg.Any<CalendarExactMoveRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessfulMoveReview(reviewedRevision, destinationHref));
        var sut = CreateWriteTools(service);

        Task<CallToolResult> Invoke() => operation switch
        {
            "create" => sut.CreateRawAsync(
                CreateArguments(destinationHref, ExactEvent("preview-budget")),
                null, null, true, TestContext.Current.CancellationToken),
            "replace" => sut.ReplaceRawAsync(
                ReplaceArguments(reviewedRevision, ExactEvent(reviewedRevision.EntityUid)),
                null, null, true, TestContext.Current.CancellationToken),
            _ => sut.MoveRawAsync(
                MoveArguments(reviewedRevision, destinationHref),
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
    [InlineData("missing-revision")]
    [InlineData("short-digest")]
    public async Task ExactCreateRawAsync_RejectsMalformedReviewEvidence(string scenario)
    {
        const string destinationHref = "https://cal.example/events/evidence.ics";
        var service = Substitute.For<ICalendarService>();
        service.ReviewExactCreateResourceAsync(
                Arg.Any<CalendarExactCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(scenario == "missing-revision"
                ? new CalendarExactCreateReviewResult(null, null, null)
                : MalformedCreateReview(destinationHref, "evidence", new byte[31]));

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
            .Returns(SuccessfulCreateReview(destinationHref, "expired"));
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
            .Returns<Task<CalendarExactCreateReviewResult>>(_ => throw new InvalidOperationException("secret"));

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
            .Returns(new CalendarExactCreateReviewResult(new CalendarExactResourceResult(
                code,
                code == CalendarExactResourceCode.Conflict
                    ? CalendarMutationState.NotCommitted
                    : code == CalendarExactResourceCode.Success
                        ? CalendarMutationState.Committed
                        : CalendarMutationState.NotAttempted,
                code == CalendarExactResourceCode.NoChange ? null : snapshot), null, null));

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
            .Returns(SuccessfulMoveReview(revision, destinationHref));

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
    public void CalendarToolResult_EnforcesExactStructuredByteBoundary(int extraByte, bool rejected)
    {
        var result = ResultWithStructuredSize(
            ExactCalendarResourceWriteTools.MaximumStructuredResultBytes + extraByte);
        var payloadFacts = CalendarTelemetryFacts.FromInputGuard(payloadTooLarge: true);

        var bounded = CalendarToolResult.Success(result, CalendarMutationState.Committed).FinalizeBounded(
            (_, _) => CalendarToolResult.Error(
                new CallToolResult
                {
                    IsError = true,
                    StructuredContent = JsonSerializer.SerializeToElement(new
                    {
                        code = payloadFacts.CodeName,
                        category = payloadFacts.CategoryName,
                        message = "The exact write result exceeds the safe payload limit.",
                        retryable = payloadFacts.Retryable,
                        phase = payloadFacts.PhaseName,
                        mutationState = "committed"
                    }),
                    Content = [new TextContentBlock { Text = "Exact Calendar Object Resource write failed." }]
                },
                payloadFacts,
                CalendarMutationState.Committed));

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
            .Returns(SuccessfulCreateReview(destinationHref, "continuation"));
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
            .Returns(SuccessfulCreateReview(destinationHref, "indeterminate"));
        service.ExactCreateResourceAsync(
                Arg.Any<CalendarReviewedExactCreate>(), Arg.Any<CancellationToken>())
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

    private static CalendarResourceSnapshot CreateProjectedSnapshot(
        CalendarResourceProjectionKind kind,
        string uid) => CreateSnapshot("\"projected\"") with
        {
            Projection = new CalendarResourceProjection(kind, uid, null)
        };

    private static CalendarExactCreateReviewResult SuccessfulCreateReview(
        string destinationHref,
        string uid,
        byte[]? intentDigest = null) => CreateReview(
            destinationHref,
            uid,
        intentDigest ?? new byte[32]);

    private static CalendarExactMoveReviewResult SuccessfulMoveReview(
        CalendarResourceRevisionReference revision,
        string destinationHref = "https://cal.example/events/destination.ics") => new(
            null,
            new CalendarExactMoveReviewBinding(
                revision,
                destinationHref,
                new byte[32],
                "server-authoritative-exact-move/1"));

    private static CalendarExactMoveReviewResult InvalidMoveReview(
        string scenario,
        CalendarResourceRevisionReference revision)
    {
        var binding = SuccessfulMoveReview(revision).Binding!;
        return scenario switch
        {
            "missing-binding" => new CalendarExactMoveReviewResult(null, null),
            "revision" => new CalendarExactMoveReviewResult(
                null,
                binding with { Revision = revision with { EntityTag = "\"r2\"" } }),
            "destination" => new CalendarExactMoveReviewResult(
                null,
                binding with { DestinationHref = "https://cal.example/events/other.ics" }),
            "digest" => new CalendarExactMoveReviewResult(
                null,
                binding with { SourceIntentDigest = new byte[31] }),
            _ => new CalendarExactMoveReviewResult(null, binding with { PolicyVersion = " " })
        };
    }

    private static CalendarExactCreateReviewResult MalformedCreateReview(
        string destinationHref,
        string uid,
        byte[] intentDigest) => CreateReview(destinationHref, uid, intentDigest);

    private static CalendarExactCreateReviewResult CreateReview(
        string destinationHref,
        string uid,
        byte[] intentDigest)
    {
        var binding = new CalendarExactCreateReviewBinding(
            destinationHref,
            uid,
            CalendarEntityKind.Event,
            intentDigest,
            "1");
        return new CalendarExactCreateReviewResult(
            null,
            binding,
            CreateReviewedExactCreate(binding, ReadOnlyMemory<byte>.Empty));
    }

    private static CalendarReviewedExactCreate CreateReviewedExactCreate(
        CalendarExactCreateReviewBinding binding,
        ReadOnlyMemory<byte> authoritativeUtf8)
    {
        var separator = binding.DestinationHref.LastIndexOf('/');
        return (CalendarReviewedExactCreate)Activator.CreateInstance(
            typeof(CalendarReviewedExactCreate),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [binding.DestinationHref[..(separator + 1)], binding, authoritativeUtf8],
            culture: null)!;
    }

    private static ExactCalendarResourceWriteTools CreateWriteTools(
        ICalendarService service,
        TimeProvider? suppliedTime = null)
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
        return new ExactCalendarResourceWriteTools(service, protector, timeProvider);
    }

    private static ExactCalendarResourceWriteTools CreateWriteTools(
        ICalendarService service,
        TimeProvider timeProvider,
        byte[] keyMaterial,
        string password)
    {
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "user",
                Password = password
            }),
            keyMaterial);
        return new ExactCalendarResourceWriteTools(service, protector, timeProvider);
    }

    private static ExactCalendarResourceWriteTools CreateWriteTools(
        ICalendarService service,
        TimeProvider timeProvider,
        byte[] keyMaterial,
        CalDavOptions options)
    {
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(options),
            keyMaterial);
        return new ExactCalendarResourceWriteTools(service, protector, timeProvider);
    }

    private static CalDavOptions MoveStateOptions() => new()
    {
        BaseUrl = "https://cal.example",
        Username = "user",
        Password = "secret",
        CalendarHrefs = "https://cal.example/events/",
        InteroperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8,
        RequestTimeout = TimeSpan.FromSeconds(30)
    };

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
            entityKind = revision.EntityKind == CalendarEntityKind.Event ? "event" : "todo",
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
        var kind = revision.EntityKind == CalendarEntityKind.Event ? "event" : "todo";
        var message = operation == "create"
            ? $"Confirm calendar_resources.exact_create for destination {destinationHref}, UID {revision.EntityUid}, and kind {kind}."
            : $"Confirm calendar_resources.exact_{operation} for href {revision.Href}"
                + (destinationHref is null ? string.Empty : $", destination {destinationHref}")
                + $", UID {revision.EntityUid}, kind {kind}, and expected ETag {revision.EntityTag}.";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            Message = message,
            Title = "Confirm exact write",
            Description = "Apply exactly the reviewed complete Calendar Object Resource write."
        }).Length;
    }

    private static async Task<CalendarExactResourceResult> WaitForCancellationAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource executionStarted)
    {
        var stalled = new TaskCompletionSource<CalendarExactResourceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => stalled.TrySetCanceled(cancellationToken));
        executionStarted.TrySetResult();
        return await stalled.Task;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<MutableTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new MutableTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class MutableTimer(
            MutableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
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

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
