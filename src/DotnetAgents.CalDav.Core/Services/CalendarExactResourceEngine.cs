using System.Xml;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarExactResourceEngine(
    ICalendarClient calendarClient,
    CalDavOptions options,
    TimeProvider timeProvider,
    Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope)
{
    private const int MaximumResourceBytes = 4 * 1024 * 1024;
    private const int MaximumInspectedResources = 5_000;
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarExactResourceResult> ReplaceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken)
    {
        var shapeFailure = ValidateReplaceShape(request);
        if (shapeFailure is not null)
            return shapeFailure;
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ReplaceCoreAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.LimitExhausted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return FromHttpFailure(exception.StatusCode);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarExactResourceCode.UpstreamProtocolError);
        }
    }

    public async Task<CalendarExactResourceReviewResult> ReviewReplaceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken)
    {
        var shapeFailure = ValidateReplaceShape(request);
        if (shapeFailure is not null)
            return FailedReview(shapeFailure);
        try
        {
            var target = await ReadScopedAsync(request.Revision.Href, cancellationToken);
            if (target.Failure is not null)
                return FailedReview(target.Failure);
            var snapshot = target.Snapshot!;
            var revisionFailure = ValidateCurrentRevision(request.Revision, snapshot);
            if (revisionFailure is not null)
                return FailedReview(revisionFailure);
            if (!CalendarExactResourceValidator.TryValidate(request.AuthoritativeUtf8.Span, out var intended)
                || intended.EntityUid != request.Revision.EntityUid
                || intended.EntityKind != request.Revision.EntityKind)
            {
                return FailedReview(Failure(
                    CalendarExactResourceCode.InvalidCalendarData,
                    CalendarExactResourcePhase.CompleteResourceSemantics));
            }
            if (snapshot.AuthoritativeUtf8.Span.SequenceEqual(request.AuthoritativeUtf8.Span))
            {
                return FailedReview(new CalendarExactResourceResult(
                    CalendarExactResourceCode.NoChange,
                    CalendarMutationState.NotAttempted,
                    snapshot,
                    Phase: CalendarExactResourcePhase.CompleteResourceSemantics));
            }
            return SuccessfulReview(request.Revision, request.AuthoritativeUtf8.Span);
        }
        catch (HttpRequestException exception)
        {
            return FailedReview(FromHttpFailure(exception.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true));
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamProtocolError));
        }
    }

    public async Task<CalendarExactResourceResult> MoveAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var shapeFailure = ValidateMoveShape(request);
        if (shapeFailure is not null)
            return shapeFailure;
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await MoveCoreAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.LimitExhausted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return FromHttpFailure(exception.StatusCode);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarExactResourceCode.UpstreamProtocolError);
        }
    }

    public async Task<CalendarExactResourceReviewResult> ReviewMoveAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var shapeFailure = ValidateMoveShape(request);
        if (shapeFailure is not null)
            return FailedReview(shapeFailure);
        try
        {
            var source = await ReadScopedAsync(request.Revision.Href, cancellationToken);
            if (source.Failure is not null)
                return FailedReview(source.Failure);
            var revisionFailure = ValidateCurrentRevision(request.Revision, source.Snapshot!);
            if (revisionFailure is not null)
                return FailedReview(revisionFailure);
            var destination = await PrepareMoveDestinationAsync(request, cancellationToken);
            if (destination.Failure is not null)
                return FailedReview(destination.Failure);
            var intent = BindMoveIntent(source.Snapshot!.AuthoritativeUtf8.Span, request.DestinationHref);
            return SuccessfulReview(request.Revision, intent);
        }
        catch (HttpRequestException exception)
        {
            return FailedReview(FromHttpFailure(exception.StatusCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true));
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return FailedReview(Failure(CalendarExactResourceCode.UpstreamProtocolError));
        }
    }

    private async Task<CalendarExactResourceResult> MoveCoreAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var source = await ReadScopedAsync(request.Revision.Href, cancellationToken);
        if (source.Failure is not null)
            return source.Failure;
        var revisionFailure = ValidateCurrentRevision(request.Revision, source.Snapshot!);
        if (revisionFailure is not null)
            return revisionFailure;
        var destination = await PrepareMoveDestinationAsync(request, cancellationToken);
        if (destination.Failure is not null)
            return destination.Failure;

        var dispatch = await calendarClient.MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(
                request.Revision.Href,
                request.DestinationHref,
                request.Revision.EntityTag),
            cancellationToken);
        return await VerifyMoveAsync(request, source.Snapshot!, destination.CalendarHref!, dispatch);
    }

    private async Task<PreparedMoveDestination> PrepareMoveDestinationAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var discovery = await DiscoverScopedAsync(cancellationToken);
        if (discovery.Failure is not null)
            return new PreparedMoveDestination(null, discovery.Failure);
        var destinationCalendar = discovery.Calendars!.SingleOrDefault(calendar =>
            IsDirectResourceOf(request.DestinationHref, calendar.Href));
        if (destinationCalendar is null)
            return new PreparedMoveDestination(null,
                Failure(CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.OriginScopeAuthorization));
        if (!Advertises(destinationCalendar, request.Revision.EntityKind))
            return new PreparedMoveDestination(null,
                Failure(CalendarExactResourceCode.UnsupportedCapability, CalendarExactResourcePhase.SelectionDiscoveryCapability));
        var target = await ReadTargetAsync(request.DestinationHref, cancellationToken);
        if (target.Failure is not null)
            return new PreparedMoveDestination(null, target.Failure);
        var destination = target.Read!;
        if (destination.Code != CalendarResourceReadCode.NotFound)
            return new PreparedMoveDestination(null, ExistingDestinationFailure(destination.Code));
        var uidFailure = await FindUidConflictAsync(
            destinationCalendar.Href,
            new CalendarExactResourceIdentity(request.Revision.EntityUid, request.Revision.EntityKind),
            request.Revision.Href,
            cancellationToken);
        return new PreparedMoveDestination(uidFailure is null ? destinationCalendar.Href : null, uidFailure);
    }

    private async Task<CalendarExactResourceResult> VerifyMoveAsync(
        CalendarExactMoveRequest request,
        CalendarResourceSnapshot source,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchResult dispatch)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        if (dispatch.Code == CalendarResourceMoveDispatchCode.Conflict)
            return await ClassifyMoveConflictAsync(request, source, destinationCalendarHref);
        if (dispatch.Code is not (CalendarResourceMoveDispatchCode.Dispatched
            or CalendarResourceMoveDispatchCode.PossiblyDispatched))
        {
            return FromMoveFailure(dispatch);
        }
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        var destination = await ObserveAsync(request.DestinationHref, verification.Token);
        var remainingSource = await ObserveAsync(request.Revision.Href, verification.Token);
        return ClassifyMoveObservation(
            request,
            source,
            destinationCalendarHref,
            dispatch.Code,
            destination,
            remainingSource);
    }

    private async Task<CalendarExactResourceResult> ClassifyMoveConflictAsync(
        CalendarExactMoveRequest request,
        CalendarResourceSnapshot source,
        string destinationCalendarHref)
    {
        using var reconciliation = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        var destination = await ObserveSnapshotAsync(
            destinationCalendarHref,
            request.DestinationHref,
            reconciliation.Token);
        if (destination is not null)
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.DestinationConflict,
                CalendarMutationState.NotCommitted,
                destination);
        }
        var observedSource = await ObserveSnapshotAsync(
            source.CalendarHref,
            request.Revision.Href,
            reconciliation.Token);
        return new CalendarExactResourceResult(
            CalendarExactResourceCode.Conflict,
            CalendarMutationState.NotCommitted,
            observedSource,
            Phase: CalendarExactResourcePhase.TargetRevision);
    }

    private static CalendarExactResourceResult ClassifyMoveObservation(
        CalendarExactMoveRequest request,
        CalendarResourceSnapshot source,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceRead? destination,
        CalendarResourceRead? remainingSource)
    {
        var verified = VerifiedMove(source, destinationCalendarHref, dispatchCode, destination, remainingSource);
        if (verified is not null)
            return verified;
        if (IsCommittedWithFidelityFailure(source, destination, remainingSource))
        {
            return dispatchCode == CalendarResourceMoveDispatchCode.PossiblyDispatched
                ? Unknown()
                : PostWrite(CalendarExactResourceCode.FidelityFailure);
        }
        if (IsCommittedWithoutStrongTag(source, destination, remainingSource))
            return PostWrite(CalendarExactResourceCode.CommittedButConcurrencyUnavailable);
        if (IsUnchangedAfterPossibleDispatch(request, dispatchCode, destination, remainingSource))
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.UpstreamUnavailable,
                CalendarMutationState.NotCommitted,
                Retryable: true);
        }
        return dispatchCode == CalendarResourceMoveDispatchCode.PossiblyDispatched
            ? Unknown()
            : PostWrite(CalendarExactResourceCode.CommittedButUnverified);
    }

    private static CalendarExactResourceResult? VerifiedMove(
        CalendarResourceSnapshot source,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceRead? destination,
        CalendarResourceRead? remainingSource)
    {
        if (destination?.Code != CalendarResourceReadCode.Success
            || remainingSource?.Code != CalendarResourceReadCode.NotFound)
            return null;
        var snapshot = CalendarResourceProjector.AttachSnapshot(destinationCalendarHref, destination).Snapshot!;
        if (source.AuthoritativeUtf8.Span.SequenceEqual(snapshot.AuthoritativeUtf8.Span))
            return CalendarExactResourceResult.Success(snapshot);
        return dispatchCode == CalendarResourceMoveDispatchCode.PossiblyDispatched
            ? Unknown(snapshot)
            : PostWrite(CalendarExactResourceCode.FidelityFailure, snapshot);
    }

    private static bool IsCommittedWithoutStrongTag(
        CalendarResourceSnapshot source,
        CalendarResourceRead? destination,
        CalendarResourceRead? remainingSource) =>
        destination?.Code == CalendarResourceReadCode.ConcurrencyUnavailable
        && remainingSource?.Code == CalendarResourceReadCode.NotFound
        && source.AuthoritativeUtf8.Span.SequenceEqual(destination.AuthoritativeUtf8.Span);

    private static bool IsCommittedWithFidelityFailure(
        CalendarResourceSnapshot source,
        CalendarResourceRead? destination,
        CalendarResourceRead? remainingSource) =>
        destination?.Code == CalendarResourceReadCode.ConcurrencyUnavailable
        && remainingSource?.Code == CalendarResourceReadCode.NotFound
        && !source.AuthoritativeUtf8.Span.SequenceEqual(destination.AuthoritativeUtf8.Span);

    private static bool IsUnchangedAfterPossibleDispatch(
        CalendarExactMoveRequest request,
        CalendarResourceMoveDispatchCode dispatchCode,
        CalendarResourceRead? destination,
        CalendarResourceRead? remainingSource) =>
        dispatchCode == CalendarResourceMoveDispatchCode.PossiblyDispatched
        && destination?.Code == CalendarResourceReadCode.NotFound
        && remainingSource?.Code == CalendarResourceReadCode.Success
        && remainingSource.EntityTag == request.Revision.EntityTag;

    private async Task<CalendarExactResourceResult> ReplaceCoreAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken)
    {
        var target = await ReadScopedAsync(request.Revision.Href, cancellationToken);
        if (target.Failure is not null)
            return target.Failure;
        var snapshot = target.Snapshot!;
        var revisionFailure = ValidateCurrentRevision(request.Revision, snapshot);
        if (revisionFailure is not null)
            return revisionFailure;
        if (!CalendarExactResourceValidator.TryValidate(request.AuthoritativeUtf8.Span, out var intended)
            || intended.EntityUid != request.Revision.EntityUid
            || intended.EntityKind != request.Revision.EntityKind)
        {
            return Failure(
                CalendarExactResourceCode.InvalidCalendarData,
                CalendarExactResourcePhase.CompleteResourceSemantics);
        }
        if (snapshot.AuthoritativeUtf8.Span.SequenceEqual(request.AuthoritativeUtf8.Span))
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.NoChange,
                CalendarMutationState.NotAttempted,
                snapshot,
                Phase: CalendarExactResourcePhase.CompleteResourceSemantics);
        }

        var dispatch = await calendarClient.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(
                request.Revision.Href,
                request.Revision.EntityTag,
                request.AuthoritativeUtf8),
            cancellationToken);
        return await VerifyReplaceAsync(request, target.CalendarHref!, intended, snapshot, dispatch);
    }

    private async Task<CalendarExactResourceResult> VerifyReplaceAsync(
        CalendarExactReplaceRequest request,
        string calendarHref,
        CalendarExactResourceIdentity identity,
        CalendarResourceSnapshot current,
        CalendarResourceUpdateDispatchResult dispatch)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        if (dispatch.Code == CalendarResourceUpdateDispatchCode.Conflict)
        {
            var conflict = await ObserveSnapshotAsync(calendarHref, request.Revision.Href);
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotCommitted,
                conflict,
                Phase: CalendarExactResourcePhase.TargetRevision);
        }
        if (dispatch.Code is not (CalendarResourceUpdateDispatchCode.Dispatched
            or CalendarResourceUpdateDispatchCode.PossiblyDispatched))
        {
            return FromUpdateFailure(dispatch);
        }
        var observed = await ObserveAsync(request.Revision.Href);
        if (observed is null)
            return MissingUpdateObservation(dispatch.Code);
        return ClassifyReplaceObservation(request, calendarHref, identity, current, dispatch.Code, observed);
    }

    private static CalendarExactResourceResult ClassifyReplaceObservation(
        CalendarExactReplaceRequest request,
        string calendarHref,
        CalendarExactResourceIdentity identity,
        CalendarResourceSnapshot current,
        CalendarResourceUpdateDispatchCode dispatchCode,
        CalendarResourceRead observed)
    {
        if (observed.Code == CalendarResourceReadCode.ConcurrencyUnavailable)
            return ClassifyWeakReplaceObservation(request, current, dispatchCode, observed);
        if (observed.Code != CalendarResourceReadCode.Success)
            return dispatchCode == CalendarResourceUpdateDispatchCode.PossiblyDispatched
                ? Unknown()
                : PostWrite(CalendarExactResourceCode.CommittedButUnverified);
        var snapshot = CalendarResourceProjector.AttachSnapshot(calendarHref, observed).Snapshot!;
        if (dispatchCode == CalendarResourceUpdateDispatchCode.PossiblyDispatched
            && IsSameRevision(request.Revision, snapshot))
            return UnchangedAfterPossibleDispatch(snapshot);
        if (IsExactMatch(request.AuthoritativeUtf8.Span, identity, snapshot))
            return CalendarExactResourceResult.Success(snapshot);
        return dispatchCode == CalendarResourceUpdateDispatchCode.PossiblyDispatched
            ? Unknown(snapshot)
            : PostWrite(CalendarExactResourceCode.FidelityFailure, snapshot);
    }

    private static CalendarExactResourceResult ClassifyWeakReplaceObservation(
        CalendarExactReplaceRequest request,
        CalendarResourceSnapshot current,
        CalendarResourceUpdateDispatchCode dispatchCode,
        CalendarResourceRead observed)
    {
        if (dispatchCode != CalendarResourceUpdateDispatchCode.PossiblyDispatched)
        {
            return ClassifyWeakObservation(
                CalendarEntityCreateFidelity.IsExactEquivalent(
                    request.AuthoritativeUtf8.Span,
                    observed.AuthoritativeUtf8.Span),
                possiblyDispatched: false);
        }
        if (request.AuthoritativeUtf8.Span.SequenceEqual(observed.AuthoritativeUtf8.Span))
            return PostWrite(CalendarExactResourceCode.CommittedButConcurrencyUnavailable);
        if (current.AuthoritativeUtf8.Span.SequenceEqual(observed.AuthoritativeUtf8.Span))
            return UnchangedAfterPossibleDispatch(current);
        var matchesIntent = CalendarEntityCreateFidelity.IsExactEquivalent(
            request.AuthoritativeUtf8.Span,
            observed.AuthoritativeUtf8.Span);
        var matchesCurrent = CalendarEntityCreateFidelity.IsExactEquivalent(
            current.AuthoritativeUtf8.Span,
            observed.AuthoritativeUtf8.Span);
        return matchesIntent && !matchesCurrent
            ? PostWrite(CalendarExactResourceCode.CommittedButConcurrencyUnavailable)
            : Unknown();
    }

    private static CalendarExactResourceResult UnchangedAfterPossibleDispatch(CalendarResourceSnapshot snapshot) => new(
        CalendarExactResourceCode.UpstreamUnavailable,
        CalendarMutationState.NotCommitted,
        snapshot,
        true);

    private async Task<ScopedSnapshot> ReadScopedAsync(string href, CancellationToken cancellationToken)
    {
        if (!IsWithinConfiguredScope(href))
            return new ScopedSnapshot(null, null, OutsideConfiguredScope());
        var discovery = await DiscoverScopedAsync(cancellationToken);
        if (discovery.Failure is not null)
            return new ScopedSnapshot(null, null, discovery.Failure);
        var calendar = discovery.Calendars!.SingleOrDefault(candidate => IsDirectResourceOf(href, candidate.Href));
        if (calendar is null)
        {
            return new ScopedSnapshot(
                null,
                null,
                Failure(CalendarExactResourceCode.OutsideScope, CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
        var target = await ReadTargetAsync(href, cancellationToken);
        if (target.Failure is not null)
            return new ScopedSnapshot(calendar.Href, null, target.Failure);
        var read = target.Read!;
        if (read.Code != CalendarResourceReadCode.Success)
            return new ScopedSnapshot(calendar.Href, null, FromReadFailure(read.Code));
        return new ScopedSnapshot(
            calendar.Href,
            CalendarResourceProjector.AttachSnapshot(calendar.Href, read).Snapshot,
            null);
    }

    private static CalendarExactResourceResult? ValidateCurrentRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot)
    {
        if (!CalendarExactResourceValidator.TryValidate(snapshot.AuthoritativeUtf8.Span, out var current))
            return Failure(CalendarExactResourceCode.InvalidCalendarData, CalendarExactResourcePhase.TargetRevision);
        if (current.EntityKind != revision.EntityKind)
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.EntityKindMismatch,
                CalendarMutationState.NotAttempted,
                snapshot,
                Phase: CalendarExactResourcePhase.TargetRevision);
        }
        return current.EntityUid == revision.EntityUid && snapshot.EntityTag == revision.EntityTag
            ? null
            : new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotAttempted,
                snapshot,
                Phase: CalendarExactResourcePhase.TargetRevision);
    }

    private CalendarExactResourceResult? ValidateReplaceShape(CalendarExactReplaceRequest request)
    {
        if (request.AuthoritativeUtf8.Length > MaximumResourceBytes)
            return Failure(CalendarExactResourceCode.PayloadTooLarge, CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        if (request.AuthoritativeUtf8.IsEmpty)
            return Failure(CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        return ValidateRevisionShape(request.Revision);
    }

    private CalendarExactResourceResult? ValidateMoveShape(CalendarExactMoveRequest request)
    {
        var revisionFailure = ValidateRevisionLexicalShape(request.Revision, out var entityTag);
        if (revisionFailure is not null)
            return revisionFailure;
        if (!TryValidateResourceHrefSyntax(request.DestinationHref))
            return Failure(CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.OriginScopeAuthorization);
        if (request.DestinationHref == request.Revision.Href)
            return Failure(CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        var sourceAuthorization = ValidateOriginAndScope(request.Revision.Href);
        if (sourceAuthorization is not null)
            return sourceAuthorization;
        var destinationAuthorization = ValidateOriginAndScope(request.DestinationHref);
        if (destinationAuthorization is not null)
            return destinationAuthorization;
        return entityTag!.IsWeak
            ? Failure(CalendarExactResourceCode.ConcurrencyUnavailable, CalendarExactResourcePhase.TargetRevision)
            : null;
    }

    private CalendarExactResourceResult? ValidateRevisionShape(CalendarResourceRevisionReference revision)
    {
        var lexicalFailure = ValidateRevisionLexicalShape(revision, out var entityTag);
        if (lexicalFailure is not null)
            return lexicalFailure;
        var authorizationFailure = ValidateOriginAndScope(revision.Href);
        if (authorizationFailure is not null)
            return authorizationFailure;
        return entityTag!.IsWeak
            ? Failure(CalendarExactResourceCode.ConcurrencyUnavailable, CalendarExactResourcePhase.TargetRevision)
            : null;
    }

    private static CalendarExactResourceResult? ValidateRevisionLexicalShape(
        CalendarResourceRevisionReference revision,
        out EntityTagHeaderValue? entityTag)
    {
        entityTag = null;
        if (!TryValidateResourceHrefSyntax(revision.Href)
            || string.IsNullOrWhiteSpace(revision.EntityUid)
            || !Enum.IsDefined(revision.EntityKind)
            || !EntityTagHeaderValue.TryParse(revision.EntityTag, out entityTag)
            || entityTag is null
            || entityTag == EntityTagHeaderValue.Any
            || !string.Equals(entityTag.ToString(), revision.EntityTag, StringComparison.Ordinal))
        {
            return Failure(CalendarExactResourceCode.InvalidInput, CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        }
        return null;
    }

    private async Task<CalendarResourceSnapshot?> ObserveSnapshotAsync(string calendarHref, string href)
    {
        var read = await ObserveAsync(href);
        return AttachObservedSnapshot(calendarHref, read);
    }

    private async Task<CalendarResourceSnapshot?> ObserveSnapshotAsync(
        string calendarHref,
        string href,
        CancellationToken cancellationToken)
    {
        var read = await ObserveAsync(href, cancellationToken);
        return AttachObservedSnapshot(calendarHref, read);
    }

    private static CalendarResourceSnapshot? AttachObservedSnapshot(
        string calendarHref,
        CalendarResourceRead? read)
    {
        return read?.Code == CalendarResourceReadCode.Success
            ? CalendarResourceProjector.AttachSnapshot(calendarHref, read).Snapshot
            : null;
    }

    private static bool IsExactMatch(
        ReadOnlySpan<byte> intendedUtf8,
        CalendarExactResourceIdentity identity,
        CalendarResourceSnapshot snapshot) =>
        CalendarExactResourceValidator.TryValidate(snapshot.AuthoritativeUtf8.Span, out var observed)
        && observed == identity
        && CalendarEntityCreateFidelity.IsExactEquivalent(intendedUtf8, snapshot.AuthoritativeUtf8.Span);

    private static bool IsSameRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot) =>
        snapshot.ResourceHref == revision.Href && snapshot.EntityTag == revision.EntityTag;

    private static CalendarExactResourceResult FromUpdateFailure(CalendarResourceUpdateDispatchResult dispatch) =>
        Rejected(dispatch.Code switch
        {
            CalendarResourceUpdateDispatchCode.InvalidInput => CalendarExactResourceCode.InvalidInput,
            CalendarResourceUpdateDispatchCode.NotFound => CalendarExactResourceCode.NotFound,
            CalendarResourceUpdateDispatchCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
            CalendarResourceUpdateDispatchCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
            CalendarResourceUpdateDispatchCode.UpstreamUnauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
            CalendarResourceUpdateDispatchCode.UpstreamForbidden => CalendarExactResourceCode.UpstreamForbidden,
            CalendarResourceUpdateDispatchCode.UpstreamRateLimited => CalendarExactResourceCode.UpstreamRateLimited,
            CalendarResourceUpdateDispatchCode.UpstreamUnavailable => CalendarExactResourceCode.UpstreamUnavailable,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        }, dispatch.Code == CalendarResourceUpdateDispatchCode.UpstreamRateLimited,
        dispatch.RetryAfterMilliseconds);

    private static CalendarExactResourceResult FromMoveFailure(CalendarResourceMoveDispatchResult dispatch) =>
        Rejected(dispatch.Code switch
        {
            CalendarResourceMoveDispatchCode.InvalidInput => CalendarExactResourceCode.InvalidInput,
            CalendarResourceMoveDispatchCode.NotFound => CalendarExactResourceCode.NotFound,
            CalendarResourceMoveDispatchCode.Conflict => CalendarExactResourceCode.Conflict,
            CalendarResourceMoveDispatchCode.DestinationConflict => CalendarExactResourceCode.DestinationConflict,
            CalendarResourceMoveDispatchCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
            CalendarResourceMoveDispatchCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
            CalendarResourceMoveDispatchCode.UpstreamUnauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
            CalendarResourceMoveDispatchCode.UpstreamForbidden => CalendarExactResourceCode.UpstreamForbidden,
            CalendarResourceMoveDispatchCode.UpstreamRateLimited => CalendarExactResourceCode.UpstreamRateLimited,
            CalendarResourceMoveDispatchCode.UpstreamUnavailable => CalendarExactResourceCode.UpstreamUnavailable,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        }, dispatch.Code == CalendarResourceMoveDispatchCode.UpstreamRateLimited,
        dispatch.RetryAfterMilliseconds);

    private static CalendarExactResourceResult MissingUpdateObservation(
        CalendarResourceUpdateDispatchCode dispatchCode) => dispatchCode == CalendarResourceUpdateDispatchCode.PossiblyDispatched
        ? Unknown()
        : PostWrite(CalendarExactResourceCode.CommittedButUnverified);

    private async Task<CalendarExactResourceResult?> FindUidConflictAsync(
        string calendarHref,
        CalendarExactResourceIdentity identity,
        string? ignoredHref,
        CancellationToken cancellationToken)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { CalendarEntityKind.Event, CalendarEntityKind.Todo })
        {
            try
            {
                hrefs.UnionWith(await calendarClient.QueryCalendarResourceHrefsAsync(
                    calendarHref,
                    kind,
                    null,
                    null,
                    cancellationToken));
            }
            catch (Exception exception) when (IsPhaseFailure(exception, cancellationToken))
            {
                return FromPhaseFailure(
                    exception,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability);
            }
            if (hrefs.Count > MaximumInspectedResources)
            {
                return Failure(
                    CalendarExactResourceCode.LimitExhausted,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability);
            }
        }
        foreach (var href in hrefs)
        {
            if (href == ignoredHref)
                continue;
            CalendarResourceRead read;
            try
            {
                read = await calendarClient.GetCalendarResourceAsync(href, cancellationToken);
            }
            catch (Exception exception) when (IsPhaseFailure(exception, cancellationToken))
            {
                return FromPhaseFailure(
                    exception,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability);
            }
            if (read.Code == CalendarResourceReadCode.NotFound)
                continue;
            if (read.Code != CalendarResourceReadCode.Success)
            {
                return FromReadFailure(
                    read.Code,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability);
            }
            var projection = CalendarResourceProjector.Project(read.AuthoritativeUtf8.Span);
            if (CalendarResourceProjector.ContainsEntityUid(projection, identity.EntityUid))
                return Rejected(CalendarExactResourceCode.Conflict);
        }
        return null;
    }

    private async Task<CalendarResourceRead?> ObserveAsync(string href)
    {
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        return await ObserveAsync(href, verification.Token);
    }

    private async Task<CalendarResourceRead?> ObserveAsync(string href, CancellationToken cancellationToken)
    {
        try
        {
            return await calendarClient.GetCalendarResourceAsync(href, cancellationToken);
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            return null;
        }
    }

    private static bool IsObservationFailure(Exception exception) => exception is HttpRequestException
        or IOException
        or TimeoutException
        or OperationCanceledException;

    private static bool TryValidateResourceHrefSyntax(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var resource) && HasSafeShape(resource, href);

    private CalendarExactResourceResult? ValidateOriginAndScope(string href)
    {
        var resource = new Uri(href, UriKind.Absolute);
        if (!HasSameOrigin(new Uri(options.BaseUrl, UriKind.Absolute), resource))
        {
            return Failure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.OriginScopeAuthorization);
        }
        return IsWithinConfiguredScope(href) ? null : OutsideConfiguredScope();
    }

    private bool IsWithinConfiguredScope(string resourceHref)
    {
        var configuredScope = options.CalendarHrefs?.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return configuredScope.Length == 0
            || configuredScope.Any(calendarHref => IsDirectResourceOf(resourceHref, calendarHref));
    }

    private static CalendarExactResourceResult OutsideConfiguredScope() => Failure(
        CalendarExactResourceCode.OutsideScope,
        CalendarExactResourcePhase.OriginScopeAuthorization);

    private static bool IsDirectResourceOf(string resourceHref, string calendarHref)
    {
        if (!Uri.TryCreate(resourceHref, UriKind.Absolute, out var resource)
            || !Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendar)
            || !HasSameOrigin(resource, calendar))
            return false;
        var calendarPath = calendar.AbsolutePath.EndsWith('/') ? calendar.AbsolutePath : calendar.AbsolutePath + '/';
        if (!resource.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relative = resource.AbsolutePath[calendarPath.Length..];
        return relative.Length > 0 && !relative.Contains('/');
    }

    private static bool HasSafeShape(Uri uri, string original) =>
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !uri.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !original.Contains('\\')
        && string.Equals(uri.AbsoluteUri, original, StringComparison.Ordinal);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool Advertises(CalendarDescriptor calendar, CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport == EntityKindSupport.Advertised,
        CalendarEntityKind.Todo => calendar.TodoSupport == EntityKindSupport.Advertised,
        _ => false
    };

    private static CalendarExactResourceResult ExistingDestinationFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.Success or CalendarResourceReadCode.ConcurrencyUnavailable =>
            Rejected(CalendarExactResourceCode.DestinationConflict),
        _ => FromReadFailure(code)
    };

    private static CalendarExactResourceResult FromReadFailure(
        CalendarResourceReadCode code,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.TargetRevision) => Failure(
        code switch
        {
            CalendarResourceReadCode.InvalidInput => CalendarExactResourceCode.InvalidInput,
            CalendarResourceReadCode.NotFound => CalendarExactResourceCode.NotFound,
            CalendarResourceReadCode.OutsideScope => CalendarExactResourceCode.OutsideScope,
            CalendarResourceReadCode.ConcurrencyUnavailable => CalendarExactResourceCode.ConcurrencyUnavailable,
            CalendarResourceReadCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
            CalendarResourceReadCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        },
        phase);

    private static CalendarExactResourceResult FromHttpFailure(
        System.Net.HttpStatusCode? statusCode,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.Execution) => Failure(
        statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
            System.Net.HttpStatusCode.Forbidden => CalendarExactResourceCode.UpstreamForbidden,
            System.Net.HttpStatusCode.TooManyRequests => CalendarExactResourceCode.UpstreamRateLimited,
            >= System.Net.HttpStatusCode.InternalServerError => CalendarExactResourceCode.UpstreamUnavailable,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        },
        phase,
        statusCode is System.Net.HttpStatusCode.TooManyRequests or >= System.Net.HttpStatusCode.InternalServerError);

    private async Task<CalendarDiscoveryAttempt> DiscoverScopedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new CalendarDiscoveryAttempt(
                applyScope(await calendarClient.GetCalendarsAsync(cancellationToken)).Items,
                null);
        }
        catch (Exception exception) when (IsPhaseFailure(exception, cancellationToken))
        {
            return new CalendarDiscoveryAttempt(
                null,
                FromPhaseFailure(exception, CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
    }

    private async Task<ResourceReadAttempt> ReadTargetAsync(string href, CancellationToken cancellationToken)
    {
        try
        {
            return new ResourceReadAttempt(
                await calendarClient.GetCalendarResourceAsync(href, cancellationToken),
                null);
        }
        catch (Exception exception) when (IsPhaseFailure(exception, cancellationToken))
        {
            return new ResourceReadAttempt(
                null,
                FromPhaseFailure(exception, CalendarExactResourcePhase.TargetRevision));
        }
    }

    private static bool IsPhaseFailure(Exception exception, CancellationToken cancellationToken) => exception is
        HttpRequestException or IOException or TimeoutException or XmlException or CalendarDiscoveryProtocolException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static CalendarExactResourceResult FromPhaseFailure(
        Exception exception,
        CalendarExactResourcePhase phase) => exception switch
        {
            HttpRequestException http => FromHttpFailure(http.StatusCode, phase),
            OperationCanceledException or IOException or TimeoutException =>
                Failure(CalendarExactResourceCode.UpstreamUnavailable, phase, retryable: true),
            XmlException or CalendarDiscoveryProtocolException =>
                Failure(CalendarExactResourceCode.UpstreamProtocolError, phase),
            _ => throw new InvalidOperationException("The exception is not a supported phase failure.", exception)
        };

    private static CalendarExactResourceResult Failure(
        CalendarExactResourceCode code,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.Execution,
        bool retryable = false,
        CalendarEntityCreateExecutionLimits? limits = null) => new(
            code,
            CalendarMutationState.NotAttempted,
            Retryable: retryable,
            Phase: phase,
            Limits: limits);

    private static CalendarExactResourceResult Rejected(
        CalendarExactResourceCode code,
        bool retryable = false,
        int? retryAfterMilliseconds = null) => new(
            code,
            CalendarMutationState.NotCommitted,
            Retryable: retryable,
            RetryAfterMilliseconds: retryAfterMilliseconds);

    private static CalendarExactResourceResult PostWrite(
        CalendarExactResourceCode code,
        CalendarResourceSnapshot? snapshot = null) => new(
        code,
        CalendarMutationState.Committed,
        snapshot,
        Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation);

    private static CalendarExactResourceResult Unknown(CalendarResourceSnapshot? snapshot = null) => new(
        CalendarExactResourceCode.Indeterminate,
        CalendarMutationState.Unknown,
        snapshot,
        Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation);

    private static CalendarExactResourceResult ClassifyWeakObservation(
        bool contentMatches,
        bool possiblyDispatched)
    {
        if (contentMatches)
            return PostWrite(CalendarExactResourceCode.CommittedButConcurrencyUnavailable);
        return possiblyDispatched
            ? Unknown()
            : PostWrite(CalendarExactResourceCode.FidelityFailure);
    }

    private static CalendarExactResourceReviewResult FailedReview(CalendarExactResourceResult failure) =>
        new(failure, null, default);

    private static CalendarExactResourceReviewResult SuccessfulReview(
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> intent) => new(null, revision, SHA256.HashData(intent));

    private static byte[] BindMoveIntent(ReadOnlySpan<byte> source, string destinationHref)
    {
        var destination = System.Text.Encoding.UTF8.GetBytes(destinationHref);
        var combined = new byte[source.Length + 1 + destination.Length];
        source.CopyTo(combined);
        destination.CopyTo(combined.AsSpan(source.Length + 1));
        return combined;
    }

    private sealed record ScopedSnapshot(
        string? CalendarHref,
        CalendarResourceSnapshot? Snapshot,
        CalendarExactResourceResult? Failure);

    private sealed record PreparedMoveDestination(
        string? CalendarHref,
        CalendarExactResourceResult? Failure);

    private sealed record CalendarDiscoveryAttempt(
        IReadOnlyList<CalendarDescriptor>? Calendars,
        CalendarExactResourceResult? Failure);

    private sealed record ResourceReadAttempt(
        CalendarResourceRead? Read,
        CalendarExactResourceResult? Failure);
}
