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

    /// <summary>Queries persisted Event and To-do snapshots within an explicit Calendar Scope.</summary>
    Task<CalendarEntityQueryResult> QueryEntitiesAsync(
        CalendarEntityQuery query,
        CancellationToken cancellationToken);

    /// <summary>Queries bounded derived Event and To-do Occurrences.</summary>
    Task<CalendarOccurrenceQueryResult> QueryOccurrencesAsync(
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken);

    /// <summary>Reads one authoritative Calendar Object Resource snapshot.</summary>
    Task<CalendarResourceRead> GetResourceAsync(string href, CancellationToken cancellationToken);

    /// <summary>Creates one complete non-recurring Event and returns the observed server revision.</summary>
    Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates one complete non-recurring To-do and returns the observed server revision.</summary>
    Task<CalendarEntityCreateResult> CreateTodoAsync(
        CalendarTodoCreateRequest request,
        CancellationToken cancellationToken);
}
