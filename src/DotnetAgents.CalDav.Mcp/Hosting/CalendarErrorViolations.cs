using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
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

    /// <summary>
    /// Anchors typed Core reasons at the caller's arguments. A reason whose semantic pointer names no argument
    /// in this request, such as a fault in stored data, contributes only its message.
    /// </summary>
    internal static IReadOnlyList<CalendarInputViolation>? FromRequestViolations(
        IReadOnlyList<CalendarRequestViolation>? violations,
        Func<string, string?> resolvePointer)
    {
        if (violations is not { Count: > 0 })
            return null;
        var resolved = new List<CalendarInputViolation>(violations.Count);
        foreach (var violation in violations)
        {
            if (violation.Pointer is not null && resolvePointer(violation.Pointer) is { } pointer)
                resolved.Add(new CalendarInputViolation(pointer, violation.Code, violation.Message));
        }
        return resolved.Count == 0 ? null : Normalize(resolved);
    }

    /// <summary>Attaches typed reasons whose pointers already address this tool's arguments.</summary>
    internal static CallToolResult AttachRequestViolations(
        CallToolResult result,
        IReadOnlyList<CalendarRequestViolation>? violations) =>
        FromRequestViolations(violations, pointer => pointer) is { } resolved ? Attach(result, resolved) : result;

    /// <summary>Returns the first typed reason's fixed message, or the caller's fallback.</summary>
    internal static string MessageOr(IReadOnlyList<CalendarRequestViolation>? violations, string fallback) =>
        violations is [var first, ..] ? first.Message : fallback;

    /// <summary>Moves violations anchored at one argument member to another and re-renders the text block.</summary>
    internal static CallToolResult RebasePointer(CallToolResult result, string from, string to)
    {
        if (result.StructuredContent is not { } structured
            || !structured.TryGetProperty("violations", out var violations)
            || !violations.EnumerateArray().Any(item => item.GetProperty("pointer").GetString() == from))
            return result;
        var body = JsonNode.Parse(structured.GetRawText())!.AsObject();
        foreach (var violation in body["violations"]!.AsArray())
        {
            if (violation!["pointer"]!.GetValue<string>() == from)
                violation["pointer"] = to;
        }
        result.StructuredContent = JsonSerializer.SerializeToElement(body);
        CalendarQueryToolSupport.ApplyCompatibilityText(result);
        return result;
    }

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
