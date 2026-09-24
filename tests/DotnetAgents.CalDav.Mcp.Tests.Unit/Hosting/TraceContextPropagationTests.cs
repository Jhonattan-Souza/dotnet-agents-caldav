using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit.Hosting;

/// <summary>Exercises MCP 2026-07-28 <c>_meta</c> trace context through the real host pipeline and export allowlist.</summary>
[Collection("TelemetryActivityCollection")]
public sealed class TraceContextPropagationTests
{
    private const string CallerTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string CallerSpanId = "00f067aa0ba902b7";
    private const string SampledTraceParent = $"00-{CallerTraceId}-{CallerSpanId}-01";
    private const string PrivateTraceState = "vendor=private-trace-state";
    private const string PrivateBaggage = "user.id=private-baggage-value";

    // An empty entry sends no tracestate; the oversized entry exceeds the W3C tracestate bound.
    public static TheoryData<string> CallerTraceStates => new()
    {
        PrivateTraceState,
        string.Empty,
        "vendor=" + new string('s', 70_000)
    };

    public static TheoryData<string> InvalidTraceContexts => new()
    {
        """{"traceparent":"not-a-trace-context"}""",
        $$"""{"traceparent":"{{new string('0', 70_000)}}"}""",
        $$"""{"traceparent":"00-00000000000000000000000000000000-{{CallerSpanId}}-01"}""",
        $$"""{"traceparent":"00-{{CallerTraceId}}-0000000000000000-01"}""",
        $$"""{"traceparent":"ff-{{CallerTraceId}}-{{CallerSpanId}}-01"}""",
        $$"""{"traceparent":"00-{{CallerTraceId.ToUpperInvariant()}}-{{CallerSpanId}}-01"}""",
        """{"traceparent":42}""",
        $$$"""{"traceparent":{"value":"{{{SampledTraceParent}}}"}}""",
        $$"""{"tracestate":"{{PrivateTraceState}}","baggage":"{{PrivateBaggage}}"}"""
    };

    [Theory]
    [MemberData(nameof(CallerTraceStates))]
    public async Task ToolCall_ContinuesCallerTraceWithoutExportingTraceStateOrBaggage(string traceState)
    {
        var meta = new JsonObject
        {
            ["traceparent"] = SampledTraceParent,
            ["baggage"] = PrivateBaggage
        };
        if (traceState.Length > 0)
            meta["tracestate"] = traceState;
        var baseline = await CallWithoutTelemetryAsync(meta: null);

        var (response, spans) = await CallWithExportAsync(meta);

        response.ShouldBe(baseline);
        var call = spans.Single(span => span.Source.Name == OpenTelemetryHostConfiguration.McpInstrumentationName);
        call.DisplayName.ShouldBe("tools/call todos.query");
        call.Kind.ShouldBe(ActivityKind.Server);
        call.HasRemoteParent.ShouldBeTrue();
        call.TraceId.ToHexString().ShouldBe(CallerTraceId);
        call.ParentSpanId.ToHexString().ShouldBe(CallerSpanId);
        var operation = spans.Single(span => span.OperationName == "caldav.operation");
        operation.TraceId.ShouldBe(call.TraceId);
        operation.ParentSpanId.ShouldBe(call.SpanId);
        operation.GetTagItem("caldav.tool.name").ShouldBe("todos.query");
        spans.ShouldAllBe(span => span.TraceStateString == null && !span.Baggage.Any());
        spans.SelectMany(span => span.TagObjects)
            .Select(tag => tag.Value as string ?? string.Empty)
            .ShouldNotContain(value => value.Contains("private", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidTraceContexts))]
    public async Task ToolCall_IgnoresInvalidTraceContextWithoutChangingTheResult(string metaJson)
    {
        var meta = JsonNode.Parse(metaJson)!.AsObject();
        var baseline = await CallWithoutTelemetryAsync(meta: null);

        var (response, spans) = await CallWithExportAsync(meta);

        response.ShouldBe(baseline);
        (await CallWithoutTelemetryAsync(meta)).ShouldBe(baseline);
        var call = spans.Single(span => span.Source.Name == OpenTelemetryHostConfiguration.McpInstrumentationName);
        call.HasRemoteParent.ShouldBeFalse();
        call.ParentSpanId.ShouldBe(default);
        call.TraceId.ToHexString().ShouldNotBe(CallerTraceId);
        var operation = spans.Single(span => span.OperationName == "caldav.operation");
        operation.TraceId.ShouldBe(call.TraceId);
        operation.ParentSpanId.ShouldBe(call.SpanId);
        spans.ShouldAllBe(span => span.TraceStateString == null && !span.Baggage.Any());
    }

    [Fact]
    public async Task ToolCall_HonorsCallerNotSampledDecisionWithoutChangingTheResult()
    {
        var meta = new JsonObject { ["traceparent"] = $"00-{CallerTraceId}-{CallerSpanId}-00" };
        var baseline = await CallWithoutTelemetryAsync(meta: null);

        var (response, spans) = await CallWithExportAsync(meta);

        response.ShouldBe(baseline);
        spans.ShouldBeEmpty();
    }

    private static async Task<string> CallWithoutTelemetryAsync(JsonObject? meta)
    {
        await using var server = await InProcessServer.StartAsync();
        return await server.CallTodoQueryAsync(meta);
    }

    private static async Task<(string Response, IReadOnlyList<Activity> Spans)> CallWithExportAsync(JsonObject meta)
    {
        var exported = new List<Activity>();
        using (var tracerProvider = Sdk.CreateTracerProviderBuilder()
                   .AddSource(
                       OpenTelemetryHostConfiguration.McpInstrumentationName,
                       OpenTelemetryHostConfiguration.InstrumentationName,
                       OpenTelemetryHostConfiguration.HttpInstrumentationName)
                   .AddProcessor(new TelemetryActivityAllowlistProcessor())
                   .AddProcessor(new SimpleActivityExportProcessor(new CollectingExporter(exported)))
                   .Build())
        {
            string response;
            // The SDK writes the response before it stops the server span, so stop the server first.
            await using (var server = await InProcessServer.StartAsync())
                response = await server.CallTodoQueryAsync(meta);
            tracerProvider.ForceFlush();
            lock (exported)
                return (response, exported.Where(IsToolCallSpan).ToArray());
        }
    }

    private static bool IsToolCallSpan(Activity span) =>
        span.Source.Name != OpenTelemetryHostConfiguration.McpInstrumentationName
        || span.DisplayName.StartsWith("tools/call", StringComparison.Ordinal);

    private sealed class CollectingExporter(List<Activity> exported) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            lock (exported)
            {
                foreach (var activity in batch)
                    exported.Add(activity);
            }
            return ExportResult.Success;
        }
    }

    private sealed class InProcessServer : IAsyncDisposable
    {
        private const int CallId = 2;
        private readonly IHost _host;
        private readonly Pipe _input;
        private readonly StreamReader _output;
        private readonly McpServer _server;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        private InProcessServer(IHost host, Pipe input, Pipe output)
        {
            _host = host;
            _input = input;
            _output = new StreamReader(output.Reader.AsStream(), Encoding.UTF8);
            var transport = new StreamServerTransport(
                input.Reader.AsStream(),
                output.Writer.AsStream(),
                "trace-context-test",
                NullLoggerFactory.Instance);
            _server = McpServer.Create(
                transport,
                host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value,
                NullLoggerFactory.Instance,
                host.Services);
            _run = _server.RunAsync(_stop.Token);
        }

        internal static async Task<InProcessServer> StartAsync()
        {
            var builder = CalDavHostBuilder.CreateBuilder();
            builder.Services.ConfigureCalDav(options =>
            {
                options.BaseUrl = "http://127.0.0.1:1";
                options.Username = "trace-context-user";
                options.Password = "trace-context-password";
            });
            var server = new InProcessServer(builder.Build(), new Pipe(), new Pipe());
            await server.SendAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"trace-context-test","version":"1"}}}""");
            await server.ReadResponseAsync(1);
            await server.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            return server;
        }

        // An empty To-do query fails input validation before any CalDAV work, so the only
        // difference between calls is the request's _meta.
        internal async Task<string> CallTodoQueryAsync(JsonObject? meta)
        {
            var parameters = new JsonObject { ["name"] = "todos.query", ["arguments"] = new JsonObject() };
            if (meta is not null)
                parameters["_meta"] = meta.DeepClone();
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = CallId,
                ["method"] = "tools/call",
                ["params"] = parameters
            }.ToJsonString());
            return await ReadResponseAsync(CallId);
        }

        private async Task SendAsync(string line) =>
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), TestContext.Current.CancellationToken);

        private async Task<string> ReadResponseAsync(int id)
        {
            while (await _output.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var value) && value.GetInt32() == id)
                    return line;
            }
            throw new EndOfStreamException("The in-process MCP server closed before responding.");
        }

        public async ValueTask DisposeAsync()
        {
            await _input.Writer.CompleteAsync();
            await _stop.CancelAsync();
            await Should.NotThrowAsync(() => _run.WaitAsync(TimeSpan.FromSeconds(10)));
            await _server.DisposeAsync();
            _output.Dispose();
            _host.Dispose();
            _stop.Dispose();
        }
    }
}
