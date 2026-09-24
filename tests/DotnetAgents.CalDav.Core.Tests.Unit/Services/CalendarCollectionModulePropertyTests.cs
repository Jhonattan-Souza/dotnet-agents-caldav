using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed partial class CalendarCollectionModuleTests
{
    private const string Home = "https://cal.example/calendars/user/";

    [Fact]
    public async Task Create_InitializesColorOrderAndTimeZoneAndVerifiesDiscoveredColorAndOrder()
    {
        var transport = new ScriptedTransport(Home);
        var module = CreateModule(transport);

        var result = await module.CreateAsync(new CalendarCollectionCreateRequest(
            "Styled", [CalendarEntityKind.Event], Color: "#FF2968", Order: 4, TimeZoneId: "Europe/Berlin"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Success);
        var initial = transport.LastCreate!.InitialProperties!;
        initial.Color.ShouldBe("#FF2968");
        initial.Order.ShouldBe(4);
        CalendarMetadataTimeZoneReader.Read(initial.TimeZone!, CancellationToken.None).ShouldBe(["Europe/Berlin"]);
        result.Calendar!.Color.ShouldBe("#FF2968");
        result.Calendar.Order.ShouldBe(4);
    }

    [Fact]
    public async Task Create_WithoutOptionalPropertiesSendsNoInitialProperties()
    {
        var transport = new ScriptedTransport(Home);

        var result = await CreateModule(transport).CreateAsync(
            new CalendarCollectionCreateRequest("Plain", [CalendarEntityKind.Todo]), CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Success);
        transport.LastCreate!.InitialProperties.ShouldBeNull();
    }

    [Fact]
    public async Task Create_ColorReadbackIgnoresHexCase()
    {
        var transport = new ScriptedTransport(Home)
        {
            CreatedDescriptor = Descriptor(Home + "styled/", "Styled") with { Color = "#ff2968" }
        };

        var result = await CreateModule(transport).CreateAsync(new CalendarCollectionCreateRequest(
            "Styled", [CalendarEntityKind.Event], Home + "styled/", Color: "#FF2968"), CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Success);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("order")]
    public async Task Create_MismatchingDiscoveredPropertyIsCommittedButUnverified(string member)
    {
        var transport = new ScriptedTransport(Home)
        {
            CreatedDescriptor = Descriptor(Home + "styled/", "Styled") with
            {
                Color = member == "color" ? "#000000" : "#FF2968",
                Order = member == "order" ? 9 : 4
            }
        };

        var result = await CreateModule(transport).CreateAsync(new CalendarCollectionCreateRequest(
            "Styled", [CalendarEntityKind.Event], Home + "styled/", Color: "#FF2968", Order: 4), CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Calendar.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_TimeZoneNeedsADefinitiveAtomicAcknowledgement()
    {
        var transport = new ScriptedTransport(Home) { CreateDispatchCode = CalendarCollectionDispatchCode.PossiblyDispatched };

        var result = await CreateModule(transport).CreateAsync(new CalendarCollectionCreateRequest(
            "Zoned", [CalendarEntityKind.Event], TimeZoneId: "Asia/Tokyo"), CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task Create_RejectsInvalidCollectionPropertiesBeforeDiscovery()
    {
        var transport = new ScriptedTransport(Home);
        var module = CreateModule(transport);
        CalendarCollectionCreateRequest[] requests =
        [
            new("Styled", [CalendarEntityKind.Event], Color: "#FF2968FF"),
            new("Styled", [CalendarEntityKind.Event], Color: "pink"),
            new("Styled", [CalendarEntityKind.Event], Order: -1),
            new("Styled", [CalendarEntityKind.Event], TimeZoneId: "Mars/Base"),
            new("Styled", [CalendarEntityKind.Event], TimeZoneId: "Europe/Berlin ")
        ];

        foreach (var request in requests)
        {
            var result = await module.CreateAsync(request, CancellationToken.None);
            result.Code.ShouldBe(CalendarCollectionCreateCode.InvalidInput);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }

        transport.DiscoveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(403, "UpstreamForbidden", false)]
    [InlineData(409, "Conflict", false)]
    [InlineData(412, "Conflict", false)]
    [InlineData(401, "UpstreamUnauthorized", false)]
    [InlineData(405, "UnsupportedCapability", false)]
    [InlineData(501, "UnsupportedCapability", false)]
    [InlineData(413, "PayloadTooLarge", false)]
    [InlineData(429, "UpstreamRateLimited", true)]
    [InlineData(507, "UpstreamUnavailable", true)]
    [InlineData(422, "UpstreamProtocolError", false)]
    public async Task Create_NamedPropertyRejectionProvesNothingWasCreatedAndUsesThePropertyStatus(
        int status, string expectedCode, bool retryable)
    {
        CalendarPropertyRejection[] rejections = [new("displayName", 424), new("timeZone", status)];
        var transport = new ScriptedTransport(Home)
        {
            CreateDispatchCode = status >= 500 ? CalendarCollectionDispatchCode.UpstreamUnavailable : CalendarCollectionDispatchCode.UpstreamForbidden,
            SuppressCreatedItem = true,
            CreateRejectedProperties = rejections
        };

        var result = await CreateModule(transport).CreateAsync(new CalendarCollectionCreateRequest(
            "Zoned", [CalendarEntityKind.Event], TimeZoneId: "Asia/Tokyo"), CancellationToken.None);

        result.Code.ToString().ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Retryable.ShouldBe(retryable);
        result.RejectedProperties.ShouldBe(rejections);
        transport.DiscoveryCount.ShouldBe(1);
    }
}
