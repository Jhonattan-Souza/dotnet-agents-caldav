using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Narrow true-external seam used only by initial query execution.</summary>
internal interface ICalendarQueryTransport
{
    Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<CalendarMultigetResult> MultigetAsync(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        CancellationToken cancellationToken);

    Task<CalendarResourceRead> GetAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken);
}

/// <summary>Closed authoritative result of one bounded Calendar multiget attempt.</summary>
internal abstract record CalendarMultigetResult
{
    private CalendarMultigetResult()
    {
    }

    internal sealed record Resources(IReadOnlyList<CalendarResourceRead> Values) : CalendarMultigetResult;

    internal sealed record VerifiedUnavailable : CalendarMultigetResult;
}
