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
    public async Task DiscoverAsync_ReturnsOneCompleteImmutableAuthority()
    {
        var transport = Substitute.For<ICalendarDiscoveryTransport>();
        transport.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Calendar("https://cal.example/events/", "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            Calendar("https://cal.example/todos/", "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);
        var sut = new CalendarOperationDiscovery(
            transport,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "principal"
            }),
            calendars => new CalendarDiscoveryResult(calendars, []),
            static (kind, discovered, authorized) => CalendarSelectionResult.Success(
                authorized.Single(calendar => kind == CalendarEntityKind.Event
                    ? calendar.EventSupport == EntityKindSupport.Advertised
                    : calendar.TodoSupport == EntityKindSupport.Advertised)));

        var authority = await sut.DiscoverAsync(CancellationToken.None);

        authority.Discovery.Items.Select(calendar => calendar.Href).ShouldBe(
            ["https://cal.example/events/", "https://cal.example/todos/"]);
        authority.Default(CalendarEntityKind.Event).Calendar!.Href.ShouldBe("https://cal.example/events/");
        authority.Default(CalendarEntityKind.Todo).Calendar!.Href.ShouldBe("https://cal.example/todos/");
        await transport.Received(1).DiscoverAsync(Arg.Any<CancellationToken>());
    }

    private static CalendarDescriptor Calendar(
        string href,
        string displayName,
        EntityKindSupport eventSupport,
        EntityKindSupport todoSupport) => new()
        {
            Href = href,
            DisplayName = displayName,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = eventSupport,
            TodoSupport = todoSupport
        };
}
