using System.Net;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarToolsTests
{
    [Fact]
    public async Task ListAsync_MapsScopedDiscoveryToTheVersionedStructuredShape()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(new CalendarDiscoveryResult(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                Color = "#AABBCCDD",
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised,
                EventEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT")],
                TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT")]
            }
        ],
        [new CalendarDiagnostic("calendar_href_not_found", "Configured Calendar href was not discovered.", CalendarDiagnosticSeverity.Warning)]));

        var wireResult = await sut.ListAsync(CancellationToken.None);
        var result = wireResult.StructuredContent!.Value.Deserialize<CalendarListResult>()!;

        result.Outcome.ShouldBe("success");
        result.Items.Single().Calendar.Href.ShouldBe("https://cal.example/events/");
        result.Items.Single().EntityKinds.Event.State.ShouldBe("advertised");
        result.Items.Single().EntityKinds.Todo.State.ShouldBe("not_advertised");
        result.Items.Single().Color.ShouldBeNull();
        result.Diagnostics.Single().Severity.ShouldBe("warning");
        result.Pagination.Mode.ShouldBe("non_snapshot");
        result.Pagination.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsTypedLimitErrorWithoutPartialItems()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CalendarDiscoveryResult>(new CalendarDiscoveryLimitException(257)));

        var result = await sut.ListAsync(CancellationToken.None);

        result.IsError.ShouldBe(true);
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe("limit_exhausted");
        error.Limits!.CalendarCount.ShouldBe(257);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized", false, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden", false, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited", true, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability", false, "capabilityAndProjection", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability", false, "capabilityAndProjection", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", false, "limitsAndAdmission", "admissionAndPayload")]
    [InlineData(HttpStatusCode.InsufficientStorage, "upstream_unavailable", false, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.InternalServerError, "upstream_unavailable", true, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.BadGateway, "upstream_unavailable", true, "upstream", "selectionDiscoveryCapability")]
    [InlineData(HttpStatusCode.NotFound, "upstream_protocol_error", false, "upstream", "selectionDiscoveryCapability")]
    public async Task ListAsync_MapsUpstreamFailuresToTypedStructuredErrors(
        HttpStatusCode statusCode,
        string expectedCode,
        bool expectedRetryable,
        string expectedCategory,
        string expectedPhase)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<CalendarDiscoveryResult>(new HttpRequestException("upstream", null, statusCode)));

        var result = await sut.ListAsync(CancellationToken.None);

        result.IsError.ShouldBe(true);
        ((TextContentBlock)result.Content.Single()).Text.ShouldBe("Calendar discovery failed.");
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe(expectedCode);
        error.Category.ShouldBe(expectedCategory);
        error.Phase.ShouldBe(expectedPhase);
        error.Retryable.ShouldBe(expectedRetryable);
        JsonSerializer.Serialize(error).ShouldNotContain("\"limits\":null");
    }

    [Fact]
    public async Task ListAsync_MapsNetworkFailureWithoutStatusToRetryableUnavailable()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<CalendarDiscoveryResult>(new HttpRequestException("network")));

        var result = await sut.ListAsync(CancellationToken.None);

        result.IsError.ShouldBe(true);
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe("upstream_unavailable");
        error.Retryable.ShouldBe(true);
    }

    [Theory]
    [InlineData("xml", "upstream_protocol_error", false)]
    [InlineData("timeout", "upstream_unavailable", true)]
    [InlineData("protocol", "upstream_protocol_error", false)]
    public async Task ListAsync_MapsProtocolAndTimeoutFailuresToTypedStructuredErrors(
        string failure,
        string expectedCode,
        bool expectedRetryable)
    {
        Exception exception = failure == "xml"
            ? new XmlException("invalid XML")
            : failure == "timeout"
                ? new TimeoutException("timed out")
                : new CalendarDiscoveryProtocolException("invalid DAV response");
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<CalendarDiscoveryResult>(exception));

        var result = await sut.ListAsync(CancellationToken.None);

        result.IsError.ShouldBe(true);
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe(expectedCode);
        error.Retryable.ShouldBe(expectedRetryable);
        error.Phase.ShouldBe("selectionDiscoveryCapability");
    }

    [Fact]
    public async Task ListAsync_MapsUpstreamCancellationToRetryableUnavailable()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromException<CalendarDiscoveryResult>(new OperationCanceledException("timed out")));

        var result = await sut.ListAsync(CancellationToken.None);

        result.IsError.ShouldBe(true);
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe("upstream_unavailable");
        error.Retryable.ShouldBe(true);
    }

    [Fact]
    public async Task ListAsync_MapsEveryCalendarDescriptorWireEnum()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarTools(service);
        service.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(new CalendarDiscoveryResult(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/derived/",
                DisplayNameProvenance = DisplayNameProvenance.DerivedFromHref,
                Color = "#aAbBcC",
                EventSupport = EntityKindSupport.Unknown,
                TodoSupport = EntityKindSupport.Advertised
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/missing/",
                DisplayNameProvenance = DisplayNameProvenance.Missing,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Unknown
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/invalid-color/",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                Color = "#ZZZZZZ",
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/malformed-color/",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                Color = "AABBCC",
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/no-color/",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ],
        [
            new CalendarDiagnostic("info", "Information.", CalendarDiagnosticSeverity.Info),
            new CalendarDiagnostic("error", "Error.", CalendarDiagnosticSeverity.Error)
        ]));

        var result = (await sut.ListAsync(CancellationToken.None)).StructuredContent!.Value
            .Deserialize<CalendarListResult>()!;

        result.Items[0].DisplayNameProvenance.ShouldBe("derived-from-href");
        result.Items[0].Color.ShouldBe("#aAbBcC");
        result.Items[0].EntityKinds.Event.State.ShouldBe("unknown");
        result.Items[0].EntityKinds.Todo.State.ShouldBe("advertised");
        result.Items[1].DisplayNameProvenance.ShouldBe("missing");
        result.Items[1].EntityKinds.Event.State.ShouldBe("not_advertised");
        result.Items[1].EntityKinds.Todo.State.ShouldBe("unknown");
        result.Items[2].Color.ShouldBeNull();
        result.Items[3].Color.ShouldBeNull();
        result.Items[4].Color.ShouldBeNull();
        result.Diagnostics.Select(diagnostic => diagnostic.Severity).ShouldBe(["info", "error"]);
    }
}
