using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class ExactCalendarResourceTests
{
    [Fact]
    public async Task ExactGetAsync_ReturnsNativeResourceLinkWithoutRawContent()
    {
        var snapshot = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot });
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync(snapshot.ResourceHref, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.Content.OfType<ResourceLinkBlock>().ShouldHaveSingleItem();
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("BEGIN:VCALENDAR");
        result.StructuredContent!.Value.GetProperty("resourceLink").GetProperty("type").GetString().ShouldBe("resource_link");
    }

    [Fact]
    public async Task ReadAsync_ReturnsOnlyTheRevisionBoundByTheProtectedLink()
    {
        var snapshot = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot });
        var link = ExactCalendarResourceLink.Create(snapshot);

        var result = await ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None);

        result.CacheScope.ShouldBe(CacheScope.Private);
        result.TimeToLive.ShouldBe(TimeSpan.Zero);
        result.Contents.ShouldHaveSingleItem().ShouldBeOfType<TextResourceContents>().Text
            .ShouldBe(Encoding.UTF8.GetString(snapshot.AuthoritativeUtf8.Span));
        ExactCalendarResourceHandler.List().Resources.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_RejectsAChangedRevision()
    {
        var linked = CreateSnapshot("\"r1\"");
        var changed = CreateSnapshot("\"r2\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(linked.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(changed.ResourceHref, changed.EntityTag, changed.AuthoritativeUtf8) with { Snapshot = changed });
        var link = ExactCalendarResourceLink.Create(linked);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None));
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.NotFound)]
    [InlineData(CalendarResourceReadCode.Success)]
    public async Task ReadAsync_RejectsUnavailableTypedRead(CalendarResourceReadCode readCode)
    {
        var linked = CreateSnapshot("\"r1\"");
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(linked.ResourceHref, Arg.Any<CancellationToken>()).Returns(new CalendarResourceRead(readCode));
        var link = ExactCalendarResourceLink.Create(linked);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(link.Uri, service, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://snapshot/abc?etag=abc")]
    [InlineData("caldav-exact://other/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot/?etag=abc")]
    [InlineData("caldav-exact://snapshot/abc?other=abc")]
    [InlineData("caldav-exact://user@snapshot/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot:123/abc?etag=abc")]
    [InlineData("caldav-exact://snapshot/abc?etag=abc#fragment")]
    [InlineData("caldav-exact://snapshot/abc?etag=")]
    [InlineData("caldav-exact://snapshot/abc?etag=abc&extra=1")]
    [InlineData("caldav-exact://snapshot/a/b?etag=abc")]
    [InlineData("caldav-exact://snapshot/aA==?etag=abc")]
    [InlineData("caldav-exact://snapshot/aB?etag=abc")]
    [InlineData("caldav-exact://snapshot/wyg?etag=InIxIg")]
    public async Task ReadAsync_RejectsForgedLinkWithoutCallingService(string uri)
    {
        var service = Substitute.For<ICalendarService>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(uri, service, CancellationToken.None));

        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative", "\"r1\"")]
    [InlineData("ftp://cal.example/events/a.ics", "\"r1\"")]
    [InlineData("https://user@cal.example/events/a.ics", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics?query=1", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics#fragment", "\"r1\"")]
    [InlineData("https://cal.example/events/a.ics", "W/\"weak\"")]
    [InlineData("https://cal.example/events/a.ics", "not-an-etag")]
    public async Task ReadAsync_RejectsInvalidDecodedBindingsWithoutCallingService(string href, string entityTag)
    {
        var uri = $"caldav-exact://snapshot/{Encode(href)}?etag={Encode(entityTag)}";
        var service = Substitute.For<ICalendarService>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ExactCalendarResourceHandler.ReadAsync(uri, service, CancellationToken.None));

        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactGetAsync_UsesSharedSafeUpstreamFailureMapping()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new OperationCanceledException());
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
    }

    [Fact]
    public async Task ExactGetAsync_UsesSharedDiscoveryLimitMapping()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new CalendarDiscoveryLimitException(300));
        var sut = new ExactCalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(300);
    }

    private static CalendarResourceSnapshot CreateSnapshot(string entityTag)
    {
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/events/",
            "https://cal.example/events/a.ics",
            entityTag,
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
            []);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
