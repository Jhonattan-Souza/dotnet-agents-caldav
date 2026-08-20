using System.Globalization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Reconciles the independent RFC completion properties without inventing precedence.</summary>
internal static class CalendarTodoCompletionClassifier
{
    private static readonly string[] KnownStatuses = ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

    public static CalendarTodoCompletionClassification Classify(CalendarResourceSnapshot snapshot)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var master = document.GetMasterComponent(CalendarEntityKind.Todo);
            return Classify(document, master.Path);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return Indeterminate(
                null,
                null,
                null,
                "completion_resource_unreadable",
                "The To-do completion properties could not be interpreted.");
        }
    }

    internal static CalendarTodoCompletionClassification Classify(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var component = CalendarTodoComponentSelector.Select(document, recurrenceIdentity);
            return Classify(document, component.Path);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            return Indeterminate(
                null,
                null,
                null,
                "completion_resource_unreadable",
                "The To-do completion properties could not be interpreted.");
        }
    }

    internal static CalendarTodoCompletionClassification Classify(
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> componentPath)
    {
        var properties = document.Properties
            .Where(property => property.ComponentPath.SequenceEqual(componentPath))
            .ToArray();
        var evidence = ReadEvidence(properties);
        var state = DetermineState(evidence);
        if (state == CalendarTodoCompletionState.Indeterminate && evidence.Diagnostics.Count == 0)
            evidence.Diagnostics.Add(new CalendarResourceDiagnostic(
                "completion_evidence_conflict",
                "The To-do completion properties contain contradictory evidence.",
                CalendarResourceDiagnosticSeverity.Warning));
        return new(state, evidence.Status, evidence.CompletedAt, evidence.PercentComplete, evidence.Diagnostics);
    }

    private static CompletionEvidence ReadEvidence(IReadOnlyList<CalendarContentProperty> properties)
    {
        var diagnostics = new List<CalendarResourceDiagnostic>();
        var statusProperty = Single(properties, "STATUS", diagnostics);
        var completedProperty = Single(properties, "COMPLETED", diagnostics);
        var percentProperty = Single(properties, "PERCENT-COMPLETE", diagnostics);
        var status = statusProperty?.RawEncodedValue;
        if (status is not null && !KnownStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            Add(diagnostics, "completion_status_unknown", "The To-do STATUS value is not recognized.");
        var completedAt = ParseCompletedAt(completedProperty, diagnostics);
        var percentComplete = ParsePercentComplete(percentProperty, diagnostics);
        return new(status, completedAt, percentComplete, diagnostics);
    }

    private static CalendarTemporalValue? ParseCompletedAt(
        CalendarContentProperty? property,
        ICollection<CalendarResourceDiagnostic> diagnostics)
    {
        if (property is null)
            return null;
        try
        {
            return CalendarPatchValueSerializer.ParseTemporal(property);
        }
        catch (FormatException)
        {
            Add(diagnostics, "completion_value_invalid", "The To-do COMPLETED value is invalid.");
            return null;
        }
    }

    private static int? ParsePercentComplete(
        CalendarContentProperty? property,
        ICollection<CalendarResourceDiagnostic> diagnostics)
    {
        if (property is null)
            return null;
        if (int.TryParse(property.RawEncodedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is >= 0 and <= 100)
            return parsed;
        Add(diagnostics, "completion_value_invalid", "The To-do PERCENT-COMPLETE value is invalid.");
        return null;
    }

    private static CalendarTodoCompletionState DetermineState(CompletionEvidence evidence)
    {
        var hasCompletionEvidence = evidence.IsCompletedStatus || evidence.CompletedAt is not null || evidence.PercentComplete == 100;
        if (HasContradiction(evidence, hasCompletionEvidence))
            return CalendarTodoCompletionState.Indeterminate;
        if (evidence.IsCancelled)
            return CalendarTodoCompletionState.Cancelled;
        return hasCompletionEvidence ? CalendarTodoCompletionState.Completed : CalendarTodoCompletionState.Open;
    }

    private static bool HasContradiction(CompletionEvidence evidence, bool hasCompletionEvidence) =>
        evidence.Diagnostics.Count > 0
        || evidence.IsCancelled && (evidence.CompletedAt is not null || evidence.PercentComplete == 100)
        || evidence.IsCompletedStatus && evidence.PercentComplete is < 100
        || hasCompletionEvidence && HasOpenStatus(evidence.Status);

    private static bool HasOpenStatus(string? status) => status is not null
        && (status.Equals("NEEDS-ACTION", StringComparison.OrdinalIgnoreCase)
            || status.Equals("IN-PROCESS", StringComparison.OrdinalIgnoreCase));

    private sealed record CompletionEvidence(
        string? Status,
        CalendarTemporalValue? CompletedAt,
        int? PercentComplete,
        List<CalendarResourceDiagnostic> Diagnostics)
    {
        public bool IsCancelled => Status?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;

        public bool IsCompletedStatus => Status?.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static CalendarContentProperty? Single(
        IReadOnlyList<CalendarContentProperty> properties,
        string name,
        ICollection<CalendarResourceDiagnostic> diagnostics)
    {
        var matches = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            Add(diagnostics, "completion_property_ambiguous", $"The To-do {name} property occurs more than once.");
        return matches.FirstOrDefault();
    }

    private static CalendarTodoCompletionClassification Indeterminate(
        string? status,
        CalendarTemporalValue? completedAt,
        int? percentComplete,
        string code,
        string message) => new(
        CalendarTodoCompletionState.Indeterminate,
        status,
        completedAt,
        percentComplete,
        [new CalendarResourceDiagnostic(code, message, CalendarResourceDiagnosticSeverity.Warning)]);

    private static void Add(
        ICollection<CalendarResourceDiagnostic> diagnostics,
        string code,
        string message) => diagnostics.Add(new CalendarResourceDiagnostic(
            code,
            message,
            CalendarResourceDiagnosticSeverity.Warning));
}
