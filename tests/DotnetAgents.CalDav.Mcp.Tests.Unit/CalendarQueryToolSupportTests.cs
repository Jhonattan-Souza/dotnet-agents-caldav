using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarQueryToolSupportTests
{
    [Fact]
    public void ScopeCreationAcceptsOnlyUnambiguousClosedReferences()
    {
        var invalid = new[]
        {
            new CalendarEntityScopeArgument("private"),
            new CalendarEntityScopeArgument("private", new CalendarEntityReferenceArgument("name", Name: "Work")),
            new CalendarEntityScopeArgument("selected"),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("private", Name: "Work")),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name", Name: "")),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("name", Name: " Work")),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument(
                "name", Name: "Work", Href: "https://cal.example/work/")),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument("href", Href: "")),
            new CalendarEntityScopeArgument("selected", new CalendarEntityReferenceArgument(
                "href", Name: "Work", Href: "https://cal.example/work/"))
        };

        foreach (var scope in invalid)
            CalendarQueryToolSupport.TryCreateScope(scope, out _).ShouldBeFalse();

        CalendarQueryToolSupport.TryCreateScope(new CalendarEntityScopeArgument("default"), out var defaultScope)
            .ShouldBeTrue();
        defaultScope.ShouldBe(CalendarEntityScope.Default);
        CalendarQueryToolSupport.TryCreateScope(new CalendarEntityScopeArgument("all"), out var allScope)
            .ShouldBeTrue();
        allScope.ShouldBe(CalendarEntityScope.All);
    }

    [Theory]
    [InlineData("2026-08-23T00:00:00.99999999Z", "2026-08-23T00:00:01.0000000+00:00", true)]
    [InlineData("2026-08-23T00:00:00.10000000Z", "2026-08-23T00:00:00.1000000+00:00", true)]
    [InlineData("9999-12-31T23:59:59.99999999Z", null, false)]
    [InlineData("2026-08-23T00:00:00.1", null, false)]
    public void UtcFractionParsingRoundsDeterministicallyWithoutOverflow(
        string lexical,
        string? expected,
        bool accepted)
    {
        var parsed = CalendarQueryToolSupport.TryParseUtc(
            new CalendarEntityUtcArgument("utcDateTime", lexical),
            out var value);

        parsed.ShouldBe(accepted);
        value?.ToString("O").ShouldBe(expected);
    }
}
