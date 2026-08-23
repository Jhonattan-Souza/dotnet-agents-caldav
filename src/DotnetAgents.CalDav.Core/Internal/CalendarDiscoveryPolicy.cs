using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Applies one authorization-bound Calendar Scope and default-selection policy.</summary>
internal sealed class CalendarDiscoveryPolicy(
    IOptions<CalDavOptions> options,
    ILogger<CalendarService> logger)
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumCalendars = 256;

    internal CalendarDiscoveryResult ApplyScope(IReadOnlyList<CalendarDescriptor> discovered)
    {
        var scope = ParseScope(options.Value.CalendarHrefs);
        var scopedHrefs = scope.ToHashSet(StringComparer.Ordinal);
        var uniqueDiscovered = discovered
            .GroupBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (uniqueDiscovered.Length > MaximumCalendars)
            throw new CalendarDiscoveryLimitException(uniqueDiscovered.Length);

        var scopedItems = scopedHrefs.Count == 0
            ? uniqueDiscovered
            : uniqueDiscovered.Where(calendar => scopedHrefs.Contains(calendar.Href)).ToArray();
        var items = scopedItems
            .GroupBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(calendar => calendar.Href, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = BuildDiagnostics(scope, discovered);

        logger.LogDebug(
            "CalDAV operation {Code} completed at {Phase}",
            "discovery_complete",
            "selectionDiscoveryCapability");
        return new CalendarDiscoveryResult(items, diagnostics);
    }

    internal CalendarSelectionResult ResolveDefault(
        CalendarEntityKind entityKind,
        IReadOnlyList<CalendarDescriptor> discovered,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        var authorizedCandidates = scoped.Take(MaximumDiagnostics).ToArray();
        var name = entityKind switch
        {
            CalendarEntityKind.Event => options.Value.DefaultEventCalendarName,
            CalendarEntityKind.Todo => options.Value.DefaultTodoCalendarName,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(name))
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, authorizedCandidates);

        var normalizedName = name.Trim();
        var matchingDiscovered = discovered.Where(calendar =>
            string.Equals(calendar.DisplayName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingDiscovered.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, authorizedCandidates);

        var matchingScoped = scoped.Where(calendar =>
            string.Equals(calendar.DisplayName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingScoped.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope, authorizedCandidates);
        if (matchingScoped.Length > 1)
            return CalendarSelectionResult.Failure(
                CalendarSelectionCode.Ambiguous,
                matchingScoped.Take(MaximumDiagnostics).ToArray());
        return SupportsEntityKind(matchingScoped[0], entityKind)
            ? CalendarSelectionResult.Success(matchingScoped[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.UnsupportedCapability, matchingScoped);
    }

    internal static bool SupportsEntityKind(CalendarDescriptor calendar, CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport != EntityKindSupport.NotAdvertised,
        CalendarEntityKind.Todo => calendar.TodoSupport != EntityKindSupport.NotAdvertised,
        _ => false
    };

    internal static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<CalendarDiagnostic> BuildDiagnostics(
        IReadOnlyList<string> scope,
        IReadOnlyList<CalendarDescriptor> discovered)
    {
        if (scope.Count == 0)
            return [];
        var duplicates = scope.GroupBy(href => href, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(_ => new CalendarDiagnostic(
                "duplicate_calendar_href",
                "A Calendar href is configured more than once.",
                CalendarDiagnosticSeverity.Warning));
        var discoveredHrefs = discovered.Select(calendar => calendar.Href).ToHashSet(StringComparer.Ordinal);
        var missing = scope.Where(href => !discoveredHrefs.Contains(href))
            .Distinct(StringComparer.Ordinal)
            .Select(_ => new CalendarDiagnostic(
                "calendar_href_not_found",
                "A configured Calendar href was not discovered.",
                CalendarDiagnosticSeverity.Warning));
        return duplicates.Concat(missing).Take(MaximumDiagnostics).ToArray();
    }
}
