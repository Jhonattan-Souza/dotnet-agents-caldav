using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>High-level Calendar discovery service used by unified MCP tools.</summary>
public interface ICalendarService
{
    /// <summary>Lists the Calendars in the configured Calendar Scope.</summary>
    Task<CalendarDiscoveryResult> GetCalendarsAsync(CancellationToken cancellationToken);

    /// <summary>Resolves the configured default Calendar for one Entity Kind without fallback.</summary>
    Task<CalendarSelectionResult> ResolveDefaultCalendarAsync(
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken);

    /// <summary>Reads one authoritative Calendar Object Resource snapshot.</summary>
    Task<CalendarResourceRead> GetResourceAsync(string href, CancellationToken cancellationToken);
}
