using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Applies configured Calendar Scope to standards-based Calendar discovery.</summary>
internal sealed class CalendarService : ICalendarService
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumCalendars = 256;
    private readonly ICalendarClient _calendarClient;
    private readonly IOptions<CalDavOptions> _options;
    private readonly ILogger<CalendarService> _logger;

    public CalendarService(
        ICalendarClient calendarClient,
        IOptions<CalDavOptions> options,
        ILogger<CalendarService> logger)
    {
        _calendarClient = calendarClient;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CalendarDiscoveryResult> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        var discovered = await _calendarClient.GetCalendarsAsync(cancellationToken);
        return ApplyScope(discovered);
    }

    /// <inheritdoc />
    public async Task<CalendarSelectionResult> ResolveDefaultCalendarAsync(
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        var discovered = await _calendarClient.GetCalendarsAsync(cancellationToken);
        var scoped = ApplyScope(discovered).Items;
        var authorizedCandidates = scoped.Take(MaximumDiagnostics).ToArray();
        var name = GetDefaultName(entityKind);
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
            return CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, matchingScoped.Take(MaximumDiagnostics).ToArray());

        return SupportsEntityKind(matchingScoped[0], entityKind)
            ? CalendarSelectionResult.Success(matchingScoped[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.UnsupportedCapability, matchingScoped);
    }

    private CalendarDiscoveryResult ApplyScope(IReadOnlyList<CalendarDescriptor> discovered)
    {
        var scope = ParseScope(_options.Value.CalendarHrefs);
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
        var uniqueItems = scopedItems
            .GroupBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(calendar => calendar.Href, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = BuildDiagnostics(scope, discovered, MaximumDiagnostics);
        var items = uniqueItems;

        _logger.LogDebug("Discovered {DiscoveredCount} Calendar(s), {InScopeCount} in scope", discovered.Count, items.Length);
        return new CalendarDiscoveryResult(items, diagnostics);
    }

    private string? GetDefaultName(CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => _options.Value.DefaultEventCalendarName,
        CalendarEntityKind.Todo => _options.Value.DefaultTodoCalendarName,
        _ => null
    };

    private static bool SupportsEntityKind(CalendarDescriptor calendar, CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport != EntityKindSupport.NotAdvertised,
        CalendarEntityKind.Todo => calendar.TodoSupport != EntityKindSupport.NotAdvertised,
        _ => false
    };

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) =>
        calendarHrefs is null
            ? []
            : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    private static IReadOnlyList<CalendarDiagnostic> BuildDiagnostics(
        IReadOnlyList<string> scope,
        IReadOnlyList<CalendarDescriptor> discovered,
        int maximumDiagnostics)
    {
        if (scope.Count == 0)
            return [];

        var duplicateHrefs = scope.GroupBy(href => href, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        var discoveredHrefs = discovered.Select(calendar => calendar.Href).ToHashSet(StringComparer.Ordinal);
        var missingHrefs = scope.Where(href => !discoveredHrefs.Contains(href))
            .Distinct(StringComparer.Ordinal);

        return duplicateHrefs.Select(_ => CreateDuplicateDiagnostic())
            .Concat(missingHrefs.Select(_ => CreateMissingDiagnostic()))
            .Take(maximumDiagnostics)
            .ToArray();
    }

    private static CalendarDiagnostic CreateDuplicateDiagnostic() =>
        new("duplicate_calendar_href", "A Calendar href is configured more than once.", CalendarDiagnosticSeverity.Warning);

    private static CalendarDiagnostic CreateMissingDiagnostic() =>
        new("calendar_href_not_found", "A configured Calendar href was not discovered.", CalendarDiagnosticSeverity.Warning);
}
