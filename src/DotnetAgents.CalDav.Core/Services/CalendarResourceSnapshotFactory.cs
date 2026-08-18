using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Creates immutable snapshots from authoritative iCalendar resource bytes.</summary>
public static class CalendarResourceSnapshotFactory
{
    public static CalendarResourceSnapshot Create(
        string calendarHref,
        string resourceHref,
        string entityTag,
        ReadOnlyMemory<byte> authoritativeUtf8)
    {
        var projected = CalendarResourceProjector.Project(authoritativeUtf8.Span);
        return new CalendarResourceSnapshot(
            calendarHref,
            resourceHref,
            entityTag,
            authoritativeUtf8,
            projected.Properties,
            projected.Projection,
            projected.Diagnostics);
    }
}
