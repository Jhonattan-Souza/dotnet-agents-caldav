using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Mcp.Hosting;
using Json.Schema;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarOutputSchemaGuardTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("late-invalid-enum", false)]
    [InlineData("late-extra-property", false)]
    [InlineData("late-wrong-revision-kind", false)]
    [InlineData("late-invalid-percentage", false)]
    public void Validate_ChecksTheEntirePageAndNestedReferences(string variation, bool valid)
    {
        var item = JsonNode.Parse("""
            {"resultKind":"entity","completionState":"open","percentComplete":0,
             "completionTarget":{"kind":"direct","entityRevision":{
               "href":"https://cal.example/todos/one.ics","entityUid":"one",
               "entityKind":"todo","entityTag":"\"strong\""}},"diagnostics":[]}
            """)!;
        var items = new JsonArray(Enumerable.Range(0, 200).Select(_ => item.DeepClone()).ToArray());
        CorruptLastItem(items[199]!, variation);
        var result = Result(new JsonObject
        {
            ["outcome"] = "success",
            ["items"] = items,
            ["diagnostics"] = new JsonArray(),
            ["excludedIndeterminateCount"] = 0,
            ["pagination"] = new JsonObject { ["mode"] = "query_result_snapshot", ["nextCursor"] = null },
            ["temporalEvaluationContext"] = new JsonObject
            {
                ["timeZone"] = "America/Sao_Paulo", ["source"] = "configuration"
            }
        }.ToJsonString());
        var schema = JsonSchema.FromText(CalendarToolContract.GetOutputSchema("todos.query").GetRawText());
        schema.Evaluate(result.StructuredContent!.Value,
            new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid.ShouldBe(valid);

        if (valid)
            Should.NotThrow(() => CalendarOutputSchemaGuard.Validate("todos.query", result));
        else
            Should.Throw<InvalidOperationException>(() => CalendarOutputSchemaGuard.Validate("todos.query", result));
    }

    [Theory]
    [InlineData("todo", true)]
    [InlineData("event", false)]
    public void Validate_AcceptsOnlyTodoOverridesInTodoQueryRecurrence(string entityKind, bool valid)
    {
        var item = JsonNode.Parse("""
            {"resultKind":"entity","uid":"one","completionState":"open",
             "recurrence":{"evaluationState":"evaluable","rrules":[{"text":"FREQ=DAILY;COUNT=3"}],
               "rdates":[],"exdates":[],"overrides":[{
                 "recurrenceIdentity":{"value":{"kind":"utcDateTime","value":"2026-08-19T09:00:00Z"}},
                 "entityKind":"todo","status":"active","fields":{"summary":"Override"}}]},
             "completionTarget":{"kind":"occurrence_required","entityRevision":{
               "href":"https://cal.example/todos/one.ics","entityUid":"one",
               "entityKind":"todo","entityTag":"\"strong\""}},"diagnostics":[]}
            """)!;
        item["recurrence"]!["overrides"]![0]!["entityKind"] = entityKind;
        var result = Result(new JsonObject
        {
            ["outcome"] = "success",
            ["items"] = new JsonArray(item),
            ["diagnostics"] = new JsonArray(),
            ["excludedIndeterminateCount"] = 0,
            ["pagination"] = new JsonObject { ["mode"] = "query_result_snapshot", ["nextCursor"] = null },
            ["temporalEvaluationContext"] = new JsonObject
            {
                ["timeZone"] = "UTC", ["source"] = "configuration"
            }
        }.ToJsonString());

        if (valid)
            Should.NotThrow(() => CalendarOutputSchemaGuard.Validate("todos.query", result));
        else
            Should.Throw<InvalidOperationException>(() => CalendarOutputSchemaGuard.Validate("todos.query", result));
    }

    private static void CorruptLastItem(JsonNode item, string variation)
    {
        switch (variation)
        {
            case "late-invalid-enum": item["completionState"] = "done"; break;
            case "late-extra-property": item["completionTarget"]!["extra"] = true; break;
            case "late-wrong-revision-kind": item["completionTarget"]!["entityRevision"]!["entityKind"] = "event"; break;
            case "late-invalid-percentage": item["percentComplete"] = 101; break;
        }
    }

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
    [InlineData("calendars.create", true)]
    [InlineData("calendars.delete", true)]
    [InlineData("calendar_resources.exact_create", true)]
    public void Validate_CoversAdmissionRejectionsOutsideTheToolHandler(string toolName, bool mutation)
    {
        var result = CalendarExecutionPolicy.CreateBusyResult(mutation);

        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, result));
    }

    [Theory]
    [InlineData("calendars.list", false)]
    [InlineData("calendars.create", true)]
    [InlineData("calendars.delete", true)]
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

    [Theory]
    [InlineData("calendars.list")]
    [InlineData("calendars.inspect")]
    [InlineData("calendars.free_busy")]
    [InlineData("calendar_resources.changes")]
    [InlineData("calendar_resources.exact_get")]
    [InlineData("todos.query")]
    [InlineData("calendar_resources.get")]
    public void Validate_RejectsACurrentSnapshotOnReadToolErrors(string toolName)
    {
        var error = new JsonObject
        {
            ["code"] = "conflict",
            ["category"] = "state",
            ["message"] = "The resource changed.",
            ["retryable"] = false,
            ["phase"] = "targetRevision"
        };
        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, ErrorResult(error)));

        error["currentSnapshot"] = EventSnapshot();

        Should.Throw<InvalidOperationException>(() => CalendarOutputSchemaGuard.Validate(toolName, ErrorResult(error)));
    }

    [Theory]
    [InlineData("calendars.create", false)]
    [InlineData("calendars.delete", false)]
    [InlineData("calendars.patch", false)]
    [InlineData("events.patch", true)]
    [InlineData("calendar_resources.delete", true)]
    public void Validate_AcceptsACurrentSnapshotOnlyOnEntityMutationErrors(string toolName, bool accepted)
    {
        var error = new JsonObject
        {
            ["code"] = "conflict",
            ["category"] = "state",
            ["message"] = "The resource changed.",
            ["retryable"] = false,
            ["phase"] = "targetRevision",
            ["mutationState"] = "not_committed"
        };
        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, ErrorResult(error)));

        error["currentSnapshot"] = EventSnapshot();

        if (accepted)
            Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, ErrorResult(error)));
        else
            Should.Throw<InvalidOperationException>(() => CalendarOutputSchemaGuard.Validate(toolName, ErrorResult(error)));
    }

    private static JsonObject EventSnapshot() => new()
    {
        ["calendar"] = new JsonObject { ["href"] = "https://cal.example/events/" },
        ["resourceRevision"] = new JsonObject
        {
            ["href"] = "https://cal.example/events/one.ics", ["entityTag"] = "\"strong\""
        },
        ["entityRevision"] = new JsonObject
        {
            ["href"] = "https://cal.example/events/one.ics", ["entityUid"] = "one",
            ["entityKind"] = "event", ["entityTag"] = "\"strong\""
        },
        ["calendarProperties"] = new JsonArray(),
        ["projection"] = new JsonObject { ["kind"] = "event", ["uid"] = "one", ["fields"] = new JsonObject() },
        ["diagnostics"] = new JsonArray()
    };

    private static CallToolResult ErrorResult(JsonObject error) => new()
    {
        IsError = true,
        StructuredContent = JsonSerializer.SerializeToElement(error),
        Content = []
    };
    [Fact]
    public void Enforce_ReturnsSchemaValidOutputUnchanged()
    {
        var result = Result("""
            {"outcome":"success","items":[],"diagnostics":[],"pagination":{"mode":"non_snapshot","nextCursor":null}}
            """);

        CalendarOutputSchemaGuard.Enforce("calendars.list", result).ShouldBeSameAs(result);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("not_committed")]
    [InlineData("not_attempted")]
    [InlineData("unknown")]
    public void Enforce_MutationViolationBecomesIndeterminateAndKeepsReportedMutationState(string mutationState)
    {
        var result = Result($$"""
            {"outcome":"success","mutationState":"{{mutationState}}","snapshot":"invalid","diagnostics":[]}
            """);

        var replacement = CalendarOutputSchemaGuard.Enforce("events.create", result);

        AssertIndeterminate(replacement, "events.create", mutationState);
    }

    [Theory]
    [InlineData("""{"outcome":"success","snapshot":"invalid"}""")]
    [InlineData("""{"outcome":"success","mutationState":true}""")]
    [InlineData("""{"outcome":"success","mutationState":"partially_committed"}""")]
    [InlineData("""{"outcome":"success","mutationState":"committed","mutationState":"committed"}""")]
    [InlineData("""["committed"]""")]
    public void Enforce_MutationViolationWithoutOneClosedMutationStateIsUnknown(string json)
    {
        var replacement = CalendarOutputSchemaGuard.Enforce("calendar_resources.exact_replace", Result(json));

        AssertIndeterminate(replacement, "calendar_resources.exact_replace", "unknown");
    }

    [Fact]
    public void Enforce_MutationWithoutStructuredOutputIsIndeterminateWithUnknownState()
    {
        var replacement = CalendarOutputSchemaGuard.Enforce(
            "calendar_resources.delete",
            new CallToolResult { Content = [new TextContentBlock { Text = "unstructured" }] });

        AssertIndeterminate(replacement, "calendar_resources.delete", "unknown");
    }

    [Theory]
    [InlineData("calendars.list", """{"outcome":"success","calendars":"not-an-array"}""",
        "A Calendar tool returned output that violates its advertised schema.")]
    [InlineData("calendars.list", null, "A Calendar tool returned no structured output to validate.")]
    [InlineData(null, "{}", "A Calendar tool returned no structured output to validate.")]
    public void Enforce_ReadViolationStillFailsLoudly(string? toolName, string? json, string message)
    {
        var result = json is null ? new CallToolResult { Content = [] } : Result(json);

        var exception = Should.Throw<InvalidOperationException>(() => CalendarOutputSchemaGuard.Enforce(toolName, result));

        exception.Message.ShouldBe(message);
    }

    private static void AssertIndeterminate(CallToolResult result, string toolName, string mutationState)
    {
        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent.ShouldNotBeNull();
        structured.GetProperty("code").GetString().ShouldBe("indeterminate");
        structured.GetProperty("category").GetString().ShouldBe("postWriteTruth");
        structured.GetProperty("phase").GetString().ShouldBe("postWriteVerificationOrReconciliation");
        structured.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        structured.GetProperty("mutationState").GetString().ShouldBe(mutationState);
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldBe(structured.GetRawText());
        Should.NotThrow(() => CalendarOutputSchemaGuard.Validate(toolName, result));
    }

    private static CallToolResult Result(string json) => new()
    {
        StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
        Content = []
    };
}
