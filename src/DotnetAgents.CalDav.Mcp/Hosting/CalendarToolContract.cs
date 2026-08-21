using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Loads the live schemas and cache metadata for the Calendar tracer bullet.</summary>
internal static class CalendarToolContract
{
    private const string ResourceName = "DotnetAgents.CalDav.Mcp.CalendarToolCatalog.json";
    private static readonly JsonObject Catalog = LoadCatalog();

    public static JsonElement GetInputSchema(string toolName = "calendars.list") => GetSchema(toolName, "inputSchema");

    public static JsonElement GetOutputSchema(string toolName = "calendars.list") => GetSchema(toolName, "outputSchema");

    public static JsonObject GetCacheMetadata(string toolName = "calendars.list") => FindTool(toolName)["cache"]!.DeepClone().AsObject();

    private static JsonElement GetSchema(string toolName, string schemaProperty)
    {
        var reference = FindTool(toolName)[schemaProperty]!["$ref"]!.GetValue<string>();
        var definitionName = reference.Split('/').Last();
        var schema = Catalog["$defs"]![definitionName]!.DeepClone().AsObject();
        var definitions = GetReferencedDefinitions(schema);
        if (definitions.Count > 0)
            schema["$defs"] = definitions;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject GetReferencedDefinitions(JsonNode schema)
    {
        var definitions = new JsonObject();
        var pending = new Queue<string>(GetDefinitionReferences(schema));

        while (pending.TryDequeue(out var definitionName))
        {
            if (definitions.ContainsKey(definitionName))
                continue;

            var definition = Catalog["$defs"]![definitionName]!.DeepClone();
            definitions[definitionName] = definition;
            foreach (var nestedReference in GetDefinitionReferences(definition))
                pending.Enqueue(nestedReference);
        }

        return definitions;
    }

    private static IEnumerable<string> GetDefinitionReferences(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => GetObjectReferences(obj),
            JsonArray array => array.SelectMany(GetDefinitionReferences),
            _ => []
        };
    }

    private static IEnumerable<string> GetObjectReferences(JsonObject obj)
    {
        const string Prefix = "#/$defs/";
        var directReference = obj["$ref"]?.GetValue<string>();
        IEnumerable<string> direct = directReference is not null && directReference.StartsWith(Prefix, StringComparison.Ordinal)
            ? [directReference[Prefix.Length..]]
            : Array.Empty<string>();
        return direct.Concat(obj.Where(property => property.Key != "$ref").SelectMany(property => GetDefinitionReferences(property.Value)));
    }

    private static JsonObject FindTool(string toolName) => Catalog["tools"]!.AsArray()
        .Select(item => item!.AsObject())
        .Single(tool => tool["name"]!.GetValue<string>() == toolName);

    private static JsonObject LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded contract resource '{ResourceName}' was not found.");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
