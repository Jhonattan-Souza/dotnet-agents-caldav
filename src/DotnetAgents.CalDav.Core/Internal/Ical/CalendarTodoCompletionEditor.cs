using System.Globalization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Applies coordinated To-do Completion without regenerating unrelated iCalendar content.</summary>
internal static class CalendarTodoCompletionEditor
{
    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryComplete(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue? recurrenceIdentity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var master = document.GetMasterComponent(CalendarEntityKind.Todo);
            var selected = SelectTarget(snapshot, document, master, recurrenceIdentity, cancellationToken);
            if (selected.Failure is not null)
                return (null, selected.Failure);
            document = selected.Document!;
            var target = selected.Component!;
            if (IsCancelled(document, target))
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            if (IsCompleted(document, target))
                return (null, null);

            var instant = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            document = CalendarContentDocument.Parse(document.SetOrClearSingleProperty(
                target.Path,
                "STATUS",
                "COMPLETED"));
            document = CalendarContentDocument.Parse(document.SetOrClearSingleProperty(
                target.Path,
                "PERCENT-COMPLETE",
                "100"));
            document = CalendarContentDocument.Parse(document.SetOrClearSingleProperty(
                target.Path,
                "COMPLETED",
                instant));
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(target.Path),
                changed: true,
                CalendarEntityKind.Todo,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    private static CalendarOccurrencePatchTarget SelectTarget(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarTemporalValue? recurrenceIdentity,
        CancellationToken cancellationToken)
    {
        var recurring = HasRecurrenceSet(document, master);
        if (recurrenceIdentity is null)
        {
            return recurring
                ? Failed(CalendarEntityPatchCode.InvalidInput, snapshot)
                : new(document, master, null);
        }
        if (!recurring)
            return Failed(CalendarEntityPatchCode.InvalidInput, snapshot);
        return SelectRecurringTarget(snapshot, document, recurrenceIdentity, cancellationToken);
    }

    private static CalendarOccurrencePatchTarget SelectRecurringTarget(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarTemporalValue recurrenceIdentity,
        CancellationToken cancellationToken)
    {
        var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
            snapshot,
            document,
            recurrenceIdentity,
            CalendarEntityKind.Todo,
            cancellationToken);
        if (inspection.Failure is not null)
            return new(null, null, inspection.Failure);
        if (inspection.IsExcluded)
            return Failed(CalendarEntityPatchCode.NotFound, snapshot);
        var effective = inspection.Individual ?? inspection.Range ?? inspection.Master!;
        if (IsCancelled(document, effective))
            return Failed(CalendarEntityPatchCode.InvalidInput, snapshot);
        if (IsCompleted(document, effective))
            return new(document, effective, null);
        if (inspection.Individual is not null)
            return new(document, inspection.Individual, null);
        return CalendarOccurrencePatchBuilder.MaterializeIndividual(
            snapshot,
            document,
            inspection,
            recurrenceIdentity,
            CalendarEntityKind.Todo);
    }

    private static bool HasRecurrenceSet(
        CalendarContentDocument document,
        CalendarContentComponent master) => document.Properties.Any(property =>
            property.ComponentPath.SequenceEqual(master.Path)
            && (property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase)))
        || document.Components.Any(component => component.Path.Count == 2
            && component.Path[^1].Name.Equals("VTODO", StringComparison.OrdinalIgnoreCase)
            && !component.Path.SequenceEqual(master.Path));

    private static bool IsCompleted(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties.Any(property =>
        property.ComponentPath.SequenceEqual(component.Path)
        && property.Name.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
        && property.RawEncodedValue.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase));

    private static bool IsCancelled(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties.Any(property =>
        property.ComponentPath.SequenceEqual(component.Path)
        && property.Name.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
        && property.RawEncodedValue.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));

    private static CalendarEntityPatchResult Failure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(
        code,
        CalendarMutationState.NotAttempted,
        snapshot,
        Phase: CalendarEntityPatchPhase.CompleteResourceSemantics);

    private static CalendarOccurrencePatchTarget Failed(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(null, null, Failure(code, snapshot));
}
