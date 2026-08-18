using System.Net.Http.Headers;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarResourceMoveEngine(
    ICalendarClient calendarClient,
    CalDavOptions options,
    TimeProvider timeProvider,
    Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
    Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        resolveDefaultCalendar,
    Func<string, CancellationToken, Task<CalendarResourceRead>> readResource)
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumInspectedResources = 5_000;
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarResourceMoveResult> MoveAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        var inputFailure = ValidateInput(request);
        if (inputFailure is not null)
            return inputFailure;
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await MoveCoreAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Failure(
                CalendarResourceMoveCode.LimitExhausted,
                limitDimension: CalendarResourceMoveLimitDimension.ElapsedTime);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarResourceMoveCode.UpstreamUnavailable, retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return FromPreflightHttpFailure(exception.StatusCode);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Failure(CalendarResourceMoveCode.UpstreamUnavailable, retryable: true);
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarResourceMoveCode.UpstreamProtocolError);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarResourceMoveCode.UnsupportedCapability);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Failure(
                CalendarResourceMoveCode.LimitExhausted,
                calendarCount: exception.CalendarCount);
        }
    }

    private async Task<CalendarResourceMoveResult> MoveCoreAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        var destination = await ResolveDestinationAsync(request, cancellationToken);
        if (destination.Failure is not null)
            return destination.Failure;
        var destinationCalendar = destination.Calendar!;

        var concurrencyFailure = ValidateStrongRevision(request.Revision);
        if (concurrencyFailure is not null)
            return concurrencyFailure;

        var sourceRead = await readResource(request.Revision.Href, cancellationToken);
        if (sourceRead.Code != CalendarResourceReadCode.Success || sourceRead.Snapshot is null)
            return FromReadFailure(sourceRead.Code);
        var revisionFailure = ValidateRevision(request.Revision, sourceRead.Snapshot);
        if (revisionFailure is not null)
            return revisionFailure;
        if (string.Equals(destinationCalendar.Href, sourceRead.Snapshot.CalendarHref, StringComparison.Ordinal))
            return Failure(CalendarResourceMoveCode.InvalidInput);

        var destinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            destinationCalendar.Href,
            request.Revision.EntityUid);
        var destinationRead = await calendarClient.GetCalendarResourceAsync(destinationHref, cancellationToken);
        if (destinationRead.Code != CalendarResourceReadCode.NotFound)
            return DestinationPreflightFailure(destinationRead);
        var uidAvailability = await CheckDestinationUidAsync(
            destinationCalendar.Href,
            request.Revision.EntityUid,
            request.Revision.EntityKind,
            cancellationToken);
        var uidFailure = DestinationUidFailure(uidAvailability);
        if (uidFailure is not null)
            return uidFailure;

        var dispatch = await calendarClient.MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(
                request.Revision.Href,
                destinationHref,
                request.Revision.EntityTag),
            cancellationToken);
        if (dispatch is null)
            return Rejected(CalendarResourceMoveCode.UpstreamProtocolError);
        return await CompleteDispatchAsync(
            request.Revision,
            sourceRead.Snapshot,
            destinationCalendar.Href,
            destinationHref,
            dispatch,
            cancellationToken);
    }

    private async Task<ResolvedMoveDestination> ResolveDestinationAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        var selection = await SelectCalendarAsync(
            request.Destination,
            request.Revision.EntityKind,
            cancellationToken);
        if (selection.Code != CalendarSelectionCode.Success)
            return new(null, SelectionFailure(selection));
        var calendar = selection.Calendar!;
        if (!TryValidateCalendarHref(calendar.Href))
            return new(null, Failure(CalendarResourceMoveCode.UpstreamProtocolError));
        return Advertises(calendar, request.Revision.EntityKind)
            ? new(calendar, null)
            : new(null, Failure(CalendarResourceMoveCode.UnsupportedCapability, candidates: [calendar]));
    }

    private async Task<DestinationUidAvailability> CheckDestinationUidAsync(
        string calendarHref,
        string uid,
        CalendarEntityKind requestedKind,
        CancellationToken cancellationToken)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in GetUidQueryKinds(requestedKind))
        {
            hrefs.UnionWith(await calendarClient.QueryCalendarResourceHrefsAsync(
                calendarHref,
                kind,
                from: null,
                to: null,
                cancellationToken));
            if (hrefs.Count > MaximumInspectedResources)
                return DestinationUidAvailability.LimitExhausted;
        }
        foreach (var href in hrefs)
        {
            var availability = InspectUidRead(
                await calendarClient.GetCalendarResourceAsync(href, cancellationToken),
                uid);
            if (availability != DestinationUidAvailability.Available)
                return availability;
        }
        return DestinationUidAvailability.Available;
    }

    private static DestinationUidAvailability InspectUidRead(CalendarResourceRead read, string uid)
    {
        if (read.Code == CalendarResourceReadCode.NotFound)
            return DestinationUidAvailability.Available;
        if (read.Code == CalendarResourceReadCode.ConcurrencyUnavailable
            && read.AuthoritativeUtf8.IsEmpty)
        {
            return DestinationUidAvailability.ConcurrencyUnavailable;
        }
        if (read.Code is CalendarResourceReadCode.Success or CalendarResourceReadCode.ConcurrencyUnavailable)
        {
            var projected = CalendarResourceProjector.Project(read.AuthoritativeUtf8.Span);
            if (CalendarResourceProjector.ContainsEntityUid(projected, uid))
                return DestinationUidAvailability.Exists;
            return projected.Projection.Kind == CalendarResourceProjectionKind.Opaque
                ? DestinationUidAvailability.OpaqueResource
                : DestinationUidAvailability.Available;
        }
        return read.Code switch
        {
            CalendarResourceReadCode.PayloadTooLarge => DestinationUidAvailability.PayloadTooLarge,
            _ => DestinationUidAvailability.UpstreamProtocolError
        };
    }

    private static CalendarResourceMoveResult? DestinationUidFailure(
        DestinationUidAvailability availability) => availability switch
        {
            DestinationUidAvailability.Available => null,
            DestinationUidAvailability.Exists => Failure(CalendarResourceMoveCode.DestinationConflict),
            DestinationUidAvailability.OpaqueResource => Failure(CalendarResourceMoveCode.OpaqueResource),
            DestinationUidAvailability.ConcurrencyUnavailable => Failure(CalendarResourceMoveCode.ConcurrencyUnavailable),
            DestinationUidAvailability.LimitExhausted => Failure(
                CalendarResourceMoveCode.LimitExhausted,
                resourcesInspected: MaximumInspectedResources + 1),
            DestinationUidAvailability.PayloadTooLarge => Failure(CalendarResourceMoveCode.PayloadTooLarge),
            _ => Failure(CalendarResourceMoveCode.UpstreamProtocolError)
        };

    private static IEnumerable<CalendarEntityKind> GetUidQueryKinds(CalendarEntityKind requestedKind)
    {
        yield return requestedKind;
        yield return requestedKind == CalendarEntityKind.Event
            ? CalendarEntityKind.Todo
            : CalendarEntityKind.Event;
    }

    private async Task<CalendarResourceMoveResult> CompleteDispatchAsync(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source,
        string destinationCalendarHref,
        string destinationHref,
        CalendarResourceMoveDispatchResult dispatch,
        CancellationToken cancellationToken)
    {
        if (dispatch.Code == CalendarResourceMoveDispatchCode.Conflict)
            return await ClassifyConflictAsync(revision, destinationCalendarHref, destinationHref, cancellationToken);
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
        var destination = await ObserveResourceAsync(
            destinationCalendarHref,
            destinationHref,
            verification.Token);
        var source = await ObserveResourceAsync(sourceCalendarHref, sourceHref, verification.Token);
        return new MoveObservation(destination, source);
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
                await calendarClient.GetCalendarResourceAsync(resourceHref, cancellationToken));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<CalendarResourceMoveResult> ClassifyConflictAsync(
        CalendarResourceRevisionReference revision,
        string destinationCalendarHref,
        string destinationHref,
        CancellationToken cancellationToken)
    {
        try
        {
            var destination = await calendarClient.GetCalendarResourceAsync(destinationHref, cancellationToken);
            if (destination.Code is CalendarResourceReadCode.Success
                or CalendarResourceReadCode.ConcurrencyUnavailable)
            {
                return Rejected(CalendarResourceMoveCode.DestinationConflict);
            }
            var source = await readResource(revision.Href, cancellationToken);
            return Rejected(
                CalendarResourceMoveCode.Conflict,
                source.Code == CalendarResourceReadCode.Success ? source.Snapshot : null);
        }
        catch (Exception)
        {
            return Rejected(CalendarResourceMoveCode.Conflict);
        }
    }

    private static CalendarResourceMoveResult ClassifyDispatched(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source,
        MoveObservation observation)
    {
        var verified = VerifiedMoveResult(source, observation);
        if (verified is not null)
            return verified;
        if (IsCommittedWithoutStrongTag(source, observation))
            return PostWrite(CalendarResourceMoveCode.CommittedButConcurrencyUnavailable);
        if (IsSourceUnchangedAfterPossibleDispatch(revision, observation))
            return Rejected(CalendarResourceMoveCode.UpstreamProtocolError, observation.Source!.Snapshot);
        if (HasObservedDestination(observation))
            return PostWrite(CalendarResourceMoveCode.FidelityFailure, observation.Destination?.Snapshot);
        return PostWrite(CalendarResourceMoveCode.CommittedButUnverified, observation.Source?.Snapshot);
    }

    private static CalendarResourceMoveResult ClassifyPossiblyDispatched(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source,
        MoveObservation observation)
    {
        var verified = VerifiedMoveResult(source, observation);
        if (verified is not null)
            return verified;
        if (IsCommittedWithoutStrongTag(source, observation))
            return PostWrite(CalendarResourceMoveCode.CommittedButConcurrencyUnavailable);
        if (IsSourceUnchangedAfterPossibleDispatch(revision, observation))
            return Rejected(CalendarResourceMoveCode.UpstreamUnavailable, observation.Source!.Snapshot, retryable: true);
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
        return CalendarResourceMoveFidelity.IsCompleteMatch(source, destination)
            ? CalendarResourceMoveResult.Success(destination)
            : PostWrite(CalendarResourceMoveCode.FidelityFailure, destination);
    }

    private static bool IsSourceUnchangedAfterPossibleDispatch(
        CalendarResourceRevisionReference revision,
        MoveObservation observation) =>
        IsSameRevision(revision, observation.Source?.Snapshot);

    private static bool IsCommittedWithoutStrongTag(
        CalendarResourceSnapshot source,
        MoveObservation observation) =>
        observation.Destination?.Code == CalendarResourceReadCode.ConcurrencyUnavailable
        && observation.Source?.Code == CalendarResourceReadCode.NotFound
        && source.AuthoritativeUtf8.Span.SequenceEqual(observation.Destination.AuthoritativeUtf8.Span);

    private static bool HasObservedDestination(MoveObservation observation) =>
        observation.Destination?.Code is CalendarResourceReadCode.Success
            or CalendarResourceReadCode.ConcurrencyUnavailable;

    private async Task<CalendarSelectionResult> SelectCalendarAsync(
        CalendarMoveDestination destination,
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        var discovered = await calendarClient.GetCalendarsAsync(cancellationToken);
        var scoped = applyScope(discovered).Items;
        if (destination.Mode == CalendarEntityScopeMode.Default)
            return resolveDefaultCalendar(entityKind, discovered, scoped);
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
        CalendarResourceReadCode.Success or CalendarResourceReadCode.ConcurrencyUnavailable =>
            Failure(CalendarResourceMoveCode.DestinationConflict),
        CalendarResourceReadCode.PayloadTooLarge => Failure(CalendarResourceMoveCode.PayloadTooLarge),
        _ => Failure(CalendarResourceMoveCode.UpstreamProtocolError)
    };

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

    private static CalendarResourceMoveResult FromPreflightHttpFailure(System.Net.HttpStatusCode? statusCode) =>
        statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => Failure(CalendarResourceMoveCode.UpstreamUnauthorized),
            System.Net.HttpStatusCode.Forbidden => Failure(CalendarResourceMoveCode.UpstreamForbidden),
            System.Net.HttpStatusCode.RequestEntityTooLarge => Failure(CalendarResourceMoveCode.PayloadTooLarge),
            System.Net.HttpStatusCode.TooManyRequests => Failure(CalendarResourceMoveCode.UpstreamRateLimited, retryable: true),
            System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented =>
                Failure(CalendarResourceMoveCode.UnsupportedCapability),
            System.Net.HttpStatusCode.InsufficientStorage => Failure(CalendarResourceMoveCode.UpstreamUnavailable),
            >= System.Net.HttpStatusCode.InternalServerError => Failure(CalendarResourceMoveCode.UpstreamUnavailable, retryable: true),
            _ => Failure(CalendarResourceMoveCode.UpstreamProtocolError)
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
        bool retryable = false) =>
        new(
            code,
            CalendarMutationState.NotCommitted,
            snapshot,
            RetryAfterMilliseconds: retryAfterMilliseconds,
            Retryable: retryable,
            Phase: code == CalendarResourceMoveCode.Conflict
                ? CalendarResourceMovePhase.TargetRevision
                : CalendarResourceMovePhase.Execution);

    private static CalendarResourceMoveResult Failure(
        CalendarResourceMoveCode code,
        CalendarResourceSnapshot? snapshot = null,
        IReadOnlyList<CalendarDescriptor>? candidates = null,
        bool retryable = false,
        CalendarResourceMoveLimitDimension? limitDimension = null,
        CalendarResourceMovePhase? phase = null,
        int? resourcesInspected = null,
        int? calendarCount = null) =>
        new(
            code,
            CalendarMutationState.NotAttempted,
            snapshot,
            candidates ?? [],
            Retryable: retryable,
            LimitDimension: limitDimension,
            Phase: phase,
            ResourcesInspected: resourcesInspected,
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
        CalendarResourceMoveResult? Failure);

    private enum DestinationUidAvailability
    {
        Available,
        Exists,
        OpaqueResource,
        ConcurrencyUnavailable,
        LimitExhausted,
        PayloadTooLarge,
        UpstreamProtocolError
    }
}
