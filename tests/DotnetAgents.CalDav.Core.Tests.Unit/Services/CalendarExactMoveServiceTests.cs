using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarExactMoveServiceTests
{
    [Fact]
    public void AuthorizationFailuresMapEveryNeutralReasonIntoTheExactContract()
    {
        (CalendarMoveAuthorizationFailureReason Reason, CalendarExactResourceCode Code, CalendarExactResourcePhase Phase)[]
            cases =
            [
                (CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref, CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator),
                (CalendarMoveAuthorizationFailureReason.SameResourceHref, CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator),
                (CalendarMoveAuthorizationFailureReason.OriginMismatch, CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.OutsideCalendarScope, CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar, CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator),
                (CalendarMoveAuthorizationFailureReason.DestinationSelectionNotFound, CalendarExactResourceCode.UpstreamProtocolError, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous, CalendarExactResourceCode.UpstreamProtocolError, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.InteroperabilityProfileUnverified, CalendarExactResourceCode.UnsupportedCapability, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing, CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous, CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.DestinationOwnershipMissing, CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.DestinationOwnershipAmbiguous, CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization),
                (CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised, CalendarExactResourceCode.UnsupportedCapability, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.InvalidResolvedCalendar, CalendarExactResourceCode.UpstreamProtocolError, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent, CalendarExactResourceCode.UpstreamProtocolError, CalendarExactResourcePhase.SelectionDiscoveryCapability),
                (CalendarMoveAuthorizationFailureReason.SameCalendarNotAllowed, CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SelectionDiscoveryCapability)
            ];

        foreach (var entry in cases)
        {
            var result = CalendarExactMoveModule.MapAuthorizationFailure(
                new CalendarMoveAuthorizationFailure(entry.Reason, []));

            result.Code.ShouldBe(entry.Code);
            result.Phase.ShouldBe(entry.Phase);
            result.Retryable.ShouldBeFalse();
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }
    }

    [Fact]
    public async Task ReviewExactMoveResourceAsync_UsesOneFreshConstantWorkReviewWithoutDispatch()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = CreateMoveClient();
        var presence = (ICalendarMoveResourceTransport)client;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        presence.ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        var service = CreateService(client, calendarHref);

        var review = await service.ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(Revision(sourceHref), destinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldBeNull();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>());
        await presence.Received(1).ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_PerformsOneFreshReviewThenConsumesOnePlan()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var sourceBytes = EventResource("exact-move");
        var client = CreateMoveClient();
        var presence = (ICalendarMoveResourceTransport)client;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(destinationHref, "\"r2\"", sourceBytes));
        presence.ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var initial = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            initial.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.Received(3).GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
        await presence.Received(2).ProbeMoveResourcePresenceAsync(
            calendarHref,
            destinationHref,
            Arg.Any<CancellationToken>());
        await client.Received(1).MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(sourceHref, destinationHref, "\"r1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_PreservesAuthorizedCalendarsAcrossWireOperations()
    {
        const string sourceCalendarHref = "https://cal.example/events/";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string sourceHref = sourceCalendarHref + "source.ics";
        const string destinationHref = destinationCalendarHref + "destination.ics";
        var bytes = EventResource("exact-move");
        var client = CreateMoveClient();
        var moveTransport = (ICalendarMoveResourceTransport)client;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EventCalendar(sourceCalendarHref), EventCalendar(destinationCalendarHref)]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(destinationHref, "\"r2\"", bytes));
        moveTransport.ProbeMoveResourcePresenceAsync(
                destinationCalendarHref,
                destinationHref,
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(
                Arg.Any<CalendarResourceMoveDispatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var service = CreateService(
            client,
            $"{sourceCalendarHref},{destinationCalendarHref}");
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(
            request,
            TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        await moveTransport.Received(2).ProbeMoveResourcePresenceAsync(
            destinationCalendarHref,
            destinationHref,
            Arg.Any<CancellationToken>());
        await moveTransport.Received(1).DispatchMoveAsync(
            sourceCalendarHref,
            destinationCalendarHref,
            new CalendarResourceMoveDispatchRequest(sourceHref, destinationHref, "\"r1\""),
            Arg.Any<CancellationToken>());
        await moveTransport.Received(3).ReadMoveResourceAsync(
            sourceCalendarHref,
            sourceHref,
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await moveTransport.Received(1).ReadMoveResourceAsync(
            destinationCalendarHref,
            destinationHref,
            absenceProbe: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewedExactMovePlan_SecondConsumePerformsZeroWireWork()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var transport = Substitute.For<ICalendarMoveTransport>();
        transport.DispatchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CalendarResourceMoveDispatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.DestinationConflict));
        var plan = new CalendarReviewedMovePlan(new CalendarReviewedMovePreparation(
            Revision(sourceHref),
            Snapshot(calendarHref, sourceHref, "\"r1\"", EventResource("exact-move")),
            calendarHref,
            calendarHref,
            destinationHref,
            CalendarMoveFidelityMode.Exact));
        var dispatcher = new CalendarMoveDispatcher(transport, TimeProvider.System);

        var first = await dispatcher.DispatchAsync(plan, TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync(plan, TestContext.Current.CancellationToken);

        first.Code.ShouldBe(CalendarResourceMoveCode.DestinationConflict);
        second.Code.ShouldBe(CalendarResourceMoveCode.InvalidInput);
        second.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await transport.Received(1).DispatchAsync(
            calendarHref,
            calendarHref,
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
        await transport.DidNotReceive().ObserveResourceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewedExactMovePlan_PreDispatchCancellationPerformsZeroWireWork()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var transport = Substitute.For<ICalendarMoveTransport>();
        var plan = new CalendarReviewedMovePlan(new CalendarReviewedMovePreparation(
            Revision(sourceHref),
            Snapshot(calendarHref, sourceHref, "\"r1\"", EventResource("exact-move")),
            calendarHref,
            calendarHref,
            destinationHref,
            CalendarMoveFidelityMode.Exact));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new CalendarMoveDispatcher(transport, TimeProvider.System)
                .DispatchAsync(plan, cancellation.Token));

        await transport.DidNotReceive().DispatchAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
        await transport.DidNotReceive().ObserveResourceAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_FreshRevisionChangePrecedesBindingComparison()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")),
            CalendarResourceRead.Success(sourceHref, "\"r2\"", EventResource("exact-move")));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var initial = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            initial.Binding! with { SourceIntentDigest = new byte[32] },
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_FreshDestinationOccupancyPrecedesBindingComparison()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        var presence = (ICalendarMoveResourceTransport)client;
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        presence.ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            CalendarResourceRead.Success(destinationHref, "\"occupied\"", Array.Empty<byte>()));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var initial = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            initial.Binding! with { PolicyVersion = "obsolete" },
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.DestinationConflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing", CalendarExactResourceCode.NotFound)]
    [InlineData("uid", CalendarExactResourceCode.Conflict)]
    [InlineData("kind", CalendarExactResourceCode.EntityKindMismatch)]
    [InlineData("capability", CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData("profile", CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData("auth", CalendarExactResourceCode.UpstreamUnauthorized)]
    public async Task ExecuteConfirmedExactMoveResourceAsync_FreshOrdinaryFailurePrecedesBindingComparison(
        string scenario,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")),
            scenario switch
            {
                "missing" => new CalendarResourceRead(CalendarResourceReadCode.NotFound),
                "uid" => CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("changed")),
                "kind" => CalendarResourceRead.Success(sourceHref, "\"r1\"", TodoResource("exact-move")),
                _ => CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move"))
            });
        if (scenario == "capability")
        {
            client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
                [EventCalendar(calendarHref)],
                [EventCalendar(calendarHref) with { EventSupport = EntityKindSupport.NotAdvertised }]);
        }
        if (scenario == "auth")
        {
            client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
                _ => Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EventCalendar(calendarHref)]),
                _ => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
                    new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized)));
        }
        var initialService = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await initialService.ReviewExactMoveResourceAsync(
            request,
            TestContext.Current.CancellationToken);
        var confirmedService = scenario == "profile"
            ? CreateService(client, calendarHref, interoperabilityProfile: null)
            : initialService;

        var result = await confirmedService.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding! with { SourceIntentDigest = Enumerable.Repeat((byte)1, 32).ToArray() },
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("policy")]
    public async Task ExecuteConfirmedExactMoveResourceAsync_BindingMismatchNeverDispatches(string mismatch)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var initial = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);
        var prior = mismatch == "intent"
            ? initial.Binding! with { SourceIntentDigest = Enumerable.Repeat((byte)1, 32).ToArray() }
            : initial.Binding! with { PolicyVersion = "obsolete" };

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            prior,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.ConfirmationMismatch);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarExactResourcePhase.Mrtr);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("review", CalendarMoveCollisionClassification.Unspecified)]
    [InlineData("revision", CalendarMoveCollisionClassification.SourceRevision)]
    [InlineData("destination", CalendarMoveCollisionClassification.DestinationHref)]
    public async Task ExactMoveReview_RecordsTruthfulMoveTelemetry(
        string scenario,
        CalendarMoveCollisionClassification expectedCollision)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(
                sourceHref,
                scenario == "revision" ? "\"r2\"" : "\"r1\"",
                EventResource("exact-move")));
        if (scenario == "destination")
        {
            ((ICalendarMoveResourceTransport)client)
                .ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>())
                .Returns(CalendarResourceRead.Success(destinationHref, "\"occupied\"", Array.Empty<byte>()));
        }
        var state = CalendarOperationProgress.CreateState();
        CalendarExactMoveReviewResult result;
        using (CalendarOperationProgress.Attach(state))
        {
            result = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
                new CalendarExactMoveRequest(Revision(sourceHref), destinationHref),
                TestContext.Current.CancellationToken);
        }

        if (scenario == "review")
            result.Outcome.ShouldBeNull();
        state.MoveTelemetry.Dispatch.ShouldBe(CalendarMoveDispatchClassification.NotAttempted);
        state.MoveTelemetry.Collision.ShouldBe(
            scenario == "review" ? CalendarMoveCollisionClassification.None : expectedCollision);
        state.MoveTelemetry.Reconciliation.ShouldBe(
            CalendarMoveReconciliationClassification.NotRun);
    }

    [Theory]
    [InlineData("https://cal.example/events/?query=1")]
    [InlineData("https://cal.example/events/#fragment")]
    [InlineData("https://cal.example/events%2F/")]
    [InlineData("https://cal.example/events/../events/")]
    [InlineData("https://other.example/events/")]
    public async Task ReviewExactMoveResourceAsync_RejectsUnsafeDiscoveredCalendarWithoutConfiguredScope(
        string discoveredCalendarHref)
    {
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = CreateMoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(discoveredCalendarHref)]);

        var review = await CreateService(client, string.Empty).ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(Revision(sourceHref), destinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(CalendarExactResourceCode.OutsideScope);
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ((ICalendarMoveResourceTransport)client).DidNotReceive()
            .ProbeMoveResourcePresenceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_RenamesProjectionOpaqueResourceExactly()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/renamed.ics";
        var bytes = OpaqueEventResource("exact-move");
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(destinationHref, "\"r2\"", bytes));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var initial = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            initial.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.Snapshot.ShouldNotBeNull().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Snapshot.AuthoritativeUtf8.Span.SequenceEqual(bytes).ShouldBeTrue();
    }

    [Theory]
    [InlineData(CalendarResourceMoveDispatchCode.Dispatched, CalendarResourceReadCode.Success, "same", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Success, CalendarMutationState.Committed, false)]
    [InlineData(CalendarResourceMoveDispatchCode.Dispatched, CalendarResourceReadCode.Success, "different", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.FidelityFailure, CalendarMutationState.Committed, false)]
    [InlineData(CalendarResourceMoveDispatchCode.Dispatched, CalendarResourceReadCode.UpstreamProtocolError, "same", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.CommittedButUnverified, CalendarMutationState.Committed, false)]
    [InlineData(CalendarResourceMoveDispatchCode.Dispatched, CalendarResourceReadCode.Success, "same", CalendarResourceReadCode.Success, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown, false)]
    [InlineData(CalendarResourceMoveDispatchCode.PossiblyDispatched, CalendarResourceReadCode.Success, "same", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Success, CalendarMutationState.Committed, false)]
    [InlineData(CalendarResourceMoveDispatchCode.PossiblyDispatched, CalendarResourceReadCode.Success, "different", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown, false)]
    [InlineData(CalendarResourceMoveDispatchCode.PossiblyDispatched, CalendarResourceReadCode.NotFound, "same", CalendarResourceReadCode.Success, CalendarExactResourceCode.UpstreamUnavailable, CalendarMutationState.NotCommitted, true)]
    [InlineData(CalendarResourceMoveDispatchCode.PossiblyDispatched, CalendarResourceReadCode.NotFound, "same", CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown, false)]
    public async Task ExecuteConfirmedExactMoveResourceAsync_UsesSharedBilateralTruth(
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceReadCode destinationCode,
        string destinationContent,
        CalendarResourceReadCode sourceCode,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var sourceBytes = EventResource("exact-move");
        var destinationBytes = destinationContent == "same" ? sourceBytes : EventResourceWithSummary("exact-move", "drifted");
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            Observed(sourceHref, sourceCode, "\"r1\"", sourceBytes));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Observed(destinationHref, destinationCode, "\"r2\"", destinationBytes));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(dispatchCode));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        result.Retryable.ShouldBe(retryable);
        await client.Received(1).MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_DispatchedSourceObservationFaultIsCommittedButUnverified()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var bytes = EventResource("exact-move");
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes)),
            _ => Task.FromResult(CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes)),
            _ => Task.FromException<CalendarResourceRead>(new IOException("observation unavailable")));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(destinationHref, "\"r2\"", bytes));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_PossiblyDispatchedUsesStrongIdentityNotSourceBytes()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var sourceBytes = EventResourceWithSummary("exact-move", "before");
        var observedBytes = EventResourceWithSummary("exact-move", "same revision but byte drift");
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", observedBytes));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.UpstreamUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("uid")]
    [InlineData("kind")]
    public async Task ExecuteConfirmedExactMoveResourceAsync_PossiblyDispatchedRejectsChangedExactIdentity(
        string observedIdentity)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var sourceBytes = EventResource("exact-move");
        var observedBytes = observedIdentity switch
        {
            "uid" => EventResource("other"),
            "kind" => TodoResource("exact-move"),
            _ => new byte[] { 0x00 }
        };
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", sourceBytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", observedBytes));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Retryable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(CalendarResourceMoveDispatchCode.DestinationConflict, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceMoveDispatchCode.Conflict, CalendarExactResourceCode.Conflict)]
    [InlineData(CalendarResourceMoveDispatchCode.NotFound, CalendarExactResourceCode.NotFound)]
    [InlineData(CalendarResourceMoveDispatchCode.InvalidInput, CalendarExactResourceCode.InvalidInput)]
    [InlineData(CalendarResourceMoveDispatchCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceMoveDispatchCode.PayloadTooLarge, CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamUnauthorized, CalendarExactResourceCode.UpstreamUnauthorized)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamForbidden, CalendarExactResourceCode.UpstreamForbidden)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamRateLimited, CalendarExactResourceCode.UpstreamRateLimited)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamUnavailable, CalendarExactResourceCode.UpstreamUnavailable)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ExecuteConfirmedExactMoveResourceAsync_DefiniteRejectionDoesNotReconcile(
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(dispatchCode));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(2).GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success, CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Success, CalendarMutationState.Committed, false)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarResourceReadCode.Success, CalendarExactResourceCode.UpstreamUnavailable, CalendarMutationState.NotCommitted, true)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown, false)]
    public async Task ExecuteConfirmedExactMoveResourceAsync_NullDispatchUsesPossiblyDispatchedBilateralTruth(
        CalendarResourceReadCode destinationCode,
        CalendarResourceReadCode sourceAfterCode,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        var bytes = EventResource("exact-move");
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            Observed(sourceHref, sourceAfterCode, "\"r1\"", bytes));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Observed(destinationHref, destinationCode, "\"r2\"", bytes));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns((CalendarResourceMoveDispatchResult)null!);
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        result.Retryable.ShouldBe(retryable);
        await client.Received(1).GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_DispatchInvocationFaultReconcilesConservatively()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceMoveDispatchResult>>(_ => throw new IOException("dispatch boundary fault"));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.UpstreamUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBeTrue();
        await client.Received(1).GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unsupported", CalendarExactResourceCode.UnsupportedCapability, false)]
    [InlineData("limit", CalendarExactResourceCode.LimitExhausted, false)]
    [InlineData("protocol", CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("http", CalendarExactResourceCode.UpstreamUnauthorized, false)]
    [InlineData("cancelled", CalendarExactResourceCode.UpstreamUnavailable, true)]
    public async Task ExecuteConfirmedExactMoveResourceAsync_MapsFreshDiscoveryTypedFailures(
        string failure,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EventCalendar(calendarHref)]),
            _ => FreshDiscoveryFailure(failure));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Retryable.ShouldBe(retryable);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_RejectsInvalidRequestBeforeDiscovery()
    {
        const string sourceHref = "https://cal.example/events/source.ics";
        var request = new CalendarExactMoveRequest(Revision(sourceHref), "/events/destination.ics");
        var priorBinding = new CalendarExactMoveReviewBinding(
            request.Revision,
            request.DestinationHref,
            new byte[32],
            CalendarExactMoveModule.PolicyVersion);
        var client = CreateMoveClient();

        var result = await CreateService(client, "https://cal.example/events/")
            .ExecuteConfirmedExactMoveResourceAsync(
                request,
                priorBinding,
                TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_PropagatesCallerCancellationBeforeDispatch()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        using var caller = new CancellationTokenSource();
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(CalendarResourceRead.Success(
                sourceHref,
                "\"r1\"",
                EventResource("exact-move"))),
            call => Task.FromCanceled<CalendarResourceRead>(call.ArgAt<CancellationToken>(1)));
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);
        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            caller.Token));

        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_DeadlineCoversMoveDispatch()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var time = new ExactMoveManualTimeProvider(DateTimeOffset.Parse("2026-08-23T12:00:00Z"));
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(1);
                var stalled = new TaskCompletionSource<CalendarResourceMoveDispatchResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => stalled.TrySetCanceled(cancellationToken));
                dispatchStarted.TrySetResult();
                return await stalled.Task;
            });
        var service = CreateService(client, calendarHref, time);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);
        var pending = service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            TestContext.Current.CancellationToken);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.UpstreamUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBeTrue();
        await client.Received(1).GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteConfirmedExactMoveResourceAsync_CallerCancellationAfterPossibleDispatchCannotStopReconciliation()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        using var caller = new CancellationTokenSource();
        var bytes = EventResource("exact-move");
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            CalendarResourceRead.Success(sourceHref, "\"r1\"", bytes),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(destinationHref, "\"r2\"", bytes));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                caller.Cancel();
                return new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched);
            });
        var service = CreateService(client, calendarHref);
        var request = new CalendarExactMoveRequest(Revision(sourceHref), destinationHref);
        var review = await service.ReviewExactMoveResourceAsync(request, TestContext.Current.CancellationToken);

        var result = await service.ExecuteConfirmedExactMoveResourceAsync(
            request,
            review.Binding!,
            caller.Token);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Theory]
    [InlineData("source-relative", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-noncanonical", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-user", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-fragment", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-query", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-collection", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-dot", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-slash", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-backslash", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-relative", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-noncanonical", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-user", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-fragment", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-query", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-collection", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-dot", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-slash", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("destination-backslash", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("same", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("empty-uid", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("invalid-kind", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("invalid-etag", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("any-etag", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("etag-whitespace", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("lowercase-weak-etag", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator)]
    [InlineData("source-origin", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.OriginScopeAuthorization)]
    [InlineData("destination-origin", CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.OriginScopeAuthorization)]
    [InlineData("source-scope", CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization)]
    [InlineData("destination-scope", CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization)]
    public async Task ReviewExactMoveResourceAsync_RejectsEveryUnsafeInputBeforeDiscovery(
        string scenario,
        CalendarExactResourceCode expectedCode,
        CalendarExactResourcePhase expectedPhase)
    {
        const string calendarHref = "https://cal.example/events/";
        var invalid = InvalidMoveInputs[scenario];
        var revision = new CalendarResourceRevisionReference(
            invalid.SourceHref,
            invalid.EntityUid,
            invalid.EntityKind,
            invalid.EntityTag);
        var client = CreateMoveClient();

        var review = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(revision, invalid.DestinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.Phase.ShouldBe(expectedPhase);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("weak", CalendarExactResourceCode.ConcurrencyUnavailable)]
    [InlineData("missing", CalendarExactResourceCode.NotFound)]
    [InlineData("concurrency", CalendarExactResourceCode.ConcurrencyUnavailable)]
    [InlineData("payload", CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData("unsupported", CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData("invalid-data", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("kind", CalendarExactResourceCode.EntityKindMismatch)]
    [InlineData("uid", CalendarExactResourceCode.Conflict)]
    [InlineData("etag", CalendarExactResourceCode.Conflict)]
    public async Task ReviewExactMoveResourceAsync_MapsEverySourceRevisionOutcome(
        string scenario,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(SourceRead(scenario, sourceHref));
        var revision = scenario == "weak" ? Revision(sourceHref) with { EntityTag = "W/\"r1\"" } : Revision(sourceHref);

        var review = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(revision, destinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.Phase.ShouldBe(scenario == "unsupported"
            ? CalendarExactResourcePhase.SelectionDiscoveryCapability
            : CalendarExactResourcePhase.TargetRevision);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ReviewExactMoveResourceAsync_MapsEveryDestinationPresenceOutcome(
        CalendarResourceReadCode destinationCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));
        ((ICalendarMoveResourceTransport)client)
            .ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(destinationCode));

        var review = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(Revision(sourceHref), destinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.Phase.ShouldBe(CalendarExactResourcePhase.SelectionDiscoveryCapability);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CalendarExactResourceCode.UpstreamUnauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, CalendarExactResourceCode.UpstreamForbidden, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CalendarExactResourceCode.PayloadTooLarge, false)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarExactResourceCode.UpstreamRateLimited, true)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CalendarExactResourceCode.UnsupportedCapability, false)]
    [InlineData(HttpStatusCode.NotImplemented, CalendarExactResourceCode.UnsupportedCapability, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, CalendarExactResourceCode.UpstreamProtocolError, false)]
    public async Task ReviewExactMoveResourceAsync_MapsEveryDiscoveryHttpOutcome(
        HttpStatusCode statusCode,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        var client = CreateMoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<CalendarDescriptor>>>(
            _ => throw new HttpRequestException("failure", null, statusCode));

        var review = await CreateService(client, "https://cal.example/events/").ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(
                Revision("https://cal.example/events/source.ics"),
                "https://cal.example/events/destination.ics"),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.Retryable.ShouldBe(retryable);
        review.Outcome.Phase.ShouldBe(CalendarExactResourcePhase.SelectionDiscoveryCapability);
    }

    [Theory]
    [InlineData("unsupported", CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData("limit", CalendarExactResourceCode.LimitExhausted)]
    public async Task ReviewExactMoveResourceAsync_MapsTypedDiscoveryFailures(
        string failure,
        CalendarExactResourceCode expectedCode)
    {
        var client = CreateMoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ => FreshDiscoveryFailure(failure));

        var review = await CreateService(client, "https://cal.example/events/").ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(
                Revision("https://cal.example/events/source.ics"),
                "https://cal.example/events/destination.ics"),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
    }

    [Fact]
    public async Task ReviewExactMoveResourceAsync_AllowsTodoWithAdvertisedCapability()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string sourceHref = "https://cal.example/todos/source.ics";
        const string destinationHref = "https://cal.example/todos/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([new CalendarDescriptor
        {
            Href = calendarHref,
            DisplayName = "Todos",
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.NotAdvertised,
            TodoSupport = EntityKindSupport.Advertised
        }]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", TodoResource("exact-move")));
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                sourceHref,
                "exact-move",
                CalendarEntityKind.Todo,
                "\"r1\""),
            destinationHref);

        var review = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
            request,
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldBeNull();
        review.Binding.ShouldNotBeNull().Revision.EntityKind.ShouldBe(CalendarEntityKind.Todo);
    }

    [Fact]
    public async Task ReviewExactMoveResourceAsync_RejectsTodoWithoutAdvertisedCapability()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string sourceHref = "https://cal.example/todos/source.ics";
        const string destinationHref = "https://cal.example/todos/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([new CalendarDescriptor
        {
            Href = calendarHref,
            DisplayName = "Todos",
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.NotAdvertised,
            TodoSupport = EntityKindSupport.NotAdvertised
        }]);
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                sourceHref,
                "exact-move",
                CalendarEntityKind.Todo,
                "\"r1\""),
            destinationHref);

        var review = await CreateService(client, calendarHref).ReviewExactMoveResourceAsync(
            request,
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(CalendarExactResourceCode.UnsupportedCapability);
        review.Outcome.Phase.ShouldBe(CalendarExactResourcePhase.SelectionDiscoveryCapability);
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewExactMoveResourceAsync_AllowsSafeDiscoveredCalendarsWithoutConfiguredScope()
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        const string destinationHref = "https://cal.example/events/destination.ics";
        var client = PreparedClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move")));

        var review = await CreateService(client, calendarHref: null).ReviewExactMoveResourceAsync(
            new CalendarExactMoveRequest(Revision(sourceHref), destinationHref),
            TestContext.Current.CancellationToken);

        review.Outcome.ShouldBeNull();
        review.Binding.ShouldNotBeNull();
    }

    private static IReadOnlyDictionary<string, InvalidMoveInput> InvalidMoveInputs { get; } =
        new Dictionary<string, InvalidMoveInput>(StringComparer.Ordinal)
        {
            ["source-relative"] = InvalidMove(sourceHref: "/events/source.ics"),
            ["source-noncanonical"] = InvalidMove(sourceHref: "https://cal.example/events/../source.ics"),
            ["source-user"] = InvalidMove(sourceHref: "https://user@cal.example/events/source.ics"),
            ["source-fragment"] = InvalidMove(sourceHref: "https://cal.example/events/source.ics#private"),
            ["source-query"] = InvalidMove(sourceHref: "https://cal.example/events/source.ics?private=1"),
            ["source-collection"] = InvalidMove(sourceHref: "https://cal.example/events/source.ics/"),
            ["source-dot"] = InvalidMove(sourceHref: "https://cal.example/events/%2e/source.ics"),
            ["source-slash"] = InvalidMove(sourceHref: "https://cal.example/events%2Fsource.ics"),
            ["source-backslash"] = InvalidMove(sourceHref: "https://cal.example/events%5Csource.ics"),
            ["destination-relative"] = InvalidMove(destinationHref: "/events/destination.ics"),
            ["destination-noncanonical"] = InvalidMove(destinationHref: "https://cal.example/events/../destination.ics"),
            ["destination-user"] = InvalidMove(destinationHref: "https://user@cal.example/events/destination.ics"),
            ["destination-fragment"] = InvalidMove(destinationHref: "https://cal.example/events/destination.ics#private"),
            ["destination-query"] = InvalidMove(destinationHref: "https://cal.example/events/destination.ics?private=1"),
            ["destination-collection"] = InvalidMove(destinationHref: "https://cal.example/events/destination.ics/"),
            ["destination-dot"] = InvalidMove(destinationHref: "https://cal.example/events/%2e/destination.ics"),
            ["destination-slash"] = InvalidMove(destinationHref: "https://cal.example/events%2Fdestination.ics"),
            ["destination-backslash"] = InvalidMove(destinationHref: "https://cal.example/events%5Cdestination.ics"),
            ["same"] = InvalidMove(destinationHref: "https://cal.example/events/source.ics"),
            ["empty-uid"] = InvalidMove(entityUid: ""),
            ["invalid-kind"] = InvalidMove(entityKind: (CalendarEntityKind)999),
            ["invalid-etag"] = InvalidMove(entityTag: "invalid"),
            ["any-etag"] = InvalidMove(entityTag: "*"),
            ["etag-whitespace"] = InvalidMove(entityTag: " \"r1\" "),
            ["lowercase-weak-etag"] = InvalidMove(entityTag: "w/\"r1\""),
            ["source-origin"] = InvalidMove(sourceHref: "https://other.example/events/source.ics"),
            ["destination-origin"] = InvalidMove(destinationHref: "https://other.example/events/destination.ics"),
            ["source-scope"] = InvalidMove(sourceHref: "https://cal.example/outside/source.ics"),
            ["destination-scope"] = InvalidMove(destinationHref: "https://cal.example/outside/destination.ics")
        };

    private static InvalidMoveInput InvalidMove(
        string sourceHref = "https://cal.example/events/source.ics",
        string destinationHref = "https://cal.example/events/destination.ics",
        string entityUid = "exact-move",
        CalendarEntityKind entityKind = CalendarEntityKind.Event,
        string entityTag = "\"r1\"") => new(sourceHref, destinationHref, entityUid, entityKind, entityTag);

    private static CalendarResourceRead SourceRead(string scenario, string sourceHref) => scenario switch
    {
        "missing" => new CalendarResourceRead(CalendarResourceReadCode.NotFound),
        "concurrency" => new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable),
        "payload" => new CalendarResourceRead(CalendarResourceReadCode.PayloadTooLarge),
        "unsupported" => new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability),
        "invalid-data" => CalendarResourceRead.Success(sourceHref, "\"r1\"", new byte[] { 0x00 }),
        "kind" => CalendarResourceRead.Success(sourceHref, "\"r1\"", TodoResource("exact-move")),
        "uid" => CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("other")),
        "etag" => CalendarResourceRead.Success(sourceHref, "\"r2\"", EventResource("exact-move")),
        _ => CalendarResourceRead.Success(sourceHref, "\"r1\"", EventResource("exact-move"))
    };

    private static Task<IReadOnlyList<CalendarDescriptor>> FreshDiscoveryFailure(string failure) => failure switch
    {
        "limit" => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
            new CalendarDiscoveryLimitException(601)),
        "unsupported" => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
            new CalendarDiscoveryUnsupportedCapabilityException("unsupported")),
        "protocol" => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
            new CalendarDiscoveryProtocolException("protocol")),
        "http" => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
            new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)),
        _ => Task.FromException<IReadOnlyList<CalendarDescriptor>>(new OperationCanceledException())
    };

    private sealed record InvalidMoveInput(
        string SourceHref,
        string DestinationHref,
        string EntityUid,
        CalendarEntityKind EntityKind,
        string EntityTag);

    private static CalendarService CreateService(
        ICalendarClient client,
        string? calendarHref,
        TimeProvider? timeProvider = null,
        string? interoperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = calendarHref,
            InteroperabilityProfile = interoperabilityProfile
        }),
        Substitute.For<ILogger<CalendarService>>(),
        timeProvider ?? TimeProvider.System,
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private static CalendarDescriptor EventCalendar(string href) => new()
    {
        Href = href,
        DisplayName = "Events",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static CalendarResourceRevisionReference Revision(string href) => new(
        href,
        "exact-move",
        CalendarEntityKind.Event,
        "\"r1\"");

    private static byte[] EventResource(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] OpaqueEventResource(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nCALSCALE:X-CUSTOM\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] TodoResource(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "END:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] EventResourceWithSummary(string uid, string summary) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\nSUMMARY:{summary}\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static CalendarResourceRead Observed(
        string href,
        CalendarResourceReadCode code,
        string entityTag,
        byte[] bytes) => new(code)
        {
            ResourceHref = href,
            EntityTag = entityTag,
            AuthoritativeUtf8 = bytes
        };

    private static ICalendarClient CreateMoveClient()
    {
        var client = Substitute.For<ICalendarClient, ICalendarMoveResourceTransport>();
        var moveTransport = (ICalendarMoveResourceTransport)client;
        moveTransport.ReadMoveResourceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call => client.GetCalendarResourceAsync(
                call.ArgAt<string>(1),
                call.ArgAt<CancellationToken>(3)));
        moveTransport.DispatchMoveAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CalendarResourceMoveDispatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call => client.MoveCalendarResourceAsync(
                call.ArgAt<CalendarResourceMoveDispatchRequest>(2),
                call.ArgAt<CancellationToken>(3)));
        return client;
    }

    private static ICalendarClient PreparedClient(string calendarHref, string destinationHref)
    {
        var client = CreateMoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        ((ICalendarMoveResourceTransport)client)
            .ProbeMoveResourcePresenceAsync(calendarHref, destinationHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        return client;
    }

    private static CalendarResourceSnapshot Snapshot(
        string calendarHref,
        string href,
        string entityTag,
        byte[] bytes) => CalendarResourceProjector.AttachSnapshot(
            calendarHref,
            CalendarResourceRead.Success(href, entityTag, bytes)).Snapshot!;

    private sealed class ExactMoveManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => _timestamp;

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
            _timestamp += amount.Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ExactMoveManualTimeProvider owner,
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
}
