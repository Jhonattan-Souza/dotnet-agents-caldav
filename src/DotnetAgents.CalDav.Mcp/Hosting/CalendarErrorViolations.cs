using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;
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

    /// <summary>Names each property of an atomic property write that the server did not apply.</summary>
    internal static IEnumerable<CalendarInputViolation> FromRejectedProperties(
        string pointerPrefix,
        IEnumerable<CalendarPropertyRejection> rejections) => rejections.Select(rejection => rejection.StatusCode == 424
            ? new CalendarInputViolation(pointerPrefix + rejection.Property, "property_not_applied",
                "The server did not apply this property because another property in the same atomic request failed (HTTP 424).")
            : new CalendarInputViolation(pointerPrefix + rejection.Property, "property_rejected",
                string.Create(CultureInfo.InvariantCulture, $"The server rejected this property with HTTP status {rejection.StatusCode}.")));

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
