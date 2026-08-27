using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class LegacyContractRemovalTests
{
    [Fact]
    public void QueryModule_ExposesExactlyTheThreeClosedQueryFamilies()
    {
        typeof(ICalendarQueryModule).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe([
                "QueryEntitiesAsync",
                "QueryOccurrencesAsync",
                "QueryTodosAsync"
            ]);
    }

    [Fact]
    public void CalendarServiceAndShippedAssemblies_ContainNoLegacyQueryPath()
    {
        typeof(ICalendarService).GetMethods()
            .Select(method => method.Name)
            .ShouldNotContain(name => name.StartsWith("Query", StringComparison.Ordinal));

        var coreTypes = typeof(ICalendarService).Assembly.GetTypes();
        coreTypes.Single(type => type.Name == "CalendarService")
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ShouldNotContain(name => name.StartsWith("Query", StringComparison.Ordinal));

        var shippedTypeNames = coreTypes
            .Concat(typeof(CalDavHostBuilder).Assembly.GetTypes())
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        string[] removedTypes =
        [
            "CalendarEntityQueryEngine",
            "CalendarOccurrenceQueryEngine",
            "CalendarTodoQueryEngine",
            "CalendarEntityQueryResult",
            "CalendarOccurrenceQueryResult",
            "CalendarTodoQueryResult",
            "CalendarTodoQueryItem",
            "CalendarEntityQueryExecutionLimits",
            "CalendarOccurrenceQueryExecutionLimits",
            "CalendarEntityQueryCode",
            "CalendarOccurrenceQueryCode",
            "CalendarTodoQueryCode",
            "CalendarTodoQueryResultKind",
            "CalendarOccurrenceTiming",
            "CalendarOccurrenceSnapshot",
            "CalendarTodoCompletionClassification",
            "CalendarEntityCursorProtector",
            "CalendarEntityContinuation"
        ];

        foreach (var removedType in removedTypes)
            shippedTypeNames.ShouldNotContain(removedType);
    }

    [Fact]
    public void EveryStartExecutor_UsesTheOneConcreteQueryPolicy()
    {
        var core = typeof(ICalendarQueryModule).Assembly;
        string[] executorNames =
        [
            "CalendarEntityQueryStartExecutor",
            "CalendarOccurrenceQueryStartExecutor",
            "CalendarTodoQueryStartExecutor"
        ];

        foreach (var executorName in executorNames)
        {
            var parameters = core.GetTypes().Single(type => type.Name == executorName)
                .GetConstructors(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)
                .ShouldHaveSingleItem()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name)
                .ToArray();

            parameters.Count(name => name == "CalendarQueryPolicy").ShouldBe(1);
            parameters.ShouldNotContain("TimeProvider");
        }

        core.GetTypes().Count(type => type.Name == "CalendarQueryPolicy").ShouldBe(1);
    }

    [Fact]
    public void QueryTools_HoldOnlyTheClosedQueryModuleDependency()
    {
        Type[] tools = [typeof(CalendarEntityTools), typeof(CalendarOccurrenceTools), typeof(CalendarTodoTools)];

        foreach (var tool in tools)
        {
            tool.GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)
                .Select(field => field.FieldType)
                .ShouldBe([typeof(ICalendarQueryModule)]);
        }
    }

    [Fact]
    public void ContinueExecutors_HaveOnlyCursorSnapshotAndPageDependencies()
    {
        var core = typeof(ICalendarQueryModule).Assembly;
        var executors = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["CalendarEntityQueryContinueExecutor"] =
                ["CalendarQueryCursorAuthenticator", "CalendarQuerySnapshotReader", "CalendarEntityQueryPageCodec"],
            ["CalendarOccurrenceQueryContinueExecutor"] =
                ["CalendarQueryCursorAuthenticator", "CalendarQuerySnapshotReader", "CalendarOccurrenceQueryPageCodec"],
            ["CalendarTodoQueryContinueExecutor"] =
                ["CalendarQueryCursorAuthenticator", "CalendarQuerySnapshotReader", "CalendarTodoQueryPageCodec"]
        };

        foreach (var executor in executors)
        {
            var executorType = core.GetTypes().Single(type => type.Name == executor.Key);
            var parameters = executorType.GetConstructors(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)
                .ShouldHaveSingleItem()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name)
                .ToArray();

            parameters.ShouldBe(executor.Value);
        }
    }

    [Fact]
    public void ShippedCore_HasOneDiscoveryAuthorityAndNarrowProductionAdapters()
    {
        var coreTypes = typeof(ICalendarQueryModule).Assembly.GetTypes();
        coreTypes.ShouldNotContain(type => type.Name == "ICalendarQueryResourceTransport");
        typeof(ICalendarClient).GetMethods()
            .ShouldNotContain(method => method.Name.Contains("Query", StringComparison.Ordinal));
        var discovery = coreTypes.Single(type => type.Name == "CalendarOperationDiscovery");
        discovery.GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ShouldBe(["DiscoverAsync"]);
        typeof(ICalendarClient).IsAssignableFrom(discovery).ShouldBeFalse();
        coreTypes.Single(type => type.Name == "ICalendarCreateTransport").IsAssignableFrom(discovery).ShouldBeFalse();
        coreTypes.Single(type => type.Name == "ICalendarMoveTransport").IsAssignableFrom(discovery).ShouldBeFalse();

        var discoveryTransport = coreTypes.Single(type => type.Name == "ICalendarDiscoveryTransport");
        coreTypes.Where(type => !type.IsInterface && discoveryTransport.IsAssignableFrom(type))
            .Select(type => type.Name)
            .ShouldBe(["CalendarClientDiscoveryTransport"]);

        var transport = coreTypes.Single(type => type.Name == "ICalendarQueryTransport");
        coreTypes.Where(type => !type.IsInterface && transport.IsAssignableFrom(type))
            .Select(type => type.Name)
            .ShouldBe(["CalendarQueryTransport"]);
    }

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
