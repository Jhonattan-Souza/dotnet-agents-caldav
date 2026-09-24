using DotnetAgents.CalDav.Core.Configuration;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Configuration;

public sealed class CalDavAccountOriginsTests
{
    [Theory]
    [InlineData("https://caldav.icloud.com/1/calendars/", true)]
    [InlineData("https://CALDAV.icloud.com:443/", true)]
    [InlineData("https://p01-caldav.icloud.com/1/calendars/", true)]
    [InlineData("https://P42-CalDAV.iCloud.com/1/calendars/", true)]
    [InlineData("https://exact.example.org/calendars/", true)]
    [InlineData("https://icloud.com/1/calendars/", false)]
    [InlineData("https://p01-caldav.icloud.com.evil.test/", false)]
    [InlineData("https://p01-caldavicloud.com/", false)]
    [InlineData("https://sub.exact.example.org/", false)]
    [InlineData("http://p01-caldav.icloud.com/1/calendars/", false)]
    [InlineData("http://caldav.icloud.com/1/calendars/", false)]
    [InlineData("https://p01-caldav.icloud.com:8443/1/calendars/", false)]
    [InlineData("https://other.example/", false)]
    public void Contains_AcceptsTheConfiguredOriginAndAllowlistedHttpsHostsAtTheConfiguredPort(
        string candidate,
        bool expected)
    {
        var origins = CalDavAccountOrigins.From(new CalDavOptions
        {
            BaseUrl = "https://caldav.icloud.com/",
            RedirectHosts = ".icloud.com, exact.example.org"
        });

        origins.Contains(new Uri(candidate)).ShouldBe(expected);
    }

    [Fact]
    public void Contains_KeepsTheConfiguredOriginOnlyWithoutAnAllowlist()
    {
        var origins = CalDavAccountOrigins.From(new CalDavOptions { BaseUrl = "https://caldav.icloud.com/" });

        origins.Contains(new Uri("https://caldav.icloud.com/other/")).ShouldBeTrue();
        origins.Contains(new Uri("https://p01-caldav.icloud.com/")).ShouldBeFalse();
    }

    [Fact]
    public void Contains_FailsClosedToTheConfiguredOriginForAnInvalidAllowlist()
    {
        var origins = CalDavAccountOrigins.From(new CalDavOptions
        {
            BaseUrl = "https://caldav.icloud.com/",
            RedirectHosts = "*.icloud.com"
        });

        origins.Contains(new Uri("https://caldav.icloud.com/")).ShouldBeTrue();
        origins.Contains(new Uri("https://p01-caldav.icloud.com/")).ShouldBeFalse();
    }

    [Fact]
    public void Contains_NeverAllowlistsHostsForAnHttpConfiguredEndpoint()
    {
        var origins = new CalDavAccountOrigins(
            new Uri("http://caldav.example.com:443/"),
            [new CalDavRedirectHostRule("example.com", IncludesSubdomains: true)]);

        origins.Contains(new Uri("http://caldav.example.com:443/")).ShouldBeTrue();
        origins.Contains(new Uri("https://p01.example.com/")).ShouldBeFalse();
    }

    [Fact]
    public void TryParseList_NormalizesExactHostsAndStrictSubdomainSuffixes()
    {
        CalDavRedirectHostRule.TryParseList(" P01-CalDAV.iCloud.com ,.Example.org", out var rules).ShouldBeTrue();

        rules.ShouldBe(
        [
            new CalDavRedirectHostRule("p01-caldav.icloud.com", IncludesSubdomains: false),
            new CalDavRedirectHostRule("example.org", IncludesSubdomains: true)
        ]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParseList_TreatsAnAbsentValueAsNoAllowlistedHost(string? value)
    {
        CalDavRedirectHostRule.TryParseList(value, out var rules).ShouldBeTrue();

        rules.ShouldBeEmpty();
    }
}
