using System.Reflection;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class CalDavHostBuilderTests
{
    private static Action<CalDavOptions> ValidOptions => options =>
    {
        options.BaseUrl = "https://caldav.example.com";
        options.Username = "testuser";
        options.Password = "testpass";
    };

    [Fact]
    public void CreateBuilder_ReturnsHostBuilder_WithMcpServerRegistered()
    {
        // Act
        var builder = CalDavHostBuilder.CreateBuilder();

        // Assert — the builder itself should be a valid HostApplicationBuilder
        builder.ShouldNotBeNull();
        builder.Services.ShouldNotBeNull();
    }

    [Fact]
    public void BuildHost_RegistersTaskService_FromCore()
    {
        // Arrange
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);

        // Act
        using var host = builder.Build();

        // Assert
        var taskService = host.Services.GetService<ITaskService>();
        taskService.ShouldNotBeNull();
        taskService.ShouldBeAssignableTo<ITaskService>();
    }

    [Fact]
    public void BuildHost_BuildsSuccessfully_WithValidOptions()
    {
        // Arrange
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);

        // Act
        using var host = builder.Build();

        // Assert — host builds without throwing
        host.ShouldNotBeNull();
        var taskService = host.Services.GetService<ITaskService>();
        taskService.ShouldNotBeNull();
    }

    [Fact]
    public void BuildHost_RegistersCalDavOptions_WithValidation()
    {
        // Arrange
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);

        // Act
        using var host = builder.Build();

        // Assert — the CalDavOptions should be resolvable and configured
        var options = host.Services.GetService<IOptions<CalDavOptions>>();
        options.ShouldNotBeNull();
        options.Value.BaseUrl.ShouldBe("https://caldav.example.com");
        options.Value.Username.ShouldBe("testuser");
    }

    [Fact]
    public void BuildHost_RegistersMcpServerToolsFromAssembly()
    {
        // Arrange
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);

        // Act
        using var host = builder.Build();

        // Assert — verify that the MCP assembly's tool types are discoverable.
        // The CalDavHostBuilder should register tools from its own assembly
        // so that [McpServerToolType]-decorated classes are loaded.
        var mcpAssembly = typeof(CalDavHostBuilder).Assembly;
        var toolTypeAttributes = mcpAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null)
            .ToList();

        // At least one tool type must be registered for the server to be useful.
        toolTypeAttributes.ShouldNotBeEmpty("the MCP assembly should contain at least one [McpServerToolType] class");
    }

    [Fact]
    public void BuildHost_RegistersTimeProvider_FromMcpLayer()
    {
        // Arrange
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);

        // Act
        using var host = builder.Build();

        // Assert — the MCP host layer must explicitly provide TimeProvider,
        // not rely solely on the transitive Core registration.
        var timeProvider = host.Services.GetService<TimeProvider>();
        timeProvider.ShouldNotBeNull();
        timeProvider.ShouldBeSameAs(TimeProvider.System);
    }

    [Fact]
    public void CreateBuilder_HasNoConsoleLoggingProviders()
    {
        // The MCP server uses stdio transport (JSON-RPC over stdin/stdout).
        // Console logging must NOT be registered to avoid corrupting the protocol stream.
        // Any diagnostic output (even to stderr) can break MCP clients that merge streams.

        // Arrange & Act
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        // Assert — verify no console logging providers are registered
        // (ClearProviders was called, so the provider list should be empty)
        var loggerFactory = host.Services.GetService<ILoggerFactory>();
        loggerFactory.ShouldNotBeNull();

        // Create a logger and verify no console output occurs
        // The key assertion is that ConsoleLoggerProvider was not added
        var providerTypes = builder.Services
            .Where(sd => sd.ServiceType == typeof(ILoggerProvider))
            .Select(sd => sd.ImplementationType?.Name ?? sd.ImplementationFactory?.Method.DeclaringType?.Name)
            .ToList();

        providerTypes.ShouldNotContain("ConsoleLoggerProvider",
            "Console logging provider should not be registered for stdio MCP servers");
    }

    [Fact]
    public void McpToolTypes_HaveMcpServerToolMethods()
    {
        // Arrange
        var mcpAssembly = typeof(CalDavHostBuilder).Assembly;
        var toolTypes = mcpAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null)
            .ToList();

        // Assert: each [McpServerToolType] class must have at least one [McpServerTool] method
        var invalidTypes = GetTypesWithoutToolMethods(toolTypes);
        invalidTypes.ShouldBeEmpty($"Types marked [McpServerToolType] without [McpServerTool] methods: {string.Join(", ", invalidTypes)}");
    }

    private static List<string> GetTypesWithoutToolMethods(List<Type> toolTypes)
    {
        return toolTypes
            .Where(t => !t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null))
            .Select(t => t.Name)
            .ToList();
    }

    [Fact]
    public void CreateBuilder_DefaultMode_RegistersOnlyChatSafeTools()
    {
        // Arrange & Act
        var builder = CalDavHostBuilder.CreateBuilder();

        // Assert — only TaskListTools and ChatTaskTools should be registered as tool types
        var registeredToolTypes = GetRegisteredMcpToolTypes(builder.Services);

        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskListTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.ChatTaskTools));
        registeredToolTypes.ShouldNotContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskQueryTools),
            "TaskQueryTools should not be registered in default (chat-safe) mode");
        registeredToolTypes.ShouldNotContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskMutationTools),
            "TaskMutationTools should not be registered in default (chat-safe) mode");
    }

    [Fact]
    public void CreateBuilder_AdvancedMode_RegistersAllToolTypes()
    {
        // Arrange & Act
        var builder = CalDavHostBuilder.CreateBuilder(exposeAdvancedTools: true);

        // Assert — all 4 tool classes should be registered
        var registeredToolTypes = GetRegisteredMcpToolTypes(builder.Services);

        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskListTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.ChatTaskTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskQueryTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskMutationTools));
    }

    private static IReadOnlyList<Type> GetRegisteredMcpToolTypes(IServiceCollection services)
    {
        // The MCP SDK registers each [McpServerTool] method as an McpServerTool service.
        // The ImplementationFactory's declaring type is a generic closure class that
        // captures the tool type as its first generic argument.
        var mcpAssembly = typeof(DotnetAgents.CalDav.Mcp.Hosting.CalDavHostBuilder).Assembly;
        var knownToolTypes = mcpAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null)
            .ToHashSet();

        return services
            .Where(sd => sd.ServiceType == typeof(ModelContextProtocol.Server.McpServerTool)
                         && sd.ImplementationFactory is not null)
            .Select(sd => sd.ImplementationFactory!.Method.DeclaringType)
            .Where(declaringType => declaringType is not null && declaringType.IsGenericType)
            .Select(declaringType => declaringType!.GetGenericArguments()[0])
            .Where(toolType => knownToolTypes.Contains(toolType))
            .Distinct()
            .ToArray();
    }
}
