using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQuerySnapshotStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SlotReservationsProveBelowAtAndAboveSixteenIndependently()
    {
        using var store = new CalendarQuerySnapshotStore(new FixedTimeProvider());
        var leases = new List<CalendarQuerySnapshotLease>();
        for (var index = 0; index < CalendarQuerySnapshotStore.MaximumSnapshots; index++)
        {
            var admitted = store.TryReserve(Snapshot(Guid.NewGuid(), 1));
            admitted.IsAccepted.ShouldBeTrue();
            leases.Add(admitted.Lease!);
            store.ActiveReservationCount.ShouldBe(index + 1);
        }

        store.ActiveReservationCount.ShouldBe(CalendarQuerySnapshotStore.MaximumSnapshots);
        store.RetainedBytes.ShouldBe(CalendarQuerySnapshotStore.MaximumSnapshots);
        var refused = store.TryReserve(Snapshot(Guid.NewGuid(), 1));
        refused.IsAccepted.ShouldBeFalse();
        refused.RetryAfterMs.ShouldBe(600_000);

        foreach (var lease in leases)
            lease.Dispose();
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public void ByteReservationsProveBelowAtAndAboveOneHundredTwentyEightMiBIndependently()
    {
        using var store = new CalendarQuerySnapshotStore(new FixedTimeProvider());
        var leases = new List<CalendarQuerySnapshotLease>();
        for (var index = 0; index < 4; index++)
        {
            var admitted = store.TryReserve(Snapshot(Guid.NewGuid(), 32L * 1024 * 1024));
            admitted.IsAccepted.ShouldBeTrue();
            leases.Add(admitted.Lease!);
            store.RetainedBytes.ShouldBe((index + 1L) * 32 * 1024 * 1024);
        }

        store.ActiveReservationCount.ShouldBe(4);
        store.RetainedBytes.ShouldBe(CalendarQuerySnapshotStore.MaximumRetainedBytes);
        var refused = store.TryReserve(Snapshot(Guid.NewGuid(), 1));
        refused.IsAccepted.ShouldBeFalse();
        refused.RetryAfterMs.ShouldBe(600_000);

        store.Dispose();
        store.ActiveSnapshotCount.ShouldBe(0);
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(0);
        foreach (var lease in leases)
            lease.Dispose();
    }

    [Fact]
    public void DuplicatePublicationRollsBackTheSecondReservation()
    {
        using var store = new CalendarQuerySnapshotStore(TimeProvider.System);
        var id = Guid.NewGuid();
        var first = store.TryReserve(Snapshot(id, 7));
        first.Lease!.Commit().ShouldBeTrue();

        var duplicate = store.TryReserve(Snapshot(id, 11));
        duplicate.Lease!.Commit().ShouldBeFalse();
        duplicate.Lease.Dispose();

        store.ActiveSnapshotCount.ShouldBe(1);
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(7);
    }

    [Fact]
    public void TimerFailureLeavesNoPublishedOrReservedState()
    {
        using var store = new CalendarQuerySnapshotStore(new ThrowingTimerTimeProvider());
        var admitted = store.TryReserve(Snapshot(Guid.NewGuid(), 13));

        admitted.Lease!.Commit().ShouldBeFalse();
        admitted.Lease.Dispose();

        store.ActiveSnapshotCount.ShouldBe(0);
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(0);
    }

    private static CalendarQuerySnapshot Snapshot(Guid id, long retainedBytes) => new(
        id,
        Now.AddMinutes(10),
        ImmutableArray<StoredCalendarEntityQueryItem>.Empty,
        "[]"u8.ToArray(),
        retainedBytes);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
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
