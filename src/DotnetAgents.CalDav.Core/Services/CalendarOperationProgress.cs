namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Tracks the current safe aggregate phase for one asynchronous Calendar operation.</summary>
public static class CalendarOperationProgress
{
    private static readonly AsyncLocal<State?> CurrentState = new();

    public static State CreateState(Action<CalendarOperationPhase>? phaseObserver = null) =>
        new(CalendarOperationPhase.Discovery, phaseObserver);

    public static ProgressScope Attach(State state)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = state;
        return new ProgressScope(previous, state);
    }

    internal static void SetPhase(CalendarOperationPhase phase) => CurrentState.Value?.AdvanceTo(phase);

    public sealed class State(CalendarOperationPhase phase, Action<CalendarOperationPhase>? phaseObserver = null)
    {
        private int _phase = (int)phase;

        public string PhaseName => ((CalendarOperationPhase)Volatile.Read(ref _phase)).ToString().ToLowerInvariant();

        public void AdvanceTo(CalendarOperationPhase next)
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

    public sealed class ProgressScope : IDisposable
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

public enum CalendarOperationPhase
{
    Discovery,
    Fetch,
    Filter,
    Expand,
    Reconcile
}
