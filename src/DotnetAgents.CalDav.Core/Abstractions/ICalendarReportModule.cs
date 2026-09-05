using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Runs bounded native reports against one authorized Calendar.</summary>
public interface ICalendarReportModule
{
    /// <summary>Returns server-computed free/busy periods without fetching Calendar Object Resources.</summary>
    Task<CalendarFreeBusyResult> FreeBusyAsync(CalendarFreeBusyRequest request, CancellationToken cancellationToken);

    /// <summary>Returns one native synchronization page and an authenticated session checkpoint.</summary>
    Task<CalendarResourceChangesResult> ChangesAsync(CalendarResourceChangesRequest request, CancellationToken cancellationToken);
}
