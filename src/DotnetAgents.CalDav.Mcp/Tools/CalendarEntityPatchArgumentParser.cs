using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class CalendarEntityPatchArgumentParser
{
    private static readonly HashSet<string> EventFields = new(StringComparer.Ordinal)
    {
        "summary", "description", "start", "end", "duration", "location", "geo", "status",
        "transparency", "classification", "priority", "url", "organizer", "recurrenceSet"
    };
    private static readonly HashSet<string> TodoFields = new(StringComparer.Ordinal)
    {
        "summary", "description", "start", "due", "duration", "status", "priority", "percentComplete",
        "organizer", "recurrenceSet"
    };

    public static bool TryParseEvent(
        IDictionary<string, JsonElement>? arguments,
        out CalendarEventPatchRequest request)
    {
        request = null!;
        if (!TryRoot(arguments, "event", out var revision, out var target, out var patch)
            || !TryParseScalars(patch, EventFields, out var scalars, out var recurrence)
            || !TryParseCollections(patch, out var categories, out var collections))
            return false;
        request = new CalendarEventPatchRequest(revision, target, new CalendarEventPatch(
            Summary: Get<string>(scalars, "summary"),
            Description: Get<string>(scalars, "description"),
            Start: Get<CalendarTemporalValue>(scalars, "start"),
            End: Get<CalendarTemporalValue>(scalars, "end"),
            Duration: Get<string>(scalars, "duration"),
            Location: Get<string>(scalars, "location"),
            Geo: Get<CalendarGeo>(scalars, "geo"),
            Status: Get<string>(scalars, "status"),
            Transparency: Get<string>(scalars, "transparency"),
            Classification: Get<string>(scalars, "classification"),
            Priority: Get<int>(scalars, "priority"),
            Url: Get<string>(scalars, "url"),
            Organizer: Get<CalendarNamedUri>(scalars, "organizer"),
            Categories: categories,
            Collections: collections,
            RecurrenceSetAddressed: recurrence));
        return true;
    }

    public static bool TryParseTodo(
        IDictionary<string, JsonElement>? arguments,
        out CalendarTodoPatchRequest request)
    {
        request = null!;
        if (!TryRoot(arguments, "todo", out var revision, out var target, out var patch)
            || !TryParseScalars(patch, TodoFields, out var scalars, out var recurrence)
            || !TryParseCollections(patch, out var categories, out var collections))
            return false;
        request = new CalendarTodoPatchRequest(revision, target, new CalendarTodoPatch(
            Summary: Get<string>(scalars, "summary"),
            Description: Get<string>(scalars, "description"),
            Start: Get<CalendarTemporalValue>(scalars, "start"),
            Due: Get<CalendarTemporalValue>(scalars, "due"),
            Duration: Get<string>(scalars, "duration"),
            Status: Get<string>(scalars, "status"),
            Priority: Get<int>(scalars, "priority"),
            PercentComplete: Get<int>(scalars, "percentComplete"),
            Organizer: Get<CalendarNamedUri>(scalars, "organizer"),
            Categories: categories,
            Collections: collections,
            RecurrenceSetAddressed: recurrence));
        return true;
    }

    private static bool TryRoot(
        IDictionary<string, JsonElement>? arguments,
        string expectedKind,
        out CalendarResourceRevisionReference revision,
        out CalendarMutationTarget target,
        out JsonElement patch)
    {
        revision = null!;
        target = null!;
        patch = default;
        return arguments is not null
            && arguments.Count == 3
            && arguments.TryGetValue("snapshot", out var snapshot)
            && arguments.TryGetValue("target", out var targetElement)
            && arguments.TryGetValue("patch", out patch)
            && TryRevision(snapshot, expectedKind, out revision)
            && TryMaster(targetElement, out target)
            && HasPatchShape(patch);
    }

    private static bool TryRevision(
        JsonElement value,
        string expectedKind,
        out CalendarResourceRevisionReference revision)
    {
        revision = null!;
        if (!HasExactProperties(value, "href", "entityUid", "entityKind", "entityTag")
            || !TryString(value, "href", out var href)
            || !TryString(value, "entityUid", out var uid)
            || !TryString(value, "entityKind", out var kind)
            || !TryString(value, "entityTag", out var tag)
            || kind != expectedKind)
            return false;
        revision = new(href, uid, expectedKind == "event" ? CalendarEntityKind.Event : CalendarEntityKind.Todo, tag);
        return true;
    }

    private static bool TryMaster(JsonElement value, out CalendarMutationTarget target)
    {
        target = null!;
        if (!HasExactProperties(value, "scope") || !TryString(value, "scope", out var scope) || scope != "master")
            return false;
        target = new(scope);
        return true;
    }

    private static bool HasPatchShape(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
            return false;
        var names = patch.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length is 1 or 2
            && names.All(name => name is "scalars" or "collections")
            && names.Distinct(StringComparer.Ordinal).Count() == names.Length;
    }

    private static bool TryParseScalars(
        JsonElement patch,
        IReadOnlySet<string> allowedFields,
        out Dictionary<string, ScalarValue> scalars,
        out bool recurrence)
    {
        scalars = new(StringComparer.Ordinal);
        recurrence = false;
        if (!patch.TryGetProperty("scalars", out var values))
            return true;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            return false;
        foreach (var value in values.EnumerateArray())
        {
            if (!TryParseScalar(value, allowedFields, out var field, out var scalar) || !scalars.TryAdd(field, scalar))
                return false;
            recurrence |= field == "recurrenceSet";
        }
        return true;
    }

    private static bool TryParseScalar(
        JsonElement value,
        IReadOnlySet<string> allowedFields,
        out string field,
        out ScalarValue scalar)
    {
        field = string.Empty;
        scalar = default;
        if (!TryString(value, "field", out field)
            || !allowedFields.Contains(field)
            || !TryString(value, "operation", out var operation))
            return false;
        return operation == "clear"
            ? TryClearScalar(value, field, out scalar)
            : TrySetScalar(value, field, operation, out scalar);
    }

    private static bool TryClearScalar(JsonElement value, string field, out ScalarValue scalar)
    {
        scalar = new(CalendarScalarPatchOperation.Clear, null);
        return HasExactProperties(value, "field", "operation");
    }

    private static bool TrySetScalar(
        JsonElement value,
        string field,
        string operation,
        out ScalarValue scalar)
    {
        scalar = default;
        if (operation != "set" || !HasExactProperties(value, "field", "operation", "value"))
            return false;
        if (field == "recurrenceSet")
        {
            scalar = new(CalendarScalarPatchOperation.Set, value.GetProperty("value"));
            return true;
        }
        if (!CalendarEntityCreateArgumentParser.TryParsePatchScalarValue(field, value.GetProperty("value"), out var parsed))
            return false;
        scalar = new(CalendarScalarPatchOperation.Set, parsed);
        return true;
    }

    private static CalendarScalarPatch<T>? Get<T>(IReadOnlyDictionary<string, ScalarValue> values, string field)
    {
        if (!values.TryGetValue(field, out var scalar))
            return null;
        return scalar.Operation == CalendarScalarPatchOperation.Clear
            ? new(scalar.Operation)
            : new(scalar.Operation, (T)scalar.Value!);
    }

    private static bool TryParseCollections(
        JsonElement patch,
        out CalendarCollectionPatch<string>? categories,
        out IReadOnlyList<ICalendarCollectionPatch>? collections)
    {
        categories = null;
        collections = null;
        if (!patch.TryGetProperty("collections", out var values))
            return true;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            return false;
        var parsed = new List<ICalendarCollectionPatch>();
        var seen = new HashSet<CalendarCollectionField>();
        foreach (var value in values.EnumerateArray())
        {
            if (!TryParseCollection(value, out var item) || !seen.Add(item.Field))
                return false;
            if (item is CalendarCollectionPatch<string> category && item.Field == CalendarCollectionField.Categories)
                categories = category;
            else
                parsed.Add(item);
        }
        collections = parsed.Count == 0 ? null : parsed;
        return true;
    }

    private static bool TryParseCollection(JsonElement value, out ICalendarCollectionPatch patch)
    {
        patch = null!;
        if (!TryString(value, "field", out var field)
            || !TryField(field, out var fieldCode)
            || !TryString(value, "operation", out var operation))
            return false;
        return operation == "replaceAll"
            ? TryReplaceAllCollection(value, field, fieldCode, out patch)
            : TryAddRemoveCollection(value, field, fieldCode, operation, out patch);
    }

    private static bool TryReplaceAllCollection(
        JsonElement value,
        string field,
        CalendarCollectionField fieldCode,
        out ICalendarCollectionPatch patch)
    {
        patch = null!;
        if (!HasExactProperties(value, "field", "operation", "values")
            || !TryItems(field, value.GetProperty("values"), out var items))
            return false;
        patch = CreatePatch(fieldCode, CalendarCollectionPatchOperation.ReplaceAll, null, null, items);
        return true;
    }

    private static bool TryAddRemoveCollection(
        JsonElement value,
        string field,
        CalendarCollectionField fieldCode,
        string operation,
        out ICalendarCollectionPatch patch)
    {
        patch = null!;
        if (operation != "addRemove" || !HasAddRemoveShape(value))
            return false;
        if (!TryOptionalItems(value, field, "add", out var add)
            || !TryOptionalItems(value, field, "remove", out var remove)
            || !HasAnyItem(add, remove))
            return false;
        patch = CreatePatch(fieldCode, CalendarCollectionPatchOperation.AddRemove, add, remove, null);
        return true;
    }

    private static bool HasAnyItem(IReadOnlyList<object>? add, IReadOnlyList<object>? remove) =>
        (add?.Count ?? 0) > 0 || (remove?.Count ?? 0) > 0;

    private static bool HasAddRemoveShape(JsonElement value)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length is 3 or 4
            && names.Contains("field", StringComparer.Ordinal)
            && names.Contains("operation", StringComparer.Ordinal)
            && (names.Contains("add", StringComparer.Ordinal) || names.Contains("remove", StringComparer.Ordinal))
            && names.All(name => name is "field" or "operation" or "add" or "remove");
    }

    private static bool TryOptionalItems(
        JsonElement owner,
        string field,
        string name,
        out IReadOnlyList<object>? items)
    {
        items = null;
        return !owner.TryGetProperty(name, out var value) || TryItems(field, value, out items);
    }

    private static bool TryItems(string field, JsonElement value, out IReadOnlyList<object> items)
    {
        items = [];
        if (value.ValueKind != JsonValueKind.Array)
            return false;
        var parsed = new List<object>();
        foreach (var item in value.EnumerateArray())
        {
            if (field == "categories")
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;
                parsed.Add(item.GetString()!);
            }
            else if (!CalendarEntityCreateArgumentParser.TryParseStructuredCollectionItem(field, item, out var structured))
                return false;
            else
                parsed.Add(structured);
        }
        items = parsed;
        return true;
    }

    private static ICalendarCollectionPatch CreatePatch(
        CalendarCollectionField field,
        CalendarCollectionPatchOperation operation,
        IReadOnlyList<object>? add,
        IReadOnlyList<object>? remove,
        IReadOnlyList<object>? values) => field switch
        {
            CalendarCollectionField.Categories => Typed<string>(field, operation, add, remove, values),
            CalendarCollectionField.Attendees => Typed<CalendarAttendee>(field, operation, add, remove, values),
            CalendarCollectionField.Participants => Typed<CalendarParticipant>(field, operation, add, remove, values),
            CalendarCollectionField.Contacts or CalendarCollectionField.Resources or CalendarCollectionField.Comments
                or CalendarCollectionField.StyledDescriptions => Typed<CalendarTextValue>(field, operation, add, remove, values),
            CalendarCollectionField.RelatedTo => Typed<CalendarRelation>(field, operation, add, remove, values),
            CalendarCollectionField.RequestStatuses => Typed<CalendarRequestStatus>(field, operation, add, remove, values),
            CalendarCollectionField.Alarms => Typed<CalendarAlarm>(field, operation, add, remove, values),
            CalendarCollectionField.Attachments or CalendarCollectionField.Images or CalendarCollectionField.Conferences
                or CalendarCollectionField.Links or CalendarCollectionField.LocationUris or CalendarCollectionField.ResourceUris =>
                Typed<CalendarNamedUri>(field, operation, add, remove, values),
            _ => Typed<CalendarUriValue>(field, operation, add, remove, values)
        };

    private static CalendarCollectionPatch<T> Typed<T>(
        CalendarCollectionField field,
        CalendarCollectionPatchOperation operation,
        IReadOnlyList<object>? add,
        IReadOnlyList<object>? remove,
        IReadOnlyList<object>? values) => new(
        operation,
        add?.Cast<T>().ToArray(),
        remove?.Cast<T>().ToArray(),
        values?.Cast<T>().ToArray(),
        field);

    private static bool TryField(string field, out CalendarCollectionField parsed)
    {
        var candidate = field switch
        {
            "categories" => CalendarCollectionField.Categories,
            "attendees" => CalendarCollectionField.Attendees,
            "participants" => CalendarCollectionField.Participants,
            "contacts" => CalendarCollectionField.Contacts,
            "resources" => CalendarCollectionField.Resources,
            "relatedTo" => CalendarCollectionField.RelatedTo,
            "requestStatuses" => CalendarCollectionField.RequestStatuses,
            "alarms" => CalendarCollectionField.Alarms,
            "attachments" => CalendarCollectionField.Attachments,
            "comments" => CalendarCollectionField.Comments,
            "styledDescriptions" => CalendarCollectionField.StyledDescriptions,
            "images" => CalendarCollectionField.Images,
            "conferences" => CalendarCollectionField.Conferences,
            "links" => CalendarCollectionField.Links,
            "concepts" => CalendarCollectionField.Concepts,
            "structuredDataUris" => CalendarCollectionField.StructuredDataUris,
            "locationUris" => CalendarCollectionField.LocationUris,
            "resourceUris" => CalendarCollectionField.ResourceUris,
            _ => (CalendarCollectionField?)null
        };
        parsed = candidate.GetValueOrDefault();
        return candidate.HasValue;
    }

    private static bool TryString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return true;
    }

    private static bool HasExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.Length == expected.Count && actual.All(expected.Contains);
    }

    private readonly record struct ScalarValue(CalendarScalarPatchOperation Operation, object? Value);
}
