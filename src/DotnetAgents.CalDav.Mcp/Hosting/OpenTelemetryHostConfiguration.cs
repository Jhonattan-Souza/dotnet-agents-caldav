using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class OpenTelemetryHostConfiguration
{
    internal const string DefaultServiceName = "dotnet-agents-caldav";
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    internal const string McpInstrumentationName = "Experimental.ModelContextProtocol";

    internal static bool IsEnabled(Func<string, string?>? environmentProvider = null)
    {
        var getEnvironmentVariable = environmentProvider ?? Environment.GetEnvironmentVariable;
        var endpoint = getEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var sdkDisabled = getEnvironmentVariable("OTEL_SDK_DISABLED");
        return !string.IsNullOrWhiteSpace(endpoint)
            && !string.Equals(sdkDisabled, "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static void Configure(
        HostApplicationBuilder builder,
        Func<string, string?>? environmentProvider = null)
    {
        var getEnvironmentVariable = environmentProvider ?? Environment.GetEnvironmentVariable;
        if (!IsEnabled(getEnvironmentVariable))
            return;

        var serviceName = getEnvironmentVariable("OTEL_SERVICE_NAME");
        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = DefaultServiceName;

        var resource = ResourceBuilder.CreateEmpty()
            .AddService(serviceName)
            .AddTelemetrySdk();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .Clear()
                .AddService(serviceName)
                .AddTelemetrySdk())
            .WithTracing(tracing => tracing
                .AddSource(McpInstrumentationName, InstrumentationName)
                .AddHttpClientInstrumentation(options => options.RecordException = false)
                .AddProcessor(new TelemetryActivityAllowlistProcessor())
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(McpInstrumentationName, InstrumentationName)
                .AddView("mcp.server.operation.duration", new MetricStreamConfiguration
                {
                    TagKeys = ["mcp.method.name", "gen_ai.tool.name", "rpc.response.status_code"]
                })
                .AddView("mcp.server.session.duration", new MetricStreamConfiguration
                {
                    TagKeys = ["mcp.protocol.version", "network.protocol.name", "network.transport"]
                })
                .AddOtlpExporter());

        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(IsSafeLog);
        builder.Logging.AddOpenTelemetry(logging => logging
            .SetResourceBuilder(resource)
            .AddProcessor(new TelemetryLogAllowlistProcessor())
            .AddOtlpExporter());
    }

    internal static bool IsSafeLog(string? category, LogLevel level) =>
        category?.StartsWith("DotnetAgents.CalDav", StringComparison.Ordinal) == true
            ? level >= LogLevel.Debug
            : category?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true
                && level >= LogLevel.Information;
}
