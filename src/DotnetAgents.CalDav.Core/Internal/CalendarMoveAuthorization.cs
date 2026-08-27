using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Owns complete Move target authorization for both Move surfaces.</summary>
internal sealed class CalendarMoveAuthorization
{
    private readonly CalendarOperationDiscovery _discovery;
    private readonly Uri _origin;
    private readonly IReadOnlyList<string> _scope;
    private readonly string? _interoperabilityProfile;

    internal CalendarMoveAuthorization(
        CalendarOperationDiscovery discovery,
        CalDavOptions options)
    {
        _discovery = discovery;
        _origin = new Uri(options.BaseUrl, UriKind.Absolute);
        _scope = ParseScope(options.CalendarHrefs);
        _interoperabilityProfile = options.InteroperabilityProfile;
    }

    public async Task<CalendarMoveAuthorizationResult> AuthorizeAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken)
    {
        var input = ValidateSemanticInput(request);
        if (input is SemanticInputAuthorization.Rejected invalid)
            return Reject(invalid.Failure);
        var sourceUri = ((SemanticInputAuthorization.Valid)input).SourceUri;
        var authority = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var sourceResolution = ResolveDirectOwner(
            sourceUri,
            authority.Discovery.Items,
            CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing,
            CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous);
        if (sourceResolution is CalendarResolution.Rejected sourceFailure)
            return Reject(sourceFailure.Failure);
        var source = ((CalendarResolution.Resolved)sourceResolution).Calendar;
        var destinationResolution = ResolveSemanticDestination(request, authority);
        if (destinationResolution is CalendarResolution.Rejected destinationFailure)
            return Reject(destinationFailure.Failure);
        var destination = ((CalendarResolution.Resolved)destinationResolution).Calendar;
        if (string.Equals(source.Href, destination.Href, StringComparison.Ordinal))
            return Reject(CalendarMoveAuthorizationFailureReason.SameCalendarNotAllowed);
        return new CalendarMoveAuthorizationResult.Authorized(new CalendarMoveAuthorizedTarget(
            request.Revision.Href,
            CalendarResourceCreateProtocol.BuildResourceHref(destination.Href, request.Revision.EntityUid),
            source,
            destination));
    }

    private SemanticInputAuthorization ValidateSemanticInput(CalendarResourceMoveRequest request)
    {
        if (!TryParseCanonicalHref(request.Revision.Href, requireTrailingSlash: false, out var sourceUri))
            return SemanticInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref));
        if (!HasSameOrigin(_origin, sourceUri))
            return SemanticInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.OriginMismatch));
        if (_scope.Count > 0 && !_scope.Any(calendarHref => IsDirectResourceOf(sourceUri, calendarHref)))
            return SemanticInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.OutsideCalendarScope));
        var selectionFailure = ValidateSelectedCalendar(request.Destination);
        return selectionFailure is null
            ? new SemanticInputAuthorization.Valid(sourceUri)
            : SemanticInputAuthorization.Reject(Failure(selectionFailure.Value));
    }

    private CalendarResolution ResolveSemanticDestination(
        CalendarResourceMoveRequest request,
        CalendarOperationDiscoveryResult authority)
    {
        var selection = request.Destination.Mode == CalendarEntityScopeMode.Default
            ? authority.Default(request.Revision.EntityKind)
            : SelectCalendar(authority.Discovery.Items, request.Destination.Calendar!);
        if (selection.Code != CalendarSelectionCode.Success)
        {
            return CalendarResolution.Reject(Failure(
                selection.Code switch
                {
                    CalendarSelectionCode.Ambiguous => CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous,
                    CalendarSelectionCode.OutsideScope => CalendarMoveAuthorizationFailureReason.OutsideCalendarScope,
                    CalendarSelectionCode.UnsupportedCapability =>
                        CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised,
                    _ => CalendarMoveAuthorizationFailureReason.DestinationSelectionNotFound
                },
                selection.Candidates));
        }
        if (selection.Calendar is null)
            return CalendarResolution.Reject(Failure(CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent));
        if (!TryParseCanonicalHref(selection.Calendar.Href, requireTrailingSlash: true, out var destinationUri)
            || !HasSameOrigin(_origin, destinationUri))
        {
            return CalendarResolution.Reject(Failure(CalendarMoveAuthorizationFailureReason.InvalidResolvedCalendar));
        }
        var resolvedDestinations = authority.Discovery.Items
            .Where(calendar => string.Equals(calendar.Href, selection.Calendar.Href, StringComparison.Ordinal))
            .ToArray();
        if (resolvedDestinations.Length != 1
            || !SameCalendar(resolvedDestinations[0], selection.Calendar))
        {
            return CalendarResolution.Reject(Failure(CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent));
        }
        return RequireMoveCapability(resolvedDestinations[0], request.Revision.EntityKind);
    }

    private CalendarMoveAuthorizationFailureReason? ValidateSelectedCalendar(CalendarMoveDestination destination)
    {
        if (destination.Mode == CalendarEntityScopeMode.Default)
            return destination.Calendar is null ? null : CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar;
        if (destination.Mode != CalendarEntityScopeMode.Selected || !HasExactlyOneSelector(destination.Calendar))
            return CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar;
        if (destination.Calendar!.Href is null)
            return null;
        if (!TryParseCanonicalHref(destination.Calendar.Href, requireTrailingSlash: true, out var calendar))
            return CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar;
        if (!HasSameOrigin(_origin, calendar))
            return CalendarMoveAuthorizationFailureReason.OriginMismatch;
        return _scope.Count > 0 && !_scope.Contains(destination.Calendar.Href, StringComparer.Ordinal)
            ? CalendarMoveAuthorizationFailureReason.OutsideCalendarScope
            : null;
    }

    private static bool HasExactlyOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var hasName = !string.IsNullOrWhiteSpace(reference.Name);
        var hasHref = !string.IsNullOrWhiteSpace(reference.Href);
        return hasName != hasHref
            && (!hasName || string.Equals(reference.Name, reference.Name!.Trim(), StringComparison.Ordinal));
    }

    private static CalendarSelectionResult SelectCalendar(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarReference reference)
    {
        var matches = calendars.Where(calendar => reference.Name is not null
            ? string.Equals(calendar.DisplayName?.Trim(), reference.Name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(calendar.Href, reference.Href, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars.Take(32).ToArray());
        return matches.Length == 1
            ? CalendarSelectionResult.Success(matches[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, matches.Take(32).ToArray());
    }

    public async Task<CalendarMoveAuthorizationResult> AuthorizeAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var input = ValidateExactInput(request);
        if (input is ExactInputAuthorization.Rejected invalid)
            return Reject(invalid.Failure);
        var valid = (ExactInputAuthorization.Valid)input;
        var authority = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var sourceResolution = ResolveDirectOwner(
            valid.SourceUri,
            authority.Discovery.Items,
            CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing,
            CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous);
        if (sourceResolution is CalendarResolution.Rejected sourceFailure)
            return Reject(sourceFailure.Failure);
        var destinationResolution = ResolveDirectOwner(
            valid.DestinationUri,
            authority.Discovery.Items,
            CalendarMoveAuthorizationFailureReason.DestinationOwnershipMissing,
            CalendarMoveAuthorizationFailureReason.DestinationOwnershipAmbiguous);
        if (destinationResolution is CalendarResolution.Rejected destinationFailure)
            return Reject(destinationFailure.Failure);
        var source = ((CalendarResolution.Resolved)sourceResolution).Calendar;
        var capability = RequireMoveCapability(
            ((CalendarResolution.Resolved)destinationResolution).Calendar,
            request.Revision.EntityKind);
        if (capability is CalendarResolution.Rejected capabilityFailure)
            return Reject(capabilityFailure.Failure);
        var destination = ((CalendarResolution.Resolved)capability).Calendar;
        return new CalendarMoveAuthorizationResult.Authorized(new CalendarMoveAuthorizedTarget(
            request.Revision.Href,
            request.DestinationHref,
            source,
            destination));
    }

    private ExactInputAuthorization ValidateExactInput(CalendarExactMoveRequest request)
    {
        if (!TryParseCanonicalHref(request.Revision.Href, requireTrailingSlash: false, out var sourceUri)
            || !TryParseCanonicalHref(request.DestinationHref, requireTrailingSlash: false, out var destinationUri))
        {
            return ExactInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref));
        }
        if (string.Equals(request.Revision.Href, request.DestinationHref, StringComparison.Ordinal))
            return ExactInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.SameResourceHref));
        if (!HasSameOrigin(_origin, sourceUri) || !HasSameOrigin(_origin, destinationUri))
            return ExactInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.OriginMismatch));
        if (_scope.Count > 0
            && (!_scope.Any(calendarHref => IsDirectResourceOf(sourceUri, calendarHref))
                || !_scope.Any(calendarHref => IsDirectResourceOf(destinationUri, calendarHref))))
        {
            return ExactInputAuthorization.Reject(Failure(CalendarMoveAuthorizationFailureReason.OutsideCalendarScope));
        }
        return new ExactInputAuthorization.Valid(sourceUri, destinationUri);
    }

    private CalendarResolution RequireMoveCapability(CalendarDescriptor calendar, CalendarEntityKind entityKind)
    {
        if (!Advertises(calendar, entityKind))
            return CalendarResolution.Reject(Failure(
                CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised,
                [calendar]));
        return string.Equals(
            _interoperabilityProfile,
            CalDavInteroperabilityProfiles.Radicale_3_7_8,
            StringComparison.Ordinal)
            ? new CalendarResolution.Resolved(calendar)
            : CalendarResolution.Reject(Failure(
                CalendarMoveAuthorizationFailureReason.InteroperabilityProfileUnverified,
                [calendar]));
    }

    private static CalendarResolution ResolveDirectOwner(
        Uri resource,
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarMoveAuthorizationFailureReason missing,
        CalendarMoveAuthorizationFailureReason ambiguous)
    {
        var owners = FindDirectOwners(resource, calendars);
        return owners.Length switch
        {
            0 => CalendarResolution.Reject(Failure(missing)),
            1 => new CalendarResolution.Resolved(owners[0]),
            _ => CalendarResolution.Reject(Failure(ambiguous))
        };
    }

    private static CalendarDescriptor[] FindDirectOwners(
        Uri resource,
        IReadOnlyList<CalendarDescriptor> calendars) => calendars
        .Where(calendar => IsDirectResourceOf(resource, calendar.Href))
        .ToArray();

    private static bool Advertises(CalendarDescriptor calendar, CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport == EntityKindSupport.Advertised,
        CalendarEntityKind.Todo => calendar.TodoSupport == EntityKindSupport.Advertised,
        _ => false
    };

    private static bool SameCalendar(CalendarDescriptor left, CalendarDescriptor right) =>
        string.Equals(left.Href, right.Href, StringComparison.Ordinal)
        && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
        && left.DisplayNameProvenance == right.DisplayNameProvenance
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && string.Equals(left.Color, right.Color, StringComparison.Ordinal)
        && left.EventSupport == right.EventSupport
        && left.TodoSupport == right.TodoSupport
        && left.EventEvidence.SequenceEqual(right.EventEvidence)
        && left.TodoEvidence.SequenceEqual(right.TodoEvidence)
        && left.UnavailableProperties.SequenceEqual(right.UnavailableProperties);

    private static CalendarMoveAuthorizationFailure Failure(
        CalendarMoveAuthorizationFailureReason reason,
        IEnumerable<CalendarDescriptor>? authorizedCandidates = null) =>
        new(
            reason,
            authorizedCandidates?.ToImmutableArray() ?? []);

    private static CalendarMoveAuthorizationResult Reject(CalendarMoveAuthorizationFailureReason reason) =>
        Reject(Failure(reason));

    private static CalendarMoveAuthorizationResult Reject(CalendarMoveAuthorizationFailure failure) =>
        new CalendarMoveAuthorizationResult.Rejected(failure);

    private static bool IsDirectResourceOf(string resourceHref, string calendarHref) =>
        TryParseCanonicalHref(resourceHref, requireTrailingSlash: false, out var resource)
        && IsDirectResourceOf(resource, calendarHref);

    private static bool IsDirectResourceOf(Uri resource, string calendarHref)
    {
        return TryParseCanonicalHref(calendarHref, requireTrailingSlash: true, out var calendar)
            && HasSameOrigin(resource, calendar)
            && resource.AbsolutePath.StartsWith(calendar.AbsolutePath, StringComparison.Ordinal)
            && resource.AbsolutePath[calendar.AbsolutePath.Length..] is { Length: > 0 } relative
            && !relative.Contains('/');
    }

    private static bool TryParseCanonicalHref(string href, bool requireTrailingSlash, out Uri uri)
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

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private abstract record SemanticInputAuthorization
    {
        private SemanticInputAuthorization()
        {
        }

        internal sealed record Valid(Uri SourceUri) : SemanticInputAuthorization;

        internal sealed record Rejected(CalendarMoveAuthorizationFailure Failure) : SemanticInputAuthorization;

        internal static SemanticInputAuthorization Reject(CalendarMoveAuthorizationFailure failure) => new Rejected(failure);
    }

    private abstract record ExactInputAuthorization
    {
        private ExactInputAuthorization()
        {
        }

        internal sealed record Valid(Uri SourceUri, Uri DestinationUri) : ExactInputAuthorization;

        internal sealed record Rejected(CalendarMoveAuthorizationFailure Failure) : ExactInputAuthorization;

        internal static ExactInputAuthorization Reject(CalendarMoveAuthorizationFailure failure) => new Rejected(failure);
    }

    private abstract record CalendarResolution
    {
        private CalendarResolution()
        {
        }

        internal sealed record Resolved(CalendarDescriptor Calendar) : CalendarResolution;

        internal sealed record Rejected(CalendarMoveAuthorizationFailure Failure) : CalendarResolution;

        internal static CalendarResolution Reject(CalendarMoveAuthorizationFailure failure) => new Rejected(failure);
    }
}

internal abstract record CalendarMoveAuthorizationResult
{
    private CalendarMoveAuthorizationResult()
    {
    }

    internal sealed record Authorized(CalendarMoveAuthorizedTarget Target) : CalendarMoveAuthorizationResult;

    internal sealed record Rejected(CalendarMoveAuthorizationFailure Failure) : CalendarMoveAuthorizationResult;
}

internal sealed record CalendarMoveAuthorizedTarget(
    string SourceHref,
    string DestinationHref,
    CalendarDescriptor SourceCalendar,
    CalendarDescriptor DestinationCalendar);

internal sealed record CalendarMoveAuthorizationFailure(
    CalendarMoveAuthorizationFailureReason Reason,
    ImmutableArray<CalendarDescriptor> AuthorizedCandidates);

internal enum CalendarMoveAuthorizationFailureReason
{
    NonCanonicalResourceHref,
    SameResourceHref,
    OriginMismatch,
    OutsideCalendarScope,
    InvalidSelectedCalendar,
    DestinationSelectionNotFound,
    DestinationSelectionAmbiguous,
    InteroperabilityProfileUnverified,
    SourceOwnershipMissing,
    SourceOwnershipAmbiguous,
    DestinationOwnershipMissing,
    DestinationOwnershipAmbiguous,
    EntityKindNotAdvertised,
    InvalidResolvedCalendar,
    ResolvedCalendarIdentityDivergent,
    SameCalendarNotAllowed
}
