using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarErrorViolations
{
    internal const int MaximumCount = 32;

    internal static IReadOnlyList<CalendarInputViolation> Normalize(
        IEnumerable<CalendarInputViolation> violations) =>
        violations
            .OrderBy(violation => violation.Pointer, StringComparer.Ordinal)
            .ThenBy(violation => violation.Code, StringComparer.Ordinal)
            .ThenBy(violation => violation.Message, StringComparer.Ordinal)
            .Take(MaximumCount)
            .ToArray();

    internal static CallToolResult Attach(
        CallToolResult result,
        IEnumerable<CalendarInputViolation> violations)
    {
        var normalized = Normalize(violations);
        if (normalized.Count == 0 || result.StructuredContent is not { } structured)
            return result;

        var body = JsonNode.Parse(structured.GetRawText())!.AsObject();
        body["violations"] = JsonSerializer.SerializeToNode(normalized);
        result.StructuredContent = JsonSerializer.SerializeToElement(body);
        return result;
    }
}

internal sealed record CalendarInputViolation(
    [property: JsonPropertyName("pointer")] string Pointer,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);
