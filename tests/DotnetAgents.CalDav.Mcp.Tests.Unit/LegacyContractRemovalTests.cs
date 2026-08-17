using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Hosting;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class LegacyContractRemovalTests
{
    [Fact]
    public void ShippedAssemblies_ContainNoLegacyTaskContractTypes()
    {
        var shippedTypeNames = typeof(ICalendarService).Assembly.GetTypes()
            .Concat(typeof(CalDavHostBuilder).Assembly.GetTypes())
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Models.TaskItem");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Models.TaskList");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Models.TaskPriority");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Models.TaskQuery");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Models.TaskStatus");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Abstractions.ICalDavClient");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Abstractions.ITaskListResolver");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Abstractions.ITaskService");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Services.TaskListResolver");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.Services.TaskService");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.CalDavConflictException");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Core.TaskListResolutionException");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Mcp.TaskCompletion");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Mcp.Tools.TaskListTools");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Mcp.Tools.TaskQueryTools");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Mcp.Tools.TaskMutationTools");
        shippedTypeNames.ShouldNotContain("DotnetAgents.CalDav.Mcp.Tools.ChatTaskTools");
    }

    [Fact]
    public void PublicRegistrationSurface_ContainsOnlyTheCalendarRegistrationName()
    {
        var publicMethodNames = typeof(CalDavServiceCollectionExtensions).GetMethods()
            .Where(method => method.IsPublic && method.IsStatic)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        publicMethodNames.ShouldContain("AddCalDavCalendars");
        publicMethodNames.ShouldNotContain("AddCalDavTasks");
    }
}
