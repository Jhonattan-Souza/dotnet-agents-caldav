using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class Issue116LegacyDirectGetDurationTests
{
    [Fact]
    public async Task FiveUnavailableMultigetResourcesUseFiveSequentialDirectReads()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(0, 5).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(hrefs);
        client.GetCalendarResourcesForQueryAsync(
                calendarHref,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CalendarResourceRead>>(_ =>
                throw new CalendarDiscoveryUnsupportedCapabilityException("unsupported"));
        var concurrencySync = new object();
        var inFlight = 0;
        var maximumInFlight = 0;
        client.GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                lock (concurrencySync)
                {
                    inFlight++;
                    maximumInFlight = Math.Max(maximumInFlight, inFlight);
                }

                try
                {
                    await Task.Yield();
                    return CalendarResourceRead.Success(
                        call.ArgAt<string>(0),
                        "\"r1\"",
                        Encoding.UTF8.GetBytes(
                            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:item\r\n"
                            + "DTSTAMP:20260823T120000Z\r\nDTSTART:20260824T120000Z\r\n"
                            + "END:VEVENT\r\nEND:VCALENDAR\r\n"));
                }
                finally
                {
                    lock (concurrencySync)
                    {
                        inFlight--;
                    }
                }
            });
        var service = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await service.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Count.ShouldBe(5);
        await client.Received(1).GetCalendarResourcesForQueryAsync(
            calendarHref, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await client.Received(5).GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        maximumInFlight.ShouldBe(1);
    }
}
