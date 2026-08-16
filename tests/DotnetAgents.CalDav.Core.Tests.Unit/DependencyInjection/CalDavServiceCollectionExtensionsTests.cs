using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.DependencyInjection;

public sealed class CalDavServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCalDavTasks_DisablesAutomaticRedirectsOnTheConfiguredHandler()
    {
        SocketsHttpHandler? handler = null;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CapturingHandlerFilter(candidate => handler = candidate as SocketsHttpHandler));
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ICalDavClient>();

        handler.ShouldNotBeNull();
        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task AddCalDavTasks_RetriesTransientReadsAtMostThreeTotalAttempts()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.GetCalendarResourceAsync(
            "https://cal.example/events/a.ics", CancellationToken.None));

        handler.RequestCount.ShouldBe(3);
        handler.Methods.ShouldAllBe(method => method == HttpMethod.Get);
    }

    [Fact]
    public async Task AddCalDavTasks_RetriesTransientCalDavReportAtMostThreeTotalAttempts()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.QueryCalendarResourceHrefsAsync(
            "https://cal.example/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        handler.RequestCount.ShouldBe(3);
        handler.Methods.ShouldAllBe(method => method.Method == "REPORT");
    }

    [Fact]
    public async Task AddCalDavTasks_RetriesTransientCalDavPropFindAtMostThreeTotalAttempts()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.GetCalendarsAsync(CancellationToken.None));

        handler.RequestCount.ShouldBe(3);
        handler.Methods.ShouldAllBe(method => method.Method == "PROPFIND");
    }

    [Fact]
    public async Task AddCalDavTasks_DoesNotRetryMutations()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalDavClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.DeleteTaskAsync(
            "https://cal.example/tasks/a.ics", "r1", CancellationToken.None));

        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddCalDavTasks_CallerCancellationStopsReadWithoutRetry()
    {
        var handler = new CancelingHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => client.GetCalendarResourceAsync(
            "https://cal.example/events/a.ics", cancellation.Token));

        handler.RequestCount.ShouldBeLessThanOrEqualTo(1);
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        services.AddHttpClient<CalDavClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class CapturingHandlerFilter(Action<HttpMessageHandler> capture) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            capture(builder.PrimaryHandler);
        };
    }

    private sealed class CountingUnavailableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class CancelingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await pending.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
