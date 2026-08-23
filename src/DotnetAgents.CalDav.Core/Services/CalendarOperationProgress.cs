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

    internal static void SetMoveDispatch(CalendarMoveDispatchClassification classification) =>
        CurrentState.Value?.SetMoveDispatch(classification);

    internal static void SetMoveCollision(CalendarMoveCollisionClassification classification) =>
        CurrentState.Value?.SetMoveCollision(classification);

    internal static void SetMoveReconciliation(CalendarMoveReconciliationClassification classification) =>
        CurrentState.Value?.SetMoveReconciliation(classification);

    public static CalendarMoveTelemetrySnapshot? CurrentMoveTelemetry =>
        CurrentState.Value?.MoveTelemetry;

    public sealed class State(CalendarOperationPhase phase, Action<CalendarOperationPhase>? phaseObserver = null)
    {
        private int _phase = (int)phase;
        private int _moveDispatch;
        private int _moveCollision;
        private int _moveReconciliation;

        public string PhaseName => ((CalendarOperationPhase)Volatile.Read(ref _phase)).ToString().ToLowerInvariant();

        public CalendarMoveTelemetrySnapshot MoveTelemetry => new(
            (CalendarMoveDispatchClassification)Volatile.Read(ref _moveDispatch),
            (CalendarMoveCollisionClassification)Volatile.Read(ref _moveCollision),
            (CalendarMoveReconciliationClassification)Volatile.Read(ref _moveReconciliation));

        internal void SetMoveDispatch(CalendarMoveDispatchClassification classification) =>
            Volatile.Write(ref _moveDispatch, (int)classification);

        internal void SetMoveCollision(CalendarMoveCollisionClassification classification) =>
            Volatile.Write(ref _moveCollision, (int)classification);

        internal void SetMoveReconciliation(CalendarMoveReconciliationClassification classification) =>
            Volatile.Write(ref _moveReconciliation, (int)classification);

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

public readonly record struct CalendarMoveTelemetrySnapshot(
    CalendarMoveDispatchClassification Dispatch,
    CalendarMoveCollisionClassification Collision,
    CalendarMoveReconciliationClassification Reconciliation);

public enum CalendarMoveDispatchClassification
{
    Unspecified,
    NotAttempted,
    Rejected,
    Dispatched,
    PossiblyDispatched
}

public enum CalendarMoveCollisionClassification
{
    Unspecified,
    None,
    SourceRevision,
    DestinationHref,
    Uid,
    Unclassified
}

public enum CalendarMoveReconciliationClassification
{
    Unspecified,
    NotRun,
    FaithfulDestinationSourceAbsent,
    DivergentDestinationSourceAbsent,
    ObservationUnavailable,
    UnchangedSourceDestinationAbsent,
    Indeterminate
}

public enum CalendarOperationPhase
{
    Discovery,
    Fetch,
    Filter,
    Expand,
    Reconcile
}
