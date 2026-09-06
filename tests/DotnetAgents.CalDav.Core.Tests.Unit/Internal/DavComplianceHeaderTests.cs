using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class DavComplianceHeaderTests
{
    [Theory]
    [InlineData("vendor.feature")]
    [InlineData("!#$%&'*+-.^_`|~")]
    [InlineData("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")]
    [InlineData("<urn:example:feature,version>")]
    [InlineData("<urn:example:x,calendar-auto-schedule,y>")]
    [InlineData("<x:feature,version>")]
    [InlineData("<x:>")]
    [InlineData("<x:/>")]
    [InlineData("<x://>")]
    [InlineData("<x:///path>")]
    [InlineData("<x://@>")]
    [InlineData("<x://:>")]
    [InlineData("<http:feature>")]
    [InlineData("<tag:example.org,2026:feature>")]
    [InlineData("<a+b-c.d://user:secret@host.example:123/path;a?b=2,c/?>")]
    [InlineData("<https://example.org/%5Bfeature%5D?x=%23value>")]
    [InlineData("<dav:>")]
    [InlineData("<custom://[::1]:8080/path>")]
    [InlineData("<custom://[2001:db8::1]>")]
    [InlineData("<custom://[::ffff:192.0.2.1]/>")]
    [InlineData("<custom://[::ffff:0.255.2.1]/>")]
    [InlineData("<custom://[vF.future:!$&'()*+,;=]>")]
    [InlineData("<custom://[V1.future]>")]
    public void ValidExtensionClassesPreserveSchedulingAbsence(string extension)
    {
        string[] headers = [$"1, calendar-access, {extension}"];

        DavComplianceHeader.TryRead(headers, out var scheduling).ShouldBeTrue();
        scheduling.ShouldBeFalse();
        CalendarSchedulingSafety.ProvesSchedulingAbsent(headers).ShouldBeTrue();
    }

    [Theory]
    [InlineData("vendor.feature,calendar-auto-schedule")]
    [InlineData("<urn:x,calendar-auto-schedule,y>,CALENDAR-AUTO-SCHEDULE")]
    [InlineData("calendar-auto-schedule,vendor.feature")]
    [InlineData(",, calendar-auto-schedule, ,")]
    public void ActualSchedulingTokenAlwaysBlocksAbsence(string value)
    {
        string[] headers = ["1", value];

        DavComplianceHeader.TryRead(headers, out var scheduling).ShouldBeTrue();
        scheduling.ShouldBeTrue();
        CalendarSchedulingSafety.ProvesSchedulingAbsent(headers).ShouldBeFalse();
    }

    [Theory]
    [InlineData(",1, ,calendar-access,")]
    [InlineData(" \t1\t,\tcalendar-access \t")]
    public void HttpListWhitespaceAndEmptyElementsRemainCompatible(string value)
    {
        string[] headers = [string.Empty, value, " \t"];

        DavComplianceHeader.TryRead(headers, out var scheduling).ShouldBeTrue();
        scheduling.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , , ")]
    [InlineData("1, bad value")]
    [InlineData("1, \"quoted\"")]
    [InlineData("1;parameter=value")]
    [InlineData("1\r\n, calendar-access")]
    [InlineData("1\v, calendar-access")]
    [InlineData("1\f, calendar-access")]
    [InlineData("1\u00a0, calendar-access")]
    [InlineData("1, v\u00e9ndor")]
    [InlineData("1, <urn:x>trailing")]
    [InlineData("1, <urn:x><urn:y>")]
    [InlineData("1, <urn:x")]
    [InlineData("1, <relative/path>")]
    [InlineData("1, <1scheme:part>")]
    [InlineData("1, <bad_scheme:part>")]
    [InlineData("1, <urn:x#fragment>")]
    [InlineData("1, <urn:x#>")]
    [InlineData("1, <urn:with space>")]
    [InlineData("1, <urn:x\tvalue>")]
    [InlineData("1, <urn:x\nvalue>")]
    [InlineData("1, <urn:x%>")]
    [InlineData("1, <urn:x%2>")]
    [InlineData("1, <urn:x%GG>")]
    [InlineData("1, <urn:x%0G>")]
    [InlineData("1, <x:path[part]>")]
    [InlineData("1, <x:path?x=[part]>")]
    [InlineData("1, <x://user@@host>")]
    [InlineData("1, <x://host:port>")]
    [InlineData("1, <x://host:12:13>")]
    [InlineData("1, <x://[::1]tail>")]
    [InlineData("1, <x://[::1]:x>")]
    [InlineData("1, <x://[::1>")]
    [InlineData("1, <x://[]>")]
    [InlineData("1, <x://[127.0.0.1]>")]
    [InlineData("1, <x://[ ::1]>")]
    [InlineData("1, <x://[[::1]]>")]
    [InlineData("1, <x://[1::2::3]>")]
    [InlineData("1, <x://[::ffff:192.0.2.0000]>")]
    [InlineData("1, <x://[::ffff:192.0.2.+1]>")]
    [InlineData("1, <x://[fe80::1%25eth0]>")]
    [InlineData("1, <x://[::ffff:192.00.2.1]>")]
    [InlineData("1, <x://[::ffff:192.0.2.256]>")]
    [InlineData("1, <x://[::ffff:192.0.2]>")]
    [InlineData("1, <x://[::ffff:192.0.2.1.2]>")]
    [InlineData("1, <x://[v.future]>")]
    [InlineData("1, <x://[v1.]>")]
    [InlineData("1, <x://[vZ.future]>")]
    [InlineData("1, <x://[v1.future%41]>")]
    [InlineData("1, <x://[v1.future@host]>")]
    public void InvalidOrEmptyComplianceCannotProveSchedulingAbsence(string value)
    {
        DavComplianceHeader.TryRead([value], out _).ShouldBeFalse();
        CalendarSchedulingSafety.ProvesSchedulingAbsent([value]).ShouldBeFalse();
    }

    [Fact]
    public void AnEarlierValidFieldCannotHideAnInvalidTail()
    {
        DavComplianceHeader.TryRead(["1, calendar-access", "bad value"], out _).ShouldBeFalse();
        CalendarSchedulingSafety.ProvesSchedulingAbsent(["1, calendar-auto-schedule", "bad value"]).ShouldBeFalse();
        DavComplianceHeader.TryRead([], out _).ShouldBeFalse();
    }

    [Fact]
    public void EmptyElementAllowanceIsBoundedAcrossFields()
    {
        DavComplianceHeader.TryRead([new string(',', 32) + "1"], out _).ShouldBeTrue();
        DavComplianceHeader.TryRead([new string(',', 33) + "1"], out _).ShouldBeFalse();
        DavComplianceHeader.TryRead([new string(',', 31), "1"], out _).ShouldBeTrue();
        DavComplianceHeader.TryRead([new string(',', 32), "1"], out _).ShouldBeFalse();
    }

    [Fact]
    public void CombinedHeaderCharactersHaveAFixedBudget()
    {
        DavComplianceHeader.TryRead([new string('a', 64 * 1024)], out _).ShouldBeTrue();
        DavComplianceHeader.TryRead([new string('a', 64 * 1024), "1"], out _).ShouldBeFalse();
        DavComplianceHeader.TryRead([new string('a', 64 * 1024 + 1)], out _).ShouldBeFalse();
    }
}
