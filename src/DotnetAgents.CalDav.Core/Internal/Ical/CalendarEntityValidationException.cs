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
    internal static string OverrideFields(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/recurrenceSet/overrides/{index}/fields");

    internal static string Override(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/recurrenceSet/overrides/{index}");

    internal static string Item(string collection, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/recurrenceSet/{collection}/{index}");

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
        new(new CalendarEntityViolation(pointer, "temporal_value_invalid", inner.Message), inner);

    internal static CalendarEntityValidationException RecurrenceStartRequired(string fields) => Reject(
        fields + "/start", "recurrence_start_required", "A recurring Calendar Entity requires start.");

    internal static CalendarEntityValidationException RecurrenceDataRequired(string fields) => Reject(
        fields + "/recurrenceSet", "recurrence_data_required",
        "recurrenceSet requires rrule, rdates, exdates, or overrides.");

    internal static CalendarEntityValidationException RecurrenceRuleInvalid(Exception? inner = null) => new(
        new CalendarEntityViolation("/recurrenceSet/rrule", "recurrence_rule_invalid",
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
        "/recurrenceSet/rrule", "recurrence_count_invalid", "rrule COUNT must be at least 1.");

    internal static CalendarEntityViolation RecurrenceOccurrenceLimit { get; } = new(
        "/recurrenceSet/rrule", "recurrence_occurrence_limit_exceeded",
        string.Create(CultureInfo.InvariantCulture,
            $"A bounded rrule may produce at most {CalendarCreateRecurrenceAnalyzer.MaximumProfileOccurrences} occurrences."));

    internal static CalendarEntityViolation RecurrenceNoOccurrences { get; } = new(
        "/recurrenceSet/rrule", "recurrence_no_occurrences", "rrule produces no occurrences from start.");

    internal static CalendarEntityViolation RecurrenceStartMismatch { get; } = new(
        "/start", "recurrence_start_mismatch",
        "start must be the first occurrence of rrule; move start to a date the rule matches or change the rule.");

    internal static CalendarEntityViolation RecurrenceEvaluationFailed { get; } = new(
        "/recurrenceSet/rrule", "recurrence_evaluation_failed", "rrule could not be evaluated from start.");

    private static CalendarEntityValidationException Reject(string pointer, string code, string message) =>
        new(new CalendarEntityViolation(pointer, code, message));
}
