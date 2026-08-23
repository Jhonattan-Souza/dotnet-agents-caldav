using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarResourceMoveServiceTests
{
    [Fact]
    public async Task MoveResourceAsync_PreservesHeadersOnlyProbeThroughOperationDiscovery()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = MoveClient();
        var moveTransport = (ICalendarMoveResourceTransport)client;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar(destinationCalendarHref, "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", uid));
        moveTransport.ProbeMoveResourcePresenceAsync(
                destinationCalendarHref,
                destinationHref,
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        await moveTransport.Received(1).ProbeMoveResourcePresenceAsync(
            destinationCalendarHref,
            destinationHref,
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_FailsClosedWhenHeadersOnlyPresenceCapabilityIsUnavailable()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive/", "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", "reviewed-move"));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Is<string>(href => href.StartsWith("https://cal.example/archive/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_UsesAtomicMoveAndVerifiesDestinationAndSource()
    {
        const string sourceCalendarHref = "https://cal.example/tasks/";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string entityUid = "reviewed-move";
        const string entityTag = "\"r1\"";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, entityUid);
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar(sourceCalendarHref, "Tasks"),
            TodoCalendar(destinationCalendarHref, "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, entityTag, entityUid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", entityUid));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            new CalendarResourceMoveRequest(
                new CalendarResourceRevisionReference(
                    sourceHref,
                    entityUid,
                    CalendarEntityKind.Todo,
                    entityTag),
                CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldNotBeNull();
        result.Snapshot.ResourceHref.ShouldBe(destinationHref);
        result.Snapshot.EntityTag.ShouldBe("\"r2\"");
        await client.Received(1).MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(sourceHref, destinationHref, entityTag),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_MovesEventToCompatibleSelectedCalendar()
    {
        const string sourceHref = "https://cal.example/events/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "event-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EventCalendar("https://cal.example/events/", "Events"),
            EventCalendar(destinationCalendarHref, "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            EventResource(sourceHref, "\"r1\"", uid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            EventResource(destinationHref, "\"r2\"", uid));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(
            client,
            "https://cal.example/events/,https://cal.example/archive/");

        var result = await sut.MoveResourceAsync(
            new CalendarResourceMoveRequest(
                new CalendarResourceRevisionReference(sourceHref, uid, CalendarEntityKind.Event, "\"r1\""),
                CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.Snapshot.ShouldNotBeNull().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        await client.Received(1).MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(sourceHref, destinationHref, "\"r1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_DefaultDestinationUsesKindSpecificConfiguredCalendar()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = ConfiguredClient(sourceHref, "reviewed-move");
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            "https://cal.example/archive/",
            "reviewed-move");
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", "reviewed-move"));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", "reviewed-move"),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client, defaultTodoCalendarName: "Archive");

        var result = await sut.MoveResourceAsync(
            Request(sourceHref) with { Destination = CalendarMoveDestination.Default },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.Snapshot.ShouldNotBeNull().CalendarHref.ShouldBe("https://cal.example/archive/");
    }

    [Fact]
    public async Task MoveResourceAsync_RejectsSameCalendarWithoutRenameOrDispatch()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", "reviewed-move"));
        var sut = CreateService(client, "https://cal.example/tasks/");

        var result = await sut.MoveResourceAsync(
            Request(sourceHref) with
            {
                Destination = CalendarMoveDestination.Selected(new CalendarReference(Name: "Tasks"))
            },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError)]
    public async Task MoveResourceAsync_UnrelatedDestinationResourceCannotAffectMove(
        CalendarResourceReadCode unrelatedReadCode)
    {
        const string sourceCalendarHref = "https://cal.example/tasks/";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string collisionHref = "https://cal.example/archive/existing.ics";
        const string entityUid = "reviewed-move";
        var generatedDestinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, entityUid);
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar(sourceCalendarHref, "Tasks"),
            TodoCalendar(destinationCalendarHref, "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", entityUid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(generatedDestinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(generatedDestinationHref, "\"r2\"", entityUid));
        client.GetCalendarResourceAsync(collisionHref, Arg.Any<CancellationToken>()).Returns(
            unrelatedReadCode == CalendarResourceReadCode.Success
                ? CalendarResourceRead.Success(collisionHref, "\"opaque\"", Encoding.UTF8.GetBytes("not iCalendar"))
                : new CalendarResourceRead(unrelatedReadCode));
        client.MoveCalendarResourceAsync(
                Arg.Any<CalendarResourceMoveDispatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            new CalendarResourceMoveRequest(
                new CalendarResourceRevisionReference(
                    sourceHref,
                    entityUid,
                    CalendarEntityKind.Todo,
                    "\"r1\""),
                CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        await client.DidNotReceive().GetCalendarResourceAsync(
            collisionHref,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", CalendarResourceMoveCode.InvalidInput)]
    [InlineData("r1", CalendarResourceMoveCode.InvalidInput)]
    [InlineData("*", CalendarResourceMoveCode.InvalidInput)]
    public async Task MoveResourceAsync_RejectsInvalidRevisionBeforeNetwork(
        string entityTag,
        CalendarResourceMoveCode expectedCode)
    {
        var client = MoveClient();
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            new CalendarResourceMoveRequest(
                new CalendarResourceRevisionReference(
                    "https://cal.example/tasks/reviewed.ics",
                    "move-1",
                    CalendarEntityKind.Todo,
                    entityTag),
                CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_RejectsWeakRevisionAfterDestinationSelectionWithoutResourceRead()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive/", "Archive")
        ]);
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            Request(sourceHref) with
            {
                Revision = Request(sourceHref).Revision with { EntityTag = "W/\"r1\"" }
            },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.ConcurrencyUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_RejectsMissingSourceBeforeDispatch()
    {
        const string sourceHref = "https://cal.example/tasks/missing.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive/", "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("not_advertised")]
    public async Task MoveResourceAsync_RequiresAdvertisedDestinationKind(string support)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var destination = TodoCalendar("https://cal.example/archive/", "Archive") with
        {
            TodoSupport = support == "unknown" ? EntityKindSupport.Unknown : EntityKindSupport.NotAdvertised
        };
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            destination
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", "reviewed-move"));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.AuthorizedCandidates.ShouldBe([destination]);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_UnverifiedServerProfileFailsClosedBeforeSourceAccess()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = ConfiguredClient(sourceHref, "reviewed-move");
        var sut = CreateService(client, interoperabilityProfile: null);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarResourceMovePhase.SelectionDiscoveryCapability);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_AmbiguousDestinationNameDoesNotDispatch()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive-a/", "Archive"),
            TodoCalendar("https://cal.example/archive-b/", "archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", "reviewed-move"));
        var sut = CreateService(
            client,
            "https://cal.example/tasks/,https://cal.example/archive-a/,https://cal.example/archive-b/");

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Ambiguous);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.AuthorizedCandidates!.Count.ShouldBe(2);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_DestinationSelectionFailurePrecedesSourceRevisionRead()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks")
        ]);
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarResourceAsync(
            sourceHref,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_SourceOutsideConfiguredScopeFailsBeforeNetwork()
    {
        var client = MoveClient();
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            Request("https://cal.example/private/reviewed.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("archive/")]
    [InlineData("https://other.example/archive/")]
    [InlineData("https://cal.example/archive/?view=all")]
    [InlineData("https://cal.example/archive/#fragment")]
    [InlineData("https://user@cal.example/archive/")]
    [InlineData("https://cal.example/archive")]
    [InlineData("https://cal.example/%2e/archive/")]
    [InlineData("https://cal.example/archive%2Fprivate/")]
    [InlineData("https://cal.example/archive%5Cprivate/")]
    public async Task MoveResourceAsync_RejectsUnsafeSelectedCalendarHrefBeforeNetwork(
        string destinationCalendarHref)
    {
        var client = MoveClient();
        var sut = CreateService(client);
        var request = Request("https://cal.example/tasks/reviewed.ics") with
        {
            Destination = CalendarMoveDestination.Selected(
                new CalendarReference(Href: destinationCalendarHref))
        };

        var result = await sut.MoveResourceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_RejectsSelectedCalendarHrefOutsideConfiguredScopeBeforeNetwork()
    {
        var client = MoveClient();
        var sut = CreateService(client);
        var request = Request("https://cal.example/tasks/reviewed.ics") with
        {
            Destination = CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://cal.example/private/"))
        };

        var result = await sut.MoveResourceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_RejectsCrossOriginCalendarFromDiscoveryBeforeResourceAccess()
    {
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://other.example/archive/", "Archive")
        ]);
        var sut = CreateService(client, calendarHrefs: string.Empty);

        var result = await sut.MoveResourceAsync(
            Request("https://cal.example/tasks/reviewed.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_DestinationCollisionFromConcurrentMoveRaceIsNotCommitted()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Conflict));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceMoveDispatchCode.Conflict, CalendarResourceMoveCode.Conflict, false)]
    [InlineData(CalendarResourceMoveDispatchCode.DestinationConflict, CalendarResourceMoveCode.DestinationConflict, false)]
    [InlineData(CalendarResourceMoveDispatchCode.NotFound, CalendarResourceMoveCode.NotFound, false)]
    [InlineData(CalendarResourceMoveDispatchCode.InvalidInput, CalendarResourceMoveCode.InvalidInput, false)]
    [InlineData(CalendarResourceMoveDispatchCode.UnsupportedCapability, CalendarResourceMoveCode.UnsupportedCapability, false)]
    [InlineData(CalendarResourceMoveDispatchCode.PayloadTooLarge, CalendarResourceMoveCode.PayloadTooLarge, false)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamUnauthorized, CalendarResourceMoveCode.UpstreamUnauthorized, false)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamForbidden, CalendarResourceMoveCode.UpstreamForbidden, false)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamRateLimited, CalendarResourceMoveCode.UpstreamRateLimited, true)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamUnavailable, CalendarResourceMoveCode.UpstreamUnavailable, false)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamProtocolError, CalendarResourceMoveCode.UpstreamProtocolError, false)]
    public async Task MoveResourceAsync_MapsDefinitiveDispatchFailureWithoutRetry(
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceMoveCode expectedCode,
        bool retryable)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        var client = ConfiguredClient(sourceHref, "reviewed-move");
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            destinationCalendarHref,
            "reviewed-move");
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(dispatchCode, 2_000));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBe(retryable);
        result.RetryAfterMilliseconds.ShouldBe(retryable ? 2_000 : null);
        result.Phase.ShouldBe(CalendarResourceMovePhase.Execution);
        await client.Received(1).MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_StaleSourceConflictReturnsCurrentSnapshot()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            Resource(sourceHref, "\"r2\"", uid));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Conflict));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task MoveResourceAsync_PostMoveContentDifferenceIsCommittedFidelityFailure()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", uid, "X-KEEP;P=One,one:changed"));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.FidelityFailure);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldNotBeNull().ResourceHref.ShouldBe(destinationHref);
    }

    [Theory]
    [InlineData("unchanged", CalendarResourceMoveCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData("unavailable", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task MoveResourceAsync_ReconcilesPossiblyDispatchedMoveWithoutRetry(
        string observation,
        CalendarResourceMoveCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            observation == "unchanged"
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
                : new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            observation == "unchanged"
                ? Resource(sourceHref, "\"r1\"", uid)
                : new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("source-unchanged", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData("source-unavailable", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task MoveResourceAsync_DoesNotInferCommitFromDestinationAfterUncertainDispatch(
        string sourceObservation,
        CalendarResourceMoveCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", uid, "X-KEEP;P=One,one:concurrent"));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            sourceObservation == "source-unchanged"
                ? Resource(sourceHref, "\"r1\"", uid)
                : new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        result.Snapshot?.ResourceHref.ShouldBe(sourceObservation == "source-unchanged" ? sourceHref : null);
    }

    [Fact]
    public async Task MoveResourceAsync_UncertainDispatchRetainsDestinationObservationWhenSourceReadThrows()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            _ => Resource(destinationHref, "\"r2\"", uid, "X-KEEP:concurrent"));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            _ => Resource(sourceHref, "\"r1\"", uid),
            _ => throw new IOException("source reconciliation unavailable"));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Snapshot.ShouldBeNull();
        await client.Received(1).GetCalendarResourceAsync(
            destinationHref,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceMoveDispatchCode.Dispatched, CalendarResourceMoveCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceMoveDispatchCode.PossiblyDispatched, CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task MoveResourceAsync_WeakDestinationObservationPreservesDispatchTruth(
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceMoveCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var source = Resource(sourceHref, "\"r1\"", uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(
                CalendarResourceReadCode.ConcurrencyUnavailable,
                destinationHref,
                AuthoritativeUtf8: source.AuthoritativeUtf8));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            source,
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(dispatchCode));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Fact]
    public async Task MoveResourceAsync_DefinitiveMoveProvenUnchangedIsNotCommitted()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Snapshot.ShouldNotBeNull().ResourceHref.ShouldBe(sourceHref);
    }

    [Fact]
    public async Task MoveResourceAsync_DefinitiveMoveWithoutVerificationIsCommittedButUnverified()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarResourceRead>(new IOException("destination verification unavailable")));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            _ => Resource(sourceHref, "\"r1\"", uid),
            _ => throw new IOException("source verification unavailable"));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task MoveResourceAsync_DefinitiveMoveRetainsDestinationFidelityEvidenceWhenSourceReadFails()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var destination = Resource(destinationHref, "\"r2\"", uid, "X-KEEP:changed");
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            _ => destination);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            _ => Resource(sourceHref, "\"r1\"", uid),
            _ => throw new IOException("source verification unavailable"));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldBeNull();
    }

    [Theory]
    [InlineData("matching", CalendarResourceMoveCode.Success)]
    [InlineData("different", CalendarResourceMoveCode.Indeterminate)]
    public async Task MoveResourceAsync_ReconcilesPossiblyDispatchedDestinationAndSourceAbsence(
        string destinationState,
        CalendarResourceMoveCode expectedCode)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(
                destinationHref,
                "\"r2\"",
                uid,
                destinationState == "matching" ? "X-KEEP;P=One,one:opaque" : "X-KEEP:changed"));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.MoveCalendarResourceAsync(Arg.Any<CalendarResourceMoveDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(destinationState == "matching"
            ? CalendarMutationState.Committed
            : CalendarMutationState.Unknown);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success, CalendarResourceMoveCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarResourceMoveCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarResourceMoveCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarResourceMoveCode.UpstreamProtocolError)]
    public async Task MoveResourceAsync_MapsGeneratedDestinationPreflightFailure(
        CalendarResourceReadCode readCode,
        CalendarResourceMoveCode expectedCode)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = ConfiguredClient(sourceHref, "reviewed-move");
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            "https://cal.example/archive/",
            "reviewed-move");
        SetPresence(
            client,
            "https://cal.example/archive/",
            destinationHref,
            new CalendarResourceRead(readCode));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarResourceAsync(
            destinationHref,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.InvalidInput, CalendarResourceMoveCode.InvalidInput)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarResourceMoveCode.NotFound)]
    [InlineData(CalendarResourceReadCode.OutsideScope, CalendarResourceMoveCode.OutsideScope)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarResourceMoveCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarResourceMoveCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, CalendarResourceMoveCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarResourceMoveCode.UpstreamProtocolError)]
    public async Task MoveResourceAsync_MapsSourceReadFailure(
        CalendarResourceReadCode readCode,
        CalendarResourceMoveCode expectedCode)
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive/", "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(readCode));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(401, CalendarResourceMoveCode.UpstreamUnauthorized, false)]
    [InlineData(403, CalendarResourceMoveCode.UpstreamForbidden, false)]
    [InlineData(413, CalendarResourceMoveCode.PayloadTooLarge, false)]
    [InlineData(429, CalendarResourceMoveCode.UpstreamRateLimited, true)]
    [InlineData(405, CalendarResourceMoveCode.UnsupportedCapability, false)]
    [InlineData(501, CalendarResourceMoveCode.UnsupportedCapability, false)]
    [InlineData(507, CalendarResourceMoveCode.UpstreamUnavailable, false)]
    [InlineData(500, CalendarResourceMoveCode.UpstreamUnavailable, true)]
    [InlineData(400, CalendarResourceMoveCode.UpstreamProtocolError, false)]
    public async Task MoveResourceAsync_MapsPreflightHttpFailure(
        int statusCode,
        CalendarResourceMoveCode expectedCode,
        bool retryable)
    {
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<CalendarDescriptor>>(
            _ => throw new HttpRequestException(
                "private response",
                null,
                (System.Net.HttpStatusCode)statusCode));
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(
            Request("https://cal.example/tasks/reviewed.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Retryable.ShouldBe(retryable);
    }

    [Fact]
    public async Task MoveResourceAsync_PreDispatchWorkStopsAtThirtySecondsWithoutDispatch()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var client = MoveClient();
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            discoveryEntered.TrySetResult();
            return await completion.Task;
        });
        var sut = CreateService(client, timeProvider: timeProvider);
        var pending = sut.MoveResourceAsync(
            Request("https://cal.example/tasks/reviewed.ics"),
            CancellationToken.None);
        await discoveryEntered.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceMoveCode.LimitExhausted);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.LimitDimension.ShouldBe(CalendarResourceMoveLimitDimension.ElapsedTime);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_CallerCancellationBeforeDispatchPropagatesWithoutWrite()
    {
        var client = MoveClient();
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            discoveryEntered.TrySetResult();
            return await completion.Task;
        });
        var sut = CreateService(client);
        using var cancellation = new CancellationTokenSource();
        var pending = sut.MoveResourceAsync(
            Request("https://cal.example/tasks/reviewed.ics"),
            cancellation.Token);
        await discoveryEntered.Task;

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(pending);
        await client.DidNotReceive().MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveResourceAsync_CallerCancellationAfterPossibleDispatchStillReconcilesTruth()
    {
        const string sourceHref = "https://cal.example/tasks/reviewed.ics";
        const string destinationCalendarHref = "https://cal.example/archive/";
        const string uid = "reviewed-move";
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(destinationCalendarHref, uid);
        var client = ConfiguredClient(sourceHref, uid);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            Resource(destinationHref, "\"r2\"", uid));
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        using var cancellation = new CancellationTokenSource();
        client.MoveCalendarResourceAsync(
                Arg.Any<CalendarResourceMoveDispatchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return new CalendarResourceMoveDispatchResult(
                    CalendarResourceMoveDispatchCode.PossiblyDispatched);
            });
        var sut = CreateService(client);

        var result = await sut.MoveResourceAsync(Request(sourceHref), cancellation.Token);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        await client.Received(1).MoveCalendarResourceAsync(
            Arg.Any<CalendarResourceMoveDispatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static CalendarService CreateService(
        ICalendarClient client,
        string calendarHrefs = "https://cal.example/tasks/,https://cal.example/archive/",
        string? defaultTodoCalendarName = null,
        TimeProvider? timeProvider = null,
        string? interoperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = calendarHrefs,
            DefaultTodoCalendarName = defaultTodoCalendarName,
            InteroperabilityProfile = interoperabilityProfile
        }),
        Substitute.For<ILogger<CalendarService>>(),
        timeProvider ?? TimeProvider.System,
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private static CalendarDescriptor TodoCalendar(string href, string displayName) => new()
    {
        Href = href,
        DisplayName = displayName,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };

    private static CalendarDescriptor EventCalendar(string href, string displayName) => new()
    {
        Href = href,
        DisplayName = displayName,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static CalendarResourceMoveRequest Request(string sourceHref) => new(
        new CalendarResourceRevisionReference(
            sourceHref,
            "reviewed-move",
            CalendarEntityKind.Todo,
            "\"r1\""),
        CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive")));

    private static ICalendarClient ConfiguredClient(string sourceHref, string uid)
    {
        var client = MoveClient();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar("https://cal.example/tasks/", "Tasks"),
            TodoCalendar("https://cal.example/archive/", "Archive")
        ]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(sourceHref, "\"r1\"", uid));
        return client;
    }

    private static ICalendarClient MoveClient()
    {
        var client = (ICalendarClient)Substitute.For(
            [
                typeof(ICalendarClient),
                typeof(ICalendarMoveResourceTransport)
            ],
            []);
        var moveTransport = (ICalendarMoveResourceTransport)client;
        moveTransport.ReadMoveResourceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call => client.GetCalendarResourceAsync(
                call.ArgAt<string>(1),
                call.ArgAt<CancellationToken>(3)));
        moveTransport.ProbeMoveResourcePresenceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
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

    private static void SetPresence(
        ICalendarClient client,
        string authorizedCalendarHref,
        string href,
        CalendarResourceRead read) => ((ICalendarMoveResourceTransport)client)
        .ProbeMoveResourcePresenceAsync(
            authorizedCalendarHref,
            href,
            Arg.Any<CancellationToken>())
        .Returns(read);

    private static CalendarResourceRead Resource(
        string href,
        string entityTag,
        string uid,
        string opaqueLine = "X-KEEP;P=One,one:opaque")
    {
        var content = Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n{opaqueLine}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        return CalendarResourceRead.Success(href, entityTag, content);
    }

    private static CalendarResourceRead EventResource(string href, string entityTag, string uid)
    {
        var content = Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return CalendarResourceRead.Success(href, entityTag, content);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
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
            ManualTimeProvider owner,
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
