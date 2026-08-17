using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class ExactCalendarResourceArgumentParser
{
    private static readonly HashSet<string> CreateProperties = ["destinationHref", "utf8Resource", "base64Utf8Resource"];
    private static readonly HashSet<string> ReplaceProperties = ["revision", "utf8Resource", "base64Utf8Resource"];
    private static readonly HashSet<string> MoveProperties = ["revision", "destinationHref"];
    private static readonly HashSet<string> GetProperties = ["href"];
    private static readonly HashSet<string> RevisionProperties = ["href", "entityUid", "entityKind", "entityTag"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParseGet(IDictionary<string, JsonElement>? arguments, out string href)
    {
        href = string.Empty;
        return arguments is not null
            && HasExactProperties(arguments.Keys, GetProperties)
            && TryGetString(arguments, "href", out href)
            && Uri.TryCreate(href, UriKind.Absolute, out _);
    }

    public static bool TryParseCreate(
        IDictionary<string, JsonElement>? arguments,
        out CalendarExactCreateRequest request)
    {
        request = default!;
        if (arguments is null
            || !HasExactResourceProperties(arguments, CreateProperties)
            || !TryGetString(arguments, "destinationHref", out var destinationHref)
            || !TryGetResource(arguments, out var resource)
            || !Uri.TryCreate(destinationHref, UriKind.Absolute, out _))
        {
            return false;
        }
        request = new CalendarExactCreateRequest(destinationHref, resource);
        return true;
    }

    public static bool TryParseReplace(
        IDictionary<string, JsonElement>? arguments,
        out CalendarExactReplaceRequest request)
    {
        request = default!;
        if (arguments is null
            || !HasExactResourceProperties(arguments, ReplaceProperties)
            || !arguments.TryGetValue("revision", out var revisionElement)
            || !TryParseRevision(revisionElement, out var revision)
            || !TryGetResource(arguments, out var resource))
        {
            return false;
        }
        request = new CalendarExactReplaceRequest(revision, resource);
        return true;
    }

    public static bool TryParseMove(
        IDictionary<string, JsonElement>? arguments,
        out CalendarExactMoveRequest request)
    {
        request = default!;
        if (arguments is null
            || !HasExactProperties(arguments.Keys, MoveProperties)
            || !arguments.TryGetValue("revision", out var revisionElement)
            || !TryParseRevision(revisionElement, out var revision)
            || !TryGetString(arguments, "destinationHref", out var destinationHref)
            || !Uri.TryCreate(destinationHref, UriKind.Absolute, out _))
        {
            return false;
        }
        request = new CalendarExactMoveRequest(revision, destinationHref);
        return true;
    }

    private static bool TryParseRevision(JsonElement element, out CalendarResourceRevisionReference revision)
    {
        revision = default!;
        if (element.ValueKind != JsonValueKind.Object
            || !HasExactProperties(element.EnumerateObject().Select(property => property.Name), RevisionProperties)
            || !TryGetString(element, "href", out var href)
            || !TryGetString(element, "entityUid", out var uid)
            || !TryGetString(element, "entityKind", out var kindText)
            || !TryGetString(element, "entityTag", out var entityTag)
            || !Uri.TryCreate(href, UriKind.Absolute, out _)
            || !TryParseKind(kindText, out var kind)
            || !IsExactEntityTag(entityTag))
        {
            return false;
        }
        revision = new CalendarResourceRevisionReference(href, uid, kind, entityTag);
        return true;
    }

    private static bool TryGetString(
        IDictionary<string, JsonElement> owner,
        string name,
        out string value)
    {
        value = string.Empty;
        return owner.TryGetValue(name, out var element) && TryGetString(element, out value);
    }

    private static bool TryGetString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        return owner.TryGetProperty(name, out var element) && TryGetString(element, out value);
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        try
        {
            return element.GetString() is { Length: > 0 } text
                && (value = text) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetResource(
        IDictionary<string, JsonElement> arguments,
        out byte[] resource)
    {
        resource = [];
        if (TryGetString(arguments, "utf8Resource", out var text))
        {
            try
            {
                resource = StrictUtf8.GetBytes(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
        if (!TryGetString(arguments, "base64Utf8Resource", out var encoded))
            return false;
        try
        {
            resource = Convert.FromBase64String(encoded);
            return Convert.ToBase64String(resource).Equals(encoded, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseKind(string value, out CalendarEntityKind kind)
    {
        kind = value == "event" ? CalendarEntityKind.Event : CalendarEntityKind.Todo;
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

    private static bool HasExactResourceProperties(
        IDictionary<string, JsonElement> arguments,
        IReadOnlySet<string> allowed)
    {
        var properties = arguments.Keys.ToArray();
        return properties.Length == 2
            && properties.All(allowed.Contains)
            && arguments.ContainsKey("utf8Resource") != arguments.ContainsKey("base64Utf8Resource");
    }
}
