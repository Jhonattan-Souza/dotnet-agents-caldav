using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

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
    /// <param name="exposeExactTools">Whether to expose protected exact resource reads.</param>
    public static HostApplicationBuilder CreateBuilder(bool exposeAdvancedTools = false, bool exposeExactTools = false)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();

        var mcpBuilder = builder.Services.AddMcpServer(options => options.ProtocolVersion = "2026-07-28")
            .WithStdioServerTransport()
            .WithMessageFilters(filters => filters.AddIncomingFilter(StrictToolInputGuard.Incoming))
            .WithRequestFilters(filters => filters.AddCallToolFilter(StrictToolInputGuard.CallTool))
            .WithTools<CalendarTools>()
            .WithTools<CalendarEntityTools>()
            .WithTools<CalendarOccurrenceTools>()
            .WithTools<CalendarResourceTools>()
            .WithTools<CalendarEntityCreateTools>()
            .WithTools<TaskListTools>()
            .WithTools<ChatTaskTools>();

        if (exposeAdvancedTools)
        {
            mcpBuilder
                .WithTools<TaskQueryTools>()
                .WithTools<TaskMutationTools>();
        }

        if (exposeExactTools)
        {
            mcpBuilder
                .WithTools<ExactCalendarResourceTools>()
                .WithListResourcesHandler((_, _) => ValueTask.FromResult(ExactCalendarResourceHandler.List()))
                .WithReadResourceHandler(async (request, cancellationToken) =>
                {
                    var service = request.Services!.GetRequiredService<DotnetAgents.CalDav.Core.Abstractions.ICalendarService>();
                    return await ExactCalendarResourceHandler.ReadAsync(
                        request.Params!.Uri,
                        service,
                        cancellationToken).ConfigureAwait(false);
                });
        }

        builder.Services.PostConfigure<ModelContextProtocol.Server.McpServerOptions>(ConfigureCalendarToolContract);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CalendarMutationAdmission>();
        builder.Services.AddSingleton<CalendarEntityCursorProtector>();
        builder.Services.AddTransient(serviceProvider => new CalendarEntityTools(
            serviceProvider.GetRequiredService<DotnetAgents.CalDav.Core.Abstractions.ICalendarService>(),
            serviceProvider.GetRequiredService<CalendarEntityCursorProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddTransient(serviceProvider => new CalendarOccurrenceTools(
            serviceProvider.GetRequiredService<DotnetAgents.CalDav.Core.Abstractions.ICalendarService>(),
            serviceProvider.GetRequiredService<CalendarEntityCursorProtector>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        builder.Services.AddTransient<TaskCompletion>();

        return builder;
    }

    private static void ConfigureCalendarToolContract(ModelContextProtocol.Server.McpServerOptions options)
    {
        if (options.ToolCollection is null)
            return;

        options.ToolCollection = new OrderedToolCollection(options.ToolCollection.ToArray());
        ConfigureTool(options.ToolCollection, "calendars.list");
        ConfigureTool(options.ToolCollection, "calendar_entities.query");
        ConfigureTool(options.ToolCollection, "calendar_occurrences.query");
        ConfigureTool(options.ToolCollection, "calendar_resources.get");
        ConfigureTool(options.ToolCollection, "events.create");
        ConfigureTool(options.ToolCollection, "todos.create");
        ConfigureTool(options.ToolCollection, "calendar_resources.exact_get");
    }

    private static void ConfigureTool(
        ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool> tools,
        string toolName)
    {
        if (!tools.TryGetPrimitive(toolName, out var tool))
            return;
        tool.ProtocolTool.InputSchema = CalendarToolContract.GetInputSchema(toolName);
        tool.ProtocolTool.OutputSchema = CalendarToolContract.GetOutputSchema(toolName);
        tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
        {
            ["cache"] = CalendarToolContract.GetCacheMetadata(toolName)
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
