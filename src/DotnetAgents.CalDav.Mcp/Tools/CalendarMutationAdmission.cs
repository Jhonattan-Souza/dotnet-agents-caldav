namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Bounds and orders semantic mutations for the single configured CalDAV origin.</summary>
internal sealed class CalendarMutationAdmission(TimeProvider timeProvider)
{
    internal const int MaximumQueuedMutations = 16;
    internal const int RetryAfterMilliseconds = 2_000;
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(RetryAfterMilliseconds);

    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private bool _active;

    public async ValueTask<Lease?> AcquireAsync(CancellationToken cancellationToken)
    {
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            if (!_active && _waiters.Count == 0)
            {
                _active = true;
                return new Lease(this);
            }

            if (_waiters.Count >= MaximumQueuedMutations)
                return null;

            node = _waiters.AddLast(new Waiter());
        }

        try
        {
            return await node.Value.Source.Task.WaitAsync(MaximumWait, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    _waiters.Remove(node);
                    return null;
                }
            }

            return await node.Value.Source.Task;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (node.List is not null)
                {
                    _waiters.Remove(node);
                    throw;
                }
            }

            var racedLease = await node.Value.Source.Task;
            racedLease.Dispose();
            throw;
        }
    }

    private void Release()
    {
        Waiter? next = null;
        lock (_gate)
        {
            if (_waiters.First is null)
            {
                _active = false;
            }
            else
            {
                next = _waiters.First.Value;
                _waiters.RemoveFirst();
            }
        }

        next?.Source.TrySetResult(new Lease(this));
    }

    internal sealed class Lease(CalendarMutationAdmission owner) : IDisposable
    {
        private CalendarMutationAdmission? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<Lease> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
