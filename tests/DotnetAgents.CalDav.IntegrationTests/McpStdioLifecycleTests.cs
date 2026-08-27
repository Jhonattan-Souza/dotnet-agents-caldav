using System.Collections.Concurrent;
using System.Diagnostics;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed class McpStdioLifecycleTests
{
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task SdkDefaultShutdownPolicy_ReproducesFiveSecondWaitAgainstBuiltServer()
    {
        McpStdioClientFactory.SdkDefaultShutdownTimeout.ShouldBe(TimeSpan.FromSeconds(5));

        var disposalDuration = await MeasureBuiltServerDisposalAsync(useSdkDefaultShutdown: true);

        disposalDuration.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.5));
        disposalDuration.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(6.5));
    }

    [Fact]
    public async Task DisposeAsync_StopsBuiltServerWithinConfiguredBoundWithoutOrphan()
    {
        var disposalDuration = await MeasureBuiltServerDisposalAsync(useSdkDefaultShutdown: false);

        disposalDuration.ShouldBeLessThanOrEqualTo(
            McpStdioClientFactory.ShutdownTimeout + TimeSpan.FromSeconds(1));
    }

    private static async Task<TimeSpan> MeasureBuiltServerDisposalAsync(bool useSdkDefaultShutdown)
    {
        var stderr = new ConcurrentQueue<string>();
        var processLogger = new ProcessStartedLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(processLogger);
        });
        var launch = McpStdioClientFactory.CreateBuiltServerLaunch(
            new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = "http://127.0.0.1:1/",
                ["CALDAV_USERNAME"] = "lifecycle",
                ["CALDAV_PASSWORD"] = "lifecycle",
                ["CALDAV_CALENDAR_HREFS"] = "http://127.0.0.1:1/calendars/lifecycle/",
                ["CALDAV_EXPOSE_EXACT_TOOLS"] = "false"
            },
            stderr.Enqueue);
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        McpClient? client = null;
        Process? serverProcess = null;
        Task? disposalTask = null;
        var disposed = false;

        try
        {
            client = useSdkDefaultShutdown
                ? await McpStdioClientFactory.ConnectWithSdkDefaultShutdownAsync(
                    launch,
                    options,
                    loggerFactory,
                    timeout.Token)
                : await McpStdioClientFactory.ConnectAsync(
                    launch,
                    options,
                    loggerFactory,
                    timeout.Token);
            var processId = await processLogger.ProcessId.Task.WaitAsync(timeout.Token);
            serverProcess = Process.GetProcessById(processId);
            var tools = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);

            tools.Tools.ShouldNotBeEmpty();
            serverProcess.HasExited.ShouldBeFalse(
                "a crash before client disposal must not be accepted as successful shutdown");
            var startedAt = Stopwatch.GetTimestamp();
            disposalTask = client.DisposeAsync().AsTask();
            await AwaitDisposalWithFallbackAsync(
                disposalTask,
                serverProcess,
                (useSdkDefaultShutdown
                    ? McpStdioClientFactory.SdkDefaultShutdownTimeout
                    : McpStdioClientFactory.ShutdownTimeout) + CleanupBound,
                timeout.Token);
            disposed = true;
            var disposalDuration = Stopwatch.GetElapsedTime(startedAt);

            serverProcess.HasExited.ShouldBeTrue();
            stderr.ShouldBeEmpty();
            return disposalDuration;
        }
        finally
        {
            if (client is not null && !disposed)
            {
                disposalTask ??= client.DisposeAsync().AsTask();
                await ForceCleanupAsync(disposalTask, serverProcess);
            }
            serverProcess?.Dispose();
        }
    }

    private static async Task AwaitDisposalWithFallbackAsync(
        Task disposalTask,
        Process serverProcess,
        TimeSpan bound,
        CancellationToken cancellationToken)
    {
        try
        {
            await disposalTask.WaitAsync(bound, cancellationToken);
        }
        catch (Exception disposalError) when (disposalError is TimeoutException or OperationCanceledException)
        {
            try
            {
                await ForceCleanupAsync(disposalTask, serverProcess);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(disposalError, cleanupError);
            }
            throw;
        }
    }

    private static async Task ForceCleanupAsync(Task disposalTask, Process? serverProcess)
    {
        if (serverProcess is not null && !serverProcess.HasExited)
            serverProcess.Kill(entireProcessTree: true);
        if (serverProcess is not null)
            await serverProcess.WaitForExitAsync().WaitAsync(CleanupBound);
        await disposalTask.WaitAsync(CleanupBound);
    }

    private sealed class ProcessStartedLoggerProvider : ILoggerProvider
    {
        public TaskCompletionSource<int> ProcessId { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new ProcessStartedLogger(ProcessId);

        public void Dispose()
        {
        }

        private sealed class ProcessStartedLogger(TaskCompletionSource<int> processId) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Name != "LogTransportProcessStarted"
                    || state is not IEnumerable<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                var value = values.FirstOrDefault(pair => pair.Key == "ProcessId").Value;
                if (value is int capturedProcessId)
                    processId.TrySetResult(capturedProcessId);
            }
        }
    }
}
