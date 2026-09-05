using DotnetAgents.CalDav.Core.Abstractions;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class PublicContractBoundaryTests
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
