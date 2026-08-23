using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>Low-level CalDAV discovery client for Calendar collections.</summary>
public interface ICalendarClient
{
    /// <summary>Invalidates process-lifetime capability observations before an explicit rediscovery.</summary>
    void RediscoverCapabilities()
    {
    }

    /// <summary>Discovers every Calendar collection visible to the configured account.</summary>
    Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken);

    /// <summary>Uses a minimal CalDAV REPORT to collect candidate resource hrefs.</summary>
    Task<IReadOnlyList<string>> QueryCalendarResourceHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    /// <summary>Reads one complete Calendar Object Resource revision by canonical absolute href.</summary>
    Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken);

    /// <summary>Reads one resource whose absence is an expected semantic observation.</summary>
    async Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = Internal.CalendarHttpTelemetry.BeginAbsenceProbe();
        return await GetCalendarResourceAsync(href, cancellationToken);
    }

    /// <summary>Verifies Calendar multiget support, then reads authoritative bytes with GET.</summary>
    async Task<CalendarResourceRead> GetCalendarResourceForQueryAsync(
        string calendarHref,
        string href,
        CancellationToken cancellationToken) => await GetCalendarResourceAsync(href, cancellationToken);

    /// <summary>Reads one bounded batch of complete Calendar Object Resource revisions for query evaluation.</summary>
    async Task<IReadOnlyList<CalendarResourceRead>> GetCalendarResourcesForQueryAsync(
        string calendarHref,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken)
    {
        var reads = new List<CalendarResourceRead>(hrefs.Count);
        foreach (var href in hrefs)
            reads.Add(await GetCalendarResourceAsync(href, cancellationToken));
        return reads;
    }

    /// <summary>Conditionally creates one complete Calendar Object Resource without overwriting.</summary>
    Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Conditionally deletes one complete Calendar Object Resource without retrying.</summary>
    Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
        CalendarResourceDeleteRequest request,
        CancellationToken cancellationToken);

    /// <summary>Atomically moves one resource with exact source revision and no-overwrite conditions.</summary>
    Task<CalendarResourceMoveDispatchResult> MoveCalendarResourceAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => Task.FromResult(
            new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.UnsupportedCapability));

    /// <summary>Conditionally replaces one complete Calendar Object Resource without retrying.</summary>
    Task<CalendarResourceUpdateDispatchResult> UpdateCalendarResourceAsync(
        CalendarResourceUpdateRequest request,
        CancellationToken cancellationToken) => Task.FromResult(
            new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.UnsupportedCapability));
}
