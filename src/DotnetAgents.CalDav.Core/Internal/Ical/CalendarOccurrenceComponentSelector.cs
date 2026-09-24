using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Selects the component whose properties are effective for a derived Event or To-do occurrence.</summary>
internal static class CalendarOccurrenceComponentSelector
{
    internal static CalendarContentComponent Select(
        CalendarContentDocument document,
        CalendarTemporalValue recurrenceIdentity,
        CalendarEntityKind kind)
    {
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var candidates = document.Components
            .Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
            .Select(component => (Component: component, Identity: Identity(document, component)))
            .Where(candidate => candidate.Identity is not null)
            .ToArray();

        var exact = candidates.FirstOrDefault(candidate =>
            candidate.Identity!.GetCanonicalSortKey() == recurrenceIdentity.GetCanonicalSortKey()
            && !HasRange(candidate.Component, document, "THISANDFUTURE"));
        if (exact.Component is not null)
            return exact.Component;

        var targetKey = recurrenceIdentity.GetCanonicalSortKey();
        var range = candidates
            .Where(candidate => HasRange(candidate.Component, document, "THISANDFUTURE")
                && string.CompareOrdinal(candidate.Identity!.GetCanonicalSortKey(), targetKey) <= 0)
            .OrderByDescending(candidate => candidate.Identity!.GetCanonicalSortKey(), StringComparer.Ordinal)
            .FirstOrDefault();
        return range.Component ?? document.GetMasterComponent(kind);
    }

    private static CalendarTemporalValue? Identity(
        CalendarContentDocument document,
        CalendarContentComponent component) => document.Properties
        .Where(property => property.ComponentPath.SequenceEqual(component.Path)
            && property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase))
        .Select(CalendarPatchValueSerializer.ParseTemporal)
        .SingleOrDefault();

    private static bool HasRange(
        CalendarContentComponent component,
        CalendarContentDocument document,
        string range) => document.Properties
        .Where(property => property.ComponentPath.SequenceEqual(component.Path)
            && property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase))
        .SelectMany(property => property.Parameters)
        .Any(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase)
            && parameter.Values.Any(value => value.Equals(range, StringComparison.OrdinalIgnoreCase)));
}
