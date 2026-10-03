using System.Globalization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>An <see cref="ArgumentException"/> that keeps the typed reason for a rejected Calendar Entity.</summary>
internal sealed class CalendarEntityValidationException : ArgumentException
{
    public CalendarEntityValidationException(CalendarEntityViolation violation, Exception? innerException = null)
        : base(violation.Message, innerException) => Violation = violation;

    public CalendarEntityViolation Violation { get; }
}

/// <summary>Typed reasons for complete Calendar Entity creation, with fixed messages that never echo authored values.</summary>
internal static class CalendarEntityViolations
{
    /// <summary>The semantic request location of the authored Entity fields.</summary>
    internal const string Fields = "/fields";

    internal static string OverrideFields(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/fields/recurrenceSet/overrides/{index}/fields");

    internal static string Override(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/fields/recurrenceSet/overrides/{index}");

    internal static string Item(string collection, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/fields/recurrenceSet/{collection}/{index}");

    internal static CalendarEntityValidationException StartRequired(string fields) => Reject(
        fields + "/start", "start_required", "An Event requires start.");

    internal static CalendarEntityValidationException DurationExclusive(string fields, string endField) => Reject(
        fields + "/duration", endField + "_duration_exclusive",
        $"Provide either {endField} or duration, not both.");

    internal static CalendarEntityValidationException DurationRequiresStart(string fields) => Reject(
        fields + "/start", "duration_start_required", "A To-do duration requires start.");

    internal static CalendarEntityValidationException DurationInvalid(string fields) => Reject(
        fields + "/duration", "duration_invalid",
        "duration must be a positive ISO 8601 duration; a date start allows only whole days or weeks.");

    internal static CalendarEntityValidationException TemporalFamilyMismatch(string fields, string endField) => Reject(
        fields + "/" + endField, "temporal_family_mismatch",
        $"{endField} must use the same temporal kind and time zone as start.");

    internal static CalendarEntityValidationException NotAfterStart(string fields, string endField) => Reject(
        fields + "/" + endField, endField + "_not_after_start",
        $"{endField} must be later than start. A date value is an exclusive boundary, "
        + $"so a single day D uses start D and {endField} D+1.");

    internal static CalendarEntityValidationException TemporalValueInvalid(string pointer, ArgumentException inner) =>
        new(new CalendarEntityViolation(pointer, "temporal_value_invalid", AuthoredMessage(inner)), inner);

    internal static CalendarEntityValidationException FieldValueInvalid(string field, ArgumentException inner) =>
        new(new CalendarEntityViolation(Fields + "/" + field, "field_value_invalid", AuthoredMessage(inner)), inner);

    /// <summary>
    /// Surfaces a message only when the validator itself threw it as a fixed literal; messages from the BCL,
    /// Ical.Net, or NodaTime are replaced so they can never echo values or library internals.
    /// </summary>
    private static string AuthoredMessage(ArgumentException exception) =>
        exception.GetType() == typeof(ArgumentException)
        && exception.TargetSite?.DeclaringType == typeof(CalendarEntityCreateValidator)
            ? exception.Message
            : "This value is invalid for its field.";

    internal static string Collection(CalendarCollectionField field)
    {
        var name = field.ToString();
        return "/collections/" + char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Only a typed validator reason is surfaced; serializer and library messages stay internal.</summary>
    internal static CalendarEntityViolation CollectionValueInvalid(CalendarCollectionField field, Exception exception) =>
        exception is CalendarEntityValidationException typed
            ? typed.Violation with { Pointer = Collection(field) }
            : new(Collection(field), "collection_value_invalid", "A value in this collection is invalid for its field.");

    internal static CalendarEntityViolation StoredValueUnrecognized(string field) => new(
        Fields + "/" + field, "stored_value_unrecognized",
        $"The stored {field} value is not recognized, so a patch can only keep it unchanged.");

    internal static CalendarEntityViolation OccurrenceCancellationReserved { get; } = new(
        Fields + "/status", "occurrence_cancellation_reserved",
        "Use calendar_occurrences.cancel or calendar_occurrences.restore_cancellation to change whether one Occurrence is CANCELLED.");

    internal static CalendarEntityViolation DerivedValueReserved(string pointer) => new(
        pointer, "derived_value_reserved", "A value marked DERIVED is maintained by the server and cannot be patched.");

    internal static CalendarEntityViolation MasterStartWithOverrides { get; } = new(
        "/target/scope", "master_start_with_overrides",
        "Changing start on the master of a series with overrides would detach them; use target scope entire-set.");

    internal static CalendarEntityViolation DateStartRequiresEnd { get; } = new(
        Fields + "/start", "date_start_requires_end",
        "Changing a timed Event to a date start also requires an end or duration in the same patch.");

    internal static CalendarEntityViolation EffectiveSpanUnresolved { get; } = new(
        Fields + "/start", "effective_span_unresolved",
        "The stored end cannot be shifted with this start; include end or due in the same patch.");

    internal static CalendarEntityViolation RecurrenceScopeRequired { get; } = new(
        "/target/scope", "recurrence_scope_required", "recurrenceSet can be patched only with target scope entire-set.");

    internal static CalendarEntityViolation ScopedTemporalFamilyChange(string field) => new(
        Fields + "/" + field, "scoped_temporal_family_change",
        $"A scoped {field} change must keep the stored temporal kind and time zone.");

    internal static CalendarEntityViolation OrphanReconciliationMismatch { get; } = new(
        Fields + "/recurrenceSet/orphanReconciliations", "orphan_reconciliation_mismatch",
        "orphanReconciliations must name, once each, every override and exception date the new recurrence set no longer includes.");

    internal static CalendarEntityViolation RequestedOverridesMismatch { get; } = new(
        Fields + "/recurrenceSet/overrides", "requested_overrides_mismatch",
        "overrides must list exactly the overrides that remain, with matching recurrenceIdentity, range, and status.");

    internal const string TargetIdentity = "/target/recurrenceIdentity";

    internal static CalendarEntityViolation OccurrenceNotFound { get; } = new(
        TargetIdentity, "occurrence_not_found",
        "No Occurrence of this series has this recurrenceIdentity; copy it exactly from a calendar_occurrences.query result.");

    internal static CalendarEntityViolation OccurrenceExcludedForAdd { get; } = new(
        TargetIdentity, "occurrence_excluded",
        "This Occurrence is excluded; use calendar_occurrences.restore_exclusion to bring it back.");

    internal static CalendarEntityViolation OccurrenceExcluded { get; } = new(
        TargetIdentity, "occurrence_excluded", "This Occurrence is excluded from the series.");

    internal static CalendarEntityViolation IdentityFamilyMismatch { get; } = new(
        TargetIdentity + "/value", "recurrence_family_mismatch",
        "recurrenceIdentity must use the series start's temporal kind and time zone; copy it from a query result.");

    internal static CalendarEntityViolation SeriesStartMissing { get; } = new(
        null, "series_start_missing", "The stored series has no start, so its Occurrences cannot be addressed.");

    internal static CalendarEntityViolation RecurrenceIdentityRequired { get; } = new(
        TargetIdentity, "recurrence_identity_required",
        "This To-do recurs; pass the recurrenceIdentity of the Occurrence to complete, as returned in its completionTarget.");

    internal static CalendarEntityViolation RecurrenceIdentityNotApplicable { get; } = new(
        TargetIdentity, "recurrence_identity_not_applicable", "This To-do does not recur; omit recurrenceIdentity.");

    internal static CalendarEntityViolation CancelledNotCompletable { get; } = new(
        null, "cancelled_not_completable", "A CANCELLED To-do or Occurrence cannot be completed.");

    internal static CalendarEntityViolation RevisionChanged { get; } = new(
        "/snapshot/entityTag", "revision_changed",
        "The resource changed after this snapshot was read; read it again and use the new entityTag.");

    internal static CalendarEntityViolation EntityUidMismatch { get; } = new(
        "/snapshot/entityUid", "entity_uid_mismatch",
        "This href now holds a different Calendar Entity; read it again before changing it.");

    internal static CalendarEntityViolation WeakEntityTag { get; } = new(
        "/snapshot/entityTag", "weak_entity_tag", "entityTag must be the strong ETag exactly as returned by a read.");

    internal static CalendarEntityViolation SnapshotMemberInvalid(string member) => new(
        "/snapshot/" + member, "snapshot_member_invalid",
        $"snapshot.{member} must be passed exactly as returned by the read that produced this snapshot.");

    internal static CalendarEntityValidationException RecurrenceStartRequired(string fields) => Reject(
        fields + "/start", "recurrence_start_required", "A recurring Calendar Entity requires start.");

    internal static CalendarEntityValidationException RecurrenceDataRequired(string fields) => Reject(
        fields + "/recurrenceSet", "recurrence_data_required",
        "recurrenceSet requires rrule, rdates, exdates, or overrides.");

    internal static CalendarEntityValidationException RecurrenceRuleInvalid(Exception? inner = null) => new(
        new CalendarEntityViolation("/fields/recurrenceSet/rrule", "recurrence_rule_invalid",
            "rrule must be one RFC 5545 RRULE value without the RRULE: prefix."),
        inner);

    internal static CalendarEntityValidationException RecurrenceFamilyMismatch(string pointer) => Reject(
        pointer, "recurrence_family_mismatch",
        "Recurrence dates and identities must use the same temporal kind and time zone as start.");

    internal static CalendarEntityValidationException OverrideIdentityDuplicate(int index) => Reject(
        Override(index) + "/recurrenceIdentity", "recurrence_override_duplicate",
        "Each recurrence override must target a distinct recurrenceIdentity.");

    internal static CalendarEntityValidationException OverrideStatusConflict(int index) => Reject(
        Override(index) + "/status", "override_status_conflict",
        "Override status must be cancelled exactly when its fields.status is CANCELLED.");

    internal static CalendarEntityViolation RecurrenceCountInvalid { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_count_invalid", "rrule COUNT must be at least 1.");

    internal static CalendarEntityViolation RecurrenceOccurrenceLimit { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_occurrence_limit_exceeded",
        string.Create(CultureInfo.InvariantCulture,
            $"A bounded rrule may produce at most {CalendarCreateRecurrenceAnalyzer.MaximumProfileOccurrences} occurrences."));

    internal static CalendarEntityViolation RecurrenceNoOccurrences { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_no_occurrences", "rrule produces no occurrences from start.");

    internal static CalendarEntityViolation RecurrenceStartMismatch { get; } = new(
        "/fields/start", "recurrence_start_mismatch",
        "start must be the first occurrence of rrule; move start to a date the rule matches or change the rule.");

    internal static CalendarEntityViolation RecurrenceEvaluationFailed { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_evaluation_failed", "rrule could not be evaluated from start.");

    private static CalendarEntityValidationException Reject(string pointer, string code, string message) =>
        new(new CalendarEntityViolation(pointer, code, message));
}
