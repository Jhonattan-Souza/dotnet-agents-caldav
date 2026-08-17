using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class CalendarResourceMoveArgumentParser
{
    private static readonly HashSet<string> RootProperties = ["revision", "destination"];
    private static readonly HashSet<string> RevisionProperties = ["href", "entityUid", "entityKind", "entityTag"];
    private static readonly HashSet<string> DefaultDestinationProperties = ["mode"];
    private static readonly HashSet<string> SelectedDestinationProperties = ["mode", "calendar"];
    private static readonly HashSet<string> CalendarNameProperties = ["by", "name"];
    private static readonly HashSet<string> CalendarHrefProperties = ["by", "href"];

    public static bool TryParse(
        IDictionary<string, JsonElement>? arguments,
        out CalendarResourceMoveRequest request)
    {
        request = default!;
        if (arguments is null
            || !HasExactProperties(arguments.Keys, RootProperties)
            || !arguments.TryGetValue("revision", out var revisionElement)
            || !TryParseRevision(revisionElement, out var revision)
            || !arguments.TryGetValue("destination", out var destinationElement)
            || !TryParseDestination(destinationElement, out var destination))
        {
            return false;
        }
        request = new CalendarResourceMoveRequest(revision, destination);
        return true;
    }

    private static bool TryParseRevision(
        JsonElement element,
        out CalendarResourceRevisionReference revision)
    {
        revision = default!;
        if (element.ValueKind != JsonValueKind.Object
            || !HasExactProperties(element.EnumerateObject().Select(property => property.Name), RevisionProperties)
            || !TryGetExactString(element, "href", out var href)
            || !TryGetExactString(element, "entityUid", out var entityUid)
            || !TryGetExactString(element, "entityKind", out var kindText)
            || !TryGetExactString(element, "entityTag", out var entityTag)
            || !Uri.TryCreate(href, UriKind.Absolute, out _)
            || !TryParseKind(kindText, out var kind)
            || !IsExactEntityTag(entityTag))
        {
            return false;
        }
        revision = new CalendarResourceRevisionReference(href, entityUid, kind, entityTag);
        return true;
    }

    private static bool TryParseDestination(JsonElement element, out CalendarMoveDestination destination)
    {
        destination = default!;
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetExactString(element, "mode", out var mode))
        {
            return false;
        }
        if (mode == "default" && HasExactProperties(
                element.EnumerateObject().Select(property => property.Name),
                DefaultDestinationProperties))
        {
            destination = CalendarMoveDestination.Default;
            return true;
        }
        return mode == "selected"
            && HasExactProperties(element.EnumerateObject().Select(property => property.Name), SelectedDestinationProperties)
            && element.TryGetProperty("calendar", out var calendar)
            && TryParseCalendarReference(calendar, out destination);
    }

    private static bool TryParseCalendarReference(
        JsonElement element,
        out CalendarMoveDestination destination)
    {
        destination = default!;
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetExactString(element, "by", out var by))
        {
            return false;
        }
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (by == "name"
            && HasExactProperties(properties, CalendarNameProperties)
            && TryGetExactString(element, "name", out var name))
        {
            destination = CalendarMoveDestination.Selected(new CalendarReference(Name: name));
            return true;
        }
        if (by == "href"
            && HasExactProperties(properties, CalendarHrefProperties)
            && TryGetExactString(element, "href", out var href)
            && Uri.TryCreate(href, UriKind.Absolute, out _))
        {
            destination = CalendarMoveDestination.Selected(new CalendarReference(Href: href));
            return true;
        }
        return false;
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

    private static bool IsExactEntityTag(string value) =>
        EntityTagHeaderValue.TryParse(value, out var parsed)
        && parsed is not null
        && parsed != EntityTagHeaderValue.Any
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static bool HasExactProperties(IEnumerable<string> actual, IReadOnlySet<string> expected)
    {
        var properties = actual.ToArray();
        return properties.Length == expected.Count && properties.All(expected.Contains);
    }
}
