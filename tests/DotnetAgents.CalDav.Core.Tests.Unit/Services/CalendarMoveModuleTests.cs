using System.Collections.Concurrent;
using System.Text;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarMoveModuleTests
{
    private const string SourceHref = "https://cal.example/tasks/reviewed.ics";
    private const string DestinationCalendarHref = "https://cal.example/archive/";
    private const string Uid = "reviewed-move";

    [Theory]
    [InlineData("faithful-absent", CalendarResourceMoveCode.Success, CalendarMutationState.Committed)]
    [InlineData("divergent-absent", CalendarResourceMoveCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData("unavailable", CalendarResourceMoveCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData("contradictory", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task Dispatched_BilateralMatrixPreservesCommitTruth(
        string cell,
        CalendarResourceMoveCode expectedCode,
        CalendarMutationState expectedState)
    {
        var source = Resource(SourceHref, "\"r1\"", Uid);
        var transport = ScriptedTransport(source) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched),
            DestinationObservation = cell switch
            {
                "faithful-absent" or "contradictory" => Resource(DestinationHref(), "\"r2\"", Uid),
                "divergent-absent" => Resource(DestinationHref(), "\"r2\"", Uid, "X-KEEP:changed"),
                _ => new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError)
            },
            SourceObservation = cell == "contradictory"
                ? source
                : new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Theory]
    [InlineData("faithful-absent", CalendarResourceMoveCode.Success, CalendarMutationState.Committed)]
    [InlineData("unchanged-absent", CalendarResourceMoveCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData("divergent-absent", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData("contradictory", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData("unavailable", CalendarResourceMoveCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task PossiblyDispatched_BilateralMatrixDoesNotOverclaimCommit(
        string cell,
        CalendarResourceMoveCode expectedCode,
        CalendarMutationState expectedState)
    {
        var source = Resource(SourceHref, "\"r1\"", Uid);
        var transport = ScriptedTransport(source) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched),
            DestinationObservation = cell switch
            {
                "faithful-absent" or "contradictory" => Resource(DestinationHref(), "\"r2\"", Uid),
                "divergent-absent" => Resource(DestinationHref(), "\"r2\"", Uid, "X-KEEP:changed"),
                "unchanged-absent" => new CalendarResourceRead(CalendarResourceReadCode.NotFound),
                _ => new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError)
            },
            SourceObservation = cell switch
            {
                "faithful-absent" or "divergent-absent" =>
                    new CalendarResourceRead(CalendarResourceReadCode.NotFound),
                "unavailable" => new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError),
                _ => source
            }
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(600)]
    public async Task DestinationCardinalityDoesNotChangeMoveWork(int destinationCardinality)
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            DestinationCardinality = destinationCardinality,
            DestinationObservation = Resource(DestinationHref(), "\"r2\"", Uid),
            SourceObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        AssertSingleDispatchTrace(transport.Trace);
        transport.UnrelatedReads.ShouldBe(0);
    }

    [Fact]
    public async Task CallerCancellationAfterPossibleDispatchCannotStopReconciliation()
    {
        using var caller = new CancellationTokenSource();
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched),
            DestinationObservation = Resource(DestinationHref(), "\"r2\"", Uid),
            SourceObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            AfterDispatch = caller.Cancel
        };

        var result = await Module(transport).MoveAsync(Request(), caller.Token);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Fact]
    public async Task ReconciliationStartsBothReadsBeforeEitherCompletes()
    {
        var destinationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Observe = async (href, cancellationToken) =>
            {
                if (string.Equals(href, DestinationHref(), StringComparison.Ordinal))
                {
                    destinationStarted.SetResult();
                    await sourceStarted.Task.WaitAsync(cancellationToken);
                    return Resource(DestinationHref(), "\"r2\"", Uid);
                }
                sourceStarted.SetResult();
                await destinationStarted.Task.WaitAsync(cancellationToken);
                return new CalendarResourceRead(CalendarResourceReadCode.NotFound);
            }
        };

        var move = Module(transport).MoveAsync(Request(), CancellationToken.None);
        await Task.WhenAll(destinationStarted.Task, sourceStarted.Task).WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        var result = await move;

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Fact]
    public async Task UnverifiedProfileFailsClosedBeforeSourceOrPresenceWork()
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid));

        var result = await Module(transport, interoperabilityProfile: null)
            .MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBe(["discover"]);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.InvalidInput, CalendarResourceMoveCode.InvalidInput)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarResourceMoveCode.NotFound)]
    [InlineData(CalendarResourceReadCode.OutsideScope, CalendarResourceMoveCode.OutsideScope)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarResourceMoveCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarResourceMoveCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, CalendarResourceMoveCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarResourceMoveCode.UpstreamProtocolError)]
    public async Task SourceReadFailuresNeverProbeOrDispatch(
        CalendarResourceReadCode readCode,
        CalendarResourceMoveCode expectedCode)
    {
        var transport = ScriptedTransport(new CalendarResourceRead(readCode));

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBe(["discover", "read-source"]);
    }

    [Theory]
    [InlineData("opaque", CalendarResourceMoveCode.OpaqueResource)]
    [InlineData("kind", CalendarResourceMoveCode.EntityKindMismatch)]
    [InlineData("uid", CalendarResourceMoveCode.Conflict)]
    [InlineData("etag", CalendarResourceMoveCode.Conflict)]
    public async Task AuthoritativeSourceMismatchNeverProbeOrDispatch(
        string mismatch,
        CalendarResourceMoveCode expectedCode)
    {
        var source = mismatch switch
        {
            "opaque" => CalendarResourceRead.Success(SourceHref, "\"r1\"", "not-a-calendar"u8.ToArray()),
            "kind" => EventResource(SourceHref, "\"r1\"", Uid),
            "uid" => Resource(SourceHref, "\"r1\"", "different-uid"),
            _ => Resource(SourceHref, "\"different\"", Uid)
        };
        var transport = ScriptedTransport(source);

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBe(["discover", "read-source"]);
    }

    [Theory]
    [InlineData(CalendarResourceMoveDispatchCode.DestinationConflict, CalendarResourceMoveDispatchCollisionKind.None,
        CalendarResourceMoveCode.DestinationConflict)]
    [InlineData(CalendarResourceMoveDispatchCode.Conflict, CalendarResourceMoveDispatchCollisionKind.Uid,
        CalendarResourceMoveCode.Conflict)]
    [InlineData(CalendarResourceMoveDispatchCode.Conflict, CalendarResourceMoveDispatchCollisionKind.DestinationHref,
        CalendarResourceMoveCode.Conflict)]
    [InlineData(CalendarResourceMoveDispatchCode.Conflict, CalendarResourceMoveDispatchCollisionKind.Unclassified,
        CalendarResourceMoveCode.Conflict)]
    [InlineData(CalendarResourceMoveDispatchCode.UpstreamRateLimited, CalendarResourceMoveDispatchCollisionKind.None,
        CalendarResourceMoveCode.UpstreamRateLimited)]
    public async Task DefiniteDispatchRejectionsRemainNotCommitted(
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceMoveDispatchCollisionKind collisionKind,
        CalendarResourceMoveCode expectedCode)
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(dispatchCode, 2_000, collisionKind)
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBe(dispatchCode == CalendarResourceMoveDispatchCode.UpstreamRateLimited);
        transport.Trace.ShouldBe(["discover", "read-source", "probe-destination", "dispatch"]);
    }

    [Fact]
    public async Task NullDispatchIsRejectedAsProtocolFailure()
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Dispatch = null!
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.UpstreamProtocolError);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        transport.Trace.ShouldBe(["discover", "read-source", "probe-destination", "dispatch"]);
    }

    [Fact]
    public async Task DispatchedWithBothResourcesAbsentIsIndeterminate()
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            DestinationObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            SourceObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Snapshot.ShouldBeNull();
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Theory]
    [InlineData("href")]
    [InlineData("uid")]
    [InlineData("etag")]
    [InlineData("kind")]
    public async Task PossiblyDispatchedRequiresEveryStrongSourceFactToProveNotCommitted(string mismatch)
    {
        var source = Resource(SourceHref, "\"r1\"", Uid);
        var observedSource = mismatch switch
        {
            "href" => Resource("https://cal.example/tasks/other.ics", "\"r1\"", Uid),
            "uid" => Resource(SourceHref, "\"r1\"", "other-uid"),
            "etag" => Resource(SourceHref, "\"r2\"", Uid),
            _ => EventResource(SourceHref, "\"r1\"", Uid)
        };
        var transport = ScriptedTransport(source) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched),
            DestinationObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            SourceObservation = observedSource
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("kind")]
    [InlineData("etag-any")]
    [InlineData("etag-malformed")]
    [InlineData("source-format")]
    [InlineData("source-origin")]
    [InlineData("destination-shape")]
    [InlineData("destination-null")]
    public async Task InvalidAuthorizationOrRevisionShapeStopsBeforeDiscovery(string invalidity)
    {
        var revision = invalidity switch
        {
            "uid" => new CalendarResourceRevisionReference(SourceHref, " ", CalendarEntityKind.Todo, "\"r1\""),
            "kind" => new CalendarResourceRevisionReference(SourceHref, Uid, (CalendarEntityKind)999, "\"r1\""),
            "etag-any" => new CalendarResourceRevisionReference(SourceHref, Uid, CalendarEntityKind.Todo, "*"),
            "etag-malformed" => new CalendarResourceRevisionReference(SourceHref, Uid, CalendarEntityKind.Todo, "r1"),
            "source-format" => new CalendarResourceRevisionReference(
                "not-an-absolute-href", Uid, CalendarEntityKind.Todo, "\"r1\""),
            "source-origin" => new CalendarResourceRevisionReference(
                "https://other.example/tasks/reviewed.ics", Uid, CalendarEntityKind.Todo, "\"r1\""),
            _ => Request().Revision
        };
        var destination = invalidity switch
        {
            "destination-shape" =>
                CalendarMoveDestination.Selected(new CalendarReference("Archive", DestinationCalendarHref)),
            "destination-null" => new CalendarMoveDestination(CalendarEntityScopeMode.Selected),
            _ => Request().Destination
        };
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid));

        var result = await Module(transport)
            .MoveAsync(new CalendarResourceMoveRequest(revision, destination), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBeEmpty();
    }

    [Fact]
    public async Task DefaultDestinationUsesTypedDiscoveryOutcomeWithoutReselection()
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            DestinationObservation = Resource(DestinationHref(), "\"r2\"", Uid),
            SourceObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        };
        var request = Request() with { Destination = CalendarMoveDestination.Default };

        var result = await Module(transport, calendarHrefs: null).MoveAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Theory]
    [InlineData("invalid-href", CalendarResourceMoveCode.UpstreamProtocolError)]
    [InlineData("unsupported-kind", CalendarResourceMoveCode.UnsupportedCapability)]
    public async Task DiscoveredDestinationMustBeSafeAndAdvertiseTheRequestedKind(
        string invalidity,
        CalendarResourceMoveCode expectedCode)
    {
        var destination = invalidity == "invalid-href"
            ? TodoCalendar("https://other.example/archive/", "Archive")
            : TodoCalendar(DestinationCalendarHref, "Archive") with
            {
                TodoSupport = EntityKindSupport.NotAdvertised
            };
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Discovery = new CalendarMoveDiscoveryResult(
                new CalendarDiscoveryResult([
                    TodoCalendar("https://cal.example/tasks/", "Tasks"),
                    destination
                ], []),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound),
                CalendarSelectionResult.Success(destination))
        };
        var request = Request() with { Destination = CalendarMoveDestination.Default };

        var result = await Module(transport).MoveAsync(request, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBe(["discover"]);
    }

    [Fact]
    public async Task PossiblyDispatchedWithBothResourcesAbsentCannotProveNotCommitted()
    {
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched),
            DestinationObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            SourceObservation = new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        };

        var result = await Module(transport).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        AssertSingleDispatchTrace(transport.Trace);
    }

    [Fact]
    public void UnknownEntityKindCannotInheritEitherTypedDefault()
    {
        var discovery = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)).Discovery;

        var selection = discovery.ResolveDefault((CalendarEntityKind)999);

        selection.Code.ShouldBe(CalendarSelectionCode.NotFound);
        selection.Calendar.ShouldBeNull();
    }

    [Theory]
    [InlineData("not-an-absolute-calendar-href")]
    [InlineData("https://cal.example/tasks/nested/")]
    public async Task DiscoveryCannotAuthorizeAnInvalidOrNonDirectSourceCalendar(string sourceCalendarHref)
    {
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        var transport = ScriptedTransport(Resource(SourceHref, "\"r1\"", Uid)) with
        {
            Discovery = new CalendarMoveDiscoveryResult(
                new CalendarDiscoveryResult([
                    TodoCalendar(sourceCalendarHref, "Invalid source"),
                    destination
                ], []),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound),
                CalendarSelectionResult.Success(destination))
        };

        var result = await Module(transport, calendarHrefs: null).MoveAsync(Request(), CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.Trace.ShouldBe(["discover"]);
    }

    private static CalendarMoveModule Module(
        ScriptedMoveTransport transport,
        string? interoperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8,
        string? calendarHrefs = "https://cal.example/tasks/,https://cal.example/archive/") => new(
        transport,
        new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = calendarHrefs,
            InteroperabilityProfile = interoperabilityProfile
        },
        TimeProvider.System);

    private static ScriptedMoveTransport ScriptedTransport(CalendarResourceRead source) => new()
    {
        Discovery = new CalendarMoveDiscoveryResult(
            new CalendarDiscoveryResult([
                TodoCalendar("https://cal.example/tasks/", "Tasks"),
                TodoCalendar(DestinationCalendarHref, "Archive")
            ], []),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound),
            CalendarSelectionResult.Success(TodoCalendar(DestinationCalendarHref, "Archive"))),
        Source = source,
        Preflight = new CalendarResourceRead(CalendarResourceReadCode.NotFound),
        Dispatch = new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.Dispatched)
    };

    private static void AssertSingleDispatchTrace(ConcurrentQueue<string> trace)
    {
        var calls = trace.ToArray();
        calls[..4].ShouldBe(["discover", "read-source", "probe-destination", "dispatch"]);
        calls[4..].Order(StringComparer.Ordinal).ShouldBe(["observe-destination", "observe-source"]);
    }

    private static CalendarResourceMoveRequest Request() => new(
        new CalendarResourceRevisionReference(SourceHref, Uid, CalendarEntityKind.Todo, "\"r1\""),
        CalendarMoveDestination.Selected(new CalendarReference(Name: "Archive")));

    private static string DestinationHref() =>
        CalendarResourceCreateProtocol.BuildResourceHref(DestinationCalendarHref, Uid);

    private static CalendarDescriptor TodoCalendar(string href, string displayName) => new()
    {
        Href = href,
        DisplayName = displayName,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };

    private static CalendarResourceRead Resource(
        string href,
        string entityTag,
        string uid,
        string opaqueLine = "X-KEEP:opaque") => CalendarResourceRead.Success(
        href,
        entityTag,
        Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\n"
            + $"UID:{uid}\r\nDTSTAMP:20260823T120000Z\r\n{opaqueLine}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"));

    private static CalendarResourceRead EventResource(string href, string entityTag, string uid) =>
        CalendarResourceRead.Success(
            href,
            entityTag,
            Encoding.UTF8.GetBytes(
                $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VEVENT\r\n"
                + $"UID:{uid}\r\nDTSTAMP:20260823T120000Z\r\nDTSTART:20260824T120000Z\r\n"
                + "END:VEVENT\r\nEND:VCALENDAR\r\n"));

    private sealed record ScriptedMoveTransport : ICalendarMoveTransport
    {
        internal required CalendarMoveDiscoveryResult Discovery { get; init; }

        internal required CalendarResourceRead Source { get; init; }

        internal required CalendarResourceRead Preflight { get; init; }

        internal required CalendarResourceMoveDispatchResult Dispatch { get; init; }

        internal CalendarResourceRead DestinationObservation { get; init; } =
            new(CalendarResourceReadCode.UpstreamProtocolError);

        internal CalendarResourceRead SourceObservation { get; init; } =
            new(CalendarResourceReadCode.UpstreamProtocolError);

        internal Action? AfterDispatch { get; init; }

        internal Func<string, CancellationToken, Task<CalendarResourceRead>>? Observe { get; init; }

        internal int DestinationCardinality { get; init; }

        internal int UnrelatedReads { get; private set; }

        internal ConcurrentQueue<string> Trace { get; } = new();

        public Task<CalendarMoveDiscoveryResult> DiscoverCalendarsAsync(CancellationToken cancellationToken)
        {
            Trace.Enqueue("discover");
            return Task.FromResult(Discovery);
        }

        public Task<CalendarResourceRead> ReadSourceAsync(string href, CancellationToken cancellationToken)
        {
            Trace.Enqueue("read-source");
            return Task.FromResult(Source);
        }

        public Task<CalendarResourceRead> ProbeDestinationPresenceAsync(
            string href,
            CancellationToken cancellationToken)
        {
            Trace.Enqueue("probe-destination");
            return Task.FromResult(Preflight);
        }

        public Task<CalendarResourceRead> ObserveResourceAsync(string href, CancellationToken cancellationToken)
        {
            var destination = string.Equals(href, DestinationHref(), StringComparison.Ordinal);
            Trace.Enqueue(destination ? "observe-destination" : "observe-source");
            return Observe is null
                ? Task.FromResult(destination ? DestinationObservation : SourceObservation)
                : Observe(href, cancellationToken);
        }

        public Task<CalendarResourceMoveDispatchResult> DispatchAsync(
            CalendarResourceMoveDispatchRequest request,
            CancellationToken cancellationToken)
        {
            Trace.Enqueue("dispatch");
            AfterDispatch?.Invoke();
            return Task.FromResult(Dispatch);
        }
    }
}
