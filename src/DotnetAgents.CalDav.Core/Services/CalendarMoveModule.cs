using System.Net.Http.Headers;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarMoveModule(
    ICalendarMoveTransport transport,
    CalDavOptions options,
    TimeProvider timeProvider)
{
    private const int MaximumDiagnostics = 32;
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);

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
        var destination = await ResolveDestinationAsync(request, cancellationToken);
        if (destination.Failure is not null)
            return destination.Failure;
        var destinationCalendar = destination.Calendar!;
        var sourceCalendar = destination.SourceCalendar!;

        if (!string.Equals(
                options.InteroperabilityProfile,
                CalDavInteroperabilityProfiles.Radicale_3_7_8,
                StringComparison.Ordinal))
        {
            return Failure(
                CalendarResourceMoveCode.UnsupportedCapability,
                candidates: [destinationCalendar],
                phase: CalendarResourceMovePhase.SelectionDiscoveryCapability);
        }
        if (string.Equals(destinationCalendar.Href, sourceCalendar.Href, StringComparison.Ordinal))
        {
            return Failure(
                CalendarResourceMoveCode.InvalidInput,
                phase: CalendarResourceMovePhase.SelectionDiscoveryCapability);
        }

        var concurrencyFailure = ValidateStrongRevision(request.Revision);
        if (concurrencyFailure is not null)
            return concurrencyFailure;

        setFailurePhase(CalendarResourceMovePhase.TargetRevision);
        var sourceRead = Attach(
            sourceCalendar.Href,
            await transport.ReadSourceAsync(sourceCalendar.Href, request.Revision.Href, cancellationToken));
        if (sourceRead.Code != CalendarResourceReadCode.Success || sourceRead.Snapshot is null)
            return FromReadFailure(sourceRead.Code);
        var revisionFailure = ValidateRevision(request.Revision, sourceRead.Snapshot);
        if (revisionFailure is not null)
            return RecordRevisionFailure(revisionFailure);
        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            destinationCalendar.Href,
            request.Revision.EntityUid);
        setFailurePhase(CalendarResourceMovePhase.Execution);
        var destinationRead = await transport.ProbeDestinationPresenceAsync(
            destinationCalendar.Href,
            destinationHref,
            cancellationToken);
        if (destinationRead.Code != CalendarResourceReadCode.NotFound)
            return RecordDestinationPreflightFailure(destinationRead);
        CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.None);

        var dispatch = await transport.DispatchAsync(
            sourceCalendar.Href,
            destinationCalendar.Href,
            new CalendarResourceMoveDispatchRequest(
                request.Revision.Href,
                destinationHref,
                request.Revision.EntityTag),
            cancellationToken);
        if (dispatch is null)
        {
            CalendarOperationProgress.SetMoveDispatch(CalendarMoveDispatchClassification.Rejected);
            return Rejected(CalendarResourceMoveCode.UpstreamProtocolError);
        }
        RecordDispatch(dispatch);
        return await CompleteDispatchAsync(
            request.Revision,
            sourceRead.Snapshot,
            destinationCalendar.Href,
            destinationHref,
            dispatch);
    }

    private async Task<ResolvedMoveDestination> ResolveDestinationAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        var discovery = await transport.DiscoverCalendarsAsync(cancellationToken);
        var scoped = discovery.ScopedDiscovery.Items;
        var sourceUri = new Uri(request.Revision.Href, UriKind.Absolute);
        var sourceCalendar = scoped
            .Where(calendar => IsDirectResourceOf(sourceUri, calendar.Href))
            .OrderByDescending(calendar => calendar.Href.Length)
            .FirstOrDefault();
        if (sourceCalendar is null)
        {
            return new(
                null,
                null,
                Failure(
                    CalendarResourceMoveCode.OutsideScope,
                    phase: CalendarResourceMovePhase.OriginScopeAuthorization));
        }

        var selection = SelectCalendar(
            request.Destination,
            request.Revision.EntityKind,
            discovery,
            scoped);
        if (selection.Code != CalendarSelectionCode.Success)
            return new(null, sourceCalendar, SelectionFailure(selection));
        var calendar = selection.Calendar!;
        if (!TryValidateCalendarHref(calendar.Href))
            return new(null, sourceCalendar, Failure(CalendarResourceMoveCode.UpstreamProtocolError));
        return Advertises(calendar, request.Revision.EntityKind)
            ? new(calendar, sourceCalendar, null)
            : new(null, sourceCalendar, Failure(CalendarResourceMoveCode.UnsupportedCapability, candidates: [calendar]));
    }

    private async Task<CalendarResourceMoveResult> CompleteDispatchAsync(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source,
        string destinationCalendarHref,
        string destinationHref,
        CalendarResourceMoveDispatchResult dispatch)
    {
        if (dispatch.Code is not (CalendarResourceMoveDispatchCode.Dispatched
            or CalendarResourceMoveDispatchCode.PossiblyDispatched))
        {
            return FromDispatchFailure(dispatch);
        }

        var observation = await ObserveAfterDispatchAsync(
            destinationCalendarHref,
            destinationHref,
            source.CalendarHref,
            revision.Href);
        return dispatch.Code == CalendarResourceMoveDispatchCode.Dispatched
            ? ClassifyDispatched(revision, source, observation)
            : ClassifyPossiblyDispatched(revision, source, observation);
    }

    private async Task<MoveObservation> ObserveAfterDispatchAsync(
        string destinationCalendarHref,
        string destinationHref,
        string sourceCalendarHref,
        string sourceHref)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        var destinationTask = ObserveResourceAsync(
            destinationCalendarHref,
            destinationHref,
            verification.Token);
        var sourceTask = ObserveResourceAsync(sourceCalendarHref, sourceHref, verification.Token);
        await Task.WhenAll(destinationTask, sourceTask);
        return new MoveObservation(await destinationTask, await sourceTask);
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
                await transport.ObserveResourceAsync(calendarHref, resourceHref, cancellationToken));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static CalendarResourceMoveResult ClassifyDispatched(
        CalendarResourceRevisionReference _,
        CalendarResourceSnapshot source,
        MoveObservation observation)
    {
        var verified = VerifiedMoveResult(source, observation);
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
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source,
        MoveObservation observation)
    {
        if (IsFaithfulDestinationWithAbsentSource(source, observation))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.FaithfulDestinationSourceAbsent);
            return CalendarResourceMoveResult.Success(observation.Destination!.Snapshot!);
        }
        if (IsSourceUnchangedAfterPossibleDispatch(revision, observation))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.UnchangedSourceDestinationAbsent);
            return Rejected(CalendarResourceMoveCode.UpstreamUnavailable, observation.Source!.Snapshot, retryable: true);
        }
        CalendarOperationProgress.SetMoveReconciliation(ReconciliationObservation(observation));
        return Unknown(observation.Source?.Snapshot);
    }

    private static CalendarResourceMoveResult? VerifiedMoveResult(
        CalendarResourceSnapshot source,
        MoveObservation observation)
    {
        if (observation.Destination?.Snapshot is not { } destination
            || observation.Source?.Code != CalendarResourceReadCode.NotFound)
        {
            return null;
        }
        if (CalendarResourceMoveFidelity.IsCompleteMatch(source, destination))
        {
            CalendarOperationProgress.SetMoveReconciliation(
                CalendarMoveReconciliationClassification.FaithfulDestinationSourceAbsent);
            return CalendarResourceMoveResult.Success(destination);
        }
        CalendarOperationProgress.SetMoveReconciliation(
            CalendarMoveReconciliationClassification.DivergentDestinationSourceAbsent);
        return PostWrite(CalendarResourceMoveCode.FidelityFailure, destination);
    }

    private static bool IsSourceUnchangedAfterPossibleDispatch(
        CalendarResourceRevisionReference revision,
        MoveObservation observation) =>
        observation.Destination?.Code == CalendarResourceReadCode.NotFound
        && IsSameRevision(revision, observation.Source?.Snapshot);

    private static bool IsFaithfulDestinationWithAbsentSource(
        CalendarResourceSnapshot source,
        MoveObservation observation) =>
        observation.Destination?.Snapshot is { } destination
        && observation.Source?.Code == CalendarResourceReadCode.NotFound
        && CalendarResourceMoveFidelity.IsCompleteMatch(source, destination);

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
        CalendarOperationProgress.SetMoveDispatch(dispatch.Code switch
        {
            CalendarResourceMoveDispatchCode.Dispatched => CalendarMoveDispatchClassification.Dispatched,
            CalendarResourceMoveDispatchCode.PossiblyDispatched => CalendarMoveDispatchClassification.PossiblyDispatched,
            _ => CalendarMoveDispatchClassification.Rejected
        });
        CalendarOperationProgress.SetMoveCollision(dispatch.Code switch
        {
            CalendarResourceMoveDispatchCode.DestinationConflict => CalendarMoveCollisionClassification.DestinationHref,
            CalendarResourceMoveDispatchCode.Conflict => dispatch.CollisionKind switch
            {
                CalendarResourceMoveDispatchCollisionKind.DestinationHref => CalendarMoveCollisionClassification.DestinationHref,
                CalendarResourceMoveDispatchCollisionKind.Uid => CalendarMoveCollisionClassification.Uid,
                _ => CalendarMoveCollisionClassification.Unclassified
            },
            _ => CalendarMoveCollisionClassification.None
        });
    }

    private static CalendarSelectionResult SelectCalendar(
        CalendarMoveDestination destination,
        CalendarEntityKind entityKind,
        CalendarMoveDiscoveryResult discovery,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        if (destination.Mode == CalendarEntityScopeMode.Default)
            return discovery.ResolveDefault(entityKind);
        var matches = FindCalendarMatches(scoped, destination.Calendar!);
        if (matches.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, scoped.Take(MaximumDiagnostics).ToArray());
        return matches.Length == 1
            ? CalendarSelectionResult.Success(matches[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, matches.Take(MaximumDiagnostics).ToArray());
    }

    private CalendarResourceMoveResult? ValidateInput(CalendarResourceMoveRequest request)
    {
        var revisionFailure = ValidateRevisionShape(request.Revision);
        if (revisionFailure is not null)
            return revisionFailure;
        var sourceScopeFailure = ValidateResourceHrefScope(request.Revision.Href);
        if (sourceScopeFailure is not null)
            return sourceScopeFailure;
        if (!HasValidDestinationShape(request.Destination))
            return Failure(CalendarResourceMoveCode.InvalidInput);
        return ValidateSelectedDestinationScope(request.Destination);
    }

    private CalendarResourceMoveResult? ValidateSelectedDestinationScope(CalendarMoveDestination destination)
    {
        var selectedHref = destination.Mode == CalendarEntityScopeMode.Selected
            ? destination.Calendar?.Href
            : null;
        if (selectedHref is null)
            return null;
        if (!TryValidateCalendarHref(selectedHref))
            return Failure(CalendarResourceMoveCode.InvalidInput);
        var scope = ParseScope(options.CalendarHrefs);
        return scope.Count > 0 && !scope.Contains(selectedHref, StringComparer.Ordinal)
            ? Failure(CalendarResourceMoveCode.OutsideScope)
            : null;
    }

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

    private bool TryValidateCalendarHref(string href)
    {
        return TryParseSafeHref(href, requireTrailingSlash: true, out var candidate)
            && HasSameOrigin(new Uri(options.BaseUrl, UriKind.Absolute), candidate);
    }

    private CalendarResourceMoveResult? ValidateResourceHrefScope(string href)
    {
        if (!TryParseSafeHref(href, requireTrailingSlash: false, out var resource)
            || !HasSameOrigin(new Uri(options.BaseUrl, UriKind.Absolute), resource))
        {
            return Failure(CalendarResourceMoveCode.InvalidInput);
        }
        var scope = ParseScope(options.CalendarHrefs);
        return scope.Count > 0 && !scope.Any(calendarHref => IsDirectResourceOf(resource, calendarHref))
            ? Failure(CalendarResourceMoveCode.OutsideScope)
            : null;
    }

    private static bool TryParseSafeHref(string href, bool requireTrailingSlash, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query)
            || candidate.AbsolutePath.EndsWith('/') != requireTrailingSlash
            || candidate.AbsolutePath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        uri = candidate;
        return true;
    }

    private static bool IsDirectResourceOf(Uri resource, string calendarHref)
    {
        if (!Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendar)
            || !HasSameOrigin(resource, calendar)
            || !calendar.AbsolutePath.EndsWith('/'))
        {
            return false;
        }
        if (!resource.AbsolutePath.StartsWith(calendar.AbsolutePath, StringComparison.Ordinal))
            return false;
        var relative = resource.AbsolutePath[calendar.AbsolutePath.Length..];
        return relative.Length > 0
            && !relative.Contains('/');
    }

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
        retryable: dispatch.Code == CalendarResourceMoveDispatchCode.UpstreamRateLimited,
        phase: CalendarResourceMovePhase.Execution);

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

    private static CalendarResourceMoveResult SelectionFailure(CalendarSelectionResult selection) => Failure(
        selection.Code switch
        {
            CalendarSelectionCode.NotFound => CalendarResourceMoveCode.NotFound,
            CalendarSelectionCode.Ambiguous => CalendarResourceMoveCode.Ambiguous,
            CalendarSelectionCode.OutsideScope => CalendarResourceMoveCode.OutsideScope,
            _ => CalendarResourceMoveCode.UnsupportedCapability
        },
        candidates: selection.Candidates);

    private static CalendarResourceRead Attach(string calendarHref, CalendarResourceRead read) =>
        read.Code == CalendarResourceReadCode.Success
            ? CalendarResourceProjector.AttachSnapshot(calendarHref, read)
            : read;

    private static bool HasValidDestinationShape(CalendarMoveDestination destination) => destination.Mode switch
    {
        CalendarEntityScopeMode.Default => destination.Calendar is null,
        CalendarEntityScopeMode.Selected => HasExactlyOneSelector(destination.Calendar),
        _ => false
    };

    private static bool HasExactlyOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var hasName = !string.IsNullOrWhiteSpace(reference.Name);
        var hasHref = !string.IsNullOrWhiteSpace(reference.Href);
        return hasName != hasHref
            && (!hasName || string.Equals(reference.Name, reference.Name!.Trim(), StringComparison.Ordinal));
    }

    private static CalendarDescriptor[] FindCalendarMatches(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarReference reference) => calendars.Where(calendar => reference.Name is not null
            ? string.Equals(calendar.DisplayName?.Trim(), reference.Name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(calendar.Href, reference.Href, StringComparison.Ordinal)).ToArray();

    private static bool Advertises(CalendarDescriptor calendar, CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport == EntityKindSupport.Advertised,
        CalendarEntityKind.Todo => calendar.TodoSupport == EntityKindSupport.Advertised,
        _ => false
    };

    private static bool IsSameRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot? snapshot) => snapshot is not null
        && string.Equals(snapshot.ResourceHref, revision.Href, StringComparison.Ordinal)
        && string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
        && string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal)
        && snapshot.Projection.Kind == (revision.EntityKind == CalendarEntityKind.Event
            ? CalendarResourceProjectionKind.Event
            : CalendarResourceProjectionKind.Todo);

    private static CalendarResourceMoveResult PostWrite(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null) =>
        new(
            code,
            CalendarMutationState.Committed,
            snapshot,
            Phase: CalendarResourceMovePhase.PostWriteVerificationOrReconciliation);

    private static CalendarResourceMoveResult Unknown(CalendarResourceSnapshot? snapshot = null) =>
        new(
            CalendarResourceMoveCode.Indeterminate,
            CalendarMutationState.Unknown,
            snapshot,
            Phase: CalendarResourceMovePhase.PostWriteVerificationOrReconciliation);

    private static CalendarResourceMoveResult Rejected(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null,
        int? retryAfterMilliseconds = null,
        bool retryable = false,
        CalendarResourceMovePhase phase = CalendarResourceMovePhase.Execution) =>
        new(
            code,
            CalendarMutationState.NotCommitted,
            snapshot,
            RetryAfterMilliseconds: retryAfterMilliseconds,
            Retryable: retryable,
            Phase: phase);

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

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private sealed record MoveObservation(
        CalendarResourceRead? Destination,
        CalendarResourceRead? Source);

    private sealed record ResolvedMoveDestination(
        CalendarDescriptor? Calendar,
        CalendarDescriptor? SourceCalendar,
        CalendarResourceMoveResult? Failure);

}
