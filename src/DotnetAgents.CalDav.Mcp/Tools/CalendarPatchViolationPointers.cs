using System.Globalization;
using System.Text.Json;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Resolves semantic patch reasons to the caller's <c>patch.scalars</c> and <c>patch.collections</c> items.</summary>
internal static class CalendarPatchViolationPointers
{
    private const string FieldsPrefix = "/fields/";
    private const string CollectionsPrefix = "/collections/";

    /// <summary>
    /// Returns the argument pointer for a semantic pointer, or <see langword="null"/> when the request does not
    /// address that field, so a reason about stored data never blames an argument the caller did not send.
    /// </summary>
    internal static string? Resolve(string pointer, IDictionary<string, JsonElement>? arguments)
    {
        if (pointer.StartsWith("/target/", StringComparison.Ordinal) || pointer.StartsWith("/snapshot/", StringComparison.Ordinal))
            return pointer;
        if (pointer.StartsWith(FieldsPrefix, StringComparison.Ordinal))
            return ResolveItem(arguments, "scalars", pointer[FieldsPrefix.Length..], ScalarSuffix);
        return pointer.StartsWith(CollectionsPrefix, StringComparison.Ordinal)
            ? ResolveItem(arguments, "collections", pointer[CollectionsPrefix.Length..], rest => rest)
            : null;
    }

    /// <summary>Recurrence reconciliation sits beside the scalar value; every other detail is inside it.</summary>
    private static string ScalarSuffix(string rest) =>
        rest.StartsWith("/orphanReconciliations", StringComparison.Ordinal) ? rest : "/value" + rest;

    private static string? ResolveItem(
        IDictionary<string, JsonElement>? arguments,
        string collection,
        string fieldPath,
        Func<string, string> suffix)
    {
        var separator = fieldPath.IndexOf('/', StringComparison.Ordinal);
        var field = separator < 0 ? fieldPath : fieldPath[..separator];
        var rest = separator < 0 ? string.Empty : fieldPath[separator..];
        var index = FindItem(arguments, collection, field);
        return index is null
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"/patch/{collection}/{index}") + suffix(rest);
    }

    private static int? FindItem(IDictionary<string, JsonElement>? arguments, string collection, string field)
    {
        if (arguments is null
            || !arguments.TryGetValue("patch", out var patch)
            || patch.ValueKind != JsonValueKind.Object
            || !patch.TryGetProperty(collection, out var items)
            || items.ValueKind != JsonValueKind.Array)
            return null;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("field", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.ValueEquals(field))
                return index;
            index++;
        }
        return null;
    }
}
