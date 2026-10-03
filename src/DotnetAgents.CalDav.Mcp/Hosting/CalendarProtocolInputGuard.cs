using System.Text.Json.Nodes;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarProtocolInputGuard
{
    internal static bool Applies(string? toolName) => toolName is "calendars.inspect" or "calendars.patch"
        or "calendars.free_busy" or "calendar_resources.changes";

    /// <summary>Names each schema failure; a schema rejection the explainer cannot localize keeps one root reason.</summary>
    internal static IReadOnlyList<CalendarInputViolation> Validate(string toolName, JsonNode? arguments)
    {
        if (CalendarInputSchemaExplainer.IsValid(toolName, arguments))
            return [];
        var violations = CalendarInputSchemaExplainer.Explain(toolName, arguments);
        return violations.Count > 0 ? violations : [new("/", "invalid_input",
            "Use the tool's exact input schema: required members, types, bounds and set/remove alternatives must match.")];
    }
}
