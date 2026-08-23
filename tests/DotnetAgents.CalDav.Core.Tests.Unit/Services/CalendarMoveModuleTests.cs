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

    private static CalendarMoveModule Module(
        ScriptedMoveTransport transport,
        string? interoperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8) => new(
        transport,
        new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = "https://cal.example/tasks/,https://cal.example/archive/",
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
