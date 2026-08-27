using System.Net.Http.Headers;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarMoveModule(
    ICalendarMoveTransport transport,
    CalendarMoveAuthorization authorization,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarResourceMoveResult> MoveAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetMoveDispatch(CalendarMoveDispatchClassification.NotAttempted);
        CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.Unspecified);
        CalendarOperationProgress.SetMoveReconciliation(CalendarMoveReconciliationClassification.NotRun);
        var inputFailure = ValidateInput(request);
        if (inputFailure is not null)
            return inputFailure;
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var failurePhase = CalendarResourceMovePhase.SelectionDiscoveryCapability;
        try
        {
            return await MoveCoreAsync(request, phase => failurePhase = phase, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Failure(
                CalendarResourceMoveCode.LimitExhausted,
                limitDimension: CalendarResourceMoveLimitDimension.ElapsedTime,
                phase: failurePhase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarResourceMoveCode.UpstreamUnavailable, retryable: true, phase: failurePhase);
        }
        catch (HttpRequestException exception)
        {
            return FromPreflightHttpFailure(exception.StatusCode, failurePhase);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Failure(CalendarResourceMoveCode.UpstreamUnavailable, retryable: true, phase: failurePhase);
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarResourceMoveCode.UpstreamProtocolError, phase: failurePhase);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarResourceMoveCode.UnsupportedCapability, phase: failurePhase);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Failure(
                CalendarResourceMoveCode.LimitExhausted,
                calendarCount: exception.CalendarCount,
                phase: failurePhase);
        }
    }

    private async Task<CalendarResourceMoveResult> MoveCoreAsync(
        CalendarResourceMoveRequest request,
        Action<CalendarResourceMovePhase> setFailurePhase,
        CancellationToken cancellationToken)
    {
        setFailurePhase(CalendarResourceMovePhase.SelectionDiscoveryCapability);
        var authorizationResult = await authorization.AuthorizeAsync(request, cancellationToken);
        if (authorizationResult is CalendarMoveAuthorizationResult.Rejected rejected)
            return MapAuthorizationFailure(rejected.Failure);
        var target = ((CalendarMoveAuthorizationResult.Authorized)authorizationResult).Target;

        var concurrencyFailure = ValidateStrongRevision(request.Revision);
        if (concurrencyFailure is not null)
            return concurrencyFailure;

        setFailurePhase(CalendarResourceMovePhase.TargetRevision);
        var sourceRead = Attach(
            target.SourceCalendar.Href,
            await transport.ReadSourceAsync(target.SourceCalendar.Href, target.SourceHref, cancellationToken));
        if (sourceRead.Code != CalendarResourceReadCode.Success || sourceRead.Snapshot is null)
            return FromReadFailure(sourceRead.Code);
        var revisionFailure = ValidateRevision(request.Revision, sourceRead.Snapshot);
        if (revisionFailure is not null)
            return RecordRevisionFailure(revisionFailure);
        setFailurePhase(CalendarResourceMovePhase.Execution);
        var destinationRead = await transport.ProbeDestinationPresenceAsync(
            target.DestinationCalendar.Href,
            target.DestinationHref,
            cancellationToken);
        if (destinationRead.Code != CalendarResourceReadCode.NotFound)
            return RecordDestinationPreflightFailure(destinationRead);
        CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.None);

        var plan = new CalendarReviewedMovePlan(new CalendarReviewedMovePreparation(
            request.Revision,
            sourceRead.Snapshot,
            target.SourceCalendar.Href,
            target.DestinationCalendar.Href,
            target.DestinationHref,
            CalendarMoveFidelityMode.Semantic));
        return await new CalendarMoveDispatcher(transport, timeProvider)
            .DispatchAsync(plan, cancellationToken);
    }

    private CalendarResourceMoveResult? ValidateInput(CalendarResourceMoveRequest request)
        => ValidateRevisionShape(request.Revision);

    private static CalendarResourceMoveResult? ValidateRevisionShape(
        CalendarResourceRevisionReference revision)
    {
        if (string.IsNullOrWhiteSpace(revision.EntityUid)
            || !Enum.IsDefined(revision.EntityKind)
            || !EntityTagHeaderValue.TryParse(revision.EntityTag, out var entityTag)
            || entityTag is null
            || entityTag == EntityTagHeaderValue.Any
            || !string.Equals(entityTag.ToString(), revision.EntityTag, StringComparison.Ordinal))
        {
            return Failure(CalendarResourceMoveCode.InvalidInput);
        }
        return null;
    }

    private static CalendarResourceMoveResult? ValidateStrongRevision(
        CalendarResourceRevisionReference revision) =>
        EntityTagHeaderValue.Parse(revision.EntityTag).IsWeak
            ? Failure(CalendarResourceMoveCode.ConcurrencyUnavailable)
            : null;

    private static CalendarResourceMoveResult? ValidateRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot)
    {
        if (!snapshot.SemanticMutationAvailable)
            return Failure(CalendarResourceMoveCode.OpaqueResource, snapshot);
        var kind = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            ? CalendarEntityKind.Event
            : CalendarEntityKind.Todo;
        if (kind != revision.EntityKind)
            return Failure(CalendarResourceMoveCode.EntityKindMismatch, snapshot);
        return !string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
            || !string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal)
            ? Failure(CalendarResourceMoveCode.Conflict, snapshot)
            : null;
    }

    private static CalendarResourceMoveResult DestinationPreflightFailure(CalendarResourceRead read) => read.Code switch
    {
        CalendarResourceReadCode.Success
            or CalendarResourceReadCode.ConcurrencyUnavailable
            or CalendarResourceReadCode.PayloadTooLarge =>
            Failure(CalendarResourceMoveCode.DestinationConflict),
        CalendarResourceReadCode.UnsupportedCapability => Failure(CalendarResourceMoveCode.UnsupportedCapability),
        _ => Failure(CalendarResourceMoveCode.UpstreamProtocolError)
    };

    private static CalendarResourceMoveResult RecordRevisionFailure(CalendarResourceMoveResult failure)
    {
        if (failure.Code == CalendarResourceMoveCode.Conflict)
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.SourceRevision);
        return failure;
    }

    private static CalendarResourceMoveResult RecordDestinationPreflightFailure(CalendarResourceRead read)
    {
        if (read.Code is CalendarResourceReadCode.Success
            or CalendarResourceReadCode.ConcurrencyUnavailable
            or CalendarResourceReadCode.PayloadTooLarge)
        {
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.DestinationHref);
        }
        return DestinationPreflightFailure(read);
    }

    private static CalendarResourceMoveResult FromReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.InvalidInput => Failure(
            CalendarResourceMoveCode.InvalidInput,
            phase: CalendarResourceMovePhase.SchemaLexicalDiscriminator),
        CalendarResourceReadCode.NotFound => Failure(
            CalendarResourceMoveCode.NotFound,
            phase: CalendarResourceMovePhase.TargetRevision),
        CalendarResourceReadCode.OutsideScope => Failure(
            CalendarResourceMoveCode.OutsideScope,
            phase: CalendarResourceMovePhase.OriginScopeAuthorization),
        CalendarResourceReadCode.ConcurrencyUnavailable => Failure(
            CalendarResourceMoveCode.ConcurrencyUnavailable,
            phase: CalendarResourceMovePhase.TargetRevision),
        CalendarResourceReadCode.PayloadTooLarge => Failure(
            CalendarResourceMoveCode.PayloadTooLarge,
            phase: CalendarResourceMovePhase.AdmissionAndPayload),
        CalendarResourceReadCode.UnsupportedCapability => Failure(
            CalendarResourceMoveCode.UnsupportedCapability,
            phase: CalendarResourceMovePhase.SelectionDiscoveryCapability),
        _ => Failure(
            CalendarResourceMoveCode.UpstreamProtocolError,
            phase: CalendarResourceMovePhase.TargetRevision)
    };

    private static CalendarResourceMoveResult FromPreflightHttpFailure(
        System.Net.HttpStatusCode? statusCode,
        CalendarResourceMovePhase phase) =>
        statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => Failure(CalendarResourceMoveCode.UpstreamUnauthorized, phase: phase),
            System.Net.HttpStatusCode.Forbidden => Failure(CalendarResourceMoveCode.UpstreamForbidden, phase: phase),
            System.Net.HttpStatusCode.RequestEntityTooLarge => Failure(CalendarResourceMoveCode.PayloadTooLarge, phase: phase),
            System.Net.HttpStatusCode.TooManyRequests => Failure(
                CalendarResourceMoveCode.UpstreamRateLimited,
                retryable: true,
                phase: phase),
            System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented =>
                Failure(CalendarResourceMoveCode.UnsupportedCapability, phase: phase),
            System.Net.HttpStatusCode.InsufficientStorage => Failure(CalendarResourceMoveCode.UpstreamUnavailable, phase: phase),
            >= System.Net.HttpStatusCode.InternalServerError => Failure(
                CalendarResourceMoveCode.UpstreamUnavailable,
                retryable: true,
                phase: phase),
            _ => Failure(CalendarResourceMoveCode.UpstreamProtocolError, phase: phase)
        };

    internal static CalendarResourceMoveResult MapAuthorizationFailure(CalendarMoveAuthorizationFailure failure)
    {
        var (code, phase) = failure.Reason switch
        {
            CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref
                or CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar
                or CalendarMoveAuthorizationFailureReason.SameResourceHref =>
                (CalendarResourceMoveCode.InvalidInput, CalendarResourceMovePhase.SchemaLexicalDiscriminator),
            CalendarMoveAuthorizationFailureReason.OriginMismatch =>
                (CalendarResourceMoveCode.InvalidInput, CalendarResourceMovePhase.OriginScopeAuthorization),
            CalendarMoveAuthorizationFailureReason.OutsideCalendarScope
                or CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing
                or CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous
                or CalendarMoveAuthorizationFailureReason.DestinationOwnershipMissing
                or CalendarMoveAuthorizationFailureReason.DestinationOwnershipAmbiguous =>
                (CalendarResourceMoveCode.OutsideScope, CalendarResourceMovePhase.OriginScopeAuthorization),
            CalendarMoveAuthorizationFailureReason.DestinationSelectionNotFound =>
                (CalendarResourceMoveCode.NotFound, CalendarResourceMovePhase.SelectionDiscoveryCapability),
            CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous =>
                (CalendarResourceMoveCode.Ambiguous, CalendarResourceMovePhase.SelectionDiscoveryCapability),
            CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised
                or CalendarMoveAuthorizationFailureReason.InteroperabilityProfileUnverified =>
                (CalendarResourceMoveCode.UnsupportedCapability, CalendarResourceMovePhase.SelectionDiscoveryCapability),
            CalendarMoveAuthorizationFailureReason.InvalidResolvedCalendar
                or CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent =>
                (CalendarResourceMoveCode.UpstreamProtocolError, CalendarResourceMovePhase.SelectionDiscoveryCapability),
            CalendarMoveAuthorizationFailureReason.SameCalendarNotAllowed =>
                (CalendarResourceMoveCode.InvalidInput, CalendarResourceMovePhase.SelectionDiscoveryCapability),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        return Failure(code, candidates: failure.AuthorizedCandidates, phase: phase);
    }

    private static CalendarResourceRead Attach(string calendarHref, CalendarResourceRead read) =>
        read.Code == CalendarResourceReadCode.Success
            ? CalendarResourceProjector.AttachSnapshot(calendarHref, read)
            : read;

    private static CalendarResourceMoveResult Failure(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null,
        IReadOnlyList<CalendarDescriptor>? candidates = null,
        bool retryable = false,
        CalendarResourceMoveLimitDimension? limitDimension = null,
        CalendarResourceMovePhase? phase = null,
        int? calendarCount = null) =>
        new(
            code,
            CalendarMutationState.NotAttempted,
            snapshot,
            candidates ?? [],
            Retryable: retryable,
            LimitDimension: limitDimension,
            Phase: phase,
            CalendarCount: calendarCount);

}
