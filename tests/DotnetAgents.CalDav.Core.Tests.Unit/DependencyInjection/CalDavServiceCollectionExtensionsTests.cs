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
    public void AddCalDavCalendars_DisablesAutomaticRedirectsOnTheConfiguredHandler()
    {
        SocketsHttpHandler? handler = null;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CapturingHandlerFilter(candidate => handler = candidate as SocketsHttpHandler));
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ICalendarClient>();

        handler.ShouldNotBeNull();
        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public void AddCalDavCalendars_RecyclesIdleConnectionsBeforePinnedRadicaleTimeout()
    {
        SocketsHttpHandler? handler = null;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
            new CapturingHandlerFilter(candidate => handler = candidate as SocketsHttpHandler));
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ICalendarClient>();

        handler.ShouldNotBeNull();
        handler.PooledConnectionIdleTimeout.ShouldBe(TimeSpan.FromSeconds(20));
        handler.PooledConnectionIdleTimeout.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task AddCalDavCalendars_RetriesTransientReadsAtMostThreeTotalAttempts()
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
    public async Task AddCalDavCalendars_RetriesTransientCalDavReportAtMostThreeTotalAttempts()
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
    public async Task AddCalDavCalendars_RetriesTransientCalDavPropFindAtMostThreeTotalAttempts()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.GetCalendarsAsync(CancellationToken.None));

        handler.RequestCount.ShouldBe(3);
        handler.Methods.ShouldAllBe(method => method.Method == "PROPFIND");
    }

    [Fact]
    public async Task AddCalDavCalendars_DoesNotRetryMutations()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        var result = await client.DeleteCalendarResourceAsync(
            new CalendarResourceDeleteRequest("https://cal.example/tasks/a.ics", "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteDispatchCode.PossiblyDispatched);
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddCalDavCalendars_DoesNotRetryWebDavMove()
    {
        var handler = new CountingUnavailableHandler();
        using var provider = BuildProvider(handler);
        var client = provider.GetRequiredService<ICalendarClient>();

        var result = await client.MoveCalendarResourceAsync(
            new CalendarResourceMoveDispatchRequest(
                "https://cal.example/tasks/a.ics",
                "https://cal.example/tasks/b.ics",
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceMoveDispatchCode.PossiblyDispatched);
        handler.RequestCount.ShouldBe(1);
        handler.Methods.ShouldHaveSingleItem().Method.ShouldBe("MOVE");
    }

    [Fact]
    public async Task AddCalDavCalendars_CallerCancellationStopsReadWithoutRetry()
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

    [Fact]
    public async Task AddCalDavCalendars_CancelsEachInFlightHttpAttemptAtTenSeconds()
    {
        var handler = new BlockingHandler();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        using var provider = BuildProvider(handler, time);
        var client = provider.GetRequiredService<ICalendarClient>();
        using var cancellation = new CancellationTokenSource();

        var pending = client.GetCalendarResourceAsync(
            "https://cal.example/events/a.ics", cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(9_999));
        handler.CancellationCount.ShouldBe(0);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await handler.FirstCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        handler.CancellationCount.ShouldBe(1);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        if (timeProvider is not null)
            services.AddSingleton(timeProvider);
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _cancellationCount;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CancellationCount => Volatile.Read(ref _cancellationCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await pending.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
            finally
            {
                if (Interlocked.Increment(ref _cancellationCount) == 1)
                    FirstCancellation.TrySetResult();
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            utcNow += amount;
            _timestamp += amount.Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

            internal void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
