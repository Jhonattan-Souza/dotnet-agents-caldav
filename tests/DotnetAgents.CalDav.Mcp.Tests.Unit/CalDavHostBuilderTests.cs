using System.Reflection;
using System.Text.Json.Nodes;
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
    public void CreateBuilder_RegistersTaskCompletionAsTransient()
    {
        var builder = CalDavHostBuilder.CreateBuilder();

        var registration = builder.Services.SingleOrDefault(
            descriptor => descriptor.ServiceType == typeof(TaskCompletion));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Transient);
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
    public void CreateBuilder_DefaultMode_RegistersCalendarDiscoveryAlongsideChatSafeLegacyTools()
    {
        // Arrange & Act
        var builder = CalDavHostBuilder.CreateBuilder();

        var registeredToolTypes = GetRegisteredMcpToolTypes(builder.Services);

        registeredToolTypes.ShouldBe(
        [
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarEntityTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarOccurrenceTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarOccurrenceMutationTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarResourceTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarEntityCreateTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarEntityPatchTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarResourceDeleteTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.TaskListTools),
            typeof(DotnetAgents.CalDav.Mcp.Tools.ChatTaskTools)
        ]);

    }

    [Theory]
    [InlineData("calendar_occurrences.add", false)]
    [InlineData("calendar_occurrences.exclude", true)]
    [InlineData("calendar_occurrences.restore_exclusion", false)]
    [InlineData("calendar_occurrences.cancel", true)]
    [InlineData("calendar_occurrences.restore_cancellation", false)]
    public void BuildHost_AdvertisesFrozenOccurrenceMutationContract(string toolName, bool destructive)
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive(toolName, out var tool).ShouldBeTrue();
        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();

        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["snapshot", "recurrenceIdentity"]);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(3);
        tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.DestructiveHint.ShouldBe(destructive);
        tool.ProtocolTool.Annotations.IdempotentHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.OpenWorldHint.ShouldBe(true);
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(0);
        tool.ProtocolTool.Meta["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Fact]
    public void BuildHost_AdvertisesFrozenResourceOutcomeSchemaAndPrivateNoCacheHint()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendar_resources.get", out var tool).ShouldBeTrue();
        var outputSchema = JsonNode.Parse(tool!.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();

        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(2);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("resourceSuccess");
        outputSchema["$defs"]!.AsObject().ShouldContainKey("errorOutcome");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(0);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Fact]
    public void BuildHost_AdvertisesFrozenEntityQuerySchemasAndPrivateCacheHint()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendar_entities.query", out var tool).ShouldBeTrue();

        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["scope", "entityKinds"]);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(2);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("entityQuerySuccess");
        outputSchema["$defs"]!.AsObject().ShouldContainKey("errorOutcome");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(5000);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Fact]
    public void BuildHost_AdvertisesFrozenOccurrenceQuerySchemasAndPrivateCacheHint()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendar_occurrences.query", out var tool).ShouldBeTrue();

        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["scope", "from", "to"]);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(2);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("occurrenceQuerySuccess");
        outputSchema["$defs"]!.AsObject().ShouldContainKey("errorOutcome");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(5000);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Theory]
    [InlineData("events.create", "eventCreateEntity")]
    [InlineData("todos.create", "todoCreateEntity")]
    public void BuildHost_AdvertisesFrozenCreateSchemasAnnotationsAndPrivateNoCacheHint(
        string toolName,
        string entityDefinition)
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive(toolName, out var tool).ShouldBeTrue();
        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();

        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["destination", "entity"]);
        inputSchema["$defs"]!.AsObject().ShouldContainKey(entityDefinition);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(3);
        tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.DestructiveHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.IdempotentHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.OpenWorldHint.ShouldBe(true);
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(0);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Fact]
    public void BuildHost_ActivatesCalendarEntityCreateToolsThroughTheSdkConstructionPath()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        ActivatorUtilities.CreateInstance<DotnetAgents.CalDav.Mcp.Tools.CalendarEntityCreateTools>(host.Services)
            .ShouldNotBeNull();
    }

    [Fact]
    public void BuildHost_ActivatesCalendarEntityToolsThroughTheSdkConstructionPath()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        host.Services.GetRequiredService<DotnetAgents.CalDav.Mcp.Tools.CalendarEntityTools>()
            .ShouldNotBeNull();
        ActivatorUtilities.CreateInstance<DotnetAgents.CalDav.Mcp.Tools.CalendarEntityTools>(host.Services)
            .ShouldNotBeNull();
    }

    [Fact]
    public void BuildHost_AdvertisesFrozenCalendarListSchemasAndPrivateCacheHint()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendars.list", out var tool).ShouldBeTrue();

        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
        inputSchema["type"]!.GetValue<string>().ShouldBe("object");
        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(2);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("calendarListSuccess");
        inputSchema["$defs"].ShouldBeNull();
        outputSchema["$defs"]!.AsObject().ShouldNotContainKey("deleteInput");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(30000);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
    }

    [Fact]
    public void BuildHost_AdvertisesFrozenRevisionBoundDeleteContract()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendar_resources.delete", out var tool).ShouldBeTrue();

        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>()).ShouldBe(["revision"]);
        inputSchema["$defs"]!["revisionReference"]!["required"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ShouldBe(["href", "entityUid", "entityKind", "entityTag"]);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(3);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("deleteMutationSuccess");
        outputSchema["$defs"]!.AsObject().ShouldContainKey("mutationErrorOutcome");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(0);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
        tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.DestructiveHint.ShouldBe(true);
        tool.ProtocolTool.Annotations.IdempotentHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.OpenWorldHint.ShouldBe(true);
    }

    [Fact]
    public void BuildHost_DefaultMode_ListsToolsInCanonicalWireOrder()
    {
        var builder = CalDavHostBuilder.CreateBuilder();
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var tools = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value
            .ToolCollection!
            .ToArray()
            .Select(tool => tool.ProtocolTool.Name);

        tools.ShouldBe(
        [
            "calendars.list",
            "calendar_entities.query",
            "calendar_occurrences.query",
            "calendar_resources.get",
            "events.create",
            "events.patch",
            "todos.create",
            "todos.patch",
            "calendar_occurrences.add",
            "calendar_occurrences.exclude",
            "calendar_occurrences.restore_exclusion",
            "calendar_occurrences.cancel",
            "calendar_occurrences.restore_cancellation",
            "calendar_resources.delete",
            "list_task_lists",
            "show_tasks",
            "add_task",
            "find_tasks",
            "complete_task_by_summary",
            "delete_task_by_summary"
        ]);
    }

    [Fact]
    public void CreateBuilder_AdvancedMode_KeepsLegacyAdvancedTools()
    {
        var builder = CalDavHostBuilder.CreateBuilder(exposeAdvancedTools: true);

        var registeredToolTypes = GetRegisteredMcpToolTypes(builder.Services);

        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.CalendarTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskQueryTools));
        registeredToolTypes.ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.TaskMutationTools));
    }

    [Fact]
    public void BuildHost_AdvancedMode_ListsRankedToolsThenOrdinalFallback()
    {
        var builder = CalDavHostBuilder.CreateBuilder(exposeAdvancedTools: true);
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();

        var tools = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value
            .ToolCollection!
            .ToArray()
            .Select(tool => tool.ProtocolTool.Name);

        tools.ShouldBe(
        [
            "calendars.list",
            "calendar_entities.query",
            "calendar_occurrences.query",
            "calendar_resources.get",
            "events.create",
            "events.patch",
            "todos.create",
            "todos.patch",
            "calendar_occurrences.add",
            "calendar_occurrences.exclude",
            "calendar_occurrences.restore_exclusion",
            "calendar_occurrences.cancel",
            "calendar_occurrences.restore_cancellation",
            "calendar_resources.delete",
            "list_task_lists",
            "show_tasks",
            "add_task",
            "find_tasks",
            "complete_task_by_summary",
            "delete_task_by_summary",
            "complete_task",
            "create_task",
            "delete_task",
            "get_task",
            "list_tasks",
            "update_task"
        ]);
    }

    [Fact]
    public void CreateBuilder_ExactMode_RegistersExactToolIndependently()
    {
        var defaultBuilder = CalDavHostBuilder.CreateBuilder();
        var exactBuilder = CalDavHostBuilder.CreateBuilder(exposeExactTools: true);

        GetRegisteredMcpToolTypes(defaultBuilder.Services)
            .ShouldNotContain(typeof(DotnetAgents.CalDav.Mcp.Tools.ExactCalendarResourceTools));
        GetRegisteredMcpToolTypes(exactBuilder.Services)
            .ShouldContain(typeof(DotnetAgents.CalDav.Mcp.Tools.ExactCalendarResourceTools));
    }

    [Fact]
    public void BuildHost_ExactMode_AdvertisesFrozenClosedContractAndPrivateNoCacheHint()
    {
        var builder = CalDavHostBuilder.CreateBuilder(exposeExactTools: true);
        builder.Services.ConfigureCalDav(ValidOptions);
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value;
        options.ToolCollection!.TryGetPrimitive("calendar_resources.exact_get", out var tool).ShouldBeTrue();

        var inputSchema = JsonNode.Parse(tool!.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var outputSchema = JsonNode.Parse(tool.ProtocolTool.OutputSchema!.Value.GetRawText())!.AsObject();
        inputSchema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        inputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>()).ShouldBe(["href"]);
        outputSchema["oneOf"]!.AsArray().Count.ShouldBe(2);
        outputSchema["$defs"]!.AsObject().ShouldContainKey("exactGetSuccess");
        outputSchema["$defs"]!.AsObject().ShouldContainKey("errorOutcome");
        tool.ProtocolTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(0);
        tool.ProtocolTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
        tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(true);
        tool.ProtocolTool.Annotations.OpenWorldHint.ShouldBe(true);
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
