using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal enum CalendarMoveFidelityMode
{
    Semantic,
    Exact
}

internal sealed record CalendarReviewedMovePreparation(
    CalendarResourceRevisionReference Revision,
    CalendarResourceSnapshot Source,
    string SourceCalendarHref,
    string DestinationCalendarHref,
    string DestinationHref,
    CalendarMoveFidelityMode FidelityMode);

internal sealed class CalendarReviewedMovePlan(CalendarReviewedMovePreparation preparation)
{
    private CalendarReviewedMovePreparation? _preparation = preparation;

    internal CalendarReviewedMovePreparation? Consume() =>
        Interlocked.Exchange(ref _preparation, null);
}

/// <summary>Consumes one reviewed plan, dispatches one MOVE, and owns shared bilateral truth.</summary>
internal sealed class CalendarMoveDispatcher(
    ICalendarMoveTransport transport,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarResourceMoveResult> DispatchAsync(
        CalendarReviewedMovePlan plan,
        CancellationToken cancellationToken)
    {
        var preparation = plan.Consume();
        if (preparation is null)
            return Failure(CalendarResourceMoveCode.InvalidInput);

        cancellationToken.ThrowIfCancellationRequested();
        CalendarResourceMoveDispatchResult dispatch;
        try
        {
            dispatch = await transport.DispatchAsync(
                preparation.SourceCalendarHref,
                preparation.DestinationCalendarHref,
                new CalendarResourceMoveDispatchRequest(
                    preparation.Revision.Href,
                    preparation.DestinationHref,
                    preparation.Revision.EntityTag),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            dispatch = new CalendarResourceMoveDispatchResult(
                CalendarResourceMoveDispatchCode.PossiblyDispatched);
        }
        dispatch ??= new CalendarResourceMoveDispatchResult(
            CalendarResourceMoveDispatchCode.PossiblyDispatched);
        RecordDispatch(dispatch);
        return await CompleteDispatchAsync(preparation, dispatch).ConfigureAwait(false);
    }

    private async Task<CalendarResourceMoveResult> CompleteDispatchAsync(
        CalendarReviewedMovePreparation preparation,
        CalendarResourceMoveDispatchResult dispatch)
    {
        if (dispatch.Code is not (CalendarResourceMoveDispatchCode.Dispatched
            or CalendarResourceMoveDispatchCode.PossiblyDispatched))
        {
            return FromDispatchFailure(dispatch);
        }

        var observation = await ObserveAfterDispatchAsync(preparation).ConfigureAwait(false);
        return dispatch.Code == CalendarResourceMoveDispatchCode.Dispatched
            ? ClassifyDispatched(preparation, observation)
            : ClassifyPossiblyDispatched(preparation, observation);
    }

    private async Task<MoveObservation> ObserveAfterDispatchAsync(
        CalendarReviewedMovePreparation preparation)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        var destinationTask = ObserveResourceAsync(
            preparation.DestinationCalendarHref,
            preparation.DestinationHref,
            verification.Token);
        var sourceTask = ObserveResourceAsync(
            preparation.SourceCalendarHref,
            preparation.Revision.Href,
            verification.Token);
        await Task.WhenAll(destinationTask, sourceTask).ConfigureAwait(false);
        return new MoveObservation(
            await destinationTask.ConfigureAwait(false),
            await sourceTask.ConfigureAwait(false));
    }

    private async Task<CalendarResourceRead?> ObserveResourceAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken)
    {
        try
        {
            return Attach(
                calendarHref,
                await transport.ObserveResourceAsync(
                    calendarHref,
                    resourceHref,
                    cancellationToken).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CalendarResourceMoveResult ClassifyDispatched(
        CalendarReviewedMovePreparation preparation,
        MoveObservation observation)
    {
        var verified = VerifiedMoveResult(preparation, observation, possiblyDispatched: false);
        if (verified is not null)
            return verified;
        if (HasUnavailableObservation(observation))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.ObservationUnavailable);
            return PostWrite(CalendarResourceMoveCode.CommittedButUnverified);
        }
        CalendarOperationProgress.SetMoveReconciliation(CalendarMoveReconciliationClassification.Indeterminate);
        return Unknown(observation.Source?.Snapshot);
    }

    private static CalendarResourceMoveResult ClassifyPossiblyDispatched(
        CalendarReviewedMovePreparation preparation,
        MoveObservation observation)
    {
        var verified = VerifiedMoveResult(preparation, observation, possiblyDispatched: true);
        if (verified is not null)
            return verified;
        if (IsSourceUnchangedAfterPossibleDispatch(preparation, observation))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.UnchangedSourceDestinationAbsent);
            return Rejected(
                CalendarResourceMoveCode.UpstreamUnavailable,
                observation.Source!.Snapshot,
                retryable: true);
        }
        CalendarOperationProgress.SetMoveReconciliation(ReconciliationObservation(observation));
        return Unknown(observation.Source?.Snapshot);
    }

    private static CalendarResourceMoveResult? VerifiedMoveResult(
        CalendarReviewedMovePreparation preparation,
        MoveObservation observation,
        bool possiblyDispatched)
    {
        if (observation.Destination?.Snapshot is not { } destination
            || observation.Source?.Code != CalendarResourceReadCode.NotFound)
        {
            return null;
        }
        if (IsFaithful(preparation, destination))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.FaithfulDestinationSourceAbsent);
            return CalendarResourceMoveResult.Success(destination);
        }
        CalendarOperationProgress.SetMoveReconciliation(
            CalendarMoveReconciliationClassification.DivergentDestinationSourceAbsent);
        return possiblyDispatched
            ? Unknown(destination)
            : PostWrite(CalendarResourceMoveCode.FidelityFailure, destination);
    }

    private static bool IsFaithful(
        CalendarReviewedMovePreparation preparation,
        CalendarResourceSnapshot destination) => preparation.FidelityMode switch
        {
            CalendarMoveFidelityMode.Semantic =>
                preparation.Source.SemanticMutationAvailable
                && destination.SemanticMutationAvailable
                && CalendarResourceMoveFidelity.IsCompleteMatch(preparation.Source, destination),
            CalendarMoveFidelityMode.Exact => preparation.Source.AuthoritativeUtf8.Span.SequenceEqual(
                destination.AuthoritativeUtf8.Span),
            _ => false
        };

    private static bool IsSourceUnchangedAfterPossibleDispatch(
        CalendarReviewedMovePreparation preparation,
        MoveObservation observation) =>
        observation.Destination?.Code == CalendarResourceReadCode.NotFound
        && IsSameRevision(preparation, observation.Source?.Snapshot);

    private static bool IsSameRevision(
        CalendarReviewedMovePreparation preparation,
        CalendarResourceSnapshot? snapshot)
    {
        if (snapshot is null
            || !string.Equals(snapshot.ResourceHref, preparation.Revision.Href, StringComparison.Ordinal)
            || !string.Equals(snapshot.EntityTag, preparation.Revision.EntityTag, StringComparison.Ordinal))
        {
            return false;
        }
        return preparation.FidelityMode switch
        {
            CalendarMoveFidelityMode.Semantic =>
                snapshot.SemanticMutationAvailable
                && string.Equals(
                    snapshot.Projection.EntityUid,
                    preparation.Revision.EntityUid,
                    StringComparison.Ordinal)
                && snapshot.Projection.Kind == (preparation.Revision.EntityKind == CalendarEntityKind.Event
                    ? CalendarResourceProjectionKind.Event
                    : CalendarResourceProjectionKind.Todo),
            CalendarMoveFidelityMode.Exact =>
                CalendarExactResourceValidator.TryValidate(snapshot.AuthoritativeUtf8.Span, out var identity)
                && string.Equals(identity.EntityUid, preparation.Revision.EntityUid, StringComparison.Ordinal)
                && identity.EntityKind == preparation.Revision.EntityKind,
            _ => false
        };
    }

    private static bool HasUnavailableObservation(MoveObservation observation) =>
        observation.Destination?.Code is not (CalendarResourceReadCode.Success or CalendarResourceReadCode.NotFound)
        || observation.Source?.Code is not (CalendarResourceReadCode.Success or CalendarResourceReadCode.NotFound);

    private static CalendarMoveReconciliationClassification ReconciliationObservation(MoveObservation observation)
    {
        if (HasUnavailableObservation(observation))
            return CalendarMoveReconciliationClassification.ObservationUnavailable;
        if (observation.Destination?.Snapshot is not null
            && observation.Source?.Code == CalendarResourceReadCode.NotFound)
        {
            return CalendarMoveReconciliationClassification.DivergentDestinationSourceAbsent;
        }
        return CalendarMoveReconciliationClassification.Indeterminate;
    }

    private static void RecordDispatch(CalendarResourceMoveDispatchResult dispatch)
    {
        var collision = dispatch.Code switch
        {
            CalendarResourceMoveDispatchCode.DestinationConflict => CalendarMoveCollisionClassification.DestinationHref,
            CalendarResourceMoveDispatchCode.Conflict => dispatch.CollisionKind switch
            {
                CalendarResourceMoveDispatchCollisionKind.DestinationHref => CalendarMoveCollisionClassification.DestinationHref,
                CalendarResourceMoveDispatchCollisionKind.Uid => CalendarMoveCollisionClassification.Uid,
                _ => CalendarMoveCollisionClassification.Unclassified
            },
            _ => CalendarMoveCollisionClassification.None
        };
        switch (dispatch.Code)
        {
            case CalendarResourceMoveDispatchCode.Dispatched:
                CalendarOperationProgress.SetMoveDispatched();
                break;
            case CalendarResourceMoveDispatchCode.PossiblyDispatched:
                CalendarOperationProgress.SetMovePossiblyDispatched();
                break;
            default:
                CalendarOperationProgress.SetMoveRejected(collision);
                break;
        }
    }

    private static CalendarResourceMoveResult FromDispatchFailure(
        CalendarResourceMoveDispatchResult dispatch) => Rejected(
        dispatch.Code switch
        {
            CalendarResourceMoveDispatchCode.NotFound => CalendarResourceMoveCode.NotFound,
            CalendarResourceMoveDispatchCode.Conflict => CalendarResourceMoveCode.Conflict,
            CalendarResourceMoveDispatchCode.DestinationConflict => CalendarResourceMoveCode.DestinationConflict,
            CalendarResourceMoveDispatchCode.InvalidInput => CalendarResourceMoveCode.InvalidInput,
            CalendarResourceMoveDispatchCode.UnsupportedCapability => CalendarResourceMoveCode.UnsupportedCapability,
            CalendarResourceMoveDispatchCode.PayloadTooLarge => CalendarResourceMoveCode.PayloadTooLarge,
            CalendarResourceMoveDispatchCode.UpstreamUnauthorized => CalendarResourceMoveCode.UpstreamUnauthorized,
            CalendarResourceMoveDispatchCode.UpstreamForbidden => CalendarResourceMoveCode.UpstreamForbidden,
            CalendarResourceMoveDispatchCode.UpstreamRateLimited => CalendarResourceMoveCode.UpstreamRateLimited,
            CalendarResourceMoveDispatchCode.UpstreamProtocolError => CalendarResourceMoveCode.UpstreamProtocolError,
            _ => CalendarResourceMoveCode.UpstreamUnavailable
        },
        retryAfterMilliseconds: dispatch.Code == CalendarResourceMoveDispatchCode.UpstreamRateLimited
            ? dispatch.RetryAfterMilliseconds
            : null,
        retryable: dispatch.Code == CalendarResourceMoveDispatchCode.UpstreamRateLimited);

    private static CalendarResourceRead Attach(string calendarHref, CalendarResourceRead read) =>
        read.Code == CalendarResourceReadCode.Success
            ? CalendarResourceProjector.AttachSnapshot(calendarHref, read)
            : read;

    private static CalendarResourceMoveResult PostWrite(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null) => new(
            code,
            CalendarMutationState.Committed,
            snapshot,
            Phase: CalendarResourceMovePhase.PostWriteVerificationOrReconciliation);

    private static CalendarResourceMoveResult Unknown(CalendarResourceSnapshot? snapshot = null) => new(
        CalendarResourceMoveCode.Indeterminate,
        CalendarMutationState.Unknown,
        snapshot,
        Phase: CalendarResourceMovePhase.PostWriteVerificationOrReconciliation);

    private static CalendarResourceMoveResult Rejected(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null,
        int? retryAfterMilliseconds = null,
        bool retryable = false) => new(
            code,
            CalendarMutationState.NotCommitted,
            snapshot,
            RetryAfterMilliseconds: retryAfterMilliseconds,
            Retryable: retryable,
            Phase: CalendarResourceMovePhase.Execution);

    private static CalendarResourceMoveResult Failure(CalendarResourceMoveCode code) => new(
        code,
        CalendarMutationState.NotAttempted,
        Phase: CalendarResourceMovePhase.Execution);

    private sealed record MoveObservation(
        CalendarResourceRead? Destination,
        CalendarResourceRead? Source);
}
