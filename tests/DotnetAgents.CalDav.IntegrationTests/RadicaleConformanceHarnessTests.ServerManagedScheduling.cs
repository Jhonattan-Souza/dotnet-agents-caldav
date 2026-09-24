using System.Collections.Concurrent;
using System.Net;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed partial class RadicaleConformanceHarnessTests
{
    // Radicale does not implement RFC 6638, so a handler adds the calendar-auto-schedule class to
    // OPTIONS. The pinned server then stores participation data without sending anything.
    [Fact]
    public async Task Pinned_profile_server_managed_mode_admits_participation_writes_on_advertised_auto_schedule()
    {
        using var probe = CreateProbeClient();
        var calendar = new Uri(fixture.BaseUrl + "/conformance/server-managed-scheduling/", UriKind.Absolute);
        (await CreateCalendarAsync(probe, calendar, "Server Managed Scheduling", "VEVENT"))
            .ShouldBe(HttpStatusCode.Created);
        var trace = new ConcurrentQueue<string>();

        await using (var storageOnly = CreateSchedulingProvider(calendar, CalDavSchedulingModes.StorageOnly, trace))
        {
            var blocked = await CreateMeetingAsync(storageOnly, calendar, "storage-only-meeting");
            blocked.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability, DescribeCreateResult(blocked, trace));
            blocked.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
            trace.ShouldNotContain(entry => entry.StartsWith("PUT:", StringComparison.Ordinal));
            (await SendProbeAsync(probe, HttpMethod.Get, new Uri(calendar, "storage-only-meeting.ics"))).Status
                .ShouldBe(HttpStatusCode.NotFound);
        }

        await using var serverManaged = CreateSchedulingProvider(calendar, CalDavSchedulingModes.ServerManaged, trace);
        var created = await WithSchedulingStateAsync(() => CreateMeetingAsync(serverManaged, calendar, "server-managed-meeting"));
        created.Result.Code.ShouldBe(CalendarEntityCreateCode.Success, DescribeCreateResult(created.Result, trace));
        created.Result.MutationState.ShouldBe(CalendarMutationState.Committed);
        created.SideEffectsPossible.ShouldBeTrue();
        var snapshot = created.Result.Snapshot!;
        System.Text.Encoding.UTF8.GetString(snapshot.AuthoritativeUtf8.Span).ShouldContain("ATTENDEE:mailto:guest@example.test");
        (await SendProbeAsync(probe, HttpMethod.Get, new Uri(snapshot.ResourceHref))).Status
            .ShouldBe(HttpStatusCode.OK);

        var deleted = await WithSchedulingStateAsync(() => serverManaged.GetRequiredService<ICalendarService>()
            .DeleteResourceAsync(
                new CalendarResourceRevisionReference(
                    snapshot.ResourceHref,
                    snapshot.Projection.EntityUid!,
                    CalendarEntityKind.Event,
                    snapshot.EntityTag),
                TestContext.Current.CancellationToken));
        deleted.Result.Code.ShouldBe(CalendarResourceDeleteCode.Success);
        deleted.SideEffectsPossible.ShouldBeTrue();

        var collection = await WithSchedulingStateAsync(() =>
            ReviewAndDeleteCollectionWithModeAsync(serverManaged.GetRequiredService<ICalendarCollectionModule>(), calendar));
        collection.Result.Code.ShouldBe(CalendarCollectionDeleteCode.Success);
        collection.Result.MutationState.ShouldBe(CalendarMutationState.Committed);
        collection.SideEffectsPossible.ShouldBeTrue();
        (await SendProbeAsync(probe, new HttpMethod("PROPFIND"), calendar, null, ("Depth", "0"))).Status
            .ShouldBe(HttpStatusCode.NotFound);
    }

    private ServiceProvider CreateSchedulingProvider(Uri calendar, string mode, ConcurrentQueue<string> trace)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = fixture.BaseUrl;
            options.CalendarHrefs = calendar.AbsoluteUri;
            options.Username = ConformanceUsername;
            options.Password = ConformancePassword;
            options.InteroperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8;
            options.SchedulingMode = mode;
        });
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new SafeRequestTraceFilter(trace));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ServerManagedSchedulingAdvertisementFilter());
        return services.BuildServiceProvider();
    }

    private static Task<CalendarEntityCreateResult> CreateMeetingAsync(
        IServiceProvider provider,
        Uri calendar,
        string uid) => provider.GetRequiredService<ICalendarService>().CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Href: calendar.AbsoluteUri)),
                uid,
                new CalendarEventCreateFields(
                    Summary: "Planning",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-09-30T13:00:00Z"),
                    End: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-09-30T14:00:00Z"),
                    StructuredData: new CalendarStructuredData(
                        Organizer: new CalendarNamedUri("mailto:owner@example.test", "Owner", []),
                        Attendees: [new CalendarAttendee("mailto:guest@example.test", [])]))),
            TestContext.Current.CancellationToken);

    private static async Task<CalendarCollectionDeleteResult> ReviewAndDeleteCollectionWithModeAsync(
        ICalendarCollectionModule module,
        Uri calendar)
    {
        var request = new CalendarCollectionDeleteRequest(calendar.AbsoluteUri);
        var review = await module.ReviewDeleteAsync(request, TestContext.Current.CancellationToken);
        review.Outcome.ShouldBeNull();
        return await module.ExecuteConfirmedDeleteAsync(request, review.Binding!, TestContext.Current.CancellationToken);
    }

    private static async Task<(T Result, bool SideEffectsPossible)> WithSchedulingStateAsync<T>(Func<Task<T>> operation)
    {
        var state = CalendarOperationProgress.CreateState();
        using var scope = CalendarOperationProgress.Attach(state);
        var result = await operation();
        return (result, state.SchedulingSideEffectsPossible);
    }

    /// <summary>Makes the pinned server advertise RFC 6638 automatic scheduling to the scheduling lock.</summary>
    private sealed class ServerManagedSchedulingAdvertisementFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(0, new AdvertisementHandler());
        };

        private sealed class AdvertisementHandler : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (request.Method == HttpMethod.Options)
                    response.Headers.TryAddWithoutValidation("DAV", "calendar-auto-schedule");
                return response;
            }
        }
    }
}
