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
    [InlineData("calendars.list", false)]
    [InlineData("calendar_entities.query", false)]
    [InlineData("calendar_occurrences.query", false)]
    [InlineData("calendar_resources.get", false)]
    [InlineData("calendar_resources.exact_get", false)]
    [InlineData("events.create", true)]
    [InlineData("events.patch", true)]
    [InlineData("todos.create", true)]
    [InlineData("todos.patch", true)]
    [InlineData("todos.complete", true)]
    [InlineData("calendar_occurrences.add", true)]
    [InlineData("calendar_occurrences.exclude", true)]
    [InlineData("calendar_occurrences.restore_exclusion", true)]
    [InlineData("calendar_occurrences.cancel", true)]
    [InlineData("calendar_occurrences.restore_cancellation", true)]
    [InlineData("calendar_resources.move", true)]
    [InlineData("calendar_resources.delete", true)]
    [InlineData("calendar_resources.exact_create", true)]
    [InlineData("calendar_resources.exact_replace", true)]
    [InlineData("calendar_resources.exact_move", true)]
    public void Validate_CoversExecutionDeadlineRejectionsOutsideTheToolHandler(
        string toolName,
        bool mutation)
    {
        var result = CalendarExecutionPolicy.CreateDeadlineResult(mutation);

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
