using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

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
    /// <param name="exposeAdvancedTools">
    /// When <c>true</c>, registers all tool classes including <see cref="TaskQueryTools"/> and
    /// <see cref="TaskMutationTools"/> (href-based, advanced tools). When <c>false</c> (default),
    /// registers only chat-safe tools: <see cref="TaskListTools"/> and <see cref="ChatTaskTools"/>.
    /// </param>
    public static HostApplicationBuilder CreateBuilder(bool exposeAdvancedTools = false)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);

        var mcpBuilder = builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TaskListTools>()
            .WithTools<ChatTaskTools>();

        if (exposeAdvancedTools)
        {
            mcpBuilder
                .WithTools<TaskQueryTools>()
                .WithTools<TaskMutationTools>();
        }

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
