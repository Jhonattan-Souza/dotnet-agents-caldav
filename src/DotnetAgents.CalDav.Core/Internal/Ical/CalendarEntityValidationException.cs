using System.Globalization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>An <see cref="ArgumentException"/> that keeps the typed reason for a rejected Calendar Entity.</summary>
internal sealed class CalendarEntityValidationException : ArgumentException
{
    public CalendarEntityValidationException(CalendarRequestViolation violation, Exception? innerException = null)
        : base(violation.Message, innerException) => Violation = violation;

    public CalendarRequestViolation Violation { get; }
}

/// <summary>
/// An <see cref="ArgumentException"/> whose message is a fixed literal authored by the Calendar Entity validator,
/// so it can be surfaced to callers; any other argument failure keeps a generic message.
/// </summary>
internal sealed class CalendarAuthoredArgumentException(string message) : ArgumentException(message);

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
        new(new CalendarRequestViolation(pointer, "temporal_value_invalid", AuthoredMessage(inner)), inner);

    internal static CalendarEntityValidationException FieldValueInvalid(string field, ArgumentException inner) =>
        new(new CalendarRequestViolation(Fields + "/" + field, "field_value_invalid", AuthoredMessage(inner)), inner);

    /// <summary>
    /// Surfaces a message only when the validator authored it as a fixed literal; messages from the BCL,
    /// Ical.Net, or NodaTime are replaced so they can never echo values or library internals.
    /// </summary>
    private static string AuthoredMessage(ArgumentException exception) =>
        exception is CalendarAuthoredArgumentException ? exception.Message : "This value is invalid for its field.";

    internal static string Collection(CalendarCollectionField field)
    {
        var name = field.ToString();
        return "/collections/" + char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Only a typed validator reason is surfaced; serializer and library messages stay internal.</summary>
    internal static CalendarRequestViolation CollectionValueInvalid(CalendarCollectionField field, Exception exception) =>
        exception is CalendarEntityValidationException typed
            ? typed.Violation with { Pointer = Collection(field) }
            : new(Collection(field), "collection_value_invalid", "A value in this collection is invalid for its field.");

    internal static CalendarRequestViolation StoredValueUnrecognized(string field) => new(
        Fields + "/" + field, "stored_value_unrecognized",
        $"The stored {field} value is not recognized, so a patch can only keep it unchanged.");

    internal static CalendarRequestViolation OccurrenceCancellationReserved { get; } = new(
        Fields + "/status", "occurrence_cancellation_reserved",
        "Use calendar_occurrences.cancel or calendar_occurrences.restore_cancellation to change whether one Occurrence is CANCELLED.");

    internal static CalendarRequestViolation DerivedValueReserved(string pointer) => new(
        pointer, "derived_value_reserved", "A value marked DERIVED is maintained by the server and cannot be patched.");

    internal static CalendarRequestViolation MasterStartWithOverrides { get; } = new(
        "/target/scope", "master_start_with_overrides",
        "Changing start on the master of a series with overrides would detach them; use target scope entire-set.");

    internal static CalendarRequestViolation DateStartRequiresEnd { get; } = new(
        Fields + "/start", "date_start_requires_end",
        "Changing a timed Event to a date start also requires an end or duration in the same patch.");

    internal static CalendarRequestViolation EffectiveSpanUnresolved { get; } = new(
        Fields + "/start", "effective_span_unresolved",
        "The stored end cannot be shifted with this start; include end or due in the same patch.");

    internal static CalendarRequestViolation RecurrenceScopeRequired { get; } = new(
        "/target/scope", "recurrence_scope_required", "recurrenceSet can be patched only with target scope entire-set.");

    internal static CalendarRequestViolation ScopedTemporalFamilyChange(string field) => new(
        Fields + "/" + field, "scoped_temporal_family_change",
        $"A scoped {field} change must keep the stored temporal kind and time zone.");

    internal static CalendarRequestViolation OrphanReconciliationMismatch { get; } = new(
        Fields + "/recurrenceSet/orphanReconciliations", "orphan_reconciliation_mismatch",
        "orphanReconciliations must name, once each, every override and exception date the new recurrence set no longer includes.");

    internal static CalendarRequestViolation RequestedOverridesMismatch { get; } = new(
        Fields + "/recurrenceSet/overrides", "requested_overrides_mismatch",
        "overrides must list exactly the overrides that remain, with matching recurrenceIdentity, range, and status.");

    internal const string TargetIdentity = "/target/recurrenceIdentity";

    internal static CalendarRequestViolation OccurrenceNotFound { get; } = new(
        TargetIdentity, "occurrence_not_found",
        "No Occurrence of this series has this recurrenceIdentity; copy it exactly from a calendar_occurrences.query result.");

    internal static CalendarRequestViolation OccurrenceExcludedForAdd { get; } = new(
        TargetIdentity, "occurrence_excluded",
        "This Occurrence is excluded; use calendar_occurrences.restore_exclusion to bring it back.");

    internal static CalendarRequestViolation OccurrenceExcluded { get; } = new(
        TargetIdentity, "occurrence_excluded", "This Occurrence is excluded from the series.");

    internal static CalendarRequestViolation IdentityFamilyMismatch { get; } = new(
        TargetIdentity + "/value", "recurrence_family_mismatch",
        "recurrenceIdentity must use the series start's temporal kind and time zone; copy it from a query result.");

    internal static CalendarRequestViolation SeriesStartMissing { get; } = new(
        null, "series_start_missing", "The stored series has no start, so its Occurrences cannot be addressed.");

    internal static CalendarRequestViolation RecurrenceIdentityRequired { get; } = new(
        TargetIdentity, "recurrence_identity_required",
        "This To-do recurs; pass the recurrenceIdentity of the Occurrence to complete, as returned in its completionTarget.");

    internal static CalendarRequestViolation RecurrenceIdentityNotApplicable { get; } = new(
        TargetIdentity, "recurrence_identity_not_applicable", "This To-do does not recur; omit recurrenceIdentity.");

    internal static CalendarRequestViolation CancelledNotCompletable { get; } = new(
        null, "cancelled_not_completable", "A CANCELLED To-do or Occurrence cannot be completed.");

    internal static CalendarRequestViolation SnapshotMemberInvalid(string member) => new(
        "/snapshot/" + member, "snapshot_member_invalid",
        $"snapshot.{member} must be passed exactly as returned by the read that produced this snapshot.");

    internal static CalendarEntityValidationException RecurrenceStartRequired(string fields) => Reject(
        fields + "/start", "recurrence_start_required", "A recurring Calendar Entity requires start.");

    internal static CalendarEntityValidationException RecurrenceDataRequired(string fields) => Reject(
        fields + "/recurrenceSet", "recurrence_data_required",
        "recurrenceSet requires rrule, rdates, exdates, or overrides.");

    internal static CalendarEntityValidationException RecurrenceRuleInvalid(Exception? inner = null) => new(
        new CalendarRequestViolation("/fields/recurrenceSet/rrule", "recurrence_rule_invalid",
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

    internal static CalendarRequestViolation RecurrenceCountInvalid { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_count_invalid", "rrule COUNT must be at least 1.");

    internal static CalendarRequestViolation RecurrenceOccurrenceLimit { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_occurrence_limit_exceeded",
        string.Create(CultureInfo.InvariantCulture,
            $"A bounded rrule may produce at most {CalendarCreateRecurrenceAnalyzer.MaximumProfileOccurrences} occurrences."));

    internal static CalendarRequestViolation RecurrenceNoOccurrences { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_no_occurrences", "rrule produces no occurrences from start.");

    internal static CalendarRequestViolation RecurrenceStartMismatch { get; } = new(
        "/fields/start", "recurrence_start_mismatch",
        "start must be the first occurrence of rrule; move start to a date the rule matches or change the rule.");

    internal static CalendarRequestViolation RecurrenceEvaluationFailed { get; } = new(
        "/fields/recurrenceSet/rrule", "recurrence_evaluation_failed", "rrule could not be evaluated from start.");

    private static CalendarEntityValidationException Reject(string pointer, string code, string message) =>
        new(new CalendarRequestViolation(pointer, code, message));
}
