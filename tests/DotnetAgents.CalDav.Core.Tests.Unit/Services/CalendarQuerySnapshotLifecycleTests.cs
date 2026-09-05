using System.Collections.Immutable;
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

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void SharedPageAdmissionOwnsRepresentativeAndFinalPagePlanning(int pageSize)
    {
        using var context = LifecycleContext.Create();
        var snapshot = Draft(pageSize + 1).CreateSnapshot(Now.AddMinutes(10));

        var first = context.PageAdmission.Plan(
            snapshot,
            0,
            pageSize,
            context.PageCodec,
            CancellationToken.None).Value!;
        var final = context.PageAdmission.Plan(
            snapshot,
            pageSize,
            pageSize,
            context.PageCodec,
            CancellationToken.None).Value!;

        first.Items.Count.ShouldBe(pageSize);
        first.NextCursor.ShouldNotBeNull();
        final.Items.Count.ShouldBe(1);
        final.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, -1, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, 201)]
    public void SharedPageAdmissionRejectsEveryInvalidPositionAndPageSize(
        int itemCount,
        int position,
        int pageSize)
    {
        using var context = LifecycleContext.Create();
        var snapshot = Draft(itemCount).CreateSnapshot(Now.AddMinutes(10));

        var planned = context.PageAdmission.Plan(
            snapshot,
            position,
            pageSize,
            context.PageCodec,
            CancellationToken.None);

        planned.Error!.Code.ShouldBe(QueryFailureCode.InvalidInput);
    }

    [Fact]
    public void SharedPageAdmissionObservesCancellationDuringItemPlanning()
    {
        using var context = LifecycleContext.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var snapshot = Draft(1).CreateSnapshot(Now.AddMinutes(10));

        Should.Throw<OperationCanceledException>(() => context.PageAdmission.Plan(
            snapshot,
            0,
            1,
            context.PageCodec,
            cancellation.Token));
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
            CalendarQueryPageAdmission pageAdmission,
            CalendarQuerySnapshotPublication publication,
            CalendarQuerySnapshotReplay replay,
            CalendarEntityQueryPageCodec pageCodec)
        {
            Store = store;
            Reader = reader;
            Issuer = issuer;
            _authenticator = authenticator;
            PageAdmission = pageAdmission;
            Publication = publication;
            Replay = replay;
            PageCodec = pageCodec;
        }

        internal CalendarQuerySnapshotStore Store { get; }

        internal CalendarQuerySnapshotReader Reader { get; }

        internal CalendarQueryCursorIssuer Issuer { get; }

        internal CalendarQueryPageAdmission PageAdmission { get; }

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
            var pageAdmission = new CalendarQueryPageAdmission(issuer);
            return new LifecycleContext(
                store,
                reader,
                issuer,
                authenticator,
                pageAdmission,
                new CalendarQuerySnapshotPublication(new CalendarQueryPolicy(time), writer, pageAdmission),
                new CalendarQuerySnapshotReplay(authenticator, reader, pageAdmission),
                new CalendarEntityQueryPageCodec(workCounter));
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
