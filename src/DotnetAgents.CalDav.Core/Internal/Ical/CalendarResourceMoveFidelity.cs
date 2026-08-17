using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarResourceMoveFidelity
{
    public static bool IsCompleteMatch(
        CalendarResourceSnapshot source,
        CalendarResourceSnapshot destination) =>
        source.Projection.Kind == destination.Projection.Kind
        && string.Equals(source.Projection.EntityUid, destination.Projection.EntityUid, StringComparison.Ordinal)
        && source.AuthoritativeUtf8.Span.SequenceEqual(destination.AuthoritativeUtf8.Span);
}
