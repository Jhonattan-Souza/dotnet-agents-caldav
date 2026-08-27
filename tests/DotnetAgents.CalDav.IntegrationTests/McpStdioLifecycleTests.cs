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
    [Fact]
    public async Task DisposeAsync_StopsBuiltServerWithinConfiguredBoundWithoutOrphan()
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
        var disposed = false;

        try
        {
            client = await McpStdioClientFactory.ConnectAsync(
                launch,
                options,
                loggerFactory,
                timeout.Token);
            var processId = await processLogger.ProcessId.Task.WaitAsync(timeout.Token);
            using var serverProcess = Process.GetProcessById(processId);
            var tools = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);

            tools.Tools.ShouldNotBeEmpty();
            var startedAt = Stopwatch.GetTimestamp();
            await client.DisposeAsync();
            disposed = true;
            var disposalDuration = Stopwatch.GetElapsedTime(startedAt);

            disposalDuration.ShouldBeLessThanOrEqualTo(
                McpStdioClientFactory.ShutdownTimeout + TimeSpan.FromSeconds(1));
            serverProcess.HasExited.ShouldBeTrue();
            stderr.ShouldBeEmpty();
        }
        finally
        {
            if (client is not null && !disposed)
                await client.DisposeAsync();
        }
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
