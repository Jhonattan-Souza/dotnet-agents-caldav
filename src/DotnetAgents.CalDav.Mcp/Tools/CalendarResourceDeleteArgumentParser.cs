using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class CalendarResourceDeleteArgumentParser
{
    private static readonly HashSet<string> RootProperties = ["revision"];
    private static readonly HashSet<string> RevisionProperties = ["href", "entityUid", "entityKind", "entityTag"];

    public static bool TryParse(
        IDictionary<string, JsonElement>? arguments,
        out CalendarResourceRevisionReference revision)
    {
        revision = default!;
        if (!TryGetRevisionObject(arguments, out var element)
            || !TryGetRevisionFields(element, out var href, out var entityUid, out var kindText, out var entityTag)
            || !Uri.TryCreate(href, UriKind.Absolute, out _)
            || !TryParseKind(kindText, out var kind)
            || !IsExactEntityTag(entityTag))
        {
            return false;
        }

        revision = new CalendarResourceRevisionReference(href, entityUid, kind, entityTag);
        return true;
    }

    private static bool TryGetRevisionObject(
        IDictionary<string, JsonElement>? arguments,
        out JsonElement element)
    {
        element = default;
        return arguments is not null
            && HasExactProperties(arguments.Keys, RootProperties)
            && arguments.TryGetValue("revision", out element)
            && element.ValueKind == JsonValueKind.Object
            && HasExactProperties(element.EnumerateObject().Select(property => property.Name), RevisionProperties);
    }

    private static bool TryGetRevisionFields(
        JsonElement element,
        out string href,
        out string entityUid,
        out string entityKind,
        out string entityTag)
    {
        href = string.Empty;
        entityUid = string.Empty;
        entityKind = string.Empty;
        entityTag = string.Empty;
        return TryGetExactString(element, "href", out href)
            && TryGetExactString(element, "entityUid", out entityUid)
            && TryGetExactString(element, "entityKind", out entityKind)
            && TryGetExactString(element, "entityTag", out entityTag);
    }

    private static bool TryGetExactString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        return owner.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text
            && string.Equals(text, text.Trim(), StringComparison.Ordinal)
            && (value = text) is not null;
    }

    private static bool TryParseKind(string value, out CalendarEntityKind kind)
    {
        kind = value switch
        {
            "event" => CalendarEntityKind.Event,
            "todo" => CalendarEntityKind.Todo,
            _ => default
        };
        return value is "event" or "todo";
    }

    public static bool IsWeakEntityTag(string value) =>
        EntityTagHeaderValue.TryParse(value, out var parsed) && parsed?.IsWeak == true;

    private static bool IsExactEntityTag(string value) =>
        EntityTagHeaderValue.TryParse(value, out var parsed)
        && parsed is not null
        && parsed != EntityTagHeaderValue.Any
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static bool HasExactProperties(IEnumerable<string> actual, HashSet<string> expected)
    {
        var properties = actual.ToArray();
        return properties.Length == expected.Count && properties.All(expected.Contains);
    }
}
