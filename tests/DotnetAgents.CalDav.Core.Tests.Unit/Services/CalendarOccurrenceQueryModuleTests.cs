using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarOccurrenceQueryModuleTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(3);

    [Fact]
    public void StartExecutorsDependOnNeutralAcquisitionAndTemporalCollaborators()
    {
        var constructorTypes = typeof(CalendarOccurrenceQueryStartExecutor).GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .ShouldHaveSingleItem().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        constructorTypes.ShouldContain(typeof(CalendarQueryAcquisitionExecutor));
        constructorTypes.ShouldContain(typeof(CalendarTemporalContextResolver));
        constructorTypes.ShouldNotContain(typeof(CalendarEntityQueryStartExecutor));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task CancelledOccurrenceFollowsExplicitInclusionPolicy(bool includeCancelled, int expectedCount)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var href = calendarHref + "cancelled.ics";
        var transport = new OccurrenceTransport(calendarHref, [href], _ => CancelledEvent());
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(new CalendarOccurrenceQuery(
                CalendarEntityScope.All,
                new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
                "UTC",
                includeCancelled)),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        page.Value.Items.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public async Task StartFreezesTotalOrderAndContinuePerformsNoRemoteWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = new[] { calendarHref + "z.ics", calendarHref + "a.ics" };
        var transport = new OccurrenceTransport(calendarHref, resourceHrefs);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var first = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), PageSize: 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var workAfterStart = transport.TotalCalls;
        var second = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(first.Value.NextCursor!, PageSize: 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        transport.TotalCalls.ShouldBe(workAfterStart);
        Href(first).ShouldBe(resourceHrefs[1]);
        Href(second).ShouldBe(resourceHrefs[0]);
        first.Value.PaginationMode.ShouldBe("query_result_snapshot");
        first.Value.TemporalEvaluationContext.ShouldBe(
            new TemporalEvaluationContext("America/New_York", TemporalEvaluationContextSource.Caller));
        second.Value.TemporalEvaluationContext.ShouldBe(first.Value.TemporalEvaluationContext);
    }

    private static CalendarOccurrenceQuery Query() => new(
        CalendarEntityScope.All,
        From,
        To,
        "America/New_York");

    private static string? Href(QueryReply<CalendarOccurrenceQueryItem>.Page reply) => reply.Value.Items
        .ShouldHaveSingleItem().Value.GetProperty("snapshot").GetProperty("resourceRevision")
        .GetProperty("href").GetString();

    private static ServiceProvider CreateProvider(ICalendarQueryTransport transport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
            options.EvaluationTimeZone = "UTC";
        });
        services.AddSingleton(Substitute.For<ICalendarClient>());
        services.AddSingleton(transport);
        return services.BuildServiceProvider();
    }

    private static ReadOnlyMemory<byte> CancelledEvent() => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:cancelled-event\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nDTEND:20260824T130000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:cancelled-event\r\nDTSTAMP:20260823T120000Z\r\nRECURRENCE-ID:20260825T120000Z\r\n"
        + "DTSTART:20260825T120000Z\r\nDTEND:20260825T130000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
        + "END:VCALENDAR\r\n");

    private sealed class OccurrenceTransport(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        Func<string, ReadOnlyMemory<byte>>? body = null)
        : ICalendarQueryTransport
    {
        internal int TotalCalls { get; private set; }

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            var calendar = new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            };
            return Task.FromResult(new CalendarQueryDiscovery(
                new CalendarDiscoveryResult([calendar], []),
                CalendarSelectionResult.Success(calendar),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)));
        }

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string candidateCalendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult(resourceHrefs);
        }

        public Task<CalendarMultigetResult> MultigetAsync(
            string candidateCalendarHref,
            IReadOnlyList<string> requestedHrefs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources(requestedHrefs
                .Select(href => CalendarResourceRead.Success(href, "\"r1\"", body?.Invoke(href) ?? Event(href)))
                .ToArray()));
        }

        public Task<CalendarResourceRead> GetAsync(
            string candidateCalendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        private static ReadOnlyMemory<byte> Event(string href) => Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
            + $"BEGIN:VEVENT\r\nUID:{Uri.EscapeDataString(href)}\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260824T120000Z\r\nDTEND:20260824T130000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
    }
}
