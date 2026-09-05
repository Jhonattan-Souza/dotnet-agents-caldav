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

    [Fact]
    public void RejectsObservedMultiComponentNativeReportWithPerComponentBusyTypes()
    {
        const string body = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:test
            BEGIN:VFREEBUSY
            DTSTART:20260905T083000Z
            DTEND:20260905T093000Z
            DTSTAMP:20260905T000000Z
            FBTYPE:BUSY
            END:VFREEBUSY
            BEGIN:VFREEBUSY
            DTSTART:20260905T100000Z
            DTEND:20260905T110000Z
            DTSTAMP:20260905T000000Z
            FBTYPE:BUSY
            END:VFREEBUSY
            BEGIN:VFREEBUSY
            DTSTART:20260905T113000Z
            DTEND:20260905T120000Z
            DTSTAMP:20260905T000000Z
            FBTYPE:BUSY-TENTATIVE
            END:VFREEBUSY
            BEGIN:VFREEBUSY
            DTSTART:20260905T130000Z
            DTEND:20260905T140000Z
            DTSTAMP:20260905T000000Z
            FBTYPE:FREE
            END:VFREEBUSY
            END:VCALENDAR
            """;

        Should.Throw<CalendarProtocolException>(() => Parse(body)).Code.ShouldBe("upstream_protocol_error");
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
    public void RejectsMultipleBusyComponentsAndDeepNestedContent()
    {
        var duplicate = Wrap(string.Empty).Replace("END:VCALENDAR", "BEGIN:VFREEBUSY\nEND:VFREEBUSY\nEND:VCALENDAR", StringComparison.Ordinal);
        Should.Throw<CalendarProtocolException>(() => Parse(duplicate)).Code.ShouldBe("upstream_protocol_error");
        Should.Throw<CalendarProtocolException>(() => Parse(Wrap("BEGIN:VALARM\nEND:VALARM"))).Code.ShouldBe("upstream_protocol_error");
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
        var excessive = Wrap(string.Join('\n', Enumerable.Repeat("COMMENT:unused", 10000)));
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

    private static IReadOnlyList<CalendarBusyPeriod> Parse(string body) => CalendarFreeBusyReportParser.Parse(
        Encoding.UTF8.GetBytes(body), From, To, CancellationToken.None);

    private static string WithoutBounds(string content) => content
        .Replace("DTSTART:20260905T000000Z\n", string.Empty, StringComparison.Ordinal)
        .Replace("DTEND:20260906T000000Z\n", string.Empty, StringComparison.Ordinal);

    private static string Wrap(string content) => "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:test\nBEGIN:VFREEBUSY\n"
        + "DTSTART:20260905T000000Z\nDTEND:20260906T000000Z\n" + content + "\nEND:VFREEBUSY\nEND:VCALENDAR\n";
}
