using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarSchedulingMutationTests
{
    private const string CalendarHref = "https://cal.example/events/";
    private const string Href = CalendarHref + "guarded.ics";

    [Theory]
    [InlineData("exact_create")]
    [InlineData("exact_replace")]
    [InlineData("patch")]
    [InlineData("delete")]
    public async Task ParticipatingMutations_RejectUnknownSchedulingBeforeWriting(string operation)
    {
        var client = Client();
        if (operation == "exact_create")
            client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        var service = Service(client);
        var revision = new CalendarResourceRevisionReference(Href, "guarded", CalendarEntityKind.Event, "\"r1\"");
        var state = operation switch
        {
            "exact_create" => (await service.ExactCreateResourceAsync(new CalendarExactCreateRequest(Href, Resource(true)), CancellationToken.None)).MutationState,
            "exact_replace" => (await service.ExactReplaceResourceAsync(new(revision, Resource(false)), CancellationToken.None)).MutationState,
            "patch" => (await service.PatchEventAsync(new(revision, new("master"),
                new CalendarEventPatch(Organizer: new(CalendarScalarPatchOperation.Clear))), CancellationToken.None)).MutationState,
            _ => (await service.DeleteResourceAsync(revision, CancellationToken.None)).MutationState
        };

        state.ShouldBe(CalendarMutationState.NotAttempted);
        await client.Received(1).IsStorageOnlyMutationAllowedAsync(CalendarHref,
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SemanticCreate_RejectsParticipationWithoutSchedulingEvidence()
    {
        var client = Client();
        var result = await Service(client).CreateEventAsync(new(
            CalendarCreateDestination.Selected(new CalendarReference(Href: CalendarHref)),
            "guarded",
            new CalendarEventCreateFields(Start: new(CalendarTemporalKind.UtcDateTime, "2026-09-06T10:00:00Z"),
                StructuredData: new CalendarStructuredData(Organizer: new("mailto:owner@example.com", null, [])))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    private static ICalendarClient Client()
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([new CalendarDescriptor
        {
            Href = CalendarHref, DisplayName = "Events", EventSupport = EntityKindSupport.Advertised,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName, TodoSupport = EntityKindSupport.NotAdvertised
        }]);
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(Href, "\"r1\"", Resource(true)));
        return client;
    }

    private static CalendarService Service(ICalendarClient client) => new(client,
        Options.Create(new CalDavOptions { BaseUrl = "https://cal.example/" }),
        Substitute.For<ILogger<CalendarService>>());

    private static byte[] Resource(bool participating) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Guard Tests//EN\r\nBEGIN:VEVENT\r\nUID:guarded\r\n"
        + "DTSTAMP:20260905T100000Z\r\nDTSTART:20260906T100000Z\r\n"
        + (participating ? "ORGANIZER:mailto:owner@example.com\r\n" : string.Empty)
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");
}
