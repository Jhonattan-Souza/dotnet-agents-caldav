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
    public void IsCompleteMatch_RequiresKindUidAndParseableSemanticContent(
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

    [Fact]
    public void IsCompleteMatch_AcceptsLosslessSemanticNormalization()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\n"
            + "PRODID:-//Tests//EN\r\n"
            + "BEGIN:VTODO\r\n"
            + "UID:uid-1\r\n"
            + "DTSTAMP:20260823T120000Z\r\n"
            + "SUMMARY;LANGUAGE=en;ALTREP=\"cid:part1\";X-P=^q:Quarterly review\r\n"
            + "END:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "PRODID:-//Tests//EN\r\n"
            + "VERSION;VALUE=TEXT:2.0\r\n"
            + "BEGIN:VTODO\r\n"
            + "SUMMARY;ALTREP=\"cid:part1\";LANGUAGE=EN;X-P=^^q:Quarterly \r\n review\r\n"
            + "DTSTAMP:20260823T120000Z\r\n"
            + "UID:uid-1\r\n"
            + "END:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Theory]
    [InlineData("X-NODE:opaque\r\n", "X-NODE;VALUE=X-UNKNOWN:opaque\r\n")]
    [InlineData("SUMMARY:value\r\n", "SUMMARY;VALUE=TEXT;VALUE=TEXT:value\r\n")]
    [InlineData("ATTENDEE:mailto:user@example.com\r\n", "ATTENDEE;VALUE=URI:mailto:user@example.com\r\n")]
    [InlineData("X-NODE;X-ORDER=one,two:opaque\r\n", "X-NODE;X-ORDER=two,one:opaque\r\n")]
    [InlineData("A.X-NODE:opaque\r\n", "B.X-NODE:opaque\r\n")]
    [InlineData("LINK;VALUE=UID;LINKREL=https://example.test/a:linked\r\n", "LINK;VALUE=UID;LINKREL=https://example.test/A:linked\r\n")]
    [InlineData("RRULE:FREQ=DAILY;X-MODE=CaseSensitive\r\n", "RRULE:FREQ=DAILY;X-MODE=casesensitive\r\n")]
    [InlineData("CATEGORIES:a\\,b\r\n", "CATEGORIES:a,b\r\n")]
    [InlineData("REQUEST-STATUS:2.0;Success\\;detail\r\n", "REQUEST-STATUS:2.0;Success;detail\r\n")]
    [InlineData("X-NODE;RANGE=CaseSensitive:opaque\r\n", "X-NODE;RANGE=casesensitive:opaque\r\n")]
    [InlineData("X-NODE;RELATED=CaseSensitive:opaque\r\n", "X-NODE;RELATED=casesensitive:opaque\r\n")]
    [InlineData("X-NODE;CUTYPE=INDIVIDUAL:opaque\r\n", "X-NODE;CUTYPE=individual:opaque\r\n")]
    [InlineData("X-NODE;FMTTYPE=text/calendar:opaque\r\n", "X-NODE;FMTTYPE=TEXT/CALENDAR:opaque\r\n")]
    [InlineData("X-NODE;VALUE=TEXT:a\\q\r\n", "X-NODE;VALUE=TEXT:aq\r\n")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=1\r\n", "RRULE:FREQ=DAILY;INTERVAL=1\r\n")]
    [InlineData("GEO:1.0000000000000000001;2\r\n", "GEO:1.0000000000000000002;2\r\n")]
    [InlineData("X-NODE;VALUE=TEXT:a\\;b\r\n", "X-NODE;VALUE=TEXT:a;b\r\n")]
    [InlineData("X-NODE;VALUE=DATE:case\r\n", "X-NODE;VALUE=DATE:CASE\r\n")]
    [InlineData("SUMMARY:a\\q\r\n", "SUMMARY:a\\\\q\r\n")]
    [InlineData("SUMMARY:a\\\r\n", "SUMMARY:a\\\\\r\n")]
    [InlineData("SUMMARY:a\\;b\r\n", "SUMMARY:a;b\r\n")]
    [InlineData("SUMMARY:a\\,b\r\n", "SUMMARY:a,b\r\n")]
    [InlineData("ATTENDEE;RSVP=Maybe:mailto:user@example.test\r\n", "ATTENDEE;RSVP=maybe:mailto:user@example.test\r\n")]
    [InlineData("RELATED-TO;GAP=pt1h:parent\r\n", "RELATED-TO;GAP=PT1H:parent\r\n")]
    [InlineData("ATTENDEE;ORDER=00:mailto:user@example.test\r\n", "ATTENDEE;ORDER=0:mailto:user@example.test\r\n")]
    [InlineData("RECURRENCE-ID;RANGE=CaseSensitive:20260824T120000Z\r\n", "RECURRENCE-ID;RANGE=casesensitive:20260824T120000Z\r\n")]
    [InlineData("TRIGGER;RELATED=CaseSensitive:-PT5M\r\n", "TRIGGER;RELATED=casesensitive:-PT5M\r\n")]
    [InlineData("PRIORITY: 1 \r\n", "PRIORITY:1\r\n")]
    [InlineData("GEO:1e1;2\r\n", "GEO:10;2\r\n")]
    [InlineData("GEO:1.;2\r\n", "GEO:1;2\r\n")]
    [InlineData("GEO:.5;2\r\n", "GEO:0.5;2\r\n")]
    [InlineData("RRULE:FREQ=DAILY;UNTIL=20269999t259999z\r\n", "RRULE:FREQ=DAILY;UNTIL=20269999T259999Z\r\n")]
    [InlineData("X-DUP:one\r\n", "X-DUP:one\r\nX-DUP:one\r\n")]
    [InlineData("BEGIN:X-A\r\nEND:X-A\r\n", "BEGIN:X-A\r\nBEGIN:X-B\r\nEND:X-B\r\nEND:X-A\r\n")]
    public void IsCompleteMatch_PreservesEverySemanticDistinction(string sourceLine, string destinationLine)
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + sourceLine
            + "END:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + destinationLine
            + "END:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeFalse();
    }

    [Fact]
    public void IsCompleteMatch_NormalizesKnownDefaultAndTokenGrammar()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nMETHOD:REQUEST\r\n"
            + "BEGIN:VTODO\r\nUID:uid-1\r\nCLASS:X-Custom\r\nCOLOR:Blue\r\nRELATED-TO:parent\r\n"
            + "TRIGGER;RELATED=START:-PT5M\r\nLINK;VALUE=UID;LINKREL=ALTERNATE:linked\r\n"
            + "ATTENDEE;CUTYPE=X-Custom:mailto:user@example.test\r\n"
            + "ATTACH;FMTTYPE=text/calendar:https://example.test/item.ics\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260824T120000Z\r\n"
            + "RRULE:FREQ=MONTHLY;BYDAY=+01MO,TU;COUNT=01\r\nEND:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nMETHOD:request\r\n"
            + "BEGIN:VTODO\r\nUID:uid-1\r\nCLASS:x-custom\r\nCOLOR:blue\r\nRELATED-TO;VALUE=UID:parent\r\n"
            + "TRIGGER;RELATED=start:-PT5M\r\nLINK;VALUE=uid;LINKREL=alternate:linked\r\n"
            + "ATTENDEE;CUTYPE=x-custom:mailto:user@example.test\r\n"
            + "ATTACH;FMTTYPE=TEXT/CALENDAR:https://example.test/item.ics\r\n"
            + "RECURRENCE-ID;RANGE=thisandfuture:20260824T120000Z\r\n"
            + "RRULE:COUNT=1;BYDAY=tu,1mo;FREQ=monthly\r\nEND:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void IsCompleteMatch_BoundsNestedComponentDepth(int nestedComponents, bool expected)
    {
        var content = NestedResource(nestedComponents);
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", content);
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", content);

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBe(expected);
    }

    private static string NestedResource(int nestedComponents)
    {
        var content = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n");
        for (var index = 0; index < nestedComponents; index++)
            content.Append("BEGIN:X-NODE\r\n");
        for (var index = 0; index < nestedComponents; index++)
            content.Append("END:X-NODE\r\n");
        return content.Append("BEGIN:VTODO\r\nUID:uid-1\r\nEND:VTODO\r\nEND:VCALENDAR\r\n").ToString();
    }

    [Fact]
    public void IsCompleteMatch_NormalizesRegisteredTokenDurationAndIntegerGrammar()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "LOCATION-TYPE:HOME\r\nRELATED-TO;GAP=PT1H:parent\r\n"
            + "ATTENDEE;ORDER=1:mailto:user@example.test\r\nEND:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "LOCATION-TYPE:home\r\nRELATED-TO;GAP=PT1H:parent\r\n"
            + "ATTENDEE;ORDER=01:mailto:user@example.test\r\nEND:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Fact]
    public void IsCompleteMatch_NormalizesEveryRegisteredRecurrenceListPart()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "RRULE:FREQ=yearly;WKST=mo;UNTIL=20261231;BYSECOND=01,2;BYMINUTE=03,4;"
            + "BYHOUR=05,6;BYMONTHDAY=+01,-2;BYYEARDAY=001,-2;BYWEEKNO=+01,-2;"
            + "BYMONTH=01,2;BYSETPOS=+01,-2;INTERVAL=02\r\nEND:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "RRULE:INTERVAL=2;BYSETPOS=-2,1;BYMONTH=2,1;BYWEEKNO=-2,1;"
            + "BYYEARDAY=-2,1;BYMONTHDAY=-2,1;BYHOUR=6,5;BYMINUTE=4,3;"
            + "BYSECOND=2,1;UNTIL=20261231;WKST=MO;FREQ=YEARLY\r\nEND:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Theory]
    [InlineData("20261231T235959")]
    [InlineData("20261231T235959Z")]
    public void IsCompleteMatch_AcceptsEveryRegisteredUntilTemporalShape(string until)
    {
        var content = Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + $"RRULE:FREQ=DAILY;UNTIL={until}\r\nEND:VTODO\r\n");
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", content);
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", content);

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Fact]
    public void IsCompleteMatch_NormalizesExactFloatSignsZerosAndDecimalScale()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "GEO:+0001.200;-000.0\r\nEND:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "GEO:1.2;0\r\nEND:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    [Fact]
    public void IsCompleteMatch_NormalizesEveryValidFormatTypeTokenCharacter()
    {
        var source = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "ATTACH;FMTTYPE=text/x-example!#$&-^_.+json:https://example.test/item\r\n"
            + "END:VTODO\r\n"));
        var destination = Snapshot(CalendarResourceProjectionKind.Todo, "uid-1", Resource(
            "VERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:uid-1\r\n"
            + "ATTACH;FMTTYPE=TEXT/X-EXAMPLE!#$&-^_.+JSON:https://example.test/item\r\n"
            + "END:VTODO\r\n"));

        CalendarResourceMoveFidelity.IsCompleteMatch(source, destination).ShouldBeTrue();
    }

    private static string Resource(string body) => "BEGIN:VCALENDAR\r\n" + body + "END:VCALENDAR\r\n";

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
