using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarOperationProgressTests
{
    [Fact]
    public void SnapshotKeepsAbsentAndObservedOperationFactsDistinct()
    {
        var state = CalendarOperationProgress.CreateState();

        state.Telemetry.MutationState.HasValue.ShouldBeFalse();
        state.Telemetry.Move.Dispatch.HasValue.ShouldBeFalse();
        state.Telemetry.Move.Collision.HasValue.ShouldBeFalse();
        state.Telemetry.Move.Reconciliation.HasValue.ShouldBeFalse();

        using (CalendarOperationProgress.Attach(state))
        {
            CalendarOperationProgress.ObserveMutationState(CalendarMutationState.Committed);
            CalendarOperationProgress.SetMoveDispatch(
                CalendarMoveDispatchClassification.PossiblyDispatched);
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.None);
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.ObservationUnavailable);
        }

        state.Telemetry.MutationState.Value.ShouldBe(CalendarMutationState.Committed);
        state.Telemetry.Move.Dispatch.Value.ShouldBe(
            CalendarMoveDispatchClassification.PossiblyDispatched);
        state.Telemetry.Move.Collision.Value.ShouldBe(CalendarMoveCollisionClassification.None);
        state.Telemetry.Move.Reconciliation.Value.ShouldBe(
            CalendarMoveReconciliationClassification.ObservationUnavailable);
    }

    [Fact]
    public void UnspecifiedMoveFactsRemainAbsentInsteadOfBecomingExportableStates()
    {
        var state = CalendarOperationProgress.CreateState();

        using (CalendarOperationProgress.Attach(state))
        {
            CalendarOperationProgress.SetMoveDispatch(CalendarMoveDispatchClassification.Unspecified);
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.Unspecified);
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.Unspecified);
        }

        state.Telemetry.Move.Dispatch.HasValue.ShouldBeFalse();
        state.Telemetry.Move.Collision.HasValue.ShouldBeFalse();
        state.Telemetry.Move.Reconciliation.HasValue.ShouldBeFalse();
    }
}
