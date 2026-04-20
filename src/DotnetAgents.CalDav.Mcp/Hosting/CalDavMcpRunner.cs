using DotnetAgents.CalDav.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>
/// Testable startup surface for the MCP stdio host.
/// Catches <see cref="OptionsValidationException"/> at startup and produces
/// a clean, human-readable error instead of an unhandled raw exception.
/// </summary>
public sealed class CalDavMcpRunner
{
    private readonly TextWriter _errorOutput;

    /// <param name="errorOutput">
    /// Where to write error diagnostics. Defaults to <see cref="Console.Error"/>.
    /// Inject a <see cref="StringWriter"/> in tests to assert on output.
    /// </param>
    public CalDavMcpRunner(TextWriter? errorOutput = null)
    {
        _errorOutput = errorOutput ?? Console.Error;
    }

    /// <summary>
    /// Builds and runs the MCP stdio host with the given CalDAV configuration.
    /// Returns <c>0</c> on graceful exit, <c>1</c> on configuration validation failure.
    /// </summary>
    public async Task<int> RunAsync(Action<CalDavOptions> configure, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunHostAsync(configure, cancellationToken).ConfigureAwait(false);
        }
        catch (AggregateException aggregationEx) when (aggregationEx.InnerExceptions.All(e => e is OptionsValidationException))
        {
            _errorOutput.WriteLine("CalDAV configuration error:");
            foreach (var inner in aggregationEx.InnerExceptions.Cast<OptionsValidationException>())
            foreach (var failure in inner.Failures)
                _errorOutput.WriteLine($"  - {failure}");
            return 1;
        }
        catch (OptionsValidationException ex)
        {
            _errorOutput.WriteLine("CalDAV configuration error:");
            foreach (var failure in ex.Failures)
                _errorOutput.WriteLine($"  - {failure}");
            return 1;
        }
    }

    [ExcludeFromCodeCoverage]
    private static async Task<int> RunHostAsync(Action<CalDavOptions> configure, CancellationToken cancellationToken)
    {
        try
        {
            var exposeAdvancedTools = Environment.GetEnvironmentVariable("CALDAV_EXPOSE_ADVANCED_TOOLS") == "true";
            var builder = CalDavHostBuilder.CreateBuilder(exposeAdvancedTools);
            builder.Services.ConfigureCalDav(configure);
            using var host = builder.Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }
}
