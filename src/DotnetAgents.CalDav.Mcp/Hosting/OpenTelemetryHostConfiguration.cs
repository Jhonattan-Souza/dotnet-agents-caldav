using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
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
    internal const string HttpInstrumentationName = "DotnetAgents.CalDav.Http";
    internal const int ExporterTimeoutMilliseconds = 250;

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
                .AddSource(McpInstrumentationName, InstrumentationName, HttpInstrumentationName)
                .AddProcessor(new TelemetryActivityAllowlistProcessor())
                .AddOtlpExporter(ConfigureExporter))
            .WithMetrics(metrics => metrics
                .AddMeter(McpInstrumentationName, InstrumentationName)
                .AddView("mcp.server.operation.duration", new MetricStreamConfiguration
                {
                    TagKeys = ["rpc.response.status_code"]
                })
                .AddView("mcp.server.session.duration", new MetricStreamConfiguration
                {
                    TagKeys = ["mcp.protocol.version", "network.protocol.name", "network.transport"]
                })
                .AddOtlpExporter(ConfigureExporter));

        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(IsSafeLog);
        builder.Logging.AddOpenTelemetry(logging => logging
            .SetResourceBuilder(resource)
            .AddProcessor(new TelemetryLogAllowlistProcessor())
            .AddOtlpExporter(ConfigureExporter));
    }

    internal static bool IsSafeLog(string? category, LogLevel level) =>
        category?.StartsWith("DotnetAgents.CalDav", StringComparison.Ordinal) == true
            ? level >= LogLevel.Debug
            : category?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true
                && level >= LogLevel.Information;

    private static void ConfigureExporter(OtlpExporterOptions options) =>
        options.TimeoutMilliseconds = Math.Min(
            options.TimeoutMilliseconds,
            ExporterTimeoutMilliseconds);
}
