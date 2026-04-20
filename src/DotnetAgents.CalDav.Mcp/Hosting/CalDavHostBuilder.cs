using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>
/// Testable startup surface for the MCP stdio host.
/// Encapsulates DI wiring so it can be exercise-tested without top-level statements.
/// </summary>
public sealed class CalDavHostBuilder
{
    /// <summary>
    /// Creates a <see cref="HostApplicationBuilder"/> with MCP server and CalDAV services wired in.
    /// The caller must configure <see cref="CalDavOptions"/> via <see cref="ConfigureCalDav"/>
    /// before calling <see cref="HostApplicationBuilder.Build"/>.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(CalDavHostBuilder).Assembly);

        // TimeProvider abstraction — registered at the host layer so the MCP
        // program explicitly owns the dependency rather than relying on a
        // transitive registration from Core. Core's registration is kept to
        // avoid broadening the diff, but the MCP layer provides it as well.
        builder.Services.AddSingleton(TimeProvider.System);

        return builder;
    }
}

/// <summary>
/// Extension method to configure CalDAV options on the service collection.
/// Separated from <see cref="CalDavHostBuilder"/> so callers control when options are set.
/// </summary>
public static class CalDavHostBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="CalDavOptions"/> with the given configuration action,
    /// including startup validation. This must be called before building the host.
    /// </summary>
    public static IServiceCollection ConfigureCalDav(
        this IServiceCollection services,
        Action<CalDavOptions> configure)
    {
        return services.AddCalDavTasks(configure);
    }
}