using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarOutputSchemaGuardTests
{
    [Fact]
    public void Validate_AcceptsSchemaValidToolOutput()
    {
        var result = Result("""
            {
              "outcome":"success",
              "items":[],
              "diagnostics":[],
              "pagination":{"mode":"non_snapshot","nextCursor":null}
            }
            """);

        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate("calendars.list", result));
    }

    [Fact]
    public void Validate_RejectsOutputThatViolatesTheAdvertisedToolSchema()
    {
        var result = Result("""{"outcome":"success","calendars":"not-an-array"}""");

        var exception = Should.Throw<InvalidOperationException>(
            () => CalendarOutputSchemaGuard.Validate("calendars.list", result));

        exception.Message.ShouldBe("A Calendar tool returned output that violates its advertised schema.");
    }

    [Theory]
    [InlineData("calendar_entities.query", false)]
    [InlineData("events.create", true)]
    [InlineData("calendar_resources.exact_create", true)]
    public void Validate_CoversAdmissionRejectionsOutsideTheToolHandler(string toolName, bool mutation)
    {
        var result = CalendarExecutionPolicy.CreateBusyResult(mutation);

        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, result));
    }

    [Theory]
    [InlineData("calendar_entities.query")]
    [InlineData("events.create")]
    [InlineData("calendar_resources.exact_create")]
    public void Validate_CoversStrictLexicalRejectionsOutsideTheToolHandler(string toolName)
    {
        var result = StrictToolInputGuard.Reject(
            toolName,
            new StrictToolInputEvidence(1, HasDuplicateProperty: true));

        result.ShouldNotBeNull();
        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, result));
    }

    private static CallToolResult Result(string json) => new()
    {
        StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
        Content = []
    };
}
