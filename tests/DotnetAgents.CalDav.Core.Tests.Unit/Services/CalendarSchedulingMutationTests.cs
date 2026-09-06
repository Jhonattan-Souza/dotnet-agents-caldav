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

    [Theory]
    [InlineData("patch")]
    [InlineData("delete")]
    public async Task GroupedParticipationCannotBypassSemanticMutationSchedulingChecks(string operation)
    {
        var client = Client();
        // A non-mailto attendee is accepted through the existing lossless semantic
        // projection. Grouped mailto fields are already opaque to its Ical.Net corroboration.
        var prior = Resource(true, "group.ATTENDEE", "urn:uuid:owner");
        CalendarResourceSnapshotFactory.Create(CalendarHref, Href, "\"r1\"", prior)
            .Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(Href, "\"r1\"", prior));
        var revision = new CalendarResourceRevisionReference(Href, "guarded", CalendarEntityKind.Event, "\"r1\"");
        var service = Service(client);
        if (operation == "patch")
        {
            var result = await service.PatchEventAsync(new(revision, new("master"),
                new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Updated"))), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.UnsupportedCapability);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }
        else
        {
            var result = await service.DeleteResourceAsync(revision, CancellationToken.None);
            result.Code.ShouldBe(CalendarResourceDeleteCode.UnsupportedCapability);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }

        await client.Received(1).IsStorageOnlyMutationAllowedAsync(CalendarHref,
            Arg.Is<ReadOnlyMemory<byte>>(value => MatchesContent(value, prior)),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("exact_create", false)]
    [InlineData("exact_replace", false)]
    [InlineData("exact_replace", true)]
    public async Task ExactGroupedParticipationIsRejectedByExistingWireValidationBeforeAnyWrite(string operation, bool groupedPrior)
    {
        var client = Client();
        var grouped = Resource(true, "group.ATTENDEE", "urn:uuid:owner");
        var ordinary = Resource(false);
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(operation == "exact_create"
            ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
            : CalendarResourceRead.Success(Href, "\"r1\"", groupedPrior ? grouped : ordinary));
        var service = Service(client);
        var result = operation == "exact_create"
            ? await service.ExactCreateResourceAsync(new CalendarExactCreateRequest(Href, grouped), CancellationToken.None)
            : await service.ExactReplaceResourceAsync(new(
                new CalendarResourceRevisionReference(Href, "guarded", CalendarEntityKind.Event, "\"r1\""),
                groupedPrior ? ordinary : grouped), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidCalendarData);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().IsStorageOnlyMutationAllowedAsync(Arg.Any<string>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    private static bool MatchesContent(ReadOnlyMemory<byte> value, byte[] expected) => value.Span.SequenceEqual(expected);

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

    private static byte[] Resource(bool participating, string propertyName = "ORGANIZER", string address = "mailto:owner@example.com") => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Guard Tests//EN\r\nBEGIN:VEVENT\r\nUID:guarded\r\n"
        + "DTSTAMP:20260905T100000Z\r\nDTSTART:20260906T100000Z\r\n"
        + (participating ? propertyName + ":" + address + "\r\n" : string.Empty)
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");
}
