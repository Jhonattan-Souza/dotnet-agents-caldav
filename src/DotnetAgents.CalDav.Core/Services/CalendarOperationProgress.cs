using DotnetAgents.CalDav.Core.Models;

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

    internal static void ObserveMutationState(CalendarMutationState mutationState) =>
        CurrentState.Value?.ObserveMutationState(mutationState);

    internal static void SetMoveDispatch(CalendarMoveDispatchClassification classification) =>
        CurrentState.Value?.SetMoveDispatch(classification);

    internal static void SetMoveCollision(CalendarMoveCollisionClassification classification) =>
        CurrentState.Value?.SetMoveCollision(classification);

    internal static void SetMoveReconciliation(CalendarMoveReconciliationClassification classification) =>
        CurrentState.Value?.SetMoveReconciliation(classification);

    internal static CalendarOperationTelemetrySnapshot? CurrentTelemetry =>
        CurrentState.Value?.Telemetry;

    public sealed class State(CalendarOperationPhase phase, Action<CalendarOperationPhase>? phaseObserver = null)
    {
        private int _phase = (int)phase;
        private int _mutationState;
        private int _moveDispatch;
        private int _moveCollision;
        private int _moveReconciliation;

        public string PhaseName => ((CalendarOperationPhase)Volatile.Read(ref _phase)).ToString().ToLowerInvariant();

        internal CalendarOperationTelemetrySnapshot Telemetry => new(
            CalendarTelemetryFact<CalendarMutationState>.FromStorage(
                Volatile.Read(ref _mutationState)),
            new CalendarMoveTelemetrySnapshot(
                CalendarTelemetryFact<CalendarMoveDispatchClassification>.FromStorage(
                    Volatile.Read(ref _moveDispatch)),
                CalendarTelemetryFact<CalendarMoveCollisionClassification>.FromStorage(
                    Volatile.Read(ref _moveCollision)),
                CalendarTelemetryFact<CalendarMoveReconciliationClassification>.FromStorage(
                    Volatile.Read(ref _moveReconciliation))));

        internal void ObserveMutationState(CalendarMutationState mutationState) =>
            Volatile.Write(ref _mutationState, CalendarTelemetryFact<CalendarMutationState>.ToStorage(mutationState));

        internal void SetMoveDispatch(CalendarMoveDispatchClassification classification) =>
            Volatile.Write(
                ref _moveDispatch,
                classification == CalendarMoveDispatchClassification.Unspecified
                    ? 0
                    : CalendarTelemetryFact<CalendarMoveDispatchClassification>.ToStorage(classification));

        internal void SetMoveCollision(CalendarMoveCollisionClassification classification) =>
            Volatile.Write(
                ref _moveCollision,
                classification == CalendarMoveCollisionClassification.Unspecified
                    ? 0
                    : CalendarTelemetryFact<CalendarMoveCollisionClassification>.ToStorage(classification));

        internal void SetMoveReconciliation(CalendarMoveReconciliationClassification classification) =>
            Volatile.Write(
                ref _moveReconciliation,
                classification == CalendarMoveReconciliationClassification.Unspecified
                    ? 0
                    : CalendarTelemetryFact<CalendarMoveReconciliationClassification>.ToStorage(classification));

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

internal readonly record struct CalendarOperationTelemetrySnapshot(
    CalendarTelemetryFact<CalendarMutationState> MutationState,
    CalendarMoveTelemetrySnapshot Move)
{
    internal static CalendarOperationTelemetrySnapshot WithMutationState(
        CalendarMutationState mutationState) => new(
            CalendarTelemetryFact<CalendarMutationState>.FromValue(mutationState),
            default);
}

internal readonly record struct CalendarMoveTelemetrySnapshot(
    CalendarTelemetryFact<CalendarMoveDispatchClassification> Dispatch,
    CalendarTelemetryFact<CalendarMoveCollisionClassification> Collision,
    CalendarTelemetryFact<CalendarMoveReconciliationClassification> Reconciliation);

internal readonly record struct CalendarTelemetryFact<T> where T : struct, Enum
{
    private readonly T _value;

    private CalendarTelemetryFact(T value)
    {
        _value = value;
        HasValue = true;
    }

    internal bool HasValue { get; }

    internal T Value => HasValue
        ? _value
        : throw new InvalidOperationException("An absent operation fact has no value.");

    internal static CalendarTelemetryFact<T> FromStorage(int stored) => stored == 0
        ? default
        : new CalendarTelemetryFact<T>((T)Enum.ToObject(typeof(T), stored - 1));

    internal static CalendarTelemetryFact<T> FromValue(T value) => new(value);

    internal static int ToStorage(T value) => Convert.ToInt32(value) + 1;
}

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
