using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarResourceMoveFidelityTests
{
    [Theory]
    [InlineData(CalendarResourceProjectionKind.Event, "uid-1", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")]
    [InlineData(CalendarResourceProjectionKind.Todo, "uid-2", "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")]
    [InlineData(CalendarResourceProjectionKind.Todo, "uid-1", "different")]
    public void IsCompleteMatch_RequiresKindUidAndExactAuthoritativeBytes(
        CalendarResourceProjectionKind destinationKind,
        string destinationUid,
        string destinationContent)
    {
        var source = Snapshot(
            CalendarResourceProjectionKind.Todo,
            "uid-1",
            "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var destination = Snapshot(destinationKind, destinationUid, destinationContent);

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeFalse();
    }

    private static CalendarResourceSnapshot Snapshot(
        CalendarResourceProjectionKind kind,
        string uid,
        string content) => new(
            "https://example.com/tasks/",
            "https://example.com/tasks/a.ics",
            "\"r1\"",
            Encoding.UTF8.GetBytes(content),
            [],
            new CalendarResourceProjection(kind, uid, "summary"),
            []);
}
