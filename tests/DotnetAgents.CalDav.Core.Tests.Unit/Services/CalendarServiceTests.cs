using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
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
    public async Task DiscoverAsync_ConcurrentConsumersShareOneCompleteAuthority()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(acquisition.Task);
        var sut = Discovery(client);

        var first = sut.DiscoverAsync(CancellationToken.None);
        var second = sut.DiscoverAsync(CancellationToken.None);
        acquisition.SetResult([EntityCalendar(
            "https://cal.example/events/",
            "Events",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised)]);

        (await first).Discovery.Items.ShouldHaveSingleItem();
        (await second).Discovery.Items.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_SharedFailureIsMemoizedForTheToolCall()
    {
        var client = Substitute.For<ICalendarClient>();
        var failure = new CalendarDiscoveryProtocolException("private upstream response");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<CalendarDescriptor>>(failure));
        var sut = Discovery(client);

        var first = await Should.ThrowAsync<CalendarDiscoveryProtocolException>(
            () => sut.DiscoverAsync(CancellationToken.None));
        var second = await Should.ThrowAsync<CalendarDiscoveryProtocolException>(
            () => sut.DiscoverAsync(CancellationToken.None));

        first.ShouldBeSameAs(failure);
        second.ShouldBeSameAs(failure);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_SharedCancellationStopsTheSingleAcquisition()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            token.Register(() => acquisition.TrySetCanceled(token));
            return acquisition.Task;
        });
        var sut = Discovery(client);
        using var cancellation = new CancellationTokenSource();

        var first = sut.DiscoverAsync(cancellation.Token);
        var second = sut.DiscoverAsync(cancellation.Token);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        await Should.ThrowAsync<OperationCanceledException>(() => second);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_LaterSameKeyTokenDoesNotOverrideTheOperationToken()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(acquisition.Task);
        var sut = Discovery(client);
        using var operationCancellation = new CancellationTokenSource();
        using var laterConsumerCancellation = new CancellationTokenSource();

        var operation = sut.DiscoverAsync(operationCancellation.Token);
        var laterConsumer = sut.DiscoverAsync(laterConsumerCancellation.Token);
        laterConsumerCancellation.Cancel();
        acquisition.SetResult([EntityCalendar(
            "https://cal.example/events/",
            "Events",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised)]);

        (await operation).Discovery.Items.ShouldHaveSingleItem();
        (await laterConsumer).Discovery.Items.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_OperationCancellationIsTheSharedSameKeyOutcome()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            token.Register(() => acquisition.TrySetCanceled(token));
            return acquisition.Task;
        });
        var sut = Discovery(client);
        using var operationCancellation = new CancellationTokenSource();

        var operation = sut.DiscoverAsync(operationCancellation.Token);
        var laterConsumer = sut.DiscoverAsync(CancellationToken.None);
        operationCancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => operation);
        await Should.ThrowAsync<OperationCanceledException>(
            () => laterConsumer.WaitAsync(TimeSpan.FromSeconds(1)));
        await client.Received(1).GetCalendarsAsync(operationCancellation.Token);
    }

    [Fact]
    public async Task DiscoverAsync_CancelledToolCallDoesNotPoisonAnotherToolCall()
    {
        var client = Substitute.For<ICalendarClient>();
        var firstAcquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            if (Interlocked.Increment(ref calls) == 1)
            {
                token.Register(() => firstAcquisition.TrySetCanceled(token));
                return firstAcquisition.Task;
            }
            return Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EntityCalendar(
                "https://cal.example/events/",
                "Events",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised)]);
        });
        using var cancelled = new CancellationTokenSource();

        var cancelledCall = Discovery(client).DiscoverAsync(cancelled.Token);
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => cancelledCall);
        var unrelated = await Discovery(client).DiscoverAsync(CancellationToken.None);

        unrelated.Discovery.Items.ShouldHaveSingleItem();
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_NewToolCallDoesNotReuseReviewTimeAuthority()
    {
        var client = Substitute.For<ICalendarClient>();
        var calls = 0;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EntityCalendar(
                $"https://cal.example/events-{calls}/",
                $"Events {calls}",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised)]);
        });
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "principal",
            CalendarHrefs = "https://cal.example/events-1/,https://cal.example/events-2/"
        });

        var reviewCall = Discovery(client, options);
        var executionCall = Discovery(client, options);
        var reviewed = await reviewCall.DiscoverAsync(CancellationToken.None);
        var executed = await executionCall.DiscoverAsync(CancellationToken.None);

        reviewed.Discovery.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/events-1/");
        executed.Discovery.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/events-2/");
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_OperationContextIsOpaqueAndIsolatesCredentialRotation()
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var firstCredential = string.Concat("secret", "-a");
        var firstContext = new CalDavOptions
        {
            BaseUrl = "https://cal.example/server/",
            Username = "principal",
            Password = firstCredential,
            CalendarHrefs = "https://cal.example/a/",
            DefaultEventCalendarName = "Events"
        };
        var secondCredential = string.Concat("secret", "-b");
        var secondContext = new CalDavOptions
        {
            BaseUrl = firstContext.BaseUrl,
            Username = firstContext.Username,
            Password = secondCredential,
            CalendarHrefs = firstContext.CalendarHrefs,
            DefaultEventCalendarName = firstContext.DefaultEventCalendarName
        };
        var firstGeneration = CalendarOperationContextGeneration.Create();
        var secondGeneration = CalendarOperationContextGeneration.Create();

        var firstKey = CalendarDiscoveryKey.Create(firstContext, firstGeneration);
        var secondKey = CalendarDiscoveryKey.Create(secondContext, secondGeneration);
        await Service(client, Options.Create(firstContext)).GetCalendarsAsync(CancellationToken.None);
        await Service(client, Options.Create(secondContext)).GetCalendarsAsync(CancellationToken.None);

        firstKey.ShouldNotBe(secondKey);
        firstKey.ToString().ShouldNotContain(firstCredential);
        secondKey.ToString().ShouldNotContain(secondCredential);
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("principal")]
    [InlineData("origin")]
    [InlineData("endpoint")]
    [InlineData("scope")]
    [InlineData("event-default")]
    [InlineData("todo-default")]
    [InlineData("timeout")]
    public void CalendarDiscoveryKey_RelevantConfigurationChangesAreDistinct(string change)
    {
        var context = new CalDavOptions
        {
            BaseUrl = "https://cal.example/server/",
            Username = "principal-a",
            Password = "private-password",
            CalendarHrefs = "https://cal.example/a/",
            DefaultEventCalendarName = "Events",
            DefaultTodoCalendarName = "Tasks",
            RequestTimeout = TimeSpan.FromSeconds(30)
        };
        var generation = CalendarOperationContextGeneration.Create();
        var baseline = CalendarDiscoveryKey.Create(context, generation);

        switch (change)
        {
            case "principal":
                context.Username = "principal-b";
                break;
            case "origin":
                context.BaseUrl = "https://other.example/server/";
                break;
            case "endpoint":
                context.BaseUrl = "https://cal.example/other/";
                break;
            case "scope":
                context.CalendarHrefs = "https://cal.example/b/";
                break;
            case "event-default":
                context.DefaultEventCalendarName = "Archive";
                break;
            case "todo-default":
                context.DefaultTodoCalendarName = "Backlog";
                break;
            case "timeout":
                context.RequestTimeout = TimeSpan.FromSeconds(15);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }

        CalendarDiscoveryKey.Create(context, generation).ShouldNotBe(baseline);
    }

    [Fact]
    public async Task GetCalendarsAsync_RetainsOnlyCompleteInScopeDescriptors()
    {
        const string inScope = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EntityCalendar(inScope, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar(
                "https://cal.example/private/",
                "Private",
                EntityKindSupport.Advertised,
                EntityKindSupport.Advertised)
        ]);
        var sut = Service(client, Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "principal",
            Password = "private-password",
            CalendarHrefs = inScope
        }));

        var first = await sut.GetCalendarsAsync(CancellationToken.None);
        var second = await sut.GetCalendarsAsync(CancellationToken.None);

        first.Items.ShouldHaveSingleItem().Href.ShouldBe(inScope);
        second.Items.ShouldHaveSingleItem().Href.ShouldBe(inScope);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_FreezesCalendarsAndDefaultDecisionsForTheToolCall()
    {
        var evidence = new List<CapabilityEvidence> { new("supported-calendar-component-set", "VEVENT") };
        var calendars = new List<CalendarDescriptor>
        {
            EntityCalendar(
                "https://cal.example/events/",
                "Events",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised) with
            {
                EventEvidence = evidence
            }
        };
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);
        var sut = Discovery(client, Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultEventCalendarName = "Events"
        }));

        var first = await sut.DiscoverAsync(CancellationToken.None);
        calendars.Clear();
        evidence.Clear();
        var second = await sut.DiscoverAsync(CancellationToken.None);

        first.Discovery.Items.ShouldHaveSingleItem().EventEvidence.ShouldHaveSingleItem();
        first.EventDefault.Calendar!.EventEvidence.ShouldHaveSingleItem();
        second.Discovery.Items.ShouldHaveSingleItem().EventEvidence.ShouldHaveSingleItem();
        second.EventDefault.Calendar!.EventEvidence.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetResourceAsync_ReturnsOneRevisionCoherentEventSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/standup.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:Standup\r\nDESCRIPTION:Folded\r\n exactly\r\n\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = QueryClientSubstitute();
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
        result.Snapshot.Diagnostics.ShouldBeEmpty();
        var description = result.Snapshot.CalendarProperties.Single(property => property.Name == "DESCRIPTION");
        description.RawEncodedValue.ShouldBe("Foldedexactly");
        description.OriginalSlice.ShouldBe("DESCRIPTION:Folded\r\n exactly\r\n");
        description.ComponentPath.Select(component => component.Name).ShouldBe(["VCALENDAR", "VEVENT"]);
    }

    [Fact]
    public async Task GetResourceAsync_ReturnsMixedEntityKindsAsOpaqueWithSafeDiagnostic()
    {
        const string calendarHref = "https://cal.example/mixed/";
        const string resourceHref = "https://cal.example/mixed/mixed.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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
    public async Task ResolveDefaultCalendarAsync_AmbiguousCaseInsensitiveNameReturnsCompleteAuthorizedCalendarEvidence()
    {
        var client = QueryClientSubstitute();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = "https://cal.example/work/,https://cal.example/archive/",
                DefaultTodoCalendarName = "WORK"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar("https://cal.example/work/", " Work ", EntityKindSupport.Advertised, EntityKindSupport.Unknown),
            EntityCalendar("https://cal.example/archive/", "work", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(CalendarSelectionCode.Ambiguous);
        result.Candidates.Select(candidate => (
            candidate.DisplayName,
            candidate.Href,
            candidate.EventSupport,
            candidate.TodoSupport)).ShouldBe([
                ("work", "https://cal.example/archive/", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised),
                (" Work ", "https://cal.example/work/", EntityKindSupport.Advertised, EntityKindSupport.Unknown)
            ]);
    }

    [Fact]
    public async Task GetCalendarsAsync_AppliesExactScopeAndPreservesDiscoveryEvidence()
    {
        var client = QueryClientSubstitute();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/a/,https://cal.example/missing/,https://cal.example/a/"
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
            .ShouldBe(["duplicate_calendar_href", "calendar_href_not_found"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_WithoutConfiguredScope_ReturnsAllUniqueCalendarsInCanonicalOrder()
    {
        var client = QueryClientSubstitute();
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
        var client = QueryClientSubstitute();
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

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public async Task GetCalendarsAsync_DeduplicatesAndBoundsDiscoveredCalendars(int calendarCount, bool rejected)
    {
        var client = QueryClientSubstitute();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/000/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, calendarCount)
            .Select(index => Calendar($"https://cal.example/{index:D3}/", $"Calendar {index:D3}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/000/", "Duplicate", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        if (rejected)
        {
            var exception = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() =>
                sut.GetCalendarsAsync(CancellationToken.None));
            exception.CalendarCount.ShouldBe(257);
        }
        else
        {
            var result = await sut.GetCalendarsAsync(CancellationToken.None);
            result.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/000/");
        }
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

    private static CalendarDescriptor EntityCalendar(
        string href,
        string displayName,
        EntityKindSupport eventSupport,
        EntityKindSupport todoSupport) => new()
        {
            Href = href,
            DisplayName = displayName,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = eventSupport,
            TodoSupport = todoSupport
        };

    private static ICalendarClient QueryClientSubstitute() => Substitute.For<ICalendarClient>();

    private static CalendarOperationDiscovery Discovery(
        ICalendarClient client,
        IOptions<CalDavOptions>? options = null)
    {
        var configured = options ?? Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "principal"
        });
        var policy = new CalendarDiscoveryPolicy(configured, Substitute.For<ILogger<CalendarService>>());
        return new CalendarOperationDiscovery(
            new CalendarClientDiscoveryTransport(client),
            configured,
            policy.ApplyScope,
            policy.ResolveDefault);
    }

    private static CalendarService Service(
        ICalendarClient client,
        IOptions<CalDavOptions>? options = null) => new(
            client,
            options ?? Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "principal"
            }),
            Substitute.For<ILogger<CalendarService>>());

}
