using DotnetAgents.CalDav.Core.Models;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarRecurrenceSetEdit(
    CalendarContentDocument Document,
    bool Changed,
    CalendarEntityPatchResult? Failure);

/// <summary>Applies recurrence-definition replacement and exact deletion-only orphan reconciliation.</summary>
internal static class CalendarRecurrenceSetPatchEditor
{
    public static CalendarRecurrenceSetEdit TryEdit(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarRecurrenceSetPatch patch,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            if (CalendarOccurrenceEvaluator.HasUnevaluableRecurrenceStructure(snapshot))
                return Failed(document, snapshot, CalendarEntityPatchCode.RecurrenceUnevaluable);
            var masterStart = DirectProperties(document, master, "DTSTART").SingleOrDefault();
            CalendarEntityCreateValidator.ValidateRecurrencePatch(
                patch,
                masterStart is null ? null : CalendarPatchValueSerializer.ParseTemporal(masterStart),
                kind);
            var candidates = ReadCandidates(document, master, kind);
            var edited = ReplaceDefinition(document, master, patch);
            master = edited.GetComponent(master.Path);
            var definitionChanged = !HasEquivalentDefinition(document, edited, master);
            var orphans = candidates.Where(candidate => !CalendarOccurrencePatchBuilder.IsIncludedByRecurrenceDefinition(
                    edited,
                    master,
                    candidate.Identity,
                    cancellationToken))
                .ToArray();
            if (!HasExactReconciliations(orphans, patch.OrphanReconciliations))
                return Failed(document, snapshot, CalendarEntityPatchCode.InvalidCalendarData);
            edited = RemoveOrphans(edited, master, orphans);
            if (!HasExactRequestedOverrides(edited, patch.Value?.Overrides, kind))
                return Failed(document, snapshot, CalendarEntityPatchCode.InvalidCalendarData);
            var changed = definitionChanged || orphans.Length > 0;
            return new(changed ? edited : document, changed, null);
        }
        catch (EvaluationLimitExceededException)
        {
            return Failed(document, snapshot, CalendarEntityPatchCode.LimitExhausted);
        }
        catch (CalendarRecurrenceUnevaluableException)
        {
            return Failed(document, snapshot, CalendarEntityPatchCode.RecurrenceUnevaluable);
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return Failed(document, snapshot, CalendarEntityPatchCode.InvalidCalendarData);
        }
    }

    private static bool HasEquivalentDefinition(
        CalendarContentDocument before,
        CalendarContentDocument after,
        CalendarContentComponent master) => DefinitionSignature(before, before.GetComponent(master.Path))
        .SequenceEqual(DefinitionSignature(after, master), StringComparer.Ordinal);

    private static IReadOnlyList<string> DefinitionSignature(
        CalendarContentDocument document,
        CalendarContentComponent master)
    {
        var signature = new List<string>();
        foreach (var property in DirectProperties(document, master, "RRULE"))
            signature.Add("RRULE|" + new RecurrencePattern(property.RawEncodedValue));
        AddTemporalSignature(document, master, "RDATE", signature);
        AddTemporalSignature(document, master, "EXDATE", signature);
        return signature.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddTemporalSignature(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string name,
        ICollection<string> signature)
    {
        foreach (var property in DirectProperties(document, master, name))
        {
            foreach (var token in property.RawEncodedValue.Split(',', StringSplitOptions.None))
            {
                signature.Add(token.Contains('/', StringComparison.Ordinal)
                    ? name + "|PERIOD|" + property.OriginalSlice[..property.OriginalSlice.IndexOf(':')] + "|" + token
                    : name + "|" + CalendarPatchValueSerializer.ParseTemporal(property with
                {
                    RawEncodedValue = token
                }).GetCanonicalSortKey());
            }
        }
    }

    private static bool HasExactRequestedOverrides(
        CalendarContentDocument document,
        IReadOnlyList<CalendarRecurrenceOverridePatchValue>? requested,
        CalendarEntityKind kind)
    {
        if (requested is null)
            return true;
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var actual = document.Components.Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase)
                && DirectProperties(document, component, "RECURRENCE-ID").Any())
            .ToArray();
        return actual.Length == requested.Count
            && requested.All(item => actual.Any(component => OverrideMatches(document, component, item, kind)));
    }

    private static bool OverrideMatches(
        CalendarContentDocument document,
        CalendarContentComponent component,
        CalendarRecurrenceOverridePatchValue requested,
        CalendarEntityKind kind)
    {
        var identity = DirectProperties(document, component, "RECURRENCE-ID").Single();
        if (CalendarPatchValueSerializer.ParseTemporal(identity) != requested.RecurrenceIdentity
            || ReadRange(identity) != requested.Range
            || IsCancelled(document, component)
                != (requested.Status == CalendarRecurrenceOverrideStatus.Cancelled))
        {
            return false;
        }
        return OptionalTemporalMatches(document, component, "DTSTART", requested.MovedStart)
            && OptionalTemporalMatches(
                document,
                component,
                kind == CalendarEntityKind.Event ? "DTEND" : "DUE",
                requested.MovedEnd);
    }

    private static CalendarRecurrenceOverrideRange? ReadRange(CalendarContentProperty identity) =>
        identity.Parameters.Any(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase)
            && parameter.Values.Any(value => value.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase)))
            ? CalendarRecurrenceOverrideRange.ThisAndFuture
            : null;

    private static bool IsCancelled(
        CalendarContentDocument document,
        CalendarContentComponent component) => DirectProperties(document, component, "STATUS").SingleOrDefault()
        ?.RawEncodedValue.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;

    private static bool OptionalTemporalMatches(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name,
        CalendarTemporalValue? requested)
    {
        var actual = DirectProperties(document, component, name).SingleOrDefault();
        return requested is null
            ? actual is null
            : actual is not null && CalendarPatchValueSerializer.ParseTemporal(actual) == requested;
    }

    private static CalendarContentDocument ReplaceDefinition(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarRecurrenceSetPatch patch)
    {
        var value = patch.Operation == CalendarScalarPatchOperation.Set ? patch.Value : null;
        document = ReplaceProperties(
            document,
            master,
            "RRULE",
            value?.Rule is null ? [] : [$"RRULE:{new RecurrencePattern(value.Rule)}\r\n"]);
        master = document.GetComponent(master.Path);
        document = ReplaceOptionalTemporal(
            document,
            master,
            "RDATE",
            patch.Operation == CalendarScalarPatchOperation.Clear,
            value?.RecurrenceDates);
        master = document.GetComponent(master.Path);
        document = ReplaceOptionalTemporal(
            document,
            master,
            "EXDATE",
            patch.Operation == CalendarScalarPatchOperation.Clear,
            value?.ExceptionDates);
        return document;
    }

    private static CalendarContentDocument ReplaceOptionalTemporal(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string propertyName,
        bool clear,
        IReadOnlyList<CalendarTemporalValue>? values)
    {
        if (!clear && values is null)
            return document;
        var additions = (values ?? [])
            .Select(item => CalendarPatchValueSerializer.Temporal(propertyName, item))
            .ToArray();
        return ReplaceProperties(document, master, propertyName, additions);
    }

    private static CalendarContentDocument ReplaceProperties(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name,
        IReadOnlyList<string> additions)
    {
        var removals = DirectProperties(document, component, name).ToDictionary(item => item, _ => (string?)null);
        return removals.Count == 0 && additions.Count == 0
            ? document
            : CalendarContentDocument.Parse(document.EditProperties(component.Path, removals, additions));
    }

    private static IReadOnlyList<OrphanCandidate> ReadCandidates(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarEntityKind kind)
    {
        var candidates = DirectProperties(document, master, "EXDATE")
            .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
                .Select(token => new OrphanCandidate(
                    CalendarOrphanKind.ExceptionDate,
                    CalendarPatchValueSerializer.ParseTemporal(property with { RawEncodedValue = token }),
                    null,
                    property,
                    null)))
            .ToList();
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        foreach (var component in document.Components.Where(item => item.Path.Count == 2
                     && item.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase)))
        {
            var identity = DirectProperties(document, component, "RECURRENCE-ID").SingleOrDefault();
            if (identity is null)
                continue;
            var isRange = identity.Parameters.Any(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase)
                && parameter.Values.Any(value => value.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase)));
            candidates.Add(new(
                CalendarOrphanKind.Override,
                CalendarPatchValueSerializer.ParseTemporal(identity),
                isRange ? CalendarOrphanOverrideKind.ThisAndFuture : CalendarOrphanOverrideKind.Individual,
                null,
                component));
        }
        return candidates;
    }

    private static bool HasExactReconciliations(
        IReadOnlyList<OrphanCandidate> orphans,
        IReadOnlyList<CalendarOrphanReconciliation> reconciliations)
    {
        var expected = orphans.Select(Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var actual = reconciliations.Select(Key).ToArray();
        return actual.Distinct(StringComparer.Ordinal).Count() == actual.Length
            && actual.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static CalendarContentDocument RemoveOrphans(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<OrphanCandidate> orphans)
    {
        var exdateKeys = orphans.Where(item => item.Kind == CalendarOrphanKind.ExceptionDate)
            .Select(item => item.Identity.GetCanonicalSortKey())
            .ToHashSet(StringComparer.Ordinal);
        if (exdateKeys.Count > 0)
            document = RemoveExceptionDates(document, master, exdateKeys);
        var overrideComponents = orphans.Where(item => item.Component is not null)
            .Select(item => document.GetComponentOccurrence(item.Component!.Path))
            .ToArray();
        if (overrideComponents.Length == 0)
            return document;
        var root = document.GetComponent(master.Path.Take(1).ToArray());
        return CalendarContentDocument.Parse(document.EditOccurrences(root.Path, overrideComponents, []));
    }

    private static CalendarContentDocument RemoveExceptionDates(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlySet<string> keys)
    {
        var replacements = new Dictionary<CalendarContentProperty, string?>();
        foreach (var property in DirectProperties(document, master, "EXDATE"))
        {
            var retained = property.RawEncodedValue.Split(',', StringSplitOptions.None).Where(token =>
                !keys.Contains(CalendarPatchValueSerializer.ParseTemporal(property with
                {
                    RawEncodedValue = token
                }).GetCanonicalSortKey())).ToArray();
            replacements.Add(property, retained.Length == 0 ? null : ReplaceRawValue(property.OriginalSlice, retained));
        }
        return CalendarContentDocument.Parse(document.EditProperties(master.Path, replacements, []));
    }

    private static string ReplaceRawValue(string original, IReadOnlyList<string> values)
    {
        var colon = original.IndexOf(':');
        var ending = original.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : original.EndsWith('\n') ? "\n" : string.Empty;
        return original[..(colon + 1)] + string.Join(',', values) + ending;
    }

    private static string Key(OrphanCandidate candidate) =>
        $"{candidate.Kind}|{candidate.OverrideKind}|{candidate.Identity.GetCanonicalSortKey()}";

    private static string Key(CalendarOrphanReconciliation reconciliation) =>
        $"{reconciliation.Kind}|{reconciliation.OverrideKind}|{reconciliation.RecurrenceIdentity.GetCanonicalSortKey()}";

    private static IEnumerable<CalendarContentProperty> DirectProperties(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name) => document.Properties.Where(property => property.ComponentPath.SequenceEqual(component.Path)
            && property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static CalendarRecurrenceSetEdit Failed(
        CalendarContentDocument document,
        CalendarResourceSnapshot snapshot,
        CalendarEntityPatchCode code) => new(
        document,
        false,
        new CalendarEntityPatchResult(
            code,
            CalendarMutationState.NotAttempted,
            snapshot,
            Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));

    private sealed record OrphanCandidate(
        CalendarOrphanKind Kind,
        CalendarTemporalValue Identity,
        CalendarOrphanOverrideKind? OverrideKind,
        CalendarContentProperty? Property,
        CalendarContentComponent? Component);
}
