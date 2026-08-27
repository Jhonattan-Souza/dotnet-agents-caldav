using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarOperationProgressTests
{
    [Fact]
    public void PublicMoveSnapshotCompatibilityRemainsAvailable()
    {
        var state = CalendarOperationProgress.CreateState();

        using (CalendarOperationProgress.Attach(state))
        {
            CalendarOperationProgress.SetMoveNotAttempted(CalendarMoveCollisionClassification.SourceRevision);

            CalendarOperationProgress.CurrentMoveTelemetry.ShouldBe(new CalendarMoveTelemetrySnapshot(
                CalendarMoveDispatchClassification.NotAttempted,
                CalendarMoveCollisionClassification.SourceRevision,
                CalendarMoveReconciliationClassification.NotRun));
        }
    }

    [Theory]
    [InlineData(false, CalendarMoveDispatchClassification.Dispatched)]
    [InlineData(true, CalendarMoveDispatchClassification.PossiblyDispatched)]
    public void DispatchedMoveStateChangesReconciliationAtomically(
        bool possiblyDispatched,
        CalendarMoveDispatchClassification expectedDispatch)
    {
        var state = CalendarOperationProgress.CreateState();

        using (CalendarOperationProgress.Attach(state))
        {
            CalendarOperationProgress.SetMoveDispatched(possiblyDispatched);
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.ObservationUnavailable);
        }

        state.MoveState.ShouldBeOfType(possiblyDispatched
            ? typeof(CalendarMoveTelemetryState.PossiblyDispatched)
            : typeof(CalendarMoveTelemetryState.Dispatched));
        state.MoveTelemetry.ShouldBe(new CalendarMoveTelemetrySnapshot(
            expectedDispatch,
            CalendarMoveCollisionClassification.None,
            CalendarMoveReconciliationClassification.ObservationUnavailable));
    }

    [Fact]
    public void RejectedMoveCannotAcquireAReconciliationState()
    {
        var state = CalendarOperationProgress.CreateState();

        using (CalendarOperationProgress.Attach(state))
        {
            CalendarOperationProgress.SetMoveRejected(CalendarMoveCollisionClassification.Uid);
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.FaithfulDestinationSourceAbsent);
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.DestinationHref);
        }

        state.MoveState.ShouldBe(new CalendarMoveTelemetryState.Rejected(
            CalendarMoveCollisionClassification.Uid));
        state.MoveTelemetry.ShouldBe(new CalendarMoveTelemetrySnapshot(
            CalendarMoveDispatchClassification.Rejected,
            CalendarMoveCollisionClassification.Uid,
            CalendarMoveReconciliationClassification.NotRun));
    }
}
