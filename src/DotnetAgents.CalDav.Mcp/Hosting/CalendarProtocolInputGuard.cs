using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarProtocolInputGuard
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    internal static bool Applies(string? toolName) => toolName is "calendars.inspect" or "calendars.patch"
        or "calendars.free_busy" or "calendar_resources.changes";

    internal static IReadOnlyList<CalendarInputViolation> Validate(string toolName, JsonNode? arguments)
    {
        var schema = Schemas.GetOrAdd(toolName, static name =>
            JsonSchema.FromText(CalendarToolContract.GetInputSchema(name).GetRawText()));
        var evaluated = schema.Evaluate(JsonSerializer.SerializeToElement(arguments));
        return evaluated.IsValid ? [] : [new("/", "invalid_input",
            "Use the tool's exact input schema: required members, types, bounds and set/remove alternatives must match.")];
    }
}
