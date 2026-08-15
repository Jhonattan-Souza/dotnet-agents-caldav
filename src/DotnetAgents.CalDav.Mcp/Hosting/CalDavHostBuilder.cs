using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// <param name="exposeAdvancedTools">Whether to retain the existing legacy href-based tool surface.</param>
    public static HostApplicationBuilder CreateBuilder(bool exposeAdvancedTools = false)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();

        var mcpBuilder = builder.Services.AddMcpServer(options => options.ProtocolVersion = "2026-07-28")
            .WithStdioServerTransport()
            .WithTools<CalendarTools>()
            .WithTools<TaskListTools>()
            .WithTools<ChatTaskTools>();

        if (exposeAdvancedTools)
        {
            mcpBuilder
                .WithTools<TaskQueryTools>()
                .WithTools<TaskMutationTools>();
        }

        builder.Services.PostConfigure<ModelContextProtocol.Server.McpServerOptions>(ConfigureCalendarToolContract);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTransient<TaskCompletion>();

        return builder;
    }

    private static void ConfigureCalendarToolContract(ModelContextProtocol.Server.McpServerOptions options)
    {
        if (options.ToolCollection is null || !options.ToolCollection.TryGetPrimitive("calendars.list", out var tool))
            return;

        options.ToolCollection = new OrderedToolCollection(options.ToolCollection.ToArray());
        tool.ProtocolTool.InputSchema = CalendarToolContract.GetInputSchema();
        tool.ProtocolTool.OutputSchema = CalendarToolContract.GetOutputSchema();
        tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
        {
            ["cache"] = CalendarToolContract.GetCacheMetadata()
        };
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
