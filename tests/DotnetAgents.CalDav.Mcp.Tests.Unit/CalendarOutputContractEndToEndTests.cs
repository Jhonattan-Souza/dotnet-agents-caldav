using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

/// <summary>Drives the real host filter pipeline over MCP with a handler whose output violates its schema.</summary>
[Collection("TelemetryActivityCollection")]
public sealed class CalendarOutputContractEndToEndTests
{
    private static readonly Dictionary<string, object?> CreateArguments = new()
    {
        ["displayName"] = "Team",
        ["entityKinds"] = new[] { "event" }
    };

    [Fact]
    public async Task CommittedMutationWithInvalidOutput_ReturnsSchemaValidIndeterminateOutcome()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stopped)
                    stopped.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var commits = 0;
        var committedButInvalid = McpServerTool.Create(
            () =>
            {
                Interlocked.Increment(ref commits);
                return new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(new
                    {
                        outcome = "success",
                        mutationState = "committed",
                        calendar = "not-a-calendar-descriptor",
                        diagnostics = Array.Empty<object>()
                    }),
                    Content = []
                };
            },
            new McpServerToolCreateOptions { Name = "calendars.create" });
        await using var harness = await ServerHarness.StartAsync(committedButInvalid);

        var result = await harness.Client.CallToolAsync(
            "calendars.create",
            CreateArguments,
            cancellationToken: TestContext.Current.CancellationToken);

        commits.ShouldBe(1);
        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("indeterminate");
        structured.GetProperty("category").GetString().ShouldBe("postWriteTruth");
        structured.GetProperty("phase").GetString().ShouldBe("postWriteVerificationOrReconciliation");
        structured.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        structured.TryGetProperty("calendar", out _).ShouldBeFalse();
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldBe(structured.GetRawText());
        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate("calendars.create", result));
        Activity violation;
        lock (stopped)
            violation = stopped.Single(activity => activity.OperationName == "caldav.output_contract"
                && Equals(activity.GetTagItem("caldav.tool.name"), "calendars.create"));
        violation.GetTagItem("caldav.tool.name").ShouldBe("calendars.create");
        violation.GetTagItem("caldav.output_contract.violation").ShouldBe("schema_violation");
        violation.GetTagItem("caldav.mutation.state").ShouldBe("committed");
        violation.GetTagItem("caldav.error.code").ShouldBe("indeterminate");
    }

    [Fact]
    public async Task ReadWithInvalidOutput_StillFailsWithoutAStructuredOutcome()
    {
        var invalidRead = McpServerTool.Create(
            () => new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new { outcome = "success", calendars = "invalid" }),
                Content = []
            },
            new McpServerToolCreateOptions { Name = "calendars.list" });
        await using var harness = await ServerHarness.StartAsync(invalidRead);

        var result = await harness.Client.CallToolAsync(
            "calendars.list",
            cancellationToken: TestContext.Current.CancellationToken);

        // No Mutation State can be lost, so the read keeps the SDK's generic, detail-free failure.
        result.IsError.ShouldBe(true);
        result.StructuredContent.ShouldBeNull();
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("schema");
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly Pipe _clientToServer;
        private readonly Pipe _serverToClient;

        private ServerHarness(IHost host, Pipe clientToServer, Pipe serverToClient, McpClient client)
        {
            _host = host;
            _clientToServer = clientToServer;
            _serverToClient = serverToClient;
            Client = client;
        }

        internal McpClient Client { get; }

        internal static async Task<ServerHarness> StartAsync(McpServerTool replacement)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var builder = CalDavHostBuilder.CreateBuilder(environmentProvider: static _ => null);
            builder.Services.ConfigureCalDav(options =>
            {
                options.BaseUrl = "https://caldav.example.com";
                options.Username = "testuser";
                options.Password = "testpass";
            });
            builder.Services.AddMcpServer().WithStreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream());
            // Replace only the handler; the host still applies the catalog contract and every call filter.
            builder.Services.Configure<McpServerOptions>(options =>
            {
                var tools = options.ToolCollection!;
                tools.TryGetPrimitive(replacement.ProtocolTool.Name, out var real).ShouldBeTrue();
                tools.Remove(real!);
                tools.Add(replacement);
            });
            var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);
            return new ServerHarness(host, clientToServer, serverToClient, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _clientToServer.Writer.CompleteAsync();
            await _serverToClient.Writer.CompleteAsync();
            await _host.StopAsync(TestContext.Current.CancellationToken);
            _host.Dispose();
        }
    }
}
