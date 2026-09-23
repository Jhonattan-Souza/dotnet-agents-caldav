using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarMrtrCapabilityGuardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");

    [Fact]
    public void RequireConfirmationCapability_AcceptsDeclaredFormElicitation()
    {
        Should.NotThrow(() => CalendarMrtrCapabilityGuard.RequireConfirmationCapability(
            null,
            null,
            true,
            CalendarMrtrCapabilityGuard.RequiredCapabilities()));
    }

    [Fact]
    public void RequireConfirmationCapability_RefusesUndeclaredCapabilitiesWithTheRequiredPayload()
    {
        var exception = Should.Throw<MissingRequiredClientCapabilityException>(() =>
            CalendarMrtrCapabilityGuard.RequireConfirmationCapability(null, null, true, null));

        exception.RequiredCapabilities.Elicitation!.Form.ShouldNotBeNull();
        exception.RequiredCapabilities.Elicitation.Url.ShouldBeNull();
        exception.Message.ShouldContain("form elicitation");
    }

    [Fact]
    public void RequireConfirmationCapability_RefusesUrlOnlyElicitation()
    {
        var capabilities = new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() }
        };

        Should.Throw<MissingRequiredClientCapabilityException>(() =>
            CalendarMrtrCapabilityGuard.RequireConfirmationCapability(null, null, true, capabilities));
    }

    [Fact]
    public void RequireConfirmationCapability_AcceptsAContinuationRoundWithoutTheCapability()
    {
        Should.NotThrow(() => CalendarMrtrCapabilityGuard.RequireConfirmationCapability(
            "opaque-state",
            null,
            true,
            null));
        Should.NotThrow(() => CalendarMrtrCapabilityGuard.RequireConfirmationCapability(
            null,
            new Dictionary<string, InputResponse>(),
            true,
            null));
    }

    [Fact]
    public void RequireConfirmationCapability_LeavesNonMrtrClientsOnTheTypedUnsupportedPath()
    {
        Should.NotThrow(() => CalendarMrtrCapabilityGuard.RequireConfirmationCapability(
            null,
            null,
            false,
            null));
    }

    // A request that omits elicitation, or names only another mode, has not declared the in-band
    // mode this confirmation needs. A blank elicitation object arrives here already carrying the
    // form mode, because the protocol normalizes it before the guard reads the capabilities.
    [Fact]
    public void DeclaresFormElicitation_RequiresTheInBandMode()
    {
        CalendarMrtrCapabilityGuard.DeclaresFormElicitation(null).ShouldBeFalse();
        CalendarMrtrCapabilityGuard.DeclaresFormElicitation(new ClientCapabilities()).ShouldBeFalse();
        CalendarMrtrCapabilityGuard.DeclaresFormElicitation(new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() }
        }).ShouldBeFalse();
        CalendarMrtrCapabilityGuard.DeclaresFormElicitation(
            CalendarMrtrCapabilityGuard.RequiredCapabilities()).ShouldBeTrue();
    }

    [Fact]
    public async Task PatchEventRawAsync_RefusesTheConfirmationRoundBeforeAnyReview()
    {
        var service = ReviewedEventService();
        var sut = CreateTool(service);

        await Should.ThrowAsync<MissingRequiredClientCapabilityException>(() => sut.PatchEventRawAsync(
            ReplaceAllArguments("event"),
            null,
            null,
            true,
            null,
            CancellationToken.None));

        await service.DidNotReceive().ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventRawAsync_StillOpensTheConfirmationRoundForADeclaringClient()
    {
        var service = ReviewedEventService();
        var sut = CreateTool(service);

        var required = await Should.ThrowAsync<InputRequiredException>(() => sut.PatchEventRawAsync(
            ReplaceAllArguments("event"),
            null,
            null,
            true,
            CalendarMrtrCapabilityGuard.RequiredCapabilities(),
            CancellationToken.None));

        required.Result.InputRequests!.ShouldContainKey("confirm_replace_all");
        await service.Received(1).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventRawAsync_LeavesAnUnconfirmedPatchAvailableWithoutTheCapability()
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));
        var sut = CreateTool(service);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"snapshot":{"href":"https://cal.example/events/event-1.ics","entityUid":"event-1","entityKind":"event","entityTag":"\"r1\""},"target":{"scope":"master"},"patch":{"scalars":[{"field":"summary","operation":"set","value":"Updated"}]}}
            """);

        var result = await sut.PatchEventRawAsync(arguments, null, null, true, null, CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(1).PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchTodoRawAsync_RefusesTheConfirmationRoundBeforeAnyReview()
    {
        var service = ReviewedTodoService();
        var sut = CreateTool(service);

        await Should.ThrowAsync<MissingRequiredClientCapabilityException>(() => sut.PatchTodoRawAsync(
            ReplaceAllArguments("todo"),
            null,
            null,
            true,
            null,
            CancellationToken.None));

        await service.DidNotReceive().ReviewTodoPatchAsync(
            Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchTodoAsync(
            Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchTodoRawAsync_StillOpensTheConfirmationRoundForADeclaringClient()
    {
        var service = ReviewedTodoService();
        var sut = CreateTool(service);

        var required = await Should.ThrowAsync<InputRequiredException>(() => sut.PatchTodoRawAsync(
            ReplaceAllArguments("todo"),
            null,
            null,
            true,
            CalendarMrtrCapabilityGuard.RequiredCapabilities(),
            CancellationToken.None));

        required.Result.InputRequests!.ShouldContainKey("confirm_replace_all");
        await service.Received(1).ReviewTodoPatchAsync(
            Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchRawAsync_WithoutExplicitCapabilitiesKeepsTheEstablishedRawContract()
    {
        var service = ReviewedEventService();
        var sut = CreateTool(service);

        var required = await Should.ThrowAsync<InputRequiredException>(() => sut.PatchEventRawAsync(
            ReplaceAllArguments("event"),
            null,
            null,
            true,
            CancellationToken.None));

        required.Result.RequestState.ShouldNotBeNullOrWhiteSpace();
    }

    private static ICalendarService ReviewedEventService()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(null, IntentDigest()));
        return service;
    }

    private static ICalendarService ReviewedTodoService()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewTodoPatchAsync(Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(null, IntentDigest()));
        return service;
    }

    private static byte[] IntentDigest() => [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    private static CalendarEntityPatchTools CreateTool(ICalendarService service)
    {
        var time = new FixedTimeProvider(Now);
        var protector = new CalendarMutationRequestStateProtector(
            time,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "user",
                Password = "secret"
            }),
            [.. Enumerable.Range(0, 64).Select(value => (byte)value)]);
        return new CalendarEntityPatchTools(service, time, protector);
    }

    private static Dictionary<string, JsonElement> ReplaceAllArguments(string entityKind) => new()
    {
        ["snapshot"] = JsonSerializer.SerializeToElement(new
        {
            href = entityKind == "event"
                ? "https://cal.example/events/event-1.ics"
                : "https://cal.example/tasks/todo-1.ics",
            entityUid = entityKind == "event" ? "event-1" : "todo-1",
            entityKind,
            entityTag = "\"r1\""
        }),
        ["target"] = JsonSerializer.SerializeToElement(new { scope = "master" }),
        ["patch"] = JsonSerializer.SerializeToElement(new
        {
            collections = new object[]
            {
                new { field = "categories", operation = "replaceAll", values = new[] { "Work" } }
            }
        })
    };

    private static CalendarResourceSnapshot EventSnapshot() => new(
        "https://cal.example/events/",
        "https://cal.example/events/event-1.ics",
        "\"r2\"",
        "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:event-1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"u8.ToArray(),
        [],
        new CalendarResourceProjection(CalendarResourceProjectionKind.Event, "event-1", "Updated"),
        []);
}
