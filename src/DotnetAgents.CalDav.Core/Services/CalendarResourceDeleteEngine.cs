using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using System.Net.Http.Headers;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarResourceDeleteEngine(
    ICalendarClient calendarClient,
    Func<string, CancellationToken, Task<CalendarResourceRead>> readResource,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarResourceDeleteResult> DeleteAsync(
        CalendarResourceRevisionReference revision,
        CancellationToken cancellationToken)
    {
        var revisionFailure = ValidateRevisionReference(revision);
        if (revisionFailure is not null)
            return Failure(revisionFailure.Value);

        var preflight = await ReadBeforeDispatchAsync(revision.Href, cancellationToken);
        if (preflight.Failure is not null)
            return preflight.Failure;
        var current = preflight.Read!;
        if (current.Code != CalendarResourceReadCode.Success || current.Snapshot is null)
            return FromReadFailure(current.Code);

        var validation = ValidateRevision(revision, current.Snapshot);
        if (validation is not null)
            return validation;

        var dispatch = await calendarClient.DeleteCalendarResourceAsync(
            new CalendarResourceDeleteRequest(revision.Href, revision.EntityTag),
            cancellationToken);
        if (dispatch.Code == CalendarResourceDeleteDispatchCode.PossiblyDispatched)
            return await ReconcileAsync(revision);
        if (dispatch.Code == CalendarResourceDeleteDispatchCode.Conflict)
            return await RefreshConflictAsync(revision.Href, cancellationToken);
        if (dispatch.Code != CalendarResourceDeleteDispatchCode.Dispatched)
            return FromDispatchFailure(dispatch);

        return await VerifyDispatchedAsync(revision);
    }

    private async Task<(CalendarResourceRead? Read, CalendarResourceDeleteResult? Failure)> ReadBeforeDispatchAsync(
        string href,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await readResource(href, cancellationToken), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(CalendarResourceDeleteCode.UpstreamUnavailable, retryable: true));
        }
        catch (HttpRequestException exception)
        {
            return (null, FromPreflightHttpFailure(exception.StatusCode));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return (null, Failure(CalendarResourceDeleteCode.UpstreamUnavailable, retryable: true));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return (null, Failure(CalendarResourceDeleteCode.UpstreamProtocolError));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return (null, Failure(CalendarResourceDeleteCode.UnsupportedCapability));
        }
    }

    private async Task<CalendarResourceDeleteResult> VerifyDispatchedAsync(
        CalendarResourceRevisionReference revision)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        try
        {
            var observed = await readResource(revision.Href, verification.Token);
            return observed.Code == CalendarResourceReadCode.NotFound
                ? Success(revision)
                : CommittedButUnverified(observed.Snapshot);
        }
        catch (Exception)
        {
            return CommittedButUnverified();
        }
    }

    private async Task<CalendarResourceDeleteResult> ReconcileAsync(CalendarResourceRevisionReference revision)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        using var reconciliation = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        CalendarResourceRead observed;
        try
        {
            observed = await readResource(revision.Href, reconciliation.Token);
        }
        catch (Exception)
        {
            return Unknown();
        }

        if (observed.Code == CalendarResourceReadCode.NotFound)
            return Success(revision);
        if (observed.Code != CalendarResourceReadCode.Success || observed.Snapshot is null)
            return Unknown();
        if (IsSameRevision(revision, observed.Snapshot))
        {
            return new CalendarResourceDeleteResult(
                CalendarResourceDeleteCode.UpstreamUnavailable,
                CalendarMutationState.NotCommitted,
                CurrentSnapshot: observed.Snapshot);
        }
        return Unknown(observed.Snapshot);
    }

    private async Task<CalendarResourceDeleteResult> RefreshConflictAsync(
        string href,
        CancellationToken cancellationToken)
    {
        try
        {
            var observed = await readResource(href, cancellationToken);
            return Rejected(
                CalendarResourceDeleteCode.Conflict,
                observed.Code == CalendarResourceReadCode.Success ? observed.Snapshot : null);
        }
        catch (Exception)
        {
            return Rejected(CalendarResourceDeleteCode.Conflict);
        }
    }

    private static bool IsSameRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot) =>
        string.Equals(snapshot.ResourceHref, revision.Href, StringComparison.Ordinal)
        && string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
        && string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal)
        && snapshot.Projection.Kind == (revision.EntityKind == CalendarEntityKind.Event
            ? CalendarResourceProjectionKind.Event
            : CalendarResourceProjectionKind.Todo);

    private static CalendarResourceDeleteResult Unknown(CalendarResourceSnapshot? snapshot = null) => new(
        CalendarResourceDeleteCode.Indeterminate,
        CalendarMutationState.Unknown,
        CurrentSnapshot: snapshot);

    private static CalendarResourceDeleteResult Success(CalendarResourceRevisionReference revision) =>
        CalendarResourceDeleteResult.Success(new CalendarResourceDeletionReceipt(
            revision.Href,
            revision.EntityUid,
            revision.EntityKind,
            revision.EntityTag));

    private static CalendarResourceDeleteResult CommittedButUnverified(
        CalendarResourceSnapshot? snapshot = null) => new(
        CalendarResourceDeleteCode.CommittedButUnverified,
        CalendarMutationState.Committed,
        CurrentSnapshot: snapshot);

    private static CalendarResourceDeleteCode? ValidateRevisionReference(
        CalendarResourceRevisionReference revision)
    {
        if (string.IsNullOrEmpty(revision.EntityUid)
            || !Enum.IsDefined(revision.EntityKind)
            || !EntityTagHeaderValue.TryParse(revision.EntityTag, out var entityTag)
            || entityTag is null
            || entityTag == EntityTagHeaderValue.Any
            || !string.Equals(entityTag.ToString(), revision.EntityTag, StringComparison.Ordinal))
        {
            return CalendarResourceDeleteCode.InvalidInput;
        }
        return entityTag.IsWeak ? CalendarResourceDeleteCode.ConcurrencyUnavailable : null;
    }

    private static CalendarResourceDeleteResult? ValidateRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot)
    {
        if (!snapshot.SemanticMutationAvailable)
            return Failure(CalendarResourceDeleteCode.OpaqueResource, snapshot);
        var kind = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            ? CalendarEntityKind.Event
            : CalendarEntityKind.Todo;
        if (kind != revision.EntityKind)
            return Failure(CalendarResourceDeleteCode.EntityKindMismatch, snapshot);
        if (!string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
            || !string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal))
        {
            return Failure(CalendarResourceDeleteCode.Conflict, snapshot);
        }
        return null;
    }

    private static CalendarResourceDeleteResult FromReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.InvalidInput => Failure(CalendarResourceDeleteCode.InvalidInput),
        CalendarResourceReadCode.NotFound => Failure(CalendarResourceDeleteCode.NotFound),
        CalendarResourceReadCode.OutsideScope => Failure(CalendarResourceDeleteCode.OutsideScope),
        CalendarResourceReadCode.ConcurrencyUnavailable => Failure(CalendarResourceDeleteCode.ConcurrencyUnavailable),
        CalendarResourceReadCode.PayloadTooLarge => Failure(CalendarResourceDeleteCode.PayloadTooLarge),
        _ => Failure(CalendarResourceDeleteCode.UpstreamProtocolError)
    };

    private static CalendarResourceDeleteResult FromPreflightHttpFailure(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => Failure(CalendarResourceDeleteCode.UpstreamUnauthorized),
        System.Net.HttpStatusCode.Forbidden => Failure(CalendarResourceDeleteCode.UpstreamForbidden),
        System.Net.HttpStatusCode.NotFound => Failure(CalendarResourceDeleteCode.UpstreamProtocolError),
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed =>
            Failure(CalendarResourceDeleteCode.Conflict),
        System.Net.HttpStatusCode.RequestEntityTooLarge => Failure(CalendarResourceDeleteCode.PayloadTooLarge),
        System.Net.HttpStatusCode.TooManyRequests => Failure(
            CalendarResourceDeleteCode.UpstreamRateLimited,
            retryable: true),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented =>
            Failure(CalendarResourceDeleteCode.UnsupportedCapability),
        System.Net.HttpStatusCode.InsufficientStorage => Failure(
            CalendarResourceDeleteCode.UpstreamUnavailable,
            retryable: false),
        >= System.Net.HttpStatusCode.InternalServerError => Failure(
            CalendarResourceDeleteCode.UpstreamUnavailable,
            retryable: true),
        _ => Failure(CalendarResourceDeleteCode.UpstreamUnavailable, retryable: true)
    };

    private static CalendarResourceDeleteResult FromDispatchFailure(
        CalendarResourceDeleteDispatchResult dispatch) => dispatch.Code switch
    {
        CalendarResourceDeleteDispatchCode.NotFound => Rejected(CalendarResourceDeleteCode.NotFound),
        CalendarResourceDeleteDispatchCode.InvalidInput => Rejected(CalendarResourceDeleteCode.InvalidInput),
        CalendarResourceDeleteDispatchCode.UnsupportedCapability => Rejected(CalendarResourceDeleteCode.UnsupportedCapability),
        CalendarResourceDeleteDispatchCode.PayloadTooLarge => Rejected(CalendarResourceDeleteCode.PayloadTooLarge),
        CalendarResourceDeleteDispatchCode.UpstreamUnauthorized => Rejected(CalendarResourceDeleteCode.UpstreamUnauthorized),
        CalendarResourceDeleteDispatchCode.UpstreamForbidden => Rejected(CalendarResourceDeleteCode.UpstreamForbidden),
        CalendarResourceDeleteDispatchCode.UpstreamRateLimited => Rejected(
            CalendarResourceDeleteCode.UpstreamRateLimited,
            retryAfterMilliseconds: dispatch.RetryAfterMilliseconds,
            retryable: true),
        CalendarResourceDeleteDispatchCode.UpstreamProtocolError => Rejected(CalendarResourceDeleteCode.UpstreamProtocolError),
        _ => Rejected(CalendarResourceDeleteCode.UpstreamUnavailable)
    };

    private static CalendarResourceDeleteResult Rejected(
        CalendarResourceDeleteCode code,
        CalendarResourceSnapshot? currentSnapshot = null,
        int? retryAfterMilliseconds = null,
        bool retryable = false) =>
        new(
            code,
            CalendarMutationState.NotCommitted,
            CurrentSnapshot: currentSnapshot,
            RetryAfterMilliseconds: retryAfterMilliseconds,
            Retryable: retryable);

    private static CalendarResourceDeleteResult Failure(
        CalendarResourceDeleteCode code,
        CalendarResourceSnapshot? currentSnapshot = null,
        bool retryable = false) =>
        new(
            code,
            CalendarMutationState.NotAttempted,
            CurrentSnapshot: currentSnapshot,
            Retryable: retryable);
}
