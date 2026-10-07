using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public class CalendarFreeBusyReportParserTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(1);

    [Fact]
    public void CoalescesSameTypesClipsWindowAndPreservesUnknownTypes()
    {
        var result = Parse(Wrap("""
            FREEBUSY:20260904T230000Z/20260905T010000Z,20260905T003000Z/PT2H
            FREEBUSY;FBTYPE=busy:20260905T023000Z/20260905T030000Z
            FREEBUSY;FBTYPE=BUSY-TENTATIVE:20260905T010000Z/PT1H
            FREEBUSY;FBTYPE=X-team-focus:20260905T010000Z/PT1H
            FREEBUSY;FBTYPE=FREE:20260905T220000Z/PT5H
            FREEBUSY:20260906T000000Z/PT1H
            """));

        result.ShouldBe(new[]
        {
            new CalendarBusyPeriod("2026-09-05T00:00:00Z", "2026-09-05T03:00:00Z", "BUSY"),
            new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "BUSY-TENTATIVE"),
            new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "X-team-focus"),
            new CalendarBusyPeriod("2026-09-05T22:00:00Z", "2026-09-06T00:00:00Z", "FREE")
        });
    }

    [Fact]
    public void FreePeriodsNeverSubtractOverlappingBusyTime()
    {
        var expected = new[]
        {
            new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T03:00:00Z", "BUSY"),
            new CalendarBusyPeriod("2026-09-05T02:00:00Z", "2026-09-05T04:00:00Z", "FREE")
        };

        Parse(Wrap("FREEBUSY:20260905T010000Z/PT2H\nFREEBUSY;FBTYPE=FREE:20260905T020000Z/PT2H")).ShouldBe(expected);
    }

    [Fact]
    public void AcceptsFoldedContentIncludingUtf8CodePointAndPositiveWeekDuration()
    {
        var content = Wrap("FREEBUSY;FBTYPE=BUSY-UNAVAILABLE:20260905T010000Z/\r\n\t+P1W\r\nCOMMENT:café");
        var encoded = Encoding.UTF8.GetBytes(content);
        var accented = Array.IndexOf(encoded, (byte)0xc3);
        var folded = encoded[..(accented + 1)].Concat(new byte[] { 13, 10, 32 }).Concat(encoded[(accented + 1)..]).ToArray();

        var result = CalendarFreeBusyReportParser.Parse(folded, From, To, CancellationToken.None);

        result.ShouldBe(new[] { new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-06T00:00:00Z", "BUSY-UNAVAILABLE") });
    }

    [Fact]
    public void EmptyValidatedBusyComponentReturnsCompleteEmptyPeriods()
    {
        Parse(Wrap(string.Empty)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("DTSTART:20260905T010000Z", "")]
    [InlineData("", "DTEND:20260905T020000Z")]
    [InlineData("DTSTART:20260905T010000Z", "DTEND:20260905T020000Z")]
    public void AcceptsOptionalBoundsAroundBusyInformationWithinTheRequestedWindow(string start, string end)
    {
        var response = WithoutBounds(Wrap("UID:report-1\nDTSTAMP:20260905T000000Z\n"
            + start + "\n" + end + "\nFREEBUSY:20260905T010000Z/PT1H"));

        Parse(response).ShouldBe(new[] { new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "BUSY") });
    }

    [Fact]
    public void UsesRequestWindowForClippingWhenComponentBoundsAreAbsent()
    {
        var response = WithoutBounds(Wrap("UID:report-1\nDTSTAMP:20260905T000000Z\n"
            + "FREEBUSY:20260904T230000Z/PT2H,20260905T230000Z/PT2H"));

        Parse(response).ShouldBe(new[]
        {
            new CalendarBusyPeriod("2026-09-05T00:00:00Z", "2026-09-05T01:00:00Z", "BUSY"),
            new CalendarBusyPeriod("2026-09-05T23:00:00Z", "2026-09-06T00:00:00Z", "BUSY")
        });
        Parse(WithoutBounds(Wrap("UID:report-1\nDTSTAMP:20260905T000000Z"))).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DTSTART:20260905T010000")]
    [InlineData("DTEND:20260905T020000")]
    [InlineData("DTEND;VALUE=DATE:20260905")]
    [InlineData("DTSTART:20260905T010000Z\nDTSTART:20260905T010000Z")]
    [InlineData("DTEND:20260905T020000Z\nDTEND:20260905T020000Z")]
    [InlineData("DTSTART:20260905T020000Z\nDTEND:20260905T010000Z")]
    public void OptionalBoundsAreStillValidatedIndividually(string bounds)
    {
        var response = WithoutBounds(Wrap("UID:report-1\nDTSTAMP:20260905T000000Z\n" + bounds));

        Should.Throw<CalendarProtocolException>(() => Parse(response)).Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;COUNT=3")]
    [InlineData("RDATE:20260905T010000Z")]
    [InlineData("EXDATE:20260905T010000Z")]
    [InlineData("rrule:FREQ=DAILY;COUNT=3")]
    public void ProhibitedRecurrenceNeverBecomesCompleteBusyInformation(string recurrence)
    {
        foreach (var freeBusy in new[] { string.Empty, "FREEBUSY:20260905T010000Z/PT1H" })
        {
            var response = Wrap("UID:report-1\nDTSTAMP:20260905T000000Z\n" + recurrence + "\n" + freeBusy);
            Should.Throw<CalendarProtocolException>(() => Parse(response)).Code.ShouldBe("upstream_protocol_error");
        }
    }

    [Theory]
    [InlineData("BUSY")]
    [InlineData("FREE")]
    [InlineData("BUSY-TENTATIVE")]
    public void StandaloneBusyTypeNeverTurnsAComponentIntoEmptyAvailability(string busyType)
    {
        var response = Wrap("FBTYPE:" + busyType);

        Should.Throw<CalendarProtocolException>(() => Parse(response)).Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("FREEBUSY:20260905T010000Z/PT1H")]
    [InlineData("DTSTART:20260905T010000Z")]
    [InlineData("DTEND:20260905T020000Z")]
    [InlineData("FBTYPE:BUSY")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=3")]
    [InlineData("RDATE:20260905T010000Z")]
    [InlineData("EXDATE:20260905T010000Z")]
    [InlineData("group.freebusy:20260905T010000Z/PT1H")]
    [InlineData("group.dtstart:20260905T010000Z")]
    [InlineData("group.dtend:20260905T020000Z")]
    [InlineData("group.fbtype:BUSY")]
    [InlineData("group.rrule:FREQ=DAILY;COUNT=3")]
    [InlineData("group.rdate:20260905T010000Z")]
    [InlineData("group.exdate:20260905T010000Z")]
    public void MisplacedCalendarAvailabilityCannotBecomeEmptyOrPartialSuccess(string property)
    {
        foreach (var periods in new[] { string.Empty, "FREEBUSY:20260905T030000Z/PT1H" })
        {
            var response = Wrap(periods).Replace("BEGIN:VFREEBUSY", property + "\nBEGIN:VFREEBUSY", StringComparison.Ordinal);

            Should.Throw<CalendarProtocolException>(() => Parse(response)).Code.ShouldBe("upstream_protocol_error");
        }
    }

    [Fact]
    public void UnknownCalendarAndBusyComponentExtensionsDoNotInvalidateNativePeriods()
    {
        var response = Wrap("X-FREEBUSY:opaque extension\nFREEBUSY:20260905T010000Z/PT1H")
            .Replace("BEGIN:VFREEBUSY", "X-FBTYPE:opaque extension\nBEGIN:VFREEBUSY", StringComparison.Ordinal);

        Parse(response).ShouldBe(new[] { new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "BUSY") });
    }

    [Fact]
    public void AcceptsCoveringBoundsAndExplicitPeriodValueType()
    {
        var content = Wrap("FREEBUSY;VALUE=PERIOD:20260905T010000Z/PT1H")
            .Replace("DTSTART:20260905T000000Z", "DTSTART;VALUE=DATE-TIME:20260904T000000Z", StringComparison.Ordinal)
            .Replace("DTEND:20260906T000000Z", "DTEND:20260907T000000Z", StringComparison.Ordinal);

        Parse(content).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("FREEBUSY:20260905T010000Z/PT0S")]
    [InlineData("FREEBUSY:20260905T010000Z/-PT1H")]
    [InlineData("FREEBUSY:20260905T010000Z/P")]
    [InlineData("FREEBUSY:20260905T010000Z/PT999999999999999999H")]
    [InlineData("FREEBUSY:99991231T230000Z/PT2H")]
    [InlineData("FREEBUSY:20260905T010000Z/20260905T010000Z")]
    [InlineData("FREEBUSY:20260905T010000Z/20260905T000000Z")]
    [InlineData("FREEBUSY:20260905T010000/PT1H")]
    [InlineData("FREEBUSY:20260905T010000Z/20260905T020000")]
    [InlineData("FREEBUSY:20260905T010000Z/PT1H/PT1H")]
    [InlineData("FREEBUSY:20260905T010000Z")]
    [InlineData("FREEBUSY:")]
    [InlineData("FREEBUSY;VALUE=DATE-TIME:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;VALUE=PERIOD;VALUE=DATE:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;VALUE=PERIOD,DATE:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;TZID=America/Sao_Paulo:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;FBTYPE=BUSY;FBTYPE=FREE:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;FBTYPE=BUSY,FREE:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;FBTYPE=:20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;FBTYPE=\"X free\":20260905T010000Z/PT1H")]
    [InlineData("FREEBUSY;FBTYPE=\"unterminated:20260905T010000Z/PT1H")]
    public void InvalidPeriodsNeverProduceAvailability(string property)
    {
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(property))).Code.ShouldBe("upstream_protocol_error");
    }

    [Theory]
    [InlineData("DTEND:20260906T000000Z", "DTEND:20260905T000000Z")]
    [InlineData("DTSTART:20260905T000000Z", "DTSTART;VALUE=DATE:20260905")]
    [InlineData("DTSTART:20260905T000000Z", "DTSTART;TZID=UTC:20260905T000000Z")]
    [InlineData("DTSTART:20260905T000000Z", "DTSTART:20260905T000000Z\nDTSTART:20260905T000000Z")]
    [InlineData("VERSION:2.0", "VERSION:1.0")]
    [InlineData("VERSION:2.0", "")]
    [InlineData("VERSION:2.0", "VERSION:2.0\nVERSION:2.0")]
    [InlineData("VFREEBUSY", "VEVENT")]
    [InlineData("VCALENDAR", "X-CALENDAR")]
    [InlineData("END:VFREEBUSY", "END:VTODO")]
    [InlineData("END:VCALENDAR", "")]
    public void MissingOrContradictoryStructureAndBoundsFail(string before, string after)
    {
        var content = Wrap(string.Empty).Replace(before, after, StringComparison.Ordinal);
        Should.Throw<CalendarProtocolException>(() => Parse(content)).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void AcceptsEmptyAdditionalBusyComponentButRejectsNestedContent()
    {
        var duplicate = Wrap("FREEBUSY:20260905T010000Z/PT1H")
            .Replace("END:VCALENDAR", "BEGIN:VFREEBUSY\nEND:VFREEBUSY\nEND:VCALENDAR", StringComparison.Ordinal);
        Parse(duplicate).ShouldBe(new[] { new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "BUSY") });
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap("BEGIN:VALARM\nEND:VALARM"))).Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap("BEGIN:VALARM\nBEGIN:X-DEEP\nEND:X-DEEP\nEND:VALARM")))
            .Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap(string.Empty) + Wrap(string.Empty))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void CountsPeriodsBeforeMergingAndBeforeClipping()
    {
        foreach (var period in new[] { "20260905T010000Z/PT1H", "20260904T010000Z/PT1H" })
        {
            var content = Wrap("FREEBUSY:" + string.Join(',', Enumerable.Repeat(period, 5001)));
            Should.Throw<CalendarProtocolException>(() => Parse(content)).Code.ShouldBe("limit_exhausted");
        }
        Parse(Wrap("FREEBUSY:" + string.Join(',', Enumerable.Repeat("20260905T010000Z/PT1H", 5000)))).Count.ShouldBe(1);
    }

    [Fact]
    public void BoundsPhysicalStructureAndPreservesCancellation()
    {
        var excessive = Wrap(string.Join('\n', Enumerable.Repeat("COMMENT:unused", 40000)));
        Should.Throw<CalendarProtocolException>(() => Parse(excessive)).Code.ShouldBe("limit_exhausted");
        Should.Throw<OperationCanceledException>(() => CalendarFreeBusyReportParser.Parse(
            Encoding.UTF8.GetBytes(Wrap(string.Empty)), From, To, new CancellationToken(true)));
    }

    [Fact]
    public void RejectsMalformedEncodingAndOverlongBusyType()
    {
        Should.Throw<CalendarProtocolException>(() => CalendarFreeBusyReportParser.Parse([0xff], From, To, CancellationToken.None))
            .Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap($"FREEBUSY;FBTYPE={new string('X', 257)}:20260905T010000Z/PT1H")))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void MergesOverlappingPeriodsAcrossSeveralRfcBusyComponentsPerBusyType()
    {
        var body = Wrap("FREEBUSY:20260905T010000Z/PT2H\nFREEBUSY;FBTYPE=BUSY-TENTATIVE:20260905T040000Z/PT1H")
            .Replace("END:VCALENDAR", "BEGIN:VFREEBUSY\nDTSTART:20260905T000000Z\nDTEND:20260906T000000Z\n"
                + "FREEBUSY:20260905T020000Z/20260905T040000Z,20260905T050000Z/PT1H\n"
                + "FREEBUSY;FBTYPE=BUSY-TENTATIVE:20260905T043000Z/PT1H\nFREEBUSY:20260905T011500Z/PT15M\n"
                + "END:VFREEBUSY\nEND:VCALENDAR", StringComparison.Ordinal);

        Parse(body).ShouldBe(new[]
        {
            new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T04:00:00Z", "BUSY"),
            new CalendarBusyPeriod("2026-09-05T04:00:00Z", "2026-09-05T05:30:00Z", "BUSY-TENTATIVE"),
            new CalendarBusyPeriod("2026-09-05T05:00:00Z", "2026-09-05T06:00:00Z", "BUSY")
        });
    }

    [Fact]
    public void BoundsOfEachRfcBusyComponentAreValidatedIndependently()
    {
        var body = Wrap("FREEBUSY:20260905T010000Z/PT1H")
            .Replace("END:VCALENDAR", "BEGIN:VFREEBUSY\nDTSTART:20260905T020000Z\nDTEND:20260905T010000Z\n"
                + "END:VFREEBUSY\nEND:VCALENDAR", StringComparison.Ordinal);

        Should.Throw<CalendarProtocolException>(() => Parse(body)).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void RfcReportMayCarryATimeZoneDefinitionWithObservanceRecurrence()
    {
        var body = Wrap("FREEBUSY:20260905T010000Z/PT1H").Replace("BEGIN:VFREEBUSY", NewYorkTimeZone + "BEGIN:VFREEBUSY", StringComparison.Ordinal);

        Parse(body).ShouldBe(new[] { new CalendarBusyPeriod("2026-09-05T01:00:00Z", "2026-09-05T02:00:00Z", "BUSY") });
    }

    [Fact]
    public void Radicale378PeriodComponentsAndEmptyCalendarAreNotRfcFreeBusy()
    {
        Should.Throw<CalendarProtocolException>(() => Parse(ObservedRadicaleReport)).Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(RadicaleEmptyReport)).Code.ShouldBe("upstream_protocol_error");
    }

    private const string NewYorkTimeZone = """
        BEGIN:VTIMEZONE
        TZID:America/New_York
        BEGIN:STANDARD
        DTSTART:20071104T030000
        RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
        TZNAME:EST
        TZOFFSETFROM:-0400
        TZOFFSETTO:-0500
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:20070311T020000
        RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
        TZNAME:EDT
        TZOFFSETFROM:-0500
        TZOFFSETTO:-0400
        END:DAYLIGHT
        END:VTIMEZONE

        """;

    // Shape Radicale 3.7.8 returned: one VFREEBUSY per busy period, whose
    // DTSTART/DTEND are the period and whose FBTYPE is a property.
    private const string ObservedRadicaleReport = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//PYVOBJECT//NONSGML Version 0.9.9//EN
        BEGIN:VTIMEZONE
        TZID:America/New_York
        BEGIN:STANDARD
        DTSTART:20000101T000000
        RRULE:FREQ=YEARLY;BYMONTH=1;UNTIL=20060101T050000Z
        TZNAME:EST
        TZOFFSETFROM:-0500
        TZOFFSETTO:-0500
        END:STANDARD
        BEGIN:STANDARD
        DTSTART:20071104T030000
        RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
        TZNAME:EST
        TZOFFSETFROM:-0400
        TZOFFSETTO:-0500
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:20070311T020000
        RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
        TZNAME:EDT
        TZOFFSETFROM:-0500
        TZOFFSETTO:-0400
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VFREEBUSY
        DTSTART:20260904T230000Z
        DTEND:20260905T010000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:BUSY
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART:20260905T003000Z
        DTEND:20260905T020000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:busy
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART;TZID=America/New_York:20260905T090000
        DTEND;TZID=America/New_York:20260905T094500
        DTSTAMP:20260901T000000Z
        FBTYPE:BUSY
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART:20260905T013000Z
        DTEND:20260905T030000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:BUSY-TENTATIVE
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART:20260905T180000Z
        DTEND:20260905T190000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:FREE
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART:20260905T230000Z
        DTEND:20260906T010000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:X-team-focus
        END:VFREEBUSY
        BEGIN:VFREEBUSY
        DTSTART:20260906T100000Z
        DTEND:20260906T110000Z
        DTSTAMP:20260901T000000Z
        FBTYPE:BUSY
        END:VFREEBUSY
        END:VCALENDAR

        """;

    private const string RadicaleEmptyReport = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//PYVOBJECT//NONSGML Version 0.9.9//EN\r\nEND:VCALENDAR\r\n";

    private static IReadOnlyList<CalendarBusyPeriod> Parse(string body) => CalendarFreeBusyReportParser.Parse(
        Encoding.UTF8.GetBytes(body), From, To, CancellationToken.None);

    private static string WithoutBounds(string content) => content
        .Replace("DTSTART:20260905T000000Z\n", string.Empty, StringComparison.Ordinal)
        .Replace("DTEND:20260906T000000Z\n", string.Empty, StringComparison.Ordinal);

    private static string Wrap(string content) => "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:test\nBEGIN:VFREEBUSY\n"
        + "DTSTART:20260905T000000Z\nDTEND:20260906T000000Z\n" + content + "\nEND:VFREEBUSY\nEND:VCALENDAR\n";
}
