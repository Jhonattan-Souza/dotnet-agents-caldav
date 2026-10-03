using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Mcp.Tools;
using Json.Pointer;
using Json.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>
/// Explains a bare <c>invalid_input</c> refusal by evaluating the arguments against the tool's advertised input
/// schema. It runs only after a tool has already refused, so it cannot change an outcome or add work to a
/// successful call. Messages are repository-authored per schema keyword; only schema-authored allowed values,
/// bounds, and member names are quoted, never caller values or library text.
/// </summary>
internal static class CalendarInputSchemaExplainer
{
    private const int MaximumListedValues = 32;
    private static readonly ConcurrentDictionary<string, (JsonSchema Schema, JsonNode Document)> Schemas =
        new(StringComparer.Ordinal);
    private static readonly EvaluationOptions ListOutput = new() { OutputFormat = OutputFormat.List };
    private static readonly EvaluationOptions FlagOutput = new() { OutputFormat = OutputFormat.Flag };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            return Explain(request.Params?.Name, request.Params?.Arguments, result);
        };

    internal static CallToolResult Explain(
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        CallToolResult result)
    {
        if (toolName is null || !IsUnexplainedInputRefusal(result) || !CalendarToolContract.HasTool(toolName))
            return result;
        IReadOnlyList<CalendarInputViolation> violations;
        try
        {
            violations = Explain(toolName, arguments);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException
            or FormatException
            or JsonException)
        {
            // The refusal is already truthful; a diagnostic defect must not replace it with an opaque tool error.
            return result;
        }
        if (violations.Count == 0)
            return result;
        CalendarErrorViolations.Attach(result, violations);
        CalendarQueryToolSupport.ApplyCompatibilityText(result);
        return result;
    }

    internal static IReadOnlyList<CalendarInputViolation> Explain(
        string toolName,
        IDictionary<string, JsonElement>? arguments) =>
        Explain(toolName, JsonSerializer.SerializeToNode(arguments ?? new Dictionary<string, JsonElement>()));

    /// <summary>Evaluates with flag output only, for callers that must gate every request.</summary>
    internal static bool IsValid(string toolName, JsonNode? instance) =>
        Load(toolName).Schema.Evaluate(JsonSerializer.SerializeToElement(instance), FlagOutput).IsValid;

    internal static IReadOnlyList<CalendarInputViolation> Explain(string toolName, JsonNode? instance)
    {
        var (schema, document) = Load(toolName);
        var evaluation = schema.Evaluate(JsonSerializer.SerializeToElement(instance), ListOutput);
        if (evaluation.IsValid)
            return [];
        var failures = (evaluation.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 } && !IsConditionProbe(detail.EvaluationPath))
            .SelectMany(detail => detail.Errors!.Keys.Select(keyword => new SchemaFailure(detail, keyword)))
            .ToArray();
        var (leaves, choices) = CalendarSchemaBranchSelection.Select(failures);
        return CalendarErrorViolations.Normalize(leaves
            .SelectMany(failure => Describe(failure, document, instance))
            .Concat(choices.Select(choice => Describe(choice, document)))
            .DistinctBy(violation => (violation.Pointer, violation.Code)));
    }

    private static (JsonSchema Schema, JsonNode Document) Load(string toolName) => Schemas.GetOrAdd(toolName, static name =>
    {
        var text = CalendarToolContract.GetInputSchema(name).GetRawText();
        return (JsonSchema.FromText(text), JsonNode.Parse(text)!);
    });

    /// <summary>An <c>if</c> subschema that fails only selects a branch; it is not a caller error.</summary>
    private static bool IsConditionProbe(JsonPointer evaluationPath) =>
        evaluationPath.ToString().Split('/').Contains("if", StringComparer.Ordinal);

    private static bool IsUnexplainedInputRefusal(CallToolResult result) =>
        result.IsError == true
        && result.StructuredContent is { ValueKind: JsonValueKind.Object } structured
        && structured.TryGetProperty("code", out var code)
        && code.ValueEquals("invalid_input")
        && !structured.TryGetProperty("violations", out _);

    private static IEnumerable<CalendarInputViolation> Describe(
        SchemaFailure failure,
        JsonNode document,
        JsonNode? instance)
    {
        var pointer = Pointer(failure.Detail.InstanceLocation);
        var node = ResolveSchemaNode(document, failure.Detail.EvaluationPath);
        return failure.Keyword switch
        {
            "required" => MissingMembers(node, instance, failure.Detail.InstanceLocation)
                .Select(name => new CalendarInputViolation(
                    Child(pointer, name), "required_member", "This member is required.")),
            "" => [new(pointer, "unknown_member", "This member is not allowed here.")],
            _ => [Describe(failure.Keyword, pointer, node)]
        };
    }

    /// <summary>
    /// A choice whose every branch rejected the same discriminator member is reported at that member with the
    /// union of the values the branches allow; any other unmatched choice is reported as a shape mismatch.
    /// </summary>
    private static CalendarInputViolation Describe(UnresolvedChoice choice, JsonNode document)
    {
        if (choice.Discriminators.Count == 0)
        {
            return new(Pointer(choice.Choice.Detail.InstanceLocation), "shape_mismatch",
                "This value matches none of the allowed shapes; check its discriminator member, such as kind, mode, scope, field, or operation.");
        }
        var allowed = choice.Discriminators
            .Select(failure => ResolveSchemaNode(document, failure.Detail.EvaluationPath))
            .SelectMany(AllowedValues)
            .Select(Text)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(Pointer(choice.Discriminators[0].Detail.InstanceLocation), "value_not_allowed",
            $"Allowed values: {string.Join(", ", allowed.Take(MaximumListedValues))}{(allowed.Length > MaximumListedValues ? ", and others" : string.Empty)}.");
    }

    private static CalendarInputViolation Describe(string keyword, string pointer, JsonNode? node) => keyword switch
    {
        "type" => new(pointer, "invalid_type", $"Expected JSON type: {Text(node?["type"])}."),
        "enum" => new(pointer, "value_not_allowed", $"Allowed values: {Values(node?["enum"])}."),
        "const" => new(pointer, "value_not_allowed", $"Expected value: {Text(node?["const"])}."),
        "pattern" => new(pointer, "format_mismatch", $"Expected pattern: {Text(node?["pattern"])}."),
        "format" => new(pointer, "format_mismatch", $"Expected format: {Text(node?["format"])}."),
        "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" => new(pointer, "out_of_range",
            $"Allowed range: {Range(node, "minimum", "maximum")}."),
        "minLength" or "maxLength" => new(pointer, "length_out_of_range",
            $"Allowed length: {Range(node, "minLength", "maxLength")}."),
        "minItems" or "maxItems" => new(pointer, "item_count_out_of_range",
            $"Allowed item count: {Range(node, "minItems", "maxItems")}."),
        "uniqueItems" => new(pointer, "duplicate_items", "Items must be unique."),
        _ => new(pointer, "schema_violation", "This value does not satisfy the tool's input schema.")
    };

    private static IEnumerable<JsonNode?> AllowedValues(JsonNode? node) => node?["const"] is { } single
        ? new[] { single }
        : (node?["enum"] as JsonArray)?.ToArray() ?? [];

    private static IEnumerable<string> MissingMembers(JsonNode? node, JsonNode? instance, JsonPointer location)
    {
        if (node?["required"] is not JsonArray required
            || !location.TryEvaluate(instance, out var owner)
            || owner is not JsonObject value)
            return [];
        return required.Select(item => item?.GetValue<string>())
            .OfType<string>()
            .Where(name => !value.ContainsKey(name));
    }

    /// <summary>
    /// Walks the evaluation path through local <c>$ref</c>s to the subschema that owns the failing keyword.
    /// Segments the schema does not contain, such as an item index the evaluator appends, are skipped.
    /// </summary>
    private static JsonNode? ResolveSchemaNode(JsonNode document, JsonPointer evaluationPath)
    {
        var node = document;
        foreach (var segment in evaluationPath.ToString().Split('/').Skip(1))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = name == "$ref" ? FollowReference(document, node) : Child(node, name) ?? node;
            if (node is null)
                return null;
        }
        return node;
    }

    private static JsonNode? FollowReference(JsonNode document, JsonNode? node) =>
        node?["$ref"] is JsonValue reference
        && reference.TryGetValue<string>(out var target)
        && target.StartsWith("#/", StringComparison.Ordinal)
        && JsonPointer.TryParse(target[1..], out var pointer)
        && pointer.TryEvaluate(document, out var resolved)
            ? resolved
            : null;

    private static JsonNode? Child(JsonNode? node, string name) => node switch
    {
        JsonObject value => value[name],
        JsonArray array when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index < array.Count => array[index],
        _ => null
    };

    private static string Pointer(JsonPointer location)
    {
        var text = location.ToString();
        return text.Length == 0 ? "/" : text;
    }

    private static string Child(string pointer, string name) =>
        (pointer == "/" ? string.Empty : pointer) + "/" + name.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static string Values(JsonNode? values) => values is JsonArray array
        ? string.Join(", ", array.Take(MaximumListedValues).Select(Text))
            + (array.Count > MaximumListedValues ? ", and others" : string.Empty)
        : "see the input schema";

    private static string Range(JsonNode? node, string minimum, string maximum)
    {
        var low = node?[minimum] ?? node?["exclusive" + char.ToUpperInvariant(minimum[0]) + minimum[1..]];
        var high = node?[maximum] ?? node?["exclusive" + char.ToUpperInvariant(maximum[0]) + maximum[1..]];
        return string.Create(CultureInfo.InvariantCulture, $"{(low is null ? "any" : Text(low))} to {(high is null ? "any" : Text(high))}");
    }

    private static string Text(JsonNode? value) => value switch
    {
        null => "see the input schema",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString()
    };
}

internal sealed record SchemaFailure(EvaluationResults Detail, string Keyword);
