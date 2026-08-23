using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Narrow true-external seam used only by initial query execution.</summary>
internal interface ICalendarQueryTransport
{
    Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalendarResourceRead>> MultigetAsync(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        CancellationToken cancellationToken);

    Task<CalendarResourceRead> GetAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken);
}

internal sealed record CalendarQueryDiscovery(
    CalendarDiscoveryResult Discovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault)
{
    internal CalendarSelectionResult Default(CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => EventDefault,
        CalendarEntityKind.Todo => TodoDefault,
        _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
    };
}
