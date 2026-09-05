using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Inspects standard Calendar properties and applies explicit metadata changes.</summary>
public interface ICalendarMetadataModule
{
    Task<CalendarMetadataSnapshot> InspectAsync(string calendarHref, CancellationToken cancellationToken);

    Task<CalendarMetadataPatchResult> PatchAsync(
        string calendarHref,
        CalendarMetadataPatch patch,
        CancellationToken cancellationToken);
}
