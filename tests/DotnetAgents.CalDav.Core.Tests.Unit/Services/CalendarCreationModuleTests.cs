using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarCreationModuleTests
{
    [Fact]
    public async Task ClosedCreateCommandsUseOnlyTheConstantWorkTransportPort()
    {
        const string calendarHref = "https://cal.example/mixed/";
        var calendar = new CalendarDescriptor
        {
            Href = calendarHref,
            DisplayName = "Mixed",
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.Advertised,
            TodoSupport = EntityKindSupport.Advertised
        };
        var transport = new ScriptedCalendarCreateTransport(calendar, unrelatedResourceCount: 5_001);
        var identities = Substitute.For<ICalendarEntityIdentityGenerator>();
        identities.CreateUid().Returns("generated-event", "generated-todo");
        var module = new CalendarCreationModule(
            transport,
            new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Mixed",
                DefaultTodoCalendarName = "Mixed"
            },
            TimeProvider.System,
            identities,
            calendars => new CalendarDiscoveryResult(calendars, []),
            (
                CalendarEntityKind kind,
                IReadOnlyList<CalendarDescriptor> discovered,
                IReadOnlyList<CalendarDescriptor> scoped) => CalendarSelectionResult.Success(scoped.Single()));

        var eventOutcome = await module.CreateAsync(
            new CalendarCreationCommand.Event(new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                null,
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-18T13:00:00Z")))),
            CancellationToken.None);
        var todoOutcome = await module.CreateAsync(
            new CalendarCreationCommand.Todo(new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                null,
                new CalendarTodoCreateFields(Summary: "Constant work"))),
            CancellationToken.None);
        var exactRequest = new CalendarExactCreateRequest(
            calendarHref + "exact.ics",
            ExactEvent("exact-create"));
        var exactReview = await module.ReviewExactAsync(
            new ExactCreateIntent(exactRequest),
            CancellationToken.None);
        var exactOutcome = await module.CreateAsync(
            new CalendarCreationCommand.Exact(exactReview.ReviewedCreate!),
            CancellationToken.None);

        eventOutcome.ShouldBeOfType<CalendarCreationOutcome.Semantic>()
            .Result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        todoOutcome.ShouldBeOfType<CalendarCreationOutcome.Semantic>()
            .Result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        exactReview.Outcome.ShouldBeNull();
        exactOutcome.ShouldBeOfType<CalendarCreationOutcome.Exact>()
            .Result.Code.ShouldBe(CalendarExactResourceCode.Success);
        transport.UnrelatedResourceCount.ShouldBe(5_001);
        transport.DiscoveryCount.ShouldBe(3);
        transport.DirectReadCount.ShouldBe(4);
        transport.ConditionalPutCount.ShouldBe(3);
    }

    private static byte[] ExactEvent(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Creation Module Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private sealed class ScriptedCalendarCreateTransport(
        CalendarDescriptor calendar,
        int unrelatedResourceCount) : ICalendarCreateTransport
    {
        private readonly Dictionary<string, ReadOnlyMemory<byte>> _resources = new(StringComparer.Ordinal);

        public int UnrelatedResourceCount { get; } = unrelatedResourceCount;

        public int DiscoveryCount { get; private set; }

        public int DirectReadCount { get; private set; }

        public int ConditionalPutCount { get; private set; }

        public Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken)
        {
            DiscoveryCount++;
            return Task.FromResult<IReadOnlyList<CalendarDescriptor>>([calendar]);
        }

        public Task<CalendarResourceRead> GetCalendarResourceAsync(
            string href,
            CancellationToken cancellationToken)
        {
            DirectReadCount++;
            return Task.FromResult(_resources.TryGetValue(href, out var authoritativeUtf8)
                ? CalendarResourceRead.Success(href, $"\"r{DirectReadCount}\"", authoritativeUtf8)
                : new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        }

        public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
            CalendarResourceCreateRequest request,
            CancellationToken cancellationToken)
        {
            ConditionalPutCount++;
            if (!_resources.TryAdd(request.ResourceHref, request.AuthoritativeUtf8))
            {
                return Task.FromResult(new CalendarResourceCreateResult(
                    CalendarResourceCreateCode.DestinationConflict,
                    request.ResourceHref));
            }
            return Task.FromResult(CalendarResourceCreateResult.Dispatched(request.ResourceHref));
        }
    }
}
