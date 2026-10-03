using System.Collections.Frozen;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Fixed reasons for move authorization refusals, anchored at the source or destination argument.</summary>
internal static class CalendarMoveViolations
{
    private enum Anchor
    {
        None,
        Source,
        Destination
    }

    private static readonly FrozenDictionary<CalendarMoveAuthorizationFailureReason, (Anchor Anchor, string Code, string Message)> Reasons =
        new Dictionary<CalendarMoveAuthorizationFailureReason, (Anchor, string, string)>
        {
            [CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref] = (Anchor.Source, "href_not_canonical",
                "The href must be passed exactly as a query or read returned it, in canonical form."),
            [CalendarMoveAuthorizationFailureReason.SameResourceHref] = (Anchor.Destination, "same_resource_href",
                "The destination would be the same resource as the source."),
            [CalendarMoveAuthorizationFailureReason.OriginMismatch] = (Anchor.None, "href_foreign_origin",
                "The source and destination must both use the configured CalDAV account's origin."),
            [CalendarMoveAuthorizationFailureReason.OutsideCalendarScope] = (Anchor.None, "outside_configured_scope",
                "The source or destination Calendar is outside the configured Calendar Scope."),
            [CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar] = (Anchor.Destination, "destination_calendar_invalid",
                "The destination must name an authorized Calendar by name or by its canonical href."),
            [CalendarMoveAuthorizationFailureReason.DestinationSelectionNotFound] = (Anchor.Destination, "destination_not_found",
                "No authorized Calendar matches the destination; choose one from authorizedCandidates."),
            [CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous] = (Anchor.Destination, "destination_ambiguous",
                "More than one authorized Calendar matches the destination; select one by href from authorizedCandidates."),
            [CalendarMoveAuthorizationFailureReason.InteroperabilityProfileUnverified] = (Anchor.None, "interoperability_profile_unverified",
                "Moves need CALDAV_INTEROPERABILITY_PROFILE set for a verified server."),
            [CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing] = (Anchor.Source, "no_owning_calendar",
                "No discovered Calendar directly contains the source href."),
            [CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous] = (Anchor.None, "source_ownership_ambiguous",
                "More than one discovered Calendar claims the source resource."),
            [CalendarMoveAuthorizationFailureReason.DestinationOwnershipMissing] = (Anchor.None, "destination_ownership_missing",
                "No discovered Calendar owns the destination."),
            [CalendarMoveAuthorizationFailureReason.DestinationOwnershipAmbiguous] = (Anchor.None, "destination_ownership_ambiguous",
                "More than one discovered Calendar claims the destination."),
            [CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised] = (Anchor.Destination, "entity_kind_not_advertised",
                "The destination Calendar does not advertise this Entity Kind."),
            [CalendarMoveAuthorizationFailureReason.InvalidResolvedCalendar] = (Anchor.None, "calendar_identity_invalid",
                "The server returned an invalid Calendar identity during the move."),
            [CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent] = (Anchor.None, "calendar_identity_divergent",
                "The server returned an inconsistent Calendar identity during the move."),
            [CalendarMoveAuthorizationFailureReason.SameCalendarNotAllowed] = (Anchor.Destination, "same_calendar",
                "The resource is already in that Calendar; choose a different destination."),
            [CalendarMoveAuthorizationFailureReason.SameCalendarMoveUnsupported] = (Anchor.Destination, "same_calendar_move_unsupported",
                "Moving within one Calendar is not supported; choose a different destination Calendar.")
        }.ToFrozenDictionary();

    internal static CalendarRequestViolation From(
        CalendarMoveAuthorizationFailureReason reason,
        string sourcePointer,
        string destinationPointer)
    {
        var (anchor, code, message) = Reasons[reason];
        var pointer = anchor switch
        {
            Anchor.Source => sourcePointer,
            Anchor.Destination => destinationPointer,
            _ => null
        };
        return new CalendarRequestViolation(pointer, code, message);
    }

    internal static CalendarRequestViolation RevisionChanged(string root) => new(
        root + "/entityTag", "revision_changed",
        "The resource changed after this revision was read; read it again and use the new entityTag.");

    internal static CalendarRequestViolation EntityUidMismatch(string root) => new(
        root + "/entityUid", "entity_uid_mismatch", "This href now holds a different Calendar Entity; read it again.");

    internal static CalendarRequestViolation WeakEntityTag(string root) => new(
        root + "/entityTag", "weak_entity_tag", "entityTag must be the strong ETag exactly as returned by a read.");

    internal static CalendarRequestViolation DestinationOccupied { get; } = new(
        null, "destination_occupied", "A resource already exists at the destination href.");
}
