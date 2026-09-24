using System.Diagnostics.CodeAnalysis;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Replaces caller-authored Windows time zone identifiers with their mapped IANA identifiers so
/// semantic writes always store IANA TZIDs. Identifiers that already name an IANA zone, and
/// identifiers with no mapping, pass through unchanged for the existing validation to judge.
/// </summary>
internal static class CalendarAuthoringTimeZones
{
    public static CalendarEventCreateRequest Normalize(CalendarEventCreateRequest request) =>
        request with { Fields = Normalize(request.Fields) };

    public static CalendarTodoCreateRequest Normalize(CalendarTodoCreateRequest request) =>
        request with { Fields = Normalize(request.Fields) };

    public static CalendarEventPatch Normalize(CalendarEventPatch patch) => patch with
    {
        Start = Normalize(patch.Start),
        End = Normalize(patch.End),
        Due = Normalize(patch.Due),
        RecurrenceSet = Normalize(patch.RecurrenceSet)
    };

    [return: NotNullIfNotNull(nameof(value))]
    internal static CalendarTemporalValue? Normalize(CalendarTemporalValue? value) =>
        value is { Kind: CalendarTemporalKind.ZonedDateTime, TimeZoneId: { } timeZoneId }
        && CalendarTimeZoneIdentifiers.ToIanaIdentifier(timeZoneId) is { } ianaIdentifier
            ? value with { TimeZoneId = ianaIdentifier }
            : value;

    private static CalendarEventCreateFields Normalize(CalendarEventCreateFields fields) => fields with
    {
        Start = Normalize(fields.Start),
        End = Normalize(fields.End),
        RecurrenceSet = fields.RecurrenceSet is null ? null : fields.RecurrenceSet with
        {
            RecurrenceDates = NormalizeRecurrenceDates(fields.RecurrenceSet.RecurrenceDates),
            ExceptionDates = NormalizeAll(fields.RecurrenceSet.ExceptionDates),
            Overrides = fields.RecurrenceSet.Overrides?.Select(item => item with
            {
                RecurrenceIdentity = Normalize(item.RecurrenceIdentity),
                Fields = Normalize(item.Fields)
            }).ToArray()
        }
    };

    private static CalendarTodoCreateFields Normalize(CalendarTodoCreateFields fields) => fields with
    {
        Start = Normalize(fields.Start),
        Due = Normalize(fields.Due),
        RecurrenceSet = fields.RecurrenceSet is null ? null : fields.RecurrenceSet with
        {
            RecurrenceDates = NormalizeRecurrenceDates(fields.RecurrenceSet.RecurrenceDates),
            ExceptionDates = NormalizeAll(fields.RecurrenceSet.ExceptionDates),
            Overrides = fields.RecurrenceSet.Overrides?.Select(item => item with
            {
                RecurrenceIdentity = Normalize(item.RecurrenceIdentity),
                Fields = Normalize(item.Fields)
            }).ToArray()
        }
    };

    private static IReadOnlyList<CalendarRecurrenceDateCreate>? NormalizeRecurrenceDates(
        IReadOnlyList<CalendarRecurrenceDateCreate>? recurrenceDates) => recurrenceDates?
        .Select(item => item with
        {
            Value = Normalize(item.Value),
            Period = item.Period is null ? null : item.Period with
            {
                Start = Normalize(item.Period.Start),
                End = Normalize(item.Period.End)
            }
        })
        .ToArray();

    private static CalendarScalarPatch<CalendarTemporalValue>? Normalize(
        CalendarScalarPatch<CalendarTemporalValue>? patch) =>
        patch is null ? null : patch with { Value = Normalize(patch.Value) };

    private static CalendarRecurrenceSetPatch? Normalize(CalendarRecurrenceSetPatch? patch) =>
        patch?.Value is null ? patch : patch with
        {
            Value = patch.Value with
            {
                RecurrenceDates = NormalizeAll(patch.Value.RecurrenceDates),
                ExceptionDates = NormalizeAll(patch.Value.ExceptionDates),
                Overrides = patch.Value.Overrides?.Select(item => item with
                {
                    RecurrenceIdentity = Normalize(item.RecurrenceIdentity),
                    MovedStart = Normalize(item.MovedStart),
                    MovedEnd = Normalize(item.MovedEnd)
                }).ToArray()
            }
        };

    private static IReadOnlyList<CalendarTemporalValue>? NormalizeAll(IReadOnlyList<CalendarTemporalValue>? values) =>
        values?.Select(value => Normalize(value)).ToArray();
}
