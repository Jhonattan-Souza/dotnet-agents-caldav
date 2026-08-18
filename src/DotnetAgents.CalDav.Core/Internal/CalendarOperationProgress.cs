namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Tracks the current safe aggregate phase for one asynchronous Calendar operation.</summary>
internal static class CalendarOperationProgress
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    internal static State CreateState(Action<CalendarOperationPhase>? phaseObserver = null) =>
        new(CalendarOperationPhase.Admission, phaseObserver);

    internal static ProgressScope Attach(State state)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = state;
        return new ProgressScope(previous, state);
    }

    internal static void SetPhase(CalendarOperationPhase phase) => CurrentState.Value?.AdvanceTo(phase);

    internal sealed class State(CalendarOperationPhase phase, Action<CalendarOperationPhase>? phaseObserver = null)
    {
        private int _phase = (int)phase;

        internal string PhaseName => ((CalendarOperationPhase)Volatile.Read(ref _phase)).ToString().ToLowerInvariant();

        internal void AdvanceTo(CalendarOperationPhase next)
        {
            var candidate = (int)next;
            while (true)
            {
                var observed = Volatile.Read(ref _phase);
                if (candidate <= observed)
                    return;
                if (Interlocked.CompareExchange(ref _phase, candidate, observed) == observed)
                {
                    phaseObserver?.Invoke(next);
                    return;
                }
            }
        }
    }

    internal sealed class ProgressScope : IDisposable
    {
        private readonly State? _previous;
        private bool _disposed;

        private readonly State _state;

        internal ProgressScope(State? previous, State state)
        {
            _previous = previous;
            _state = state;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (ReferenceEquals(CurrentState.Value, _state))
                CurrentState.Value = _previous;
            _disposed = true;
        }
    }
}

internal enum CalendarOperationPhase
{
    Admission,
    Discovery,
    Fetch,
    Filter,
    Expand,
    Reconcile
}
