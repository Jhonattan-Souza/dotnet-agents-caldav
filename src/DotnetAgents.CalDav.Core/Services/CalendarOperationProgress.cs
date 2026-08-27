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

    internal static void SetMoveNotAttempted(
        CalendarMoveCollisionClassification collision = CalendarMoveCollisionClassification.Unspecified) =>
        CurrentState.Value?.SetMoveState(new CalendarMoveTelemetryState.NotAttempted(collision));

    internal static void SetMoveRejected(CalendarMoveCollisionClassification collision) =>
        CurrentState.Value?.SetMoveState(new CalendarMoveTelemetryState.Rejected(collision));

    internal static void SetMoveDispatched() => CurrentState.Value?.SetMoveState(
        new CalendarMoveTelemetryState.Dispatched(CalendarMoveReconciliationClassification.NotRun));

    internal static void SetMovePossiblyDispatched() => CurrentState.Value?.SetMoveState(
        new CalendarMoveTelemetryState.PossiblyDispatched(CalendarMoveReconciliationClassification.NotRun));

    internal static void SetMoveCollision(CalendarMoveCollisionClassification collision) =>
        CurrentState.Value?.SetNotAttemptedCollision(collision);

    internal static void SetMoveReconciliation(CalendarMoveReconciliationClassification classification) =>
        CurrentState.Value?.SetMoveReconciliation(classification);

    public static CalendarMoveTelemetrySnapshot? CurrentMoveTelemetry =>
        CurrentState.Value?.MoveTelemetry;

    public sealed class State(CalendarOperationPhase phase, Action<CalendarOperationPhase>? phaseObserver = null)
    {
        private int _phase = (int)phase;
        private CalendarMoveTelemetryState _moveState = CalendarMoveTelemetryState.None.Instance;

        public string PhaseName => ((CalendarOperationPhase)Volatile.Read(ref _phase)).ToString().ToLowerInvariant();

        public CalendarMoveTelemetrySnapshot MoveTelemetry => Volatile.Read(ref _moveState).ToSnapshot();

        internal CalendarMoveTelemetryState MoveState => Volatile.Read(ref _moveState);

        internal void SetMoveState(CalendarMoveTelemetryState state) =>
            Volatile.Write(ref _moveState, state);

        internal void SetNotAttemptedCollision(CalendarMoveCollisionClassification collision)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _moveState);
                if (observed is not CalendarMoveTelemetryState.NotAttempted)
                    return;
                var updated = new CalendarMoveTelemetryState.NotAttempted(collision);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _moveState, updated, observed), observed))
                    return;
            }
        }

        internal void SetMoveReconciliation(CalendarMoveReconciliationClassification classification)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _moveState);
                CalendarMoveTelemetryState? updated = observed switch
                {
                    CalendarMoveTelemetryState.Dispatched =>
                        new CalendarMoveTelemetryState.Dispatched(classification),
                    CalendarMoveTelemetryState.PossiblyDispatched =>
                        new CalendarMoveTelemetryState.PossiblyDispatched(classification),
                    _ => null
                };
                if (updated is null)
                    return;
                if (ReferenceEquals(Interlocked.CompareExchange(ref _moveState, updated, observed), observed))
                    return;
            }
        }

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

internal abstract record CalendarMoveTelemetryState
{
    private CalendarMoveTelemetryState() { }

    internal abstract CalendarMoveTelemetrySnapshot ToSnapshot();

    internal sealed record None : CalendarMoveTelemetryState
    {
        internal static None Instance { get; } = new();

        private None() { }

        internal override CalendarMoveTelemetrySnapshot ToSnapshot() => default;
    }

    internal sealed record NotAttempted(
        CalendarMoveCollisionClassification Collision) : CalendarMoveTelemetryState
    {
        internal override CalendarMoveTelemetrySnapshot ToSnapshot() => new(
            CalendarMoveDispatchClassification.NotAttempted,
            Collision,
            CalendarMoveReconciliationClassification.NotRun);
    }

    internal sealed record Rejected(
        CalendarMoveCollisionClassification Collision) : CalendarMoveTelemetryState
    {
        internal override CalendarMoveTelemetrySnapshot ToSnapshot() => new(
            CalendarMoveDispatchClassification.Rejected,
            Collision,
            CalendarMoveReconciliationClassification.NotRun);
    }

    internal sealed record Dispatched(
        CalendarMoveReconciliationClassification Reconciliation) : CalendarMoveTelemetryState
    {
        internal override CalendarMoveTelemetrySnapshot ToSnapshot() => new(
            CalendarMoveDispatchClassification.Dispatched,
            CalendarMoveCollisionClassification.None,
            Reconciliation);
    }

    internal sealed record PossiblyDispatched(
        CalendarMoveReconciliationClassification Reconciliation) : CalendarMoveTelemetryState
    {
        internal override CalendarMoveTelemetrySnapshot ToSnapshot() => new(
            CalendarMoveDispatchClassification.PossiblyDispatched,
            CalendarMoveCollisionClassification.None,
            Reconciliation);
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
