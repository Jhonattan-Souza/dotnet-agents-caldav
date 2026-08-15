using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarServiceTests
{
    [Fact]
    public async Task GetResourceAsync_ReturnsOneRevisionCoherentEventSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/standup.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = calendarHref
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Events", EntityKindSupport.NotAdvertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.CalendarHref.ShouldBe(calendarHref);
        result.Snapshot.ResourceHref.ShouldBe(resourceHref);
        result.Snapshot.EntityTag.ShouldBe("\"revision-1\"");
        result.Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(System.Text.Encoding.UTF8.GetBytes(content));
        result.Snapshot.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Snapshot.Projection.EntityUid.ShouldBe("event-1");
        result.Snapshot.Projection.Summary.ShouldBe("Standup");
    }

    [Fact]
    public async Task GetResourceAsync_ReturnsMixedEntityKindsAsOpaqueWithSafeDiagnostic()
    {
        const string calendarHref = "https://cal.example/mixed/";
        const string resourceHref = "https://cal.example/mixed/mixed.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", CalendarHrefs = calendarHref });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Mixed", EntityKindSupport.Advertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Snapshot.Projection.EntityUid.ShouldBeNull();
        result.Snapshot.SemanticMutationAvailable.ShouldBeFalse();
        result.Snapshot.Diagnostics.Select(item => item.Code).ShouldBe(["mixed_entity_kinds"]);
        JsonSerializer.Serialize(result.Snapshot.Diagnostics).ShouldNotContain("event-1");
        JsonSerializer.Serialize(result.Snapshot.Diagnostics).ShouldNotContain("todo-1");
    }

    [Fact]
    public async Task GetResourceAsync_TreatsContentUrisAsInertData()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/inert.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nURL:https://attacker.invalid/url\r\nATTACH;VALUE=URI:https://attacker.invalid/attachment\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", CalendarHrefs = calendarHref }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Events", EntityKindSupport.NotAdvertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.CalendarProperties.Single(property => property.Name == "ATTACH").RawEncodedValue
            .ShouldBe("https://attacker.invalid/attachment");
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://other.example/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://user:secret@cal.example/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics#fragment", CalendarResourceReadCode.InvalidInput)]
    [InlineData("/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events%2Fprivate/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events%5cprivate/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/private/a.ics", CalendarResourceReadCode.OutsideScope)]
    public async Task GetResourceAsync_RejectsInvalidOrOutOfScopeHrefBeforeNetwork(
        string href,
        CalendarResourceReadCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/events/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());

        var result = await sut.GetResourceAsync(href, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDefaultCalendarAsync_UsesIndependentEventAndTodoDefaults()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultEventCalendarName = "Events",
            DefaultTodoCalendarName = "To-dos"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/todos/",
                DisplayName = "To-dos",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);

        var eventResult = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Event, CancellationToken.None);
        var todoResult = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        eventResult.Calendar!.Href.ShouldBe("https://cal.example/events/");
        todoResult.Calendar!.Href.ShouldBe("https://cal.example/todos/");
    }

    [Fact]
    public async Task ResolveDefaultCalendarAsync_ResolvesAuthorizedMatchBeyondCandidateLimit()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultTodoCalendarName = "Target"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, 33)
            .Select(index => Calendar($"https://cal.example/{index:D2}/", $"Calendar {index:D2}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/target/", "Target", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(CalendarSelectionCode.Success);
        result.Calendar!.Href.ShouldBe("https://cal.example/target/");
    }

    [Theory]
    [InlineData("Work", CalendarSelectionCode.Success)]
    [InlineData("Unknown", CalendarSelectionCode.Success)]
    [InlineData("Missing", CalendarSelectionCode.NotFound)]
    [InlineData("Duplicate", CalendarSelectionCode.Ambiguous)]
    [InlineData("Private", CalendarSelectionCode.OutsideScope)]
    [InlineData("Events", CalendarSelectionCode.UnsupportedCapability)]
    public async Task ResolveDefaultCalendarAsync_ReturnsDeterministicTypedOutcome(
        string configuredName,
        CalendarSelectionCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/work/,https://cal.example/unknown/,https://cal.example/duplicate-a/,https://cal.example/duplicate-b/,https://cal.example/events/",
            DefaultTodoCalendarName = configuredName
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Calendar("https://cal.example/work/", "Work", EntityKindSupport.Advertised),
            Calendar("https://cal.example/unknown/", "Unknown", EntityKindSupport.Unknown),
            Calendar("https://cal.example/duplicate-a/", "Duplicate", EntityKindSupport.Advertised),
            Calendar("https://cal.example/duplicate-b/", " duplicate ", EntityKindSupport.Advertised),
            Calendar("https://cal.example/private/", "Private", EntityKindSupport.Advertised),
            Calendar("https://cal.example/events/", "Events", EntityKindSupport.NotAdvertised)
        ]);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        if (expectedCode == CalendarSelectionCode.Success && configuredName == "Work")
            result.Calendar!.Href.ShouldBe("https://cal.example/work/");
        if (expectedCode == CalendarSelectionCode.Ambiguous)
            result.Candidates.Select(candidate => candidate.Href).ShouldBe(
                ["https://cal.example/duplicate-a/", "https://cal.example/duplicate-b/"]);
        if (expectedCode != CalendarSelectionCode.Success)
        {
            result.Candidates.ShouldNotBeEmpty();
            result.Candidates.ShouldAllBe(candidate => candidate.Href != "https://cal.example/private/");
            result.Candidates.All(candidate => candidate.DisplayName is not null).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task GetCalendarsAsync_AppliesExactScopeAndPreservesDiscoveryEvidence()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/a/,https://cal.example/missing/,https://cal.example/a/,https://cal.example/missing/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/b/",
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Unknown,
                TodoSupport = EntityKindSupport.NotAdvertised,
                TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT")]
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/a/",
                DisplayName = "a",
                DisplayNameProvenance = DisplayNameProvenance.DerivedFromHref,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised,
                EventEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT,VTODO")],
                TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT,VTODO")]
            }
        ]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        result.Items.Select(calendar => calendar.Href).ShouldBe(["https://cal.example/a/"]);
        result.Items[0].DisplayNameProvenance.ShouldBe(DisplayNameProvenance.DerivedFromHref);
        result.Items[0].EventSupport.ShouldBe(EntityKindSupport.Advertised);
        result.Items[0].TodoSupport.ShouldBe(EntityKindSupport.Advertised);
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .ShouldBe(["duplicate_calendar_href", "duplicate_calendar_href", "calendar_href_not_found"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_WithoutConfiguredScope_ReturnsAllUniqueCalendarsInCanonicalOrder()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Calendar("https://cal.example/z/", "Z", EntityKindSupport.Advertised),
            Calendar("https://cal.example/a/", "A", EntityKindSupport.Advertised),
            Calendar("https://cal.example/a/", "Duplicate A", EntityKindSupport.Advertised)
        ]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        result.Items.Select(calendar => calendar.Href).ShouldBe(
            ["https://cal.example/a/", "https://cal.example/z/"]);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCalendarsAsync_DiagnosticsDoNotExposeConfiguredHrefText()
    {
        const string unsafeHref = "https://user:secret@cal.example/private/";
        var oversizedHref = $"https://cal.example/{new string('x', 10_000)}";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = $"{unsafeHref},{oversizedHref}"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        var serializedDiagnostics = JsonSerializer.Serialize(result.Diagnostics);
        serializedDiagnostics.ShouldNotContain(unsafeHref);
        serializedDiagnostics.ShouldNotContain(oversizedHref);
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .ShouldBe(["calendar_href_not_found", "calendar_href_not_found"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_DeduplicatesAndBoundsDiscoveredCalendars()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/000/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, 257)
            .Select(index => Calendar($"https://cal.example/{index:D3}/", $"Calendar {index:D3}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/000/", "Duplicate", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        var exception = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        exception.CalendarCount.ShouldBe(257);
    }

    private static CalendarDescriptor Calendar(string href, string displayName, EntityKindSupport todoSupport) =>
        new()
        {
            Href = href,
            DisplayName = displayName,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.Advertised,
            TodoSupport = todoSupport
        };
}
