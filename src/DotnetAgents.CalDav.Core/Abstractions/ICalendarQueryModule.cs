using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Owns one bounded Calendar Entity query from Start through snapshot continuation.</summary>
public interface ICalendarQueryModule
{
    /// <summary>Starts a complete query or reads one immutable retained result page.</summary>
    Task<QueryReply<CalendarEntityQueryItem>> QueryEntitiesAsync(
        CalendarEntityQueryRequest request,
        CancellationToken cancellationToken);

    /// <summary>Starts one complete Occurrence query or reads its retained immutable result.</summary>
    Task<QueryReply<CalendarOccurrenceQueryItem>> QueryOccurrencesAsync(
        CalendarOccurrenceQueryRequest request,
        CancellationToken cancellationToken);

    /// <summary>Starts a compact To-do query or reads one immutable retained result page.</summary>
    Task<QueryReply<CalendarTodoQueryPageItem>> QueryTodosAsync(
        CalendarTodoQueryRequest request,
        CancellationToken cancellationToken);
}
