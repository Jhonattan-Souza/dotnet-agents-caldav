using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Low-level CalDAV discovery client for Calendar collections.</summary>
public interface ICalendarClient
{
    /// <summary>Discovers every Calendar collection visible to the configured account.</summary>
    Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken);
}
