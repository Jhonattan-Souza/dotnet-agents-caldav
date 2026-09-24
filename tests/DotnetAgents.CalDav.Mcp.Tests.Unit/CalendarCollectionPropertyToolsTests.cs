using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed partial class CalendarCollectionToolsTests
{
    [Fact]
    public async Task CreateRawAsync_ForwardsColorOrderAndTimeZoneAndReturnsSchemaValidDescriptor()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor("https://cal.example/calendars/user/styled/", eventKind: true, todo: false) with
        {
            Color = "#FF2968",
            Order = 3,
            Description = "CalDAV description",
            DavDescription = "WebDAV description"
        };
        module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarCollectionCreateResult.Success(descriptor));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var arguments = CreateArguments("Styled");
        arguments["color"] = JsonSerializer.SerializeToElement("#FF2968");
        arguments["order"] = JsonSerializer.SerializeToElement(3);
        arguments["timeZone"] = JsonSerializer.SerializeToElement("Europe/Berlin");

        var result = await sut.CreateRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        var calendar = result.StructuredContent!.Value.GetProperty("calendar");
        calendar.GetProperty("color").GetString().ShouldBe("#FF2968");
        calendar.GetProperty("order").GetInt32().ShouldBe(3);
        calendar.GetProperty("description").GetString().ShouldBe("CalDAV description");
        calendar.GetProperty("davDescription").GetString().ShouldBe("WebDAV description");
        CalendarOutputSchemaGuard.Validate("calendars.create", result);
        await module.Received(1).CreateAsync(
            Arg.Is<CalendarCollectionCreateRequest>(request => request.Color == "#FF2968"
                && request.Order == 3 && request.TimeZoneId == "Europe/Berlin"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("color", "42")]
    [InlineData("color", "\" \"")]
    [InlineData("order", "\"3\"")]
    [InlineData("order", "1.5")]
    [InlineData("order", "2147483648")]
    [InlineData("timeZone", "null")]
    [InlineData("timeZone", "\"\"")]
    public async Task CreateRawAsync_RejectsMistypedCollectionPropertiesBeforeTheModule(string member, string json)
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var arguments = CreateArguments();
        arguments[member] = JsonDocument.Parse(json).RootElement.Clone();

        var result = await sut.CreateRawAsync(arguments, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await module.DidNotReceive().CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRawAsync_RejectedPropertiesAreNamedAsViolationsWithoutCommit()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionCreateResult(CalendarCollectionCreateCode.UnsupportedCapability,
                CalendarMutationState.NotCommitted)
            {
                RejectedProperties = [new("timeZone", 403), new("displayName", 424)]
            });
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.CreateRawAsync(CreateArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var content = result.StructuredContent!.Value;
        content.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        content.GetProperty("mutationState").GetString().ShouldBe("not_committed");
        content.GetProperty("message").GetString()!.ShouldContain("rejected one or more requested Calendar collection properties");
        var violations = content.GetProperty("violations").EnumerateArray()
            .Select(item => (item.GetProperty("pointer").GetString(), item.GetProperty("code").GetString())).ToArray();
        violations.ShouldBe([("/displayName", "property_not_applied"), ("/timeZone", "property_rejected")]);
        content.GetProperty("violations")[1].GetProperty("message").GetString()!.ShouldContain("403");
        CalendarOutputSchemaGuard.Validate("calendars.create", result);
    }
}
