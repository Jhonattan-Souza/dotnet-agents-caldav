using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarQuerySnapshotLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ForbiddenContinueDependencyFragments =
    [
        "Discovery",
        "CalDavClient",
        "Transport",
        "Parser",
        "Evaluator",
        "Projector",
        "Recurrence"
    ];

    [Fact]
    public void StartPublicationAndContinueReplayHaveSeparatedDependencyGraphs()
    {
        ConstructorTypes(typeof(CalendarEntityQueryStartExecutor)).ShouldContain(typeof(CalendarQuerySnapshotPublication));
        ConstructorTypes(typeof(CalendarOccurrenceQueryStartExecutor)).ShouldContain(typeof(CalendarQuerySnapshotPublication));
        ConstructorTypes(typeof(CalendarTodoQueryStartExecutor)).ShouldContain(typeof(CalendarQuerySnapshotPublication));
        ConstructorTypes(typeof(CalendarEntityQueryStartExecutor)).ShouldNotContain(typeof(CalendarQuerySnapshotWriter));
        ConstructorTypes(typeof(CalendarOccurrenceQueryStartExecutor)).ShouldNotContain(typeof(CalendarQuerySnapshotWriter));
        ConstructorTypes(typeof(CalendarTodoQueryStartExecutor)).ShouldNotContain(typeof(CalendarQuerySnapshotWriter));

        var continueExecutors = new[]
        {
            typeof(CalendarEntityQueryContinueExecutor),
            typeof(CalendarOccurrenceQueryContinueExecutor),
            typeof(CalendarTodoQueryContinueExecutor)
        };
        foreach (var executor in continueExecutors)
        {
            ConstructorTypes(executor).ShouldContain(typeof(CalendarQuerySnapshotReplay));
            var graph = ConstructorDependencyGraph(executor);
            graph.ShouldNotContain(typeof(CalendarQuerySnapshotPublication));
            graph.ShouldNotContain(typeof(CalendarQuerySnapshotWriter));
            graph.ShouldNotContain(typeof(CalendarQueryPolicy));
            graph.ShouldNotContain(typeof(CalendarQueryAcquisitionExecutor));
            graph.ShouldNotContain(typeof(CalendarEntityQueryStartExecutor));
            graph.ShouldNotContain(typeof(CalendarOccurrenceQueryStartExecutor));
            graph.ShouldNotContain(typeof(CalendarTodoQueryStartExecutor));
            graph.Any(type => ForbiddenContinueDependencyFragments.Any(fragment =>
                    type.Name.Contains(fragment, StringComparison.Ordinal)))
                .ShouldBeFalse();
        }

        var pageCodecAdapters = typeof(CalendarQuerySnapshotStore).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.GetInterfaces().Any(candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(ICalendarQueryPageCodec<>)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        pageCodecAdapters.ShouldBe([
            typeof(CalendarEntityQueryPageCodec),
            typeof(CalendarOccurrenceQueryPageCodec),
            typeof(CalendarTodoQueryPageCodec)
        ]);
    }

    [Fact]
    public void PublicationOwnsCompleteAndRetainedFirstPageAdmission()
    {
        using var context = LifecycleContext.Create();

        var complete = context.Publication.Publish(
                Draft(1),
                50,
                context.PageCodec,
                CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        complete.Value.Items.Count.ShouldBe(1);
        complete.Value.NextCursor.ShouldBeNull();
        context.Store.ActiveSnapshotCount.ShouldBe(0);
        context.Store.RetainedBytes.ShouldBe(0);

        var retainedDraft = Draft(3);
        var retained = context.Publication.Publish(
                retainedDraft,
                1,
                context.PageCodec,
                CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        retained.Value.Items.Count.ShouldBe(1);
        retained.Value.NextCursor.ShouldNotBeNull();
        context.Store.ActiveSnapshotCount.ShouldBe(1);
        context.Store.ActiveReservationCount.ShouldBe(0);
        context.Store.RetainedBytes.ShouldBe(retainedDraft.RetainedBytes);
    }

    [Fact]
    public void PublicationRollsBackCancellationDuringFinalMaterialization()
    {
        using var cancellation = new CancellationTokenSource();
        var work = new CalendarQueryPageWorkCounter(cancellation.Cancel);
        using var context = LifecycleContext.Create(workCounter: work);

        Should.Throw<OperationCanceledException>(() => context.Publication.Publish(
            Draft(2),
            1,
            context.PageCodec,
            cancellation.Token));

        work.FinalMaterializationCount.ShouldBe(1);
        context.Store.ActiveSnapshotCount.ShouldBe(0);
        context.Store.ActiveReservationCount.ShouldBe(0);
        context.Store.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public void PublicationMapsCommitFailureAfterMaterializationAndReleasesTheLease()
    {
        using var context = LifecycleContext.Create(new ThrowingTimerTimeProvider());

        var failure = context.Publication.Publish(
                Draft(2),
                1,
                context.PageCodec,
                CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamUnavailable);
        context.Store.ActiveSnapshotCount.ShouldBe(0);
        context.Store.ActiveReservationCount.ShouldBe(0);
        context.Store.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public void ReplayOwnsAuthenticationLookupContextPositionAndVariableFinalPages()
    {
        var time = new MutableTimeProvider();
        using var context = LifecycleContext.Create(time);
        var first = context.Publication.Publish(
                Draft(3, TemporalContext()),
                1,
                context.PageCodec,
                CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var cursor = first.Value.NextCursor.ShouldNotBeNull();

        var one = context.Replay.Replay(cursor, 1, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var oneAgain = context.Replay.Replay(cursor, 1, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var final = context.Replay.Replay(cursor, 2, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        one.Value.StructuredContent.GetRawText().ShouldBe(oneAgain.Value.StructuredContent.GetRawText());
        one.Value.NextCursor.ShouldBe(oneAgain.Value.NextCursor);
        final.Value.Items.Count.ShouldBe(2);
        final.Value.NextCursor.ShouldBeNull();

        var snapshot = context.Reader.Get(context.SnapshotId(cursor)).ShouldNotBeNull();
        var wrongContext = context.Issuer.Issue(
            CalendarEntityQueryPageCodec.ToolName,
            snapshot.Id,
            1,
            snapshot.ExpiresAt);
        var finalPosition = context.Issuer.Issue(
            CalendarEntityQueryPageCodec.ToolName,
            snapshot.Id,
            snapshot.Items.Length,
            snapshot.ExpiresAt,
            snapshot.TemporalEvaluationContextUtf8);
        var wrongTool = context.Issuer.Issue(
            CalendarTodoQueryPageCodec.ToolName,
            snapshot.Id,
            1,
            snapshot.ExpiresAt,
            snapshot.TemporalEvaluationContextUtf8);
        var missingSnapshot = context.Issuer.Issue(
            CalendarEntityQueryPageCodec.ToolName,
            Guid.NewGuid(),
            1,
            snapshot.ExpiresAt,
            snapshot.TemporalEvaluationContextUtf8);
        var wrongExpiry = context.Issuer.Issue(
            CalendarEntityQueryPageCodec.ToolName,
            snapshot.Id,
            1,
            snapshot.ExpiresAt.AddSeconds(-1),
            snapshot.TemporalEvaluationContextUtf8);
        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A");

        context.Replay.Replay(wrongContext, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(finalPosition, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(wrongTool, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(missingSnapshot, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(wrongExpiry, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(tampered, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(cursor, 0, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
        context.Replay.Replay(cursor, 201, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.InvalidInput);

        time.Advance(CalendarQueryPolicy.SnapshotLifetime);
        context.Replay.Replay(cursor, null, context.PageCodec, CancellationToken.None)
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.CursorExpired);
    }

    [Fact]
    public void ReplayRejectsCancellationBeforeSnapshotLookup()
    {
        using var context = LifecycleContext.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => context.Replay.Replay(
            "opaque",
            null,
            context.PageCodec,
            cancellation.Token));
    }

    private static HashSet<Type> ConstructorDependencyGraph(Type root)
    {
        var discovered = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var dependency in AllConstructorTypes(current).Where(IsCoreType))
            {
                if (discovered.Add(dependency))
                    pending.Push(dependency);
            }
        }
        return discovered;
    }

    private static bool IsCoreType(Type type) =>
        type.Assembly == typeof(CalendarQuerySnapshotStore).Assembly;

    private static Type[] ConstructorTypes(Type type) => type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ShouldHaveSingleItem()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

    private static IEnumerable<Type> AllConstructorTypes(Type type) => type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .SelectMany(constructor => constructor.GetParameters())
        .Select(parameter => parameter.ParameterType);

    private static CalendarQuerySnapshotDraft Draft(
        int itemCount,
        ReadOnlyMemory<byte> temporalContext = default)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(index => new StoredCalendarEntityQueryItem(JsonSerializer.SerializeToUtf8Bytes(new { index })))
            .ToImmutableArray();
        var diagnostics = "[]"u8.ToArray();
        return new CalendarQuerySnapshotDraft(
            items,
            diagnostics,
            items.Sum(item => item.JsonByteCount) + diagnostics.Length + temporalContext.Length,
            temporalContext);
    }

    private static ReadOnlyMemory<byte> TemporalContext() => CalendarTemporalEvaluationContextCodec.Encode(
        new TemporalEvaluationContext("America/Sao_Paulo", TemporalEvaluationContextSource.Caller));

    private sealed class LifecycleContext : IDisposable
    {
        private readonly CalendarQueryCursorAuthenticator _authenticator;

        private LifecycleContext(
            CalendarQuerySnapshotStore store,
            CalendarQuerySnapshotReader reader,
            CalendarQueryCursorIssuer issuer,
            CalendarQueryCursorAuthenticator authenticator,
            CalendarQuerySnapshotPublication publication,
            CalendarQuerySnapshotReplay replay,
            CalendarEntityQueryPageCodec pageCodec)
        {
            Store = store;
            Reader = reader;
            Issuer = issuer;
            _authenticator = authenticator;
            Publication = publication;
            Replay = replay;
            PageCodec = pageCodec;
        }

        internal CalendarQuerySnapshotStore Store { get; }

        internal CalendarQuerySnapshotReader Reader { get; }

        internal CalendarQueryCursorIssuer Issuer { get; }

        internal CalendarQuerySnapshotPublication Publication { get; }

        internal CalendarQuerySnapshotReplay Replay { get; }

        internal CalendarEntityQueryPageCodec PageCodec { get; }

        internal static LifecycleContext Create(
            TimeProvider? timeProvider = null,
            CalendarQueryPageWorkCounter? workCounter = null)
        {
            var time = timeProvider ?? new MutableTimeProvider();
            var options = Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "user",
                Password = "password"
            });
            var key = new CalendarQueryCursorKey(
                options,
                Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
            var issuer = new CalendarQueryCursorIssuer(key);
            var authenticator = new CalendarQueryCursorAuthenticator(key, time);
            var store = new CalendarQuerySnapshotStore(time);
            var reader = new CalendarQuerySnapshotReader(store);
            var writer = new CalendarQuerySnapshotWriter(store);
            return new LifecycleContext(
                store,
                reader,
                issuer,
                authenticator,
                new CalendarQuerySnapshotPublication(new CalendarQueryPolicy(time), writer),
                new CalendarQuerySnapshotReplay(authenticator, reader),
                new CalendarEntityQueryPageCodec(issuer, workCounter));
        }

        internal Guid SnapshotId(string cursor) => _authenticator
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName).Cursor!.SnapshotId;

        public void Dispose() => Store.Dispose();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = Now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class ThrowingTimerTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => throw new InvalidOperationException("scripted timer failure");
    }
}
