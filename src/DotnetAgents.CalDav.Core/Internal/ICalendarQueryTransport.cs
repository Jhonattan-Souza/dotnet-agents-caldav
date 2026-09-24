using DotnetAgents.CalDav.Core.Internal.Ical;
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

    /// <summary>
    /// Returns the union of the pre-filter branches, verified unavailability of text matching, or an unreduced
    /// outcome for one attempt; an empty pre-filter is never sent and never reduces candidates.
    /// </summary>
    Task<CalendarTextCandidateResult> QueryTextCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        CalendarTextPrefilter prefilter,
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

/// <summary>Closed result of one Calendar text-match candidate reduction.</summary>
internal abstract record CalendarTextCandidateResult
{
    private CalendarTextCandidateResult()
    {
    }

    internal sealed record Hrefs(IReadOnlySet<string> Values) : CalendarTextCandidateResult;

    internal sealed record VerifiedUnavailable : CalendarTextCandidateResult;

    /// <summary>This attempt produced no usable reduction; nothing about the capability is retained.</summary>
    internal sealed record Unreduced : CalendarTextCandidateResult;
}
