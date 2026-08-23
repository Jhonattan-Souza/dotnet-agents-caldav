using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarOperationDiscoveryTests
{
    [Fact]
    public async Task AbsenceProbe_DefaultTransportMethodPreservesMarkerThroughDiscoveryDecorator()
    {
        var markerObserved = false;
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                markerObserved = CalendarHttpTelemetry.IsAbsenceProbe;
                return Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
            });
        var sut = new CalendarOperationDiscovery(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "principal"
            }),
            calendars => new CalendarDiscoveryResult(calendars, []),
            static (_, _, _) => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound));

        var result = await ((ICalendarCreateTransport)sut).ProbeCalendarResourceAbsenceAsync(
            "https://cal.example/events/missing.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.NotFound);
        markerObserved.ShouldBeTrue();
    }
}
