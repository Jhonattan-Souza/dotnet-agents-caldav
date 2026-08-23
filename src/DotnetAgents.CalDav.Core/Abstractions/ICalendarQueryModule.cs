using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Owns one bounded Calendar Entity query from Start through snapshot continuation.</summary>
public interface ICalendarQueryModule
{
    /// <summary>Starts a complete query or reads one immutable retained result page.</summary>
    Task<QueryReply<CalendarEntityQueryItem>> QueryEntitiesAsync(
        CalendarEntityQueryRequest request,
        CancellationToken cancellationToken);
}
