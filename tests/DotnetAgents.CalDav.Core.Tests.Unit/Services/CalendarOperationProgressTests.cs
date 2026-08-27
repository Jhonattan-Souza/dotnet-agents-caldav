using System.Reflection;
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
    [InlineData(CalendarMoveDispatchClassification.Dispatched)]
    [InlineData(CalendarMoveDispatchClassification.PossiblyDispatched)]
    public void DispatchedMoveStateChangesReconciliationAtomically(
        CalendarMoveDispatchClassification dispatch)
    {
        var state = CalendarOperationProgress.CreateState();

        using (CalendarOperationProgress.Attach(state))
        {
            if (dispatch == CalendarMoveDispatchClassification.PossiblyDispatched)
                CalendarOperationProgress.SetMovePossiblyDispatched();
            else
                CalendarOperationProgress.SetMoveDispatched();
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.ObservationUnavailable);
        }

        state.MoveState.ShouldBeOfType(dispatch == CalendarMoveDispatchClassification.PossiblyDispatched
            ? typeof(CalendarMoveTelemetryState.PossiblyDispatched)
            : typeof(CalendarMoveTelemetryState.Dispatched));
        state.MoveTelemetry.ShouldBe(new CalendarMoveTelemetrySnapshot(
            dispatch,
            CalendarMoveCollisionClassification.None,
            CalendarMoveReconciliationClassification.ObservationUnavailable));
    }

    [Fact]
    public void MoveDispatchBoundaryExposesOnlyClosedParameterlessTransitions()
    {
        var setters = typeof(CalendarOperationProgress)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name is "SetMoveDispatched" or "SetMovePossiblyDispatched")
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();

        setters.Select(method => method.Name).ShouldBe(
        [
            "SetMoveDispatched",
            "SetMovePossiblyDispatched"
        ]);
        setters.ShouldAllBe(method => method.GetParameters().Length == 0);
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
