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
    public void PublicCoreContracts_ExposeNoIcalNetHttpOrXmlImplementationTypes()
    {
        string[] forbiddenNamespaces = ["Ical.Net", "System.Net.Http", "System.Xml"];
        var exposedTypes = typeof(ICalendarService).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers()
                .SelectMany(member => PublicSignatureTypes(member)))
            .SelectMany(TypeClosure)
            .Where(type => type.Namespace is not null)
            .ToArray();

        foreach (var forbiddenNamespace in forbiddenNamespaces)
        {
            exposedTypes.ShouldAllBe(type =>
                !type.Namespace!.StartsWith(forbiddenNamespace, StringComparison.Ordinal));
        }
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

    private static IEnumerable<Type> PublicSignatureTypes(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.MethodInfo method when method.IsPublic =>
            method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
        System.Reflection.PropertyInfo property => [property.PropertyType],
        System.Reflection.FieldInfo field when field.IsPublic => [field.FieldType],
        System.Reflection.EventInfo eventInfo when eventInfo.EventHandlerType is not null => [eventInfo.EventHandlerType],
        _ => []
    };

    private static IEnumerable<Type> TypeClosure(Type type)
    {
        yield return type;
        if (type.HasElementType)
            yield return type.GetElementType()!;
        foreach (var argument in type.GetGenericArguments())
            yield return argument;
    }
}
