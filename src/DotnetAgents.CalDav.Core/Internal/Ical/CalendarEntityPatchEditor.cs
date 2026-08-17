using System.Globalization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Applies one lossless semantic patch to an authoritative iCalendar resource body.</summary>
internal static class CalendarEntityPatchEditor
{
    private static readonly IReadOnlySet<string> TemporalValueParameters =
        new HashSet<string>(["VALUE", "TZID"], StringComparer.OrdinalIgnoreCase);

    public static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryEdit(
        CalendarResourceSnapshot snapshot,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            if (target.Scope is "this-and-future" or "entire-set")
                return TryEditScoped(snapshot, document, target, patch, expectedKind, now, cancellationToken);
            var selected = CalendarOccurrencePatchBuilder.SelectTarget(
                snapshot,
                document,
                target,
                expectedKind,
                cancellationToken);
            if (selected.Failure is not null)
                return (null, selected.Failure);
            document = selected.Document!;
            var master = selected.Component!;
            var targetPath = master.Path;
            var effectivePatch = PrepareScalarPatch(
                snapshot,
                document,
                master,
                patch,
                expectedKind,
                target.Scope == "master");
            if (effectivePatch is null)
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            var scalarEdit = ApplyScalars(document, master, effectivePatch);
            document = scalarEdit.Document;
            master = document.GetComponent(targetPath);
            if (!HasValidAddressedFinalShape(document, master, effectivePatch, expectedKind))
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            var changed = scalarEdit.Changed;

            var categoryEdit = ApplyCategories(document, master, patch.Categories);
            if (categoryEdit.Failure is not null)
                return (null, categoryEdit.Failure);
            if (categoryEdit.AuthoritativeUtf8 is not null)
            {
                document = CalendarContentDocument.Parse(categoryEdit.AuthoritativeUtf8);
                master = document.GetComponent(targetPath);
                changed = true;
            }
            var structuredEdit = ApplyStructuredCollections(document, master, patch.Collections, expectedKind);
            if (structuredEdit.Failure is not null)
                return (null, structuredEdit.Failure);
            if (CalendarEntityCreateFidelity.IsPatchEquivalent(
                    snapshot.AuthoritativeUtf8.Span,
                    structuredEdit.Document.Replay()))
                return (null, null);
            return FinishEdit(
                snapshot,
                structuredEdit.Document,
                structuredEdit.Document.GetComponent(targetPath),
                changed || structuredEdit.Changed,
                expectedKind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) TryEditScoped(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CalendarContentComponent primary;
        if (target.Scope == "this-and-future")
        {
            var selected = CalendarOccurrencePatchBuilder.SelectRangeTarget(
                snapshot,
                document,
                target.RecurrenceIdentity!,
                kind,
                cancellationToken);
            if (selected.Failure is not null)
                return (null, selected.Failure);
            document = selected.Document!;
            primary = selected.Component!;
        }
        else
        {
            primary = document.GetMasterComponent(kind);
        }

        var recurrenceChanged = false;
        if (patch.RecurrenceSet is not null)
        {
            if (target.Scope != "entire-set")
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            var recurrence = CalendarRecurrenceSetPatchEditor.TryEdit(
                snapshot,
                document,
                primary,
                patch.RecurrenceSet,
                kind,
                cancellationToken);
            if (recurrence.Failure is not null)
                return (null, recurrence.Failure);
            document = recurrence.Document;
            primary = document.GetComponent(primary.Path);
            recurrenceChanged = recurrence.Changed;
        }
        var paths = ScopedPaths(document, primary, target, kind);
        var shifts = GetScopedTemporalShifts(document, primary, patch, kind);
        if (shifts.Failure)
            return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
        var changed = recurrenceChanged;
        var changedPaths = new List<IReadOnlyList<CalendarComponentPathSegment>>();
        RecordChangedPath(changedPaths, primary.Path, recurrenceChanged);
        var temporalBasis = document;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var componentPatch = ScopedComponentPatch(
                snapshot,
                temporalBasis,
                path,
                primary.Path,
                patch,
                shifts,
                kind,
                cancellationToken);
            var edit = ApplyScopedComponent(snapshot, document, path, componentPatch, kind);
            if (edit.Failure is not null)
                return (null, edit.Failure);
            document = edit.Document;
            changed |= edit.Changed;
            RecordChangedPath(changedPaths, path, edit.Changed);
        }
        primary = document.GetComponent(primary.Path);
        return FinishScopedEdit(snapshot, document, primary, changed, changedPaths, kind, now);
    }

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) FinishScopedEdit(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent primary,
        bool changed,
        IReadOnlyList<IReadOnlyList<CalendarComponentPathSegment>> changedPaths,
        CalendarEntityKind kind,
        DateTimeOffset now)
    {
        if (!changed)
            return (null, null);
        var lastModified = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var path in changedPaths.DistinctBy(PathKey))
        {
            var existing = FindProperty(document, path, "LAST-MODIFIED");
            if (existing is null || !IsDerivedProperty(existing))
            {
                document = CalendarContentDocument.Parse(
                    document.SetOrClearSingleProperty(path, "LAST-MODIFIED", lastModified));
            }
        }
        return FinishEdit(snapshot, document, document.GetComponent(primary.Path), true, kind, now);
    }

    private static string PathKey(IReadOnlyList<CalendarComponentPathSegment> path) =>
        string.Join('/', path.Select(segment => $"{segment.Name}#{segment.Occurrence}"));

    private static void RecordChangedPath(
        ICollection<IReadOnlyList<CalendarComponentPathSegment>> paths,
        IReadOnlyList<CalendarComponentPathSegment> path,
        bool changed)
    {
        if (changed)
            paths.Add(path);
    }

    private static ScopedTemporalShifts GetScopedTemporalShifts(
        CalendarContentDocument document,
        CalendarContentComponent primary,
        CalendarEventPatch patch,
        CalendarEntityKind kind)
    {
        var start = GetTemporalDelta(document, primary, "DTSTART", patch.Start);
        var end = GetTemporalDelta(document, primary, "DTEND", patch.End);
        var due = GetTemporalDelta(document, primary, "DUE", patch.Due);
        return new(start.Delta, end.Delta, due.Delta, start.Failure || end.Failure || due.Failure
            || kind == CalendarEntityKind.Event && patch.Due is not null
            || kind == CalendarEntityKind.Todo && patch.End is not null);
    }

    private static (TimeSpan? Delta, bool Failure) GetTemporalDelta(
        CalendarContentDocument document,
        CalendarContentComponent primary,
        string name,
        CalendarScalarPatch<CalendarTemporalValue>? patch)
    {
        if (patch is null || patch.Operation == CalendarScalarPatchOperation.Clear)
            return (null, false);
        if (patch.Value is null)
            return (null, true);
        var current = FindProperty(document, primary.Path, name);
        if (current is null)
            return (null, false);
        var existing = CalendarPatchValueSerializer.ParseTemporal(current);
        return existing.Kind != patch.Value.Kind
            || !string.Equals(existing.TimeZoneId, patch.Value.TimeZoneId, StringComparison.Ordinal)
            ? (null, true)
            : (ParseLocal(patch.Value) - ParseLocal(existing), false);
    }

    private static CalendarEventPatch ScopedComponentPatch(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        IReadOnlyList<CalendarComponentPathSegment> primaryPath,
        CalendarEventPatch patch,
        ScopedTemporalShifts shifts,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        if (path.SequenceEqual(primaryPath))
            return patch;
        var sparseStart = FindProperty(document, path, "DTSTART") is null;
        var shiftedStart = ShiftTemporalPatch(
            snapshot,
            document,
            path,
            "DTSTART",
            patch.Start,
            shifts.Start,
            kind,
            cancellationToken);
        var shiftedEnd = ShiftScopedEndpoint(
            snapshot,
            document,
            path,
            "DTEND",
            patch.End,
            shifts.End,
            patch.Start,
            shifts.Start,
            sparseStart,
            kind == CalendarEntityKind.Event,
            kind,
            cancellationToken);
        var shiftedDue = ShiftScopedEndpoint(
            snapshot,
            document,
            path,
            "DUE",
            patch.Due,
            shifts.Due,
            patch.Start,
            shifts.Start,
            sparseStart,
            kind == CalendarEntityKind.Todo,
            kind,
            cancellationToken);
        return patch with
        {
            Start = shiftedStart,
            End = shiftedEnd,
            Due = shiftedDue
        };
    }

    private static CalendarScalarPatch<CalendarTemporalValue>? ShiftScopedEndpoint(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string name,
        CalendarScalarPatch<CalendarTemporalValue>? endpointPatch,
        TimeSpan? endpointDelta,
        CalendarScalarPatch<CalendarTemporalValue>? startPatch,
        TimeSpan? startDelta,
        bool sparseStart,
        bool applies,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        if (!applies)
            return endpointPatch;
        var inferredFromStart = endpointPatch is null && sparseStart;
        return ShiftTemporalPatch(
            snapshot,
            document,
            path,
            name,
            inferredFromStart ? startPatch : endpointPatch,
            inferredFromStart ? startDelta : endpointDelta,
            kind,
            cancellationToken);
    }

    private static CalendarScalarPatch<CalendarTemporalValue>? ShiftTemporalPatch(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string name,
        CalendarScalarPatch<CalendarTemporalValue>? patch,
        TimeSpan? delta,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        if (patch is null || patch.Operation == CalendarScalarPatchOperation.Clear || delta is null)
            return patch;
        var temporal = GetEffectiveTemporal(snapshot, document, path, name, kind, cancellationToken);
        if (temporal is null)
            return null;
        return new(
            CalendarScalarPatchOperation.Set,
            temporal with { Value = FormatLocal(ParseLocal(temporal).Add(delta.Value), temporal.Kind) });
    }

    private static CalendarTemporalValue? GetEffectiveTemporal(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string name,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        var current = FindProperty(document, path, name);
        if (current is not null)
            return CalendarPatchValueSerializer.ParseTemporal(current);
        var identityProperty = FindProperty(document, path, "RECURRENCE-ID");
        if (identityProperty is null)
            return null;
        var identity = CalendarPatchValueSerializer.ParseTemporal(identityProperty);
        var inspection = CalendarOccurrencePatchBuilder.InspectMembership(
            snapshot,
            document,
            identity,
            kind,
            cancellationToken);
        var materialized = CalendarOccurrencePatchBuilder.MaterializeIndividual(
            snapshot,
            document,
            inspection,
            identity,
            kind);
        if (materialized.Failure is not null || materialized.Document is null || materialized.Component is null)
            throw new InvalidOperationException("The effective override timing could not be materialized.");
        var effective = FindProperty(materialized.Document, materialized.Component.Path, name);
        return effective is null ? null : CalendarPatchValueSerializer.ParseTemporal(effective);
    }

    private static DateTime ParseLocal(CalendarTemporalValue value)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        return DateTime.ParseExact(value.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture);
    }

    private static string FormatLocal(DateTime value, CalendarTemporalKind kind)
    {
        var format = kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var lexical = value.ToString(format, CultureInfo.InvariantCulture);
        return kind == CalendarTemporalKind.UtcDateTime ? lexical + "Z" : lexical;
    }

    private static IReadOnlyList<IReadOnlyList<CalendarComponentPathSegment>> ScopedPaths(
        CalendarContentDocument document,
        CalendarContentComponent primary,
        CalendarMutationTarget target,
        CalendarEntityKind kind)
    {
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var components = document.Components.Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (target.Scope == "entire-set")
            return components.Select(component => component.Path).ToArray();
        var anchor = target.RecurrenceIdentity!.GetCanonicalSortKey();
        return components.Where(component => component.Path.SequenceEqual(primary.Path)
                || FindProperty(document, component.Path, "RECURRENCE-ID") is { } recurrenceId
                && string.CompareOrdinal(
                    CalendarPatchValueSerializer.ParseTemporal(recurrenceId).GetCanonicalSortKey(),
                    anchor) >= 0)
            .Select(component => component.Path)
            .ToArray();
    }

    private static (
        CalendarContentDocument Document,
        bool Changed,
        CalendarEntityPatchResult? Failure) ApplyScopedComponent(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        CalendarEventPatch patch,
        CalendarEntityKind kind)
    {
        var component = document.GetComponent(path);
        var effectivePatch = PrepareScalarPatch(snapshot, document, component, patch, kind, targetsMaster: false);
        if (effectivePatch is null)
            return (document, false, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
        var scalar = ApplyScalars(document, component, effectivePatch);
        document = scalar.Document;
        component = document.GetComponent(path);
        if (!HasValidAddressedFinalShape(document, component, effectivePatch, kind))
            return (document, false, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
        var changed = scalar.Changed;
        var categories = ApplyCategories(document, component, patch.Categories);
        if (categories.Failure is not null)
            return (document, false, categories.Failure);
        if (categories.AuthoritativeUtf8 is not null)
        {
            document = CalendarContentDocument.Parse(categories.AuthoritativeUtf8);
            component = document.GetComponent(path);
            changed = true;
        }
        var structured = ApplyStructuredCollections(document, component, patch.Collections, kind);
        return (structured.Document, changed || structured.Changed, structured.Failure);
    }

    private static bool HasReservedCancellationTransition(
        CalendarContentDocument document,
        CalendarContentComponent component,
        CalendarScalarPatch<string>? statusPatch)
    {
        if (statusPatch is null)
            return false;
        if (statusPatch is { Operation: CalendarScalarPatchOperation.Set, Value: { } requested }
            && requested.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
            return true;
        var current = FindProperty(document, component.Path, "STATUS")?.RawEncodedValue;
        return current?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static CalendarEventPatch? PrepareScalarPatch(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch,
        CalendarEntityKind kind,
        bool targetsMaster)
    {
        if (!targetsMaster && HasReservedCancellationTransition(document, master, patch.Status)
            || IntroducesDerivedOrganizer(patch)
            || targetsMaster && patch.Start is not null && HasRecurrenceMembership(document, master, kind))
            return null;
        var effectivePatch = PreserveExplicitEffectiveSpan(snapshot, document, master, patch, kind);
        return effectivePatch is not null && !HasAddressedDerivedScalar(document, master, effectivePatch)
            ? effectivePatch
            : null;
    }

    private static CalendarEventPatch? PreserveExplicitEffectiveSpan(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch,
        CalendarEntityKind kind)
    {
        if (!RequiresEffectiveSpanPreservation(patch, kind))
            return patch;
        var newStart = patch.Start!.Value!;
        var oldStart = FindProperty(document, master.Path, "DTSTART");
        var endName = kind == CalendarEntityKind.Event ? "DTEND" : "DUE";
        var oldEnd = FindProperty(document, master.Path, endName);
        if (oldStart is null)
            return patch;
        if (oldEnd is null)
        {
            if (FindProperty(document, master.Path, "DURATION") is not null)
                return patch;
            return PreserveImplicitDateEventSpan(oldStart, patch, newStart, kind);
        }
        var resolver = new CalendarTemporalResolver(snapshot.CalendarProperties, snapshot.AuthoritativeUtf8.Span);
        var shiftedEnd = CalendarDurationArithmetic.ShiftExplicitEnd(
            CalendarPatchValueSerializer.ParseTemporal(oldStart),
            CalendarPatchValueSerializer.ParseTemporal(oldEnd),
            newStart,
            resolver);
        if (shiftedEnd is null)
            return null;
        var endpointPatch = new CalendarScalarPatch<CalendarTemporalValue>(
            CalendarScalarPatchOperation.Set,
            shiftedEnd);
        return kind == CalendarEntityKind.Event
            ? patch with { End = endpointPatch }
            : patch with { Due = endpointPatch };
    }

    private static bool RequiresEffectiveSpanPreservation(CalendarEventPatch patch, CalendarEntityKind kind) =>
        patch.Start is { Operation: CalendarScalarPatchOperation.Set, Value: not null }
        && patch.Duration is null
        && (kind == CalendarEntityKind.Event ? patch.End : patch.Due) is null;

    private static CalendarEventPatch? PreserveImplicitDateEventSpan(
        CalendarContentProperty oldStart,
        CalendarEventPatch patch,
        CalendarTemporalValue newStart,
        CalendarEntityKind kind)
    {
        if (kind != CalendarEntityKind.Event)
            return patch;
        var oldKind = CalendarPatchValueSerializer.ParseTemporal(oldStart).Kind;
        var newKind = newStart.Kind;
        if (oldKind != CalendarTemporalKind.Date && newKind == CalendarTemporalKind.Date)
            return null;
        if (oldKind != CalendarTemporalKind.Date || newKind == CalendarTemporalKind.Date)
            return patch;
        var endpointPatch = new CalendarScalarPatch<CalendarTemporalValue>(
            CalendarScalarPatchOperation.Set,
            CalendarDurationArithmetic.AddNominalDay(newStart));
        return patch with { End = endpointPatch };
    }

    internal static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) FinishEdit(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        bool changed,
        CalendarEntityKind expectedKind,
        DateTimeOffset now)
    {
        if (!changed)
            return (null, null);
        var lastModified = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var existingLastModified = FindProperty(document, master.Path, "LAST-MODIFIED");
        var edited = existingLastModified is not null && IsDerivedProperty(existingLastModified)
            ? document.Replay()
            : document.SetOrClearSingleProperty(master.Path, "LAST-MODIFIED", lastModified);
        var projection = CalendarResourceProjector.Project(edited);
        if (projection.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            return (null, Failure(CalendarEntityPatchCode.FidelityFailure, snapshot));
        var projectedKind = projection.Projection.Kind == CalendarResourceProjectionKind.Event
            ? CalendarEntityKind.Event
            : CalendarEntityKind.Todo;
        return projectedKind != expectedKind
            || !string.Equals(projection.Projection.EntityUid, snapshot.Projection.EntityUid, StringComparison.Ordinal)
            ? (null, Failure(CalendarEntityPatchCode.FidelityFailure, snapshot))
            : (edited, null);
    }

    private static (
        CalendarContentDocument Document,
        bool Changed,
        CalendarEntityPatchResult? Failure) ApplyStructuredCollections(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<ICalendarCollectionPatch>? patches,
        CalendarEntityKind kind)
    {
        var changed = false;
        foreach (var patch in patches ?? [])
        {
            var edit = ApplyStructuredCollection(document, master, patch, kind);
            if (edit.Failure is not null)
                return (document, false, edit.Failure);
            if (edit.AuthoritativeUtf8 is null)
                continue;
            document = CalendarContentDocument.Parse(edit.AuthoritativeUtf8);
            master = document.GetComponent(master.Path);
            changed = true;
        }
        return (document, changed, null);
    }

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) ApplyStructuredCollection(
        CalendarContentDocument document,
        CalendarContentComponent master,
        ICalendarCollectionPatch patch,
        CalendarEntityKind kind)
    {
        var current = CalendarPatchOccurrenceSerializer.Current(document, master, patch.Field);
        var additions = SerializeOccurrences(patch.Field, patch.AddValues, kind);
        if (HasDerivedOccurrence(patch.Field, additions, kind))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        if (patch.Operation == CalendarCollectionPatchOperation.ReplaceAll)
            return ReplaceAllStructured(document, master, patch, current, kind);

        var removalMatch = MatchOccurrenceRemovals(patch.Field, current, patch.RemoveValues ?? [], kind);
        if (removalMatch.Failure is not null)
            return (null, Failure(removalMatch.Failure.Value));
        var removals = removalMatch.Removals!;
        if (removals.Any(occurrence => CalendarPatchSemanticComparer.IsDerived(patch.Field, occurrence, kind)))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        return removals.Count == 0 && additions.Count == 0
            ? (null, null)
            : (document.EditOccurrences(master.Path, removals, additions), null);
    }

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) ReplaceAllStructured(
        CalendarContentDocument document,
        CalendarContentComponent master,
        ICalendarCollectionPatch patch,
        IReadOnlyList<CalendarContentOccurrence> current,
        CalendarEntityKind kind)
    {
        if (current.Any(occurrence => CalendarPatchSemanticComparer.IsDerived(patch.Field, occurrence, kind)))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        var replacementValues = patch.ReplacementValues ?? [];
        var replacements = SerializeOccurrences(patch.Field, replacementValues, kind);
        if (HasDerivedOccurrence(patch.Field, replacements, kind))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        return AreSameOccurrences(patch.Field, current, replacementValues, kind)
            ? (null, null)
            : (document.EditOccurrences(master.Path, current, replacements), null);
    }

    private static IReadOnlyList<string> SerializeOccurrences(
        CalendarCollectionField field,
        IReadOnlyList<object>? values,
        CalendarEntityKind kind) => (values ?? [])
        .Select(value => CalendarPatchOccurrenceSerializer.Serialize(field, value, kind))
        .ToArray();

    private static bool HasDerivedOccurrence(
        CalendarCollectionField field,
        IEnumerable<string> slices,
        CalendarEntityKind kind) => slices.Any(slice => CalendarPatchSemanticComparer.IsDerived(
            field,
            new CalendarContentOccurrence(0, slice.Length, slice),
            kind));

    private static (IReadOnlyList<CalendarContentOccurrence>? Removals, CalendarEntityPatchCode? Failure)
        MatchOccurrenceRemovals(
        CalendarCollectionField field,
        IReadOnlyList<CalendarContentOccurrence> current,
        IReadOnlyList<object> requested,
        CalendarEntityKind kind)
    {
        if (requested.Select((value, index) => requested.Take(index).Any(previous =>
                CalendarPatchSemanticComparer.ValuesEquivalent(field, previous, value, kind))).Any(duplicate => duplicate))
            return (null, CalendarEntityPatchCode.RemovalAmbiguous);
        var removals = new List<CalendarContentOccurrence>();
        foreach (var value in requested)
        {
            var matches = current.Where(occurrence =>
                CalendarPatchSemanticComparer.Matches(field, occurrence, value, kind)).ToArray();
            if (matches.Length == 0)
                return (null, CalendarEntityPatchCode.RemovalNotFound);
            if (matches.Length > 1 || removals.Contains(matches[0]))
                return (null, CalendarEntityPatchCode.RemovalAmbiguous);
            removals.Add(matches[0]);
        }
        return (removals, null);
    }

    private static bool AreSameOccurrences(
        CalendarCollectionField field,
        IReadOnlyList<CalendarContentOccurrence> current,
        IReadOnlyList<object> requested,
        CalendarEntityKind kind) => current.Count == requested.Count
        && current.Zip(requested).All(pair => CalendarPatchSemanticComparer.Matches(
            field,
            pair.First,
            pair.Second,
            kind));

    private static (CalendarContentDocument Document, bool Changed) ApplyScalars(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch)
    {
        var changed = false;
        document = ApplyScalar(document, master.Path, "SUMMARY", patch.Summary,
            value => CalendarPatchValueSerializer.Text("SUMMARY", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "DESCRIPTION", patch.Description,
            value => CalendarPatchValueSerializer.Text("DESCRIPTION", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "DTSTART", patch.Start,
            value => CalendarPatchValueSerializer.Temporal("DTSTART", value), ScalarParameterMode.Temporal, ref changed);
        document = ApplyScalar(document, master.Path, "DTEND", patch.End,
            value => CalendarPatchValueSerializer.Temporal("DTEND", value), ScalarParameterMode.Temporal, ref changed);
        document = ApplyScalar(document, master.Path, "DUE", patch.Due,
            value => CalendarPatchValueSerializer.Temporal("DUE", value), ScalarParameterMode.Temporal, ref changed);
        document = ApplyScalar(document, master.Path, "DURATION", patch.Duration,
            CalendarPatchValueSerializer.Duration, ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "LOCATION", patch.Location,
            value => CalendarPatchValueSerializer.Text("LOCATION", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "GEO", patch.Geo,
            CalendarPatchValueSerializer.Geo, ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "STATUS", patch.Status,
            value => CalendarPatchValueSerializer.Token("STATUS", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "TRANSP", patch.Transparency,
            value => CalendarPatchValueSerializer.Token("TRANSP", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "CLASS", patch.Classification,
            value => CalendarPatchValueSerializer.Token("CLASS", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "PRIORITY", patch.Priority,
            value => CalendarPatchValueSerializer.Integer("PRIORITY", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "PERCENT-COMPLETE", patch.PercentComplete,
            value => CalendarPatchValueSerializer.Integer("PERCENT-COMPLETE", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "URL", patch.Url,
            value => CalendarPatchValueSerializer.Uri("URL", value), ScalarParameterMode.Preserve, ref changed);
        document = ApplyScalar(document, master.Path, "ORGANIZER", patch.Organizer,
            value => CalendarPatchValueSerializer.NamedUri("ORGANIZER", value, "CN"), ScalarParameterMode.Replace, ref changed);
        return (document, changed);
    }

    private static CalendarContentDocument ApplyScalar<T>(
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string propertyName,
        CalendarScalarPatch<T>? patch,
        Func<T, string> serialize,
        ScalarParameterMode parameterMode,
        ref bool changed)
    {
        if (patch is null)
            return document;
        var existing = FindProperty(document, path, propertyName);
        var desired = patch.Operation == CalendarScalarPatchOperation.Clear ? null : serialize(patch.Value!);
        if (existing is null && desired is null)
            return document;
        if (desired is null)
        {
            changed = true;
            return CalendarContentDocument.Parse(document.SetOrClearSinglePropertySlice(path, propertyName, null));
        }
        var candidate = CalendarContentDocument.Parse(existing is null
            ? document.SetOrClearSinglePropertySlice(path, propertyName, desired)
            : SetExistingScalar(document, path, propertyName, desired, parameterMode));
        var candidateProperty = FindProperty(candidate, path, propertyName)!;
        if (existing is not null && CalendarEntityCreateFidelity.ArePropertiesEquivalent(existing, candidateProperty))
            return document;
        changed = true;
        return candidate;
    }

    private static byte[] SetExistingScalar(
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string propertyName,
        string desired,
        ScalarParameterMode parameterMode) => parameterMode switch
        {
            ScalarParameterMode.Preserve => document.ReplaceSinglePropertyValue(
                path,
                propertyName,
                CalendarContentDocument.RawValueFromPropertySlice(desired)),
            ScalarParameterMode.Temporal => document.SetSinglePropertySlicePreservingParameters(
                path,
                propertyName,
                desired,
                TemporalValueParameters),
            _ => document.SetOrClearSinglePropertySlice(path, propertyName, desired)
        };

    private static bool HasAddressedDerivedScalar(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch)
    {
        var addressed = new (string Name, bool IsAddressed)[]
        {
            ("SUMMARY", patch.Summary is not null), ("DESCRIPTION", patch.Description is not null),
            ("DTSTART", patch.Start is not null), ("DTEND", patch.End is not null),
            ("DUE", patch.Due is not null), ("DURATION", patch.Duration is not null),
            ("LOCATION", patch.Location is not null), ("GEO", patch.Geo is not null),
            ("STATUS", patch.Status is not null), ("TRANSP", patch.Transparency is not null),
            ("CLASS", patch.Classification is not null), ("PRIORITY", patch.Priority is not null),
            ("PERCENT-COMPLETE", patch.PercentComplete is not null),
            ("URL", patch.Url is not null), ("ORGANIZER", patch.Organizer is not null)
        };
        return addressed.Where(item => item.IsAddressed).Any(item =>
            FindProperty(document, master.Path, item.Name)?.Parameters.Any(parameter =>
                parameter.Name.Equals("DERIVED", StringComparison.OrdinalIgnoreCase)
                && parameter.Values.Any(value => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase))) == true);
    }

    private static bool IntroducesDerivedOrganizer(CalendarEventPatch patch) =>
        patch.Organizer is { Operation: CalendarScalarPatchOperation.Set, Value: not null }
        && patch.Organizer.Value.Parameters.Any(parameter =>
            parameter.Name.Equals("DERIVED", StringComparison.OrdinalIgnoreCase)
            && parameter.Values.Any(value => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)));

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) ApplyCategories(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarCollectionPatch<string>? patch)
    {
        if (patch is null)
            return (null, null);
        var properties = document.Properties.Where(property =>
            PathsEqual(property.ComponentPath, master.Path)
            && property.Name.Equals("CATEGORIES", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (patch.Operation == CalendarCollectionPatchOperation.ReplaceAll)
            return ReplaceAllCategories(document, master, properties, patch.Values!);

        var occurrences = properties.SelectMany(property => SplitTextList(property.RawEncodedValue)
            .Select((value, index) => new CategoryOccurrence(property, index, value))).ToArray();
        var removalMatch = MatchCategoryRemovals(occurrences, patch.Remove ?? []);
        if (removalMatch.Failure is not null)
            return (null, Failure(removalMatch.Failure.Value));
        var matched = removalMatch.Matches!;
        if (matched.Keys.Any(IsDerivedProperty))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        var replacements = BuildCategoryReplacements(matched);
        var additions = (patch.Add ?? []).Select(value =>
            "CATEGORIES:" + CalendarContentDocument.EncodeText(value) + "\r\n").ToArray();
        if (replacements.Count == 0 && additions.Length == 0)
            return (null, null);
        return (document.EditProperties(master.Path, replacements, additions), null);
    }

    private static (
        Dictionary<CalendarContentProperty, HashSet<int>>? Matches,
        CalendarEntityPatchCode? Failure) MatchCategoryRemovals(
        IReadOnlyList<CategoryOccurrence> occurrences,
        IReadOnlyList<string> removals)
    {
        if (removals.Count != removals.Distinct(StringComparer.Ordinal).Count())
            return (null, CalendarEntityPatchCode.RemovalAmbiguous);
        var matched = new Dictionary<CalendarContentProperty, HashSet<int>>();
        foreach (var removal in removals)
        {
            var matches = occurrences.Where(occurrence =>
                string.Equals(occurrence.Value, removal, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
                return (null, CalendarEntityPatchCode.RemovalNotFound);
            if (matches.Length > 1)
                return (null, CalendarEntityPatchCode.RemovalAmbiguous);
            if (!matched.TryGetValue(matches[0].Property, out var indexes))
                matched.Add(matches[0].Property, indexes = []);
            if (!indexes.Add(matches[0].Index))
                return (null, CalendarEntityPatchCode.RemovalAmbiguous);
        }
        return (matched, null);
    }

    private static Dictionary<CalendarContentProperty, string?> BuildCategoryReplacements(
        IReadOnlyDictionary<CalendarContentProperty, HashSet<int>> matched)
    {
        var replacements = new Dictionary<CalendarContentProperty, string?>();
        foreach (var pair in matched)
        {
            var retained = SplitTextList(pair.Key.RawEncodedValue)
                .Where((_, index) => !pair.Value.Contains(index)).ToArray();
            replacements.Add(pair.Key, retained.Length == 0 ? null : ReplaceRawValue(
                pair.Key.OriginalSlice,
                string.Join(',', retained.Select(CalendarContentDocument.EncodeText))));
        }
        return replacements;
    }

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) ReplaceAllCategories(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<CalendarContentProperty> properties,
        IReadOnlyList<string> values)
    {
        if (properties.Any(IsDerivedProperty))
            return (null, Failure(CalendarEntityPatchCode.InvalidInput));
        var existing = properties.SelectMany(property => SplitTextList(property.RawEncodedValue)).ToArray();
        if (existing.SequenceEqual(values, StringComparer.Ordinal))
            return (null, null);
        var replacements = properties.ToDictionary(property => property, _ => (string?)null);
        IReadOnlyList<string> additions = values.Count == 0
            ? []
            : ["CATEGORIES:" + string.Join(',', values.Select(CalendarContentDocument.EncodeText)) + "\r\n"];
        return (document.EditProperties(master.Path, replacements, additions), null);
    }

    private static CalendarContentProperty? FindProperty(
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path,
        string name) => document.Properties.SingleOrDefault(property =>
        PathsEqual(property.ComponentPath, path)
        && property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> SplitTextList(string raw)
    {
        var values = new List<string>();
        var start = 0;
        var escaped = false;
        for (var index = 0; index < raw.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (raw[index] == '\\')
            {
                escaped = true;
                continue;
            }
            if (raw[index] != ',')
                continue;
            values.Add(CalendarContentDocument.DecodeText(raw[start..index]));
            start = index + 1;
        }
        values.Add(CalendarContentDocument.DecodeText(raw[start..]));
        return values;
    }

    private static string ReplaceRawValue(string originalSlice, string rawValue)
    {
        var colon = originalSlice.IndexOf(':');
        var ending = originalSlice.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : originalSlice.EndsWith('\n') ? "\n" : string.Empty;
        return originalSlice[..(colon + 1)] + rawValue + ending;
    }

    private static bool HasValidAddressedFinalShape(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch,
        CalendarEntityKind kind)
    {
        try
        {
            var start = FindProperty(document, master.Path, "DTSTART");
            var endOrDue = FindProperty(document, master.Path, kind == CalendarEntityKind.Event ? "DTEND" : "DUE");
            var duration = FindProperty(document, master.Path, "DURATION")?.RawEncodedValue;
            CalendarEntityCreateValidator.ValidatePatchFinalTemporal(
                kind,
                start is null ? null : CalendarPatchValueSerializer.ParseTemporal(start),
                endOrDue is null ? null : CalendarPatchValueSerializer.ParseTemporal(endOrDue),
                duration);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasRecurrenceMembership(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEntityKind kind)
    {
        var entityName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        return document.Components.Count(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(entityName, StringComparison.OrdinalIgnoreCase)) > 1
            || document.Properties.Any(property => PathsEqual(property.ComponentPath, master.Path)
                && IsRecurrenceMembershipProperty(property.Name));
    }

    private static bool IsRecurrenceMembershipProperty(string name) =>
        name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase);

    private static bool IsDerivedProperty(CalendarContentProperty property) => property.Parameters.Any(parameter =>
        parameter.Name.Equals("DERIVED", StringComparison.OrdinalIgnoreCase)
        && parameter.Values.Any(value => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)));

    private static bool PathsEqual(
        IReadOnlyList<CalendarComponentPathSegment> left,
        IReadOnlyList<CalendarComponentPathSegment> right) => left.SequenceEqual(right);

    private static CalendarEntityPatchResult Failure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot? snapshot = null) => new(
        code,
        CalendarMutationState.NotAttempted,
        snapshot,
        Phase: CalendarEntityPatchPhase.CompleteResourceSemantics);

    private sealed record CategoryOccurrence(CalendarContentProperty Property, int Index, string Value);

    private enum ScalarParameterMode
    {
        Preserve,
        Temporal,
        Replace
    }

    private sealed record ScopedTemporalShifts(
        TimeSpan? Start,
        TimeSpan? End,
        TimeSpan? Due,
        bool Failure);
}
