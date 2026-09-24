using System.Net;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed partial class RadicaleConformanceHarnessTests
{
    private static readonly DateTimeOffset FreeBusyFrom = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FreeBusyTo = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Pinned_profile_merges_native_free_busy_period_components_only_under_the_profile()
    {
        using var probe = CreateProbeClient();
        var calendar = new Uri(fixture.BaseUrl + "/conformance/evidence-free-busy/", UriKind.Absolute);
        (await CreateCalendarAsync(probe, calendar, "Free Busy", "VEVENT")).ShouldBe(HttpStatusCode.Created);
        foreach (var (name, content) in FreeBusyCorpus())
            await PutResourceAsync(probe, new Uri(calendar, name + ".ics"), content);

        await using (var provider = CreateProvider(fixture.BaseUrl, calendar.AbsoluteUri))
        {
            var reports = provider.GetRequiredService<ICalendarReportModule>();
            var busy = await reports.FreeBusyAsync(
                new(calendar.AbsoluteUri, FreeBusyFrom, FreeBusyTo), TestContext.Current.CancellationToken);
            var empty = await reports.FreeBusyAsync(
                new(calendar.AbsoluteUri, FreeBusyTo.AddDays(2), FreeBusyTo.AddDays(3)), TestContext.Current.CancellationToken);

            busy.Complete.ShouldBeTrue();
            busy.TemporalAuthority.ShouldBe("server");
            busy.Periods.ShouldBe(
            [
                new CalendarBusyPeriod("2026-10-01T08:00:00Z", "2026-10-01T08:30:00Z", "BUSY"),
                new CalendarBusyPeriod("2026-10-01T10:00:00Z", "2026-10-01T12:00:00Z", "BUSY"),
                new CalendarBusyPeriod("2026-10-01T11:30:00Z", "2026-10-01T12:30:00Z", "BUSY-TENTATIVE"),
                new CalendarBusyPeriod("2026-10-01T13:00:00Z", "2026-10-01T13:45:00Z", "BUSY"),
                new CalendarBusyPeriod("2026-10-01T14:00:00Z", "2026-10-01T15:00:00Z", "FREE"),
                new CalendarBusyPeriod("2026-10-01T16:00:00Z", "2026-10-01T17:00:00Z", "BUSY")
            ]);
            empty.Complete.ShouldBeTrue();
            empty.Periods.ShouldBeEmpty();
        }

        await using (var unverified = CreateProvider(fixture.BaseUrl, calendar.AbsoluteUri, interoperabilityProfile: null))
        {
            var error = await Should.ThrowAsync<CalendarProtocolException>(() => unverified
                .GetRequiredService<ICalendarReportModule>()
                .FreeBusyAsync(new(calendar.AbsoluteUri, FreeBusyFrom, FreeBusyTo), TestContext.Current.CancellationToken));
            error.Code.ShouldBe("upstream_protocol_error");
        }
    }

    [Fact]
    public async Task Pinned_profile_reports_one_opaque_change_tag_to_listing_and_inspection()
    {
        using var probe = CreateProbeClient();
        var calendar = new Uri(fixture.BaseUrl + "/conformance/evidence-change-tag/", UriKind.Absolute);
        (await CreateCalendarAsync(probe, calendar, "Change Tag", "VEVENT")).ShouldBe(HttpStatusCode.Created);
        await using var provider = CreateProvider(fixture.BaseUrl, calendar.AbsoluteUri);
        var calendars = provider.GetRequiredService<ICalendarService>();
        var metadata = provider.GetRequiredService<ICalendarMetadataModule>();

        var listed = (await calendars.GetCalendarsAsync(TestContext.Current.CancellationToken)).Items.ShouldHaveSingleItem();
        var inspected = await metadata.InspectAsync(calendar.AbsoluteUri, TestContext.Current.CancellationToken);
        await PutResourceAsync(probe, new Uri(calendar, "change-tag.ics"),
            Event("change-tag", "DTSTART:20261001T100000Z\r\nDTEND:20261001T110000Z\r\n"));
        var changed = await metadata.InspectAsync(calendar.AbsoluteUri, TestContext.Current.CancellationToken);

        listed.ChangeTag.ShouldNotBeNullOrWhiteSpace();
        inspected.ChangeTag.ShouldBe(listed.ChangeTag);
        inspected.Properties.ShouldContain(new CalendarPropertyObservation("http://calendarserver.org/ns/", "getctag", 200));
        changed.ChangeTag.ShouldNotBeNullOrWhiteSpace();
        changed.ChangeTag.ShouldNotBe(inspected.ChangeTag);
    }

    private static IEnumerable<(string Name, string Content)> FreeBusyCorpus()
    {
        yield return ("clipped", Event("fb-clipped", "DTSTART:20261001T073000Z\r\nDTEND:20261001T083000Z\r\n"));
        yield return ("busy", Event("fb-busy", "DTSTART:20261001T100000Z\r\nDTEND:20261001T110000Z\r\n"));
        yield return ("overlap", Event("fb-overlap", "DTSTART:20261001T103000Z\r\nDURATION:PT1H30M\r\n"));
        yield return ("tentative", Event("fb-tentative", "DTSTART:20261001T113000Z\r\nDTEND:20261001T123000Z\r\nSTATUS:TENTATIVE\r\n"));
        yield return ("zoned", ZonedEvent("fb-zoned"));
        yield return ("cancelled", Event("fb-cancelled", "DTSTART:20261001T140000Z\r\nDTEND:20261001T150000Z\r\nSTATUS:CANCELLED\r\n"));
        yield return ("recurring", Event("fb-recurring", "DTSTART:20261001T160000Z\r\nDTEND:20261001T170000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"));
        yield return ("transparent", Event("fb-transparent", "DTSTART:20261001T180000Z\r\nDTEND:20261001T190000Z\r\nTRANSP:TRANSPARENT\r\n"));
    }

    // Radicale's vobject keeps one process-wide time zone definition per TZID and
    // emits it with free/busy values. A TZID private to this test keeps other tests'
    // America/New_York definitions out of this report and this definition out of theirs.
    private static string ZonedEvent(string uid) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\n"
        + "BEGIN:VTIMEZONE\r\nTZID:X-Conformance/Free-Busy-Minus-Four\r\n"
        + "BEGIN:STANDARD\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0400\r\nDTSTART:19700101T000000\r\n"
        + "TZNAME:X-FB\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n"
        + "DTSTART;TZID=X-Conformance/Free-Busy-Minus-Four:20261001T090000\r\nDURATION:PT45M\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
}
