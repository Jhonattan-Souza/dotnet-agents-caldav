using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit.Hosting;

public sealed class OpenTelemetryHostConfigurationTests
{
    [Fact]
    public void TelemetryContract_UsesStableInstrumentationAndDefaultServiceNames()
    {
        OpenTelemetryHostConfiguration.InstrumentationName.ShouldBe("DotnetAgents.CalDav");
        OpenTelemetryHostConfiguration.McpInstrumentationName.ShouldBe("Experimental.ModelContextProtocol");
        OpenTelemetryHostConfiguration.HttpInstrumentationName.ShouldBe("DotnetAgents.CalDav.Http");
        OpenTelemetryHostConfiguration.DefaultServiceName.ShouldBe("dotnet-agents-caldav");
        OpenTelemetryHostConfiguration.ExporterTimeoutMilliseconds.ShouldBe(250);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318", null, true)]
    [InlineData("http://127.0.0.1:4318", "false", true)]
    [InlineData("http://127.0.0.1:4318", "TRUE", false)]
    [InlineData("", null, false)]
    [InlineData("   ", null, false)]
    [InlineData(null, null, false)]
    public void IsEnabled_RequiresEndpointAndHonorsSdkDisable(
        string? endpoint,
        string? sdkDisabled,
        bool expected)
    {
        var environment = new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
            ["OTEL_SDK_DISABLED"] = sdkDisabled
        };

        OpenTelemetryHostConfiguration.IsEnabled(environment.GetValueOrDefault)
            .ShouldBe(expected);
    }

    [Fact]
    public void IsEnabled_ReadsOnlyStandardOpenTelemetryGateVariables()
    {
        var requestedNames = new List<string>();

        OpenTelemetryHostConfiguration.IsEnabled(name =>
        {
            requestedNames.Add(name);
            return null;
        }).ShouldBeFalse();

        requestedNames.ShouldBe([
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_SDK_DISABLED"]);
    }

    [Fact]
    public void CreateBuilder_WithoutOptIn_RegistersNoTelemetryProviders()
    {
        var builder = CalDavHostBuilder.CreateBuilder(
            environmentProvider: _ => null);

        var registeredTypes = GetRegisteredImplementationTypeNames(builder.Services);

        registeredTypes.ShouldNotContain(type => type.Contains("OpenTelemetry", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBuilder_WithSdkDisabled_RegistersNoTelemetryProviders()
    {
        var environment = new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318",
            ["OTEL_SDK_DISABLED"] = "true"
        };

        var builder = CalDavHostBuilder.CreateBuilder(
            environmentProvider: environment.GetValueOrDefault);

        GetRegisteredImplementationTypeNames(builder.Services)
            .ShouldNotContain(type => type.Contains("OpenTelemetry", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBuilder_WithOptIn_RegistersOtlpPipelinesWithoutConsoleProviders()
    {
        var environment = new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
        };

        var builder = CalDavHostBuilder.CreateBuilder(
            environmentProvider: environment.GetValueOrDefault);

        var registeredTypes = GetRegisteredImplementationTypeNames(builder.Services);
        registeredTypes.ShouldContain(type => type.Contains("TracerProvider", StringComparison.Ordinal));
        registeredTypes.ShouldContain(type => type.Contains("MeterProvider", StringComparison.Ordinal));
        builder.Services.Any(descriptor =>
            descriptor.ServiceType == typeof(ILoggerProvider)
            && (descriptor.ImplementationType?.FullName?.Contains("OpenTelemetry", StringComparison.Ordinal) == true
                || descriptor.ImplementationFactory?.Method.DeclaringType?.FullName?.Contains(
                    "OpenTelemetry",
                    StringComparison.Ordinal) == true)).ShouldBeTrue();
        registeredTypes.ShouldNotContain(type => type.Contains("ConsoleExporter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DotnetAgents.CalDav.Core.Internal.CalDavClient", LogLevel.Debug, true)]
    [InlineData("DotnetAgents.CalDav", LogLevel.Trace, false)]
    [InlineData("ModelContextProtocol.Server", LogLevel.Information, true)]
    [InlineData("ModelContextProtocol.Server", LogLevel.Debug, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Critical, false)]
    [InlineData(null, LogLevel.Critical, false)]
    public void LogFilter_AllowsOnlySafeProductAndMcpLevels(
        string? category,
        LogLevel level,
        bool expected)
    {
        OpenTelemetryHostConfiguration.IsSafeLog(category, level).ShouldBe(expected);
    }

    private static string[] GetRegisteredImplementationTypeNames(IServiceCollection services) => services
        .Select(descriptor => descriptor.ImplementationType?.FullName
            ?? descriptor.ImplementationInstance?.GetType().FullName
            ?? descriptor.ImplementationFactory?.Method.DeclaringType?.FullName)
        .Where(name => name is not null)
        .Select(name => name!)
        .ToArray();
}
