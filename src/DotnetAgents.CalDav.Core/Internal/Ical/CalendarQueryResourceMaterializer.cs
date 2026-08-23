using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net;
using IcalCalendar = Ical.Net.Calendar;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record AcquiredCalendarResource(
    CalendarResourceSnapshot Snapshot,
    CalendarContentDocument? Document,
    IcalCalendar? TypedCalendar);

/// <summary>Creates the query-scoped semantic representation and immutable public snapshot in one parse.</summary>
internal static class CalendarQueryResourceMaterializer
{
    internal static AcquiredCalendarResource Materialize(string calendarHref, CalendarResourceRead read)
    {
        CalendarContentDocument? document = null;
        IcalCalendar? typedCalendar = null;
        CalendarProjectionResult projection;
        try
        {
            CalendarQueryTelemetry.Add("caldav.query.parse_count");
            document = CalendarContentDocument.Parse(read.AuthoritativeUtf8.Span);
            typedCalendar = CalendarResourceProjector.LoadTypedCalendar(document);
            projection = CalendarResourceProjector.Project(document, typedCalendar);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            projection = CalendarResourceProjector.InvalidCalendarData();
        }

        var snapshot = new CalendarResourceSnapshot(
            calendarHref,
            read.ResourceHref!,
            read.EntityTag!,
            read.AuthoritativeUtf8,
            projection.Properties,
            projection.Projection,
            projection.Diagnostics);
        return new AcquiredCalendarResource(snapshot, document, typedCalendar);
    }
}
