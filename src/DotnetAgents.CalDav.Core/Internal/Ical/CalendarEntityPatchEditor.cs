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
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind,
        DateTimeOffset now)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var master = document.GetMasterComponent(expectedKind);
            var effectivePatch = PrepareScalarPatch(snapshot, document, master, patch, expectedKind);
            if (effectivePatch is null)
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            var scalarEdit = ApplyScalars(document, master, effectivePatch);
            document = scalarEdit.Document;
            master = document.GetMasterComponent(expectedKind);
            if (!HasValidAddressedFinalShape(document, master, effectivePatch, expectedKind))
                return (null, Failure(CalendarEntityPatchCode.InvalidInput, snapshot));
            var changed = scalarEdit.Changed;

            var categoryEdit = ApplyCategories(document, master, patch.Categories);
            if (categoryEdit.Failure is not null)
                return (null, categoryEdit.Failure);
            if (categoryEdit.AuthoritativeUtf8 is not null)
            {
                document = CalendarContentDocument.Parse(categoryEdit.AuthoritativeUtf8);
                master = document.GetMasterComponent(expectedKind);
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
                structuredEdit.Document.GetMasterComponent(expectedKind),
                changed || structuredEdit.Changed,
                expectedKind,
                now);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return (null, Failure(CalendarEntityPatchCode.InvalidCalendarData, snapshot));
        }
    }

    private static CalendarEventPatch? PrepareScalarPatch(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEventPatch patch,
        CalendarEntityKind kind)
    {
        if (IntroducesDerivedOrganizer(patch)
            || patch.Start is not null && HasRecurrenceMembership(document, master, kind))
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

    private static (byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Failure) FinishEdit(
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
            master = document.GetMasterComponent(kind);
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
}
