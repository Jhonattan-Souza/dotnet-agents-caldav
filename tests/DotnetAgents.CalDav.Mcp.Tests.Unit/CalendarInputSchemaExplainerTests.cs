using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarInputSchemaExplainerTests
{
    private const string Snapshot = """{"href":"https://cal.example/e/1.ics","entityUid":"1","entityKind":"event","entityTag":"\"r1\""}""";

    [Theory]
    [MemberData(nameof(MistakenArguments))]
    public void Explain_names_the_failing_member_with_schema_authored_detail(
        string tool,
        string arguments,
        string pointer,
        string code,
        string message)
    {
        var violations = CalendarInputSchemaExplainer.Explain(tool, Arguments(arguments));

        violations.ShouldContain(violation => violation.Pointer == pointer
            && violation.Code == code
            && violation.Message == message);
    }

    public static TheoryData<string, string, string, string, string> MistakenArguments() => new()
    {
        { "todos.create", """{"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{"dueDate":"2026-10-25"}}}""",
            "/entity/fields/dueDate", "unknown_member", "This member is not allowed here." },
        { "todos.create", """{"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{"due":{"kind":"datetime","value":"2026-10-25"}}}}""",
            "/entity/fields/due/kind", "value_not_allowed", "Allowed values: date, floatingDateTime, utcDateTime, zonedDateTime." },
        { "todos.create", """{"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{"due":{"kind":"date","value":"25/10/2026"}}}}""",
            "/entity/fields/due/value", "format_mismatch", @"Expected pattern: ^\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])$." },
        { "events.create", """{"entity":{"kind":"event","fields":{}}}""",
            "/destination", "required_member", "This member is required." },
        { "events.create", """{"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"priority":12}}}""",
            "/entity/fields/priority", "out_of_range", "Allowed range: 0 to 9." },
        { "events.patch", "{\"snapshot\":" + Snapshot + ""","target":{"scope":"all"},"patch":{"scalars":[{"field":"summary","operation":"set","value":"x"}]}}""",
            "/target/scope", "value_not_allowed", "Allowed values: master, entire-set, one-occurrence, this-and-future." },
        { "todos.query", """{"scope":{"mode":"all"},"from":{"kind":"utcDateTime","value":"2026-10-01T00:00:00Z"}}""",
            "/to", "required_member", "This member is required." },
        { "calendar_occurrences.query", """{"scope":{"mode":"all"},"from":"2026-10-01","to":"2026-10-02"}""",
            "/from", "invalid_type", "Expected JSON type: object." },
        { "todos.complete", """{"snapshot":{"href":"https://cal.example/t/1.ics","entityUid":"1","entityKind":"todo"}}""",
            "/snapshot/entityTag", "required_member", "This member is required." },
        { "calendars.create", """{"displayName":"Work","entityKinds":["task"]}""",
            "/entityKinds/0", "value_not_allowed", "Allowed values: event, todo." },
        { "calendars.create", """{"displayName":"","entityKinds":["todo"]}""",
            "/displayName", "length_out_of_range", "Allowed length: 1 to 256." },
        { "calendar_resources.delete", """{"revision":{"href":"x"}}""",
            "/revision/href", "format_mismatch", "Expected format: uri." }
    };

    [Fact]
    public void Explain_ignores_failures_inside_a_choice_another_branch_satisfied()
    {
        var violations = CalendarInputSchemaExplainer.Explain(
            "todos.create",
            Arguments("""{"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{"dueDate":"x"}}}"""));

        violations.ShouldHaveSingleItem().Pointer.ShouldBe("/entity/fields/dueDate");
    }

    [Fact]
    public void Explain_reports_nothing_for_schema_valid_arguments()
    {
        CalendarInputSchemaExplainer.Explain(
            "events.create",
            Arguments("""{"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"start":{"kind":"utcDateTime","value":"2026-08-17T13:00:00Z"}}}}"""))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Filter_attaches_violations_to_a_bare_input_refusal_and_keeps_text_equal_to_structured_content()
    {
        var refusal = Refusal("""{"code":"invalid_input","category":"input","message":"The input is invalid.","retryable":false,"phase":"schemaLexicalDiscriminator","mutationState":"not_attempted"}""");

        var result = CalendarInputSchemaExplainer.Explain(
            "events.create",
            Arguments("""{"entity":{"kind":"event","fields":{}}}"""),
            refusal);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("violations")[0].GetProperty("pointer").GetString().ShouldBe("/destination");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        using var text = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
        JsonElement.DeepEquals(text.RootElement, structured).ShouldBeTrue();
    }

    [Theory]
    [InlineData("events.create", """{"code":"invalid_input","violations":[{"pointer":"/entity","code":"kept"}]}""")]
    [InlineData("events.create", """{"code":"invalid_calendar_data"}""")]
    [InlineData("not.a.tool", """{"code":"invalid_input"}""")]
    public void Filter_leaves_explained_semantic_and_unknown_tool_refusals_untouched(string tool, string body)
    {
        var refusal = Refusal(body);
        var before = refusal.StructuredContent!.Value.GetRawText();

        var result = CalendarInputSchemaExplainer.Explain(tool, Arguments("""{"entity":{}}"""), refusal);

        result.StructuredContent!.Value.GetRawText().ShouldBe(before);
    }

    [Fact]
    public void Filter_leaves_success_untouched()
    {
        var success = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(new { outcome = "success" })
        };

        CalendarInputSchemaExplainer.Explain("events.create", Arguments("""{"entity":{}}"""), success)
            .StructuredContent!.Value.TryGetProperty("violations", out _).ShouldBeFalse();
    }

    private static CallToolResult Refusal(string body) => new()
    {
        IsError = true,
        StructuredContent = JsonDocument.Parse(body).RootElement.Clone(),
        Content = [new TextContentBlock { Text = body }]
    };

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
