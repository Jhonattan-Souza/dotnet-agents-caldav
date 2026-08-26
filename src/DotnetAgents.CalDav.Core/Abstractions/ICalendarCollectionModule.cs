using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Deep module for creating and confirmation-bound deletion of Calendar collections.</summary>
public interface ICalendarCollectionModule
{
    /// <summary>Creates one Event, To-do, or mixed Calendar collection.</summary>
    Task<CalendarCollectionCreateResult> CreateAsync(
        CalendarCollectionCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reviews one exact collection href without dispatching DELETE.</summary>
    Task<CalendarCollectionDeleteReviewResult> ReviewDeleteAsync(
        CalendarCollectionDeleteRequest request,
        CancellationToken cancellationToken);

    /// <summary>Freshly reviews and executes one confirmed collection delete.</summary>
    Task<CalendarCollectionDeleteResult> ExecuteConfirmedDeleteAsync(
        CalendarCollectionDeleteRequest request,
        CalendarCollectionDeleteReviewBinding priorBinding,
        CancellationToken cancellationToken);
}
