using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarQuerySnapshotStore(TimeProvider timeProvider) : IDisposable
{
    internal const int MaximumSnapshots = 16;
    internal const long MaximumRetainedBytes = 128L * 1024 * 1024;
    private readonly Dictionary<Guid, SnapshotEntry> _snapshots = [];
    private readonly Dictionary<CalendarQuerySnapshotLease, DateTimeOffset> _reservations = [];
    private readonly object _gate = new();
    private bool _disposed;
    private int _reservationCount;
    private long _retainedBytes;

    internal CalendarQueryStoreAdmission TryReserve(CalendarQuerySnapshot snapshot)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RemoveExpired(timeProvider.GetUtcNow());
            if (_snapshots.Count + _reservationCount >= MaximumSnapshots
                || _retainedBytes + snapshot.RetainedBytes > MaximumRetainedBytes)
            {
                return CalendarQueryStoreAdmission.Busy(RetryAfterMilliseconds());
            }

            _reservationCount++;
            _retainedBytes += snapshot.RetainedBytes;
            var lease = new CalendarQuerySnapshotLease(this, snapshot);
            _reservations.Add(lease, snapshot.ExpiresAt);
            return CalendarQueryStoreAdmission.Accepted(lease);
        }
    }

    internal bool Commit(CalendarQuerySnapshotLease lease, CalendarQuerySnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            ITimer? timer = null;
            var leaseCompleted = false;
            try
            {
                if (_snapshots.ContainsKey(snapshot.Id))
                    return false;
                timer = timeProvider.CreateTimer(
                    static state => ((ExpiryState)state!).Expire(),
                    new ExpiryState(this, snapshot.Id),
                    snapshot.ExpiresAt - timeProvider.GetUtcNow(),
                    Timeout.InfiniteTimeSpan);
                if (!lease.TryComplete())
                    return false;
                leaseCompleted = true;
                _snapshots.Add(snapshot.Id, new SnapshotEntry(snapshot, timer));
                _reservations.Remove(lease);
                _reservationCount--;
                return true;
            }
            catch
            {
                if (leaseCompleted)
                {
                    _reservationCount--;
                    _retainedBytes -= snapshot.RetainedBytes;
                    _reservations.Remove(lease);
                }
                return false;
            }
            finally
            {
                if (!_snapshots.ContainsKey(snapshot.Id))
                    timer?.Dispose();
            }
        }
    }

    internal void Release(CalendarQuerySnapshotLease lease, long retainedBytes)
    {
        lock (_gate)
        {
            if (_disposed || !lease.TryComplete())
                return;
            _reservationCount--;
            _retainedBytes -= retainedBytes;
            _reservations.Remove(lease);
        }
    }

    internal CalendarQuerySnapshot? Get(Guid id)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;
            RemoveExpired(timeProvider.GetUtcNow());
            return _snapshots.GetValueOrDefault(id)?.Snapshot;
        }
    }

    internal int ActiveSnapshotCount
    {
        get
        {
            lock (_gate)
                return _snapshots.Count;
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
                return _retainedBytes;
        }
    }

    internal int ActiveReservationCount
    {
        get
        {
            lock (_gate)
                return _reservationCount;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _snapshots.Values)
                entry.Timer.Dispose();
            _snapshots.Clear();
            _reservations.Clear();
            _reservationCount = 0;
            _retainedBytes = 0;
        }
    }

    private void Expire(Guid id)
    {
        lock (_gate)
        {
            if (!_disposed)
                Remove(id);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var id in _snapshots.Values
                     .Where(entry => entry.Snapshot.ExpiresAt <= now)
                     .Select(entry => entry.Snapshot.Id)
                     .ToArray())
        {
            Remove(id);
        }
    }

    private void Remove(Guid id)
    {
        if (!_snapshots.Remove(id, out var entry))
            return;
        entry.Timer.Dispose();
        _retainedBytes -= entry.Snapshot.RetainedBytes;
    }

    private int RetryAfterMilliseconds()
    {
        var committedExpiry = _snapshots.Values.Select(entry => entry.Snapshot.ExpiresAt);
        var nearest = committedExpiry.Concat(_reservations.Values).Min() - timeProvider.GetUtcNow();
        return (int)Math.Clamp(Math.Ceiling(nearest.TotalMilliseconds), 1, 600_000);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record SnapshotEntry(CalendarQuerySnapshot Snapshot, ITimer Timer);

    private sealed record ExpiryState(CalendarQuerySnapshotStore Store, Guid Id)
    {
        internal void Expire() => Store.Expire(Id);
    }
}

internal sealed record CalendarQuerySnapshot(
    Guid Id,
    DateTimeOffset ExpiresAt,
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8 = default,
    ReadOnlyMemory<byte> AdditionalContextUtf8 = default);

internal sealed record StoredCalendarEntityQueryItem(ReadOnlyMemory<byte> JsonUtf8)
{
    internal int JsonByteCount => JsonUtf8.Length;
}

internal sealed record CalendarQueryStoreAdmission(
    bool IsAccepted,
    CalendarQuerySnapshotLease? Lease,
    int? RetryAfterMs)
{
    internal static CalendarQueryStoreAdmission Accepted(CalendarQuerySnapshotLease lease) => new(true, lease, null);

    internal static CalendarQueryStoreAdmission Busy(int retryAfterMs) => new(false, null, retryAfterMs);
}

internal sealed class CalendarQuerySnapshotLease(
    CalendarQuerySnapshotStore store,
    CalendarQuerySnapshot snapshot) : IDisposable
{
    private int _completed;

    internal bool Commit() => store.Commit(this, snapshot);

    internal bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;

    public void Dispose() => store.Release(this, snapshot.RetainedBytes);
}

internal sealed class CalendarQuerySnapshotReader(CalendarQuerySnapshotStore store)
{
    internal CalendarQuerySnapshot? Get(Guid id) => store.Get(id);
}

internal sealed class CalendarQuerySnapshotWriter(CalendarQuerySnapshotStore store)
{
    internal CalendarQueryStoreAdmission TryReserve(CalendarQuerySnapshot snapshot) => store.TryReserve(snapshot);
}
