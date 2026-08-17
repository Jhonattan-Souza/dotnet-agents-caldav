using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Edits explicit recurrence membership without regenerating unrelated iCalendar content.</summary>
internal static class CalendarOccurrenceMembershipEditor
{
    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryAdd(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var validation = CalendarOccurrencePatchBuilder.ValidateAddition(
                snapshot,
                document,
                identity,
                kind,
                cancellationToken);
            if (validation.Failure is not null)
                return (null, validation.Failure);
            if (!validation.ShouldAdd)
                return (null, null);

            var master = document.GetMasterComponent(kind);
            var edited = document.EditProperties(
                master.Path,
                new Dictionary<CalendarContentProperty, string?>(),
                [CalendarPatchValueSerializer.Temporal("RDATE", identity)]);
            document = CalendarContentDocument.Parse(edited);
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(master.Path),
                changed: true,
                kind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, new CalendarEntityPatchResult(
                CalendarEntityPatchCode.InvalidCalendarData,
                CalendarMutationState.NotAttempted,
                snapshot,
                Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));
        }
    }

    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryExclude(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
                snapshot,
                document,
                identity,
                kind,
                cancellationToken);
            if (inspection.Failure is not null)
                return (null, inspection.Failure);
            if (inspection.IsExcluded)
                return (null, null);
            if (!inspection.Exists)
                return (null, Failure(CalendarEntityPatchCode.NotFound, snapshot));

            var master = inspection.Master!;
            var edited = document.EditProperties(
                master.Path,
                new Dictionary<CalendarContentProperty, string?>(),
                [CalendarPatchValueSerializer.Temporal("EXDATE", identity)]);
            document = CalendarContentDocument.Parse(edited);
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(master.Path),
                changed: true,
                kind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryRestoreExclusion(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
                snapshot,
                document,
                identity,
                kind,
                cancellationToken);
            if (inspection.Failure is not null)
                return (null, inspection.Failure);
            if (!inspection.IsExcluded)
                return inspection.Exists ? (null, null) : (null, Failure(CalendarEntityPatchCode.NotFound, snapshot));

            var master = inspection.Master!;
            var key = identity.GetCanonicalSortKey();
            var replacements = document.Properties
                .Where(property => property.ComponentPath.SequenceEqual(master.Path)
                    && property.Name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
                .Select(property => new
                {
                    Property = property,
                    Remaining = property.RawEncodedValue.Split(',', StringSplitOptions.None)
                        .Where(value => CalendarPatchValueSerializer.ParseTemporal(
                            property with { RawEncodedValue = value }).GetCanonicalSortKey() != key)
                        .ToArray()
                })
                .Where(edit => edit.Remaining.Length
                    < edit.Property.RawEncodedValue.Split(',', StringSplitOptions.None).Length)
                .ToDictionary(
                    edit => edit.Property,
                    edit => edit.Remaining.Length == 0
                        ? null
                        : ReplaceRawValue(edit.Property, string.Join(',', edit.Remaining)));
            if (replacements.Count == 0)
                return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
            document = CalendarContentDocument.Parse(document.EditProperties(master.Path, replacements, []));
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(master.Path),
                changed: true,
                kind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryCancel(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
                snapshot,
                document,
                identity,
                kind,
                cancellationToken);
            if (inspection.Failure is not null)
                return (null, inspection.Failure);
            var selected = CalendarOccurrencePatchBuilder.MaterializeIndividual(
                snapshot,
                document,
                inspection,
                identity,
                kind);
            if (selected.Failure is not null)
                return (null, selected.Failure);

            document = selected.Document!;
            var individual = selected.Component!;
            document = CalendarContentDocument.Parse(document.SetOrClearSingleProperty(
                individual.Path,
                "STATUS",
                "CANCELLED"));
            if (CalendarEntityCreateFidelity.IsPatchEquivalent(
                    snapshot.AuthoritativeUtf8.Span,
                    document.Replay()))
                return (null, null);
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(individual.Path),
                changed: true,
                kind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryRestoreCancellation(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
                snapshot,
                document,
                identity,
                kind,
                cancellationToken);
            if (inspection.Failure is not null)
                return (null, inspection.Failure);
            if (!inspection.Exists)
                return (null, Failure(CalendarEntityPatchCode.NotFound, snapshot));
            var effective = inspection.Individual ?? inspection.Range ?? inspection.Master!;
            if (!IsCancelled(document, effective))
                return (null, null);

            var selected = CalendarOccurrencePatchBuilder.MaterializeIndividual(
                snapshot,
                document,
                inspection,
                identity,
                kind);
            if (selected.Failure is not null)
                return (null, selected.Failure);
            document = selected.Document!;
            var individual = selected.Component!;
            document = CalendarContentDocument.Parse(document.SetOrClearSingleProperty(
                individual.Path,
                "STATUS",
                rawEncodedValue: null));
            return CalendarEntityPatchEditor.FinishEdit(
                snapshot,
                document,
                document.GetComponent(individual.Path),
                changed: true,
                kind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    private static bool IsCancelled(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties.Any(property =>
        property.ComponentPath.SequenceEqual(component.Path)
        && property.Name.Equals("STATUS", StringComparison.OrdinalIgnoreCase)
        && property.RawEncodedValue.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));

    private static string ReplaceRawValue(CalendarContentProperty property, string rawValue)
    {
        var slice = property.OriginalSlice;
        var quoted = false;
        for (var index = 0; index < slice.Length; index++)
        {
            if (slice[index] == '"')
                quoted = !quoted;
            if (slice[index] != ':' || quoted)
                continue;
            var ending = slice.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
                : slice.EndsWith('\n') ? "\n" : string.Empty;
            return slice[..(index + 1)] + rawValue + ending;
        }
        throw new FormatException("The exclusion property is malformed.");
    }

    private static CalendarEntityPatchResult Failure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(
        code,
        CalendarMutationState.NotAttempted,
        snapshot,
        Phase: CalendarEntityPatchPhase.CompleteResourceSemantics);
}
