using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarResourceSemanticProjectionMapper
{
    private static readonly JsonSerializerOptions ProjectionJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static JsonElement Event(CalendarResourceSnapshot snapshot) =>
        JsonSerializer.SerializeToElement(CreateEventFields(snapshot), ProjectionJson);

    private static CalendarEventFieldsResult CreateEventFields(CalendarResourceSnapshot snapshot)
    {
        if (!TryDocumentMaster(snapshot, "VEVENT", out var document, out var master))
            return new CalendarEventFieldsResult(
                snapshot.Projection.Summary,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        return EventFields(document, master, includeRecurrence: true);
    }

    private static CalendarEventFieldsResult EventFields(
        CalendarContentDocument document,
        CalendarContentComponent component,
        bool includeRecurrence)
    {
        var properties = Owned(document, component.Path);
        return new CalendarEventFieldsResult(
            Text(properties, "SUMMARY"),
            Text(properties, "DESCRIPTION"),
            Temporal(properties, "DTSTART"),
            Temporal(properties, "DTEND"),
            Raw(properties, "DURATION"),
            Text(properties, "LOCATION"),
            Geo(properties, "GEO"),
            OpenEnum(properties, "STATUS", ["TENTATIVE", "CONFIRMED", "CANCELLED"]),
            OpenEnum(properties, "TRANSP", ["OPAQUE", "TRANSPARENT"]),
            OpenEnum(properties, "CLASS", ["PUBLIC", "PRIVATE", "CONFIDENTIAL"]),
            Integer(properties, "PRIORITY"),
            TextList(properties, "CATEGORIES"),
            Raw(properties, "URL"),
            includeRecurrence ? Recurrence(document, component, "event") : null,
            StructuredData(document, component));
    }

    internal static JsonElement Todo(CalendarResourceSnapshot snapshot) =>
        JsonSerializer.SerializeToElement(CreateTodoFields(snapshot), ProjectionJson);

    internal static JsonElement TodoForOccurrence(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity) =>
        JsonSerializer.SerializeToElement(CreateTodoFieldsForOccurrence(snapshot, recurrenceIdentity), ProjectionJson);

    private static CalendarTodoFieldsResult CreateTodoFields(CalendarResourceSnapshot snapshot)
    {
        if (!TryDocumentMaster(snapshot, "VTODO", out var document, out var master))
            return new CalendarTodoFieldsResult(
                snapshot.Projection.Summary,
                null, null, null, null, null, null, null, null, null, null, null);
        return TodoFields(document, master, includeRecurrence: true);
    }

    internal static JsonElement? TodoCompletedAt(CalendarResourceSnapshot snapshot)
    {
        if (!TryDocumentMaster(snapshot, "VTODO", out var document, out var master))
            return null;
        var completed = Temporal(Owned(document, master.Path), "COMPLETED");
        return completed is null ? null : JsonSerializer.SerializeToElement(completed, ProjectionJson);
    }

    internal static JsonElement? TodoCompletedAtForOccurrence(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity)
    {
        try
        {
            var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            var component = CalendarTodoComponentSelector.Select(document, recurrenceIdentity);
            var completed = Temporal(Owned(document, component.Path), "COMPLETED");
            return completed is null ? null : JsonSerializer.SerializeToElement(completed, ProjectionJson);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static CalendarTodoFieldsResult CreateTodoFieldsForOccurrence(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity)
    {
        if (!TryDocumentMaster(snapshot, "VTODO", out var document, out var master))
            return new CalendarTodoFieldsResult(
                snapshot.Projection.Summary,
                null, null, null, null, null, null, null, null, null, null, null);
        var effective = CalendarTodoComponentSelector.Select(document, recurrenceIdentity);
        return TodoFields(document, effective, includeRecurrence: true, recurrenceComponent: master);
    }

    private static bool TryDocumentMaster(
        CalendarResourceSnapshot snapshot,
        string componentName,
        out CalendarContentDocument document,
        out CalendarContentComponent master)
    {
        try
        {
            document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
            master = Master(document, componentName);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            document = null!;
            master = null!;
            return false;
        }
    }

    private static CalendarTodoFieldsResult TodoFields(
        CalendarContentDocument document,
        CalendarContentComponent component,
        bool includeRecurrence,
        CalendarContentComponent? recurrenceComponent = null)
    {
        var properties = Owned(document, component.Path);
        return new CalendarTodoFieldsResult(
            Text(properties, "SUMMARY"),
            Text(properties, "DESCRIPTION"),
            Temporal(properties, "DTSTART"),
            Temporal(properties, "DUE"),
            Raw(properties, "DURATION"),
            OpenEnum(properties, "STATUS", ["NEEDS-ACTION", "COMPLETED", "IN-PROCESS", "CANCELLED"]),
            OpenEnum(properties, "CLASS", ["PUBLIC", "PRIVATE", "CONFIDENTIAL"]),
            Integer(properties, "PRIORITY"),
            Integer(properties, "PERCENT-COMPLETE"),
            TextList(properties, "CATEGORIES"),
            includeRecurrence ? Recurrence(document, recurrenceComponent ?? component, "todo") : null,
            StructuredData(document, component));
    }

    private static CalendarContentComponent Master(CalendarContentDocument document, string componentName) =>
        document.Components.Single(component => component.Path.Count == 2
            && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase)
            && !Owned(document, component.Path).Any(property =>
                property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase)));

    private static CalendarContentProperty[] Owned(
        CalendarContentDocument document,
        IReadOnlyList<CalendarComponentPathSegment> path) => document.Properties
        .Where(property => property.ComponentPath.SequenceEqual(path))
        .ToArray();

    private static CalendarContentProperty? Property(
        IEnumerable<CalendarContentProperty> properties,
        string name) => properties.SingleOrDefault(property =>
        property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Raw(IEnumerable<CalendarContentProperty> properties, string name) =>
        Property(properties, name)?.RawEncodedValue;

    private static string? Text(IEnumerable<CalendarContentProperty> properties, string name)
    {
        var raw = Raw(properties, name);
        return raw is null ? null : CalendarContentDocument.DecodeText(raw);
    }

    private static int? Integer(IEnumerable<CalendarContentProperty> properties, string name) =>
        int.TryParse(Raw(properties, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static CalendarTemporalResult? Temporal(
        IEnumerable<CalendarContentProperty> properties,
        string name)
    {
        var property = Property(properties, name);
        return property is null ? null : CalendarTemporalResult.FromValue(CalendarPatchValueSerializer.ParseTemporal(property));
    }

    private static CalendarGeoResult? Geo(IEnumerable<CalendarContentProperty> properties, string name)
    {
        var parts = Raw(properties, name)?.Split(';');
        return parts is { Length: 2 }
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                ? new CalendarGeoResult(latitude, longitude)
                : null;
    }

    private static CalendarOpenEnumResult? OpenEnum(
        IEnumerable<CalendarContentProperty> properties,
        string name,
        IReadOnlyList<string> recognized)
    {
        var raw = Raw(properties, name);
        if (raw is null)
            return null;
        var kind = recognized.Contains(raw, StringComparer.OrdinalIgnoreCase)
            ? raw.ToLowerInvariant()
            : "other";
        return new CalendarOpenEnumResult(kind, raw);
    }

    private static IReadOnlyList<string>? TextList(
        IEnumerable<CalendarContentProperty> properties,
        string name)
    {
        var values = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => SplitEscaped(property.RawEncodedValue, ','))
            .Select(CalendarContentDocument.DecodeText)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static JsonElement? Recurrence(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string entityKind)
    {
        var masterProperties = Owned(document, master.Path);
        var rules = masterProperties.Where(property => property.Name.Equals("RRULE", StringComparison.OrdinalIgnoreCase))
            .Select(property => Node(new CalendarRecurrenceRuleResult(property.RawEncodedValue, property.OriginalSlice)))
            .ToArray();
        var dates = RecurrenceValues(masterProperties, "RDATE");
        var exceptions = RecurrenceValues(masterProperties, "EXDATE");
        var overrides = document.Components.Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(master.Path[^1].Name, StringComparison.OrdinalIgnoreCase))
            .Select(component => (Component: component, Properties: Owned(document, component.Path)))
            .Where(item => Property(item.Properties, "RECURRENCE-ID") is not null)
            .Select(item => Override(document, item.Component, item.Properties, entityKind))
            .ToArray();
        if (rules.Length == 0 && dates.Count == 0 && exceptions.Count == 0 && overrides.Length == 0)
            return null;

        var recurrence = new JsonObject
        {
            ["evaluationState"] = rules.Length > 1
                || rules.Length > 0 && Property(masterProperties, "DTSTART") is null
                ? "unevaluable"
                : "evaluable",
            ["rrules"] = new JsonArray(rules),
            ["rdates"] = dates,
            ["exdates"] = exceptions,
            ["overrides"] = new JsonArray(overrides)
        };
        return JsonSerializer.SerializeToElement(recurrence);
    }

    private static JsonArray RecurrenceValues(
        IReadOnlyList<CalendarContentProperty> properties,
        string name)
    {
        var values = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => SplitEscaped(property.RawEncodedValue, ',').Select(value => (property, value)))
            .Select(item => RecurrenceValue(item.property, item.value))
            .ToArray();
        return new JsonArray(values);
    }

    private static JsonNode RecurrenceValue(CalendarContentProperty source, string raw)
    {
        if (!raw.Contains('/', StringComparison.Ordinal))
            return Node(CalendarTemporalResult.FromValue(
                CalendarPatchValueSerializer.ParseTemporal(source with { RawEncodedValue = raw })));
        var parts = raw.Split('/', 2);
        var start = CalendarTemporalResult.FromValue(
            CalendarPatchValueSerializer.ParseTemporal(source with { RawEncodedValue = parts[0] }));
        var period = new JsonObject { ["kind"] = "period", ["start"] = Node(start) };
        if (parts[1].StartsWith('P') || parts[1].StartsWith("+P", StringComparison.Ordinal))
            period["duration"] = parts[1];
        else
            period["end"] = Node(CalendarTemporalResult.FromValue(
                CalendarPatchValueSerializer.ParseTemporal(source with { RawEncodedValue = parts[1] })));
        return period;
    }

    private static JsonNode Override(
        CalendarContentDocument document,
        CalendarContentComponent component,
        IReadOnlyList<CalendarContentProperty> properties,
        string entityKind)
    {
        var identity = Property(properties, "RECURRENCE-ID")!;
        var result = new JsonObject
        {
            ["recurrenceIdentity"] = new JsonObject
            {
                ["value"] = Node(CalendarTemporalResult.FromValue(CalendarPatchValueSerializer.ParseTemporal(identity)))
            },
            ["entityKind"] = entityKind,
            ["status"] = Raw(properties, "STATUS")?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true
                ? "cancelled"
                : "active",
            ["fields"] = entityKind == "event"
                ? Node(EventFields(document, component, includeRecurrence: false))
                : Node(TodoFields(document, component, includeRecurrence: false))
        };
        var range = Parameter(identity, "RANGE")?.SingleOrDefault();
        if (range is not null)
            result["range"] = range.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase)
                ? "this-and-future"
                : "this-and-prior";
        Add(result, "movedStart", Temporal(properties, "DTSTART"));
        Add(result, "movedEnd", Temporal(properties, entityKind == "event" ? "DTEND" : "DUE"));
        return result;
    }

    private static JsonElement? StructuredData(
        CalendarContentDocument document,
        CalendarContentComponent master)
    {
        var properties = Owned(document, master.Path);
        var result = new JsonObject();
        Add(result, "organizer", NamedUri(Property(properties, "ORGANIZER"), "CN"));
        AddArray(result, "attendees", Properties(properties, "ATTENDEE").Select(Attendee));
        var attendeeUris = Properties(properties, "ATTENDEE")
            .Select(property => property.RawEncodedValue)
            .ToHashSet(StringComparer.Ordinal);
        AddArray(result, "participants", ChildComponents(document, master, "PARTICIPANT")
            .Select(item => Participant(document, item, attendeeUris)));
        AddArray(result, "contacts", Properties(properties, "CONTACT").Select(TextProperty));
        AddArray(result, "resources", Properties(properties, "RESOURCES").Select(TextProperty));
        AddArray(result, "relatedTo", Properties(properties, "RELATED-TO").Select(Relation));
        AddArray(result, "requestStatuses", Properties(properties, "REQUEST-STATUS").Select(RequestStatus));
        AddArray(result, "alarms", ChildComponents(document, master, "VALARM").Select(item => Alarm(document, item)));
        AddArray(result, "attachments", Properties(properties, "ATTACH").Select(item => NamedUri(item, "LABEL")!));
        AddArray(result, "comments", Properties(properties, "COMMENT").Select(TextProperty));
        AddArray(result, "styledDescriptions", Properties(properties, "STYLED-DESCRIPTION").Select(TextProperty));
        AddArray(result, "images", Properties(properties, "IMAGE").Select(item => NamedUri(item, "LABEL")!));
        AddArray(result, "conferences", Properties(properties, "CONFERENCE").Select(item => NamedUri(item, "LABEL")!));
        AddArray(result, "links", Properties(properties, "LINK").Select(item => NamedUri(item, "LABEL")!));
        AddArray(result, "concepts", Properties(properties, "CONCEPT").Select(UriProperty));
        AddArray(result, "structuredDataUris", Properties(properties, "STRUCTURED-DATA").Select(UriProperty));
        AddArray(result, "locationUris", ChildComponents(document, master, "VLOCATION").Select(item => NamedComponent(document, item)));
        AddArray(result, "resourceUris", ChildComponents(document, master, "VRESOURCE").Select(item => NamedComponent(document, item)));
        return result.Count == 0 ? null : JsonSerializer.SerializeToElement(result);
    }

    private static JsonNode Participant(
        CalendarContentDocument document,
        CalendarContentComponent component,
        IReadOnlySet<string> attendeeUris)
    {
        var properties = Owned(document, component.Path);
        var result = new JsonObject
        {
            ["uid"] = TextProperty(Property(properties, "UID")!),
            ["participantType"] = OpenEnumNode(
                Property(properties, "PARTICIPANT-TYPE")!.RawEncodedValue,
                ["ACTIVE", "INACTIVE", "SPONSOR", "CONTACT", "BOOKING-CONTACT", "EMERGENCY-CONTACT", "PUBLICITY-CONTACT", "PLANNER-CONTACT", "PERFORMER", "SPEAKER"])
        };
        var calendarAddress = Property(properties, "CALENDAR-ADDRESS");
        Add(result, "calendarAddress", OptionalNode(calendarAddress, UriProperty));
        result["schedulable"] = calendarAddress is not null && attendeeUris.Contains(calendarAddress.RawEncodedValue);
        Add(result, "created", TemporalProperty(Property(properties, "CREATED")));
        Add(result, "description", OptionalNode(Property(properties, "DESCRIPTION"), TextProperty));
        Add(result, "timestamp", TemporalProperty(Property(properties, "DTSTAMP")));
        Add(result, "geo", GeoProperty(Property(properties, "GEO")));
        Add(result, "lastModified", TemporalProperty(Property(properties, "LAST-MODIFIED")));
        Add(result, "priority", IntegerProperty(Property(properties, "PRIORITY")));
        Add(result, "sequence", IntegerProperty(Property(properties, "SEQUENCE")));
        var status = Property(properties, "STATUS");
        Add(result, "status", status is null
            ? null
            : OpenEnumNode(status.RawEncodedValue, ["NEEDS-ACTION", "ACCEPTED", "DECLINED", "TENTATIVE", "DELEGATED", "COMPLETED", "IN-PROCESS", "CANCELLED"]));
        Add(result, "summary", OptionalNode(Property(properties, "SUMMARY"), TextProperty));
        Add(result, "url", OptionalNode(Property(properties, "URL"), UriProperty));
        AddArray(result, "attachments", Properties(properties, "ATTACH").Select(item => NamedUri(item, "LABEL")!));
        Add(result, "categories", TextListProperty(Property(properties, "CATEGORIES")));
        AddArray(result, "comments", Properties(properties, "COMMENT").Select(TextProperty));
        AddArray(result, "contacts", Properties(properties, "CONTACT").Select(TextProperty));
        AddArray(result, "locations", Properties(properties, "LOCATION").Select(TextProperty));
        AddArray(result, "requestStatuses", Properties(properties, "REQUEST-STATUS").Select(RequestStatus));
        AddArray(result, "relatedTo", Properties(properties, "RELATED-TO").Select(Relation));
        AddArray(result, "resources", Properties(properties, "RESOURCES").Select(TextProperty));
        AddArray(result, "styledDescriptions", Properties(properties, "STYLED-DESCRIPTION").Select(TextProperty));
        AddArray(result, "structuredDataUris", Properties(properties, "STRUCTURED-DATA").Select(UriProperty));
        AddArray(result, "locationUris", ChildComponents(document, component, "VLOCATION").Select(item => NamedComponent(document, item)));
        AddArray(result, "resourceUris", ChildComponents(document, component, "VRESOURCE").Select(item => NamedComponent(document, item)));
        return result;
    }

    private static JsonNode Alarm(CalendarContentDocument document, CalendarContentComponent component)
    {
        var properties = Owned(document, component.Path);
        var action = Property(properties, "ACTION")!;
        var result = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["value"] = OpenEnumNode(
                    CalendarContentDocument.DecodeText(action.RawEncodedValue),
                    ["DISPLAY", "AUDIO", "EMAIL"]),
                ["parameters"] = Parameters(action)
            },
            ["trigger"] = TextProperty(Property(properties, "TRIGGER")!)
        };
        Add(result, "description", OptionalNode(Property(properties, "DESCRIPTION"), TextProperty));
        Add(result, "repeat", IntegerProperty(Property(properties, "REPEAT")));
        Add(result, "duration", DurationProperty(Property(properties, "DURATION")));
        Add(result, "summary", OptionalNode(Property(properties, "SUMMARY"), TextProperty));
        AddArray(result, "attendees", Properties(properties, "ATTENDEE").Select(Attendee));
        AddArray(result, "attachments", Properties(properties, "ATTACH").Select(item => NamedUri(item, "LABEL")!));
        Add(result, "uid", OptionalNode(Property(properties, "UID"), TextProperty));
        Add(result, "acknowledged", TemporalProperty(Property(properties, "ACKNOWLEDGED")));
        Add(result, "proximity", OpenEnumProperty(
            Property(properties, "PROXIMITY"),
            ["ARRIVE", "DEPART", "CONNECT", "DISCONNECT"]));
        AddArray(result, "relatedTo", Properties(properties, "RELATED-TO").Select(Relation));
        AddArray(result, "proximityLocations", ChildComponents(document, component, "VLOCATION")
            .Select(item => NamedComponent(document, item)));
        return result;
    }

    private static JsonNode Attendee(CalendarContentProperty property)
    {
        var result = new JsonObject
        {
            ["uri"] = property.RawEncodedValue,
            ["parameters"] = Parameters(property)
        };
        Add(result, "commonName", Parameter(property, "CN")?.SingleOrDefault());
        Add(result, "role", EffectiveOpenEnum(
            property,
            "ROLE",
            "REQ-PARTICIPANT",
            ["CHAIR", "REQ-PARTICIPANT", "OPT-PARTICIPANT", "NON-PARTICIPANT"]));
        Add(result, "partStat", EffectiveOpenEnum(
            property,
            "PARTSTAT",
            "NEEDS-ACTION",
            ["NEEDS-ACTION", "ACCEPTED", "DECLINED", "TENTATIVE", "DELEGATED", "COMPLETED", "IN-PROCESS"]));
        Add(result, "cutype", EffectiveOpenEnum(
            property,
            "CUTYPE",
            "INDIVIDUAL",
            ["INDIVIDUAL", "GROUP", "RESOURCE", "ROOM", "UNKNOWN"]));
        var explicitRsvp = Parameter(property, "RSVP")?.SingleOrDefault();
        result["rsvp"] = new JsonObject
        {
            ["effectiveValue"] = explicitRsvp?.Equals("TRUE", StringComparison.OrdinalIgnoreCase) == true
        };
        if (explicitRsvp is not null)
            result["rsvp"]!["explicitValue"] = explicitRsvp.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        Add(result, "delegatedTo", Parameter(property, "DELEGATED-TO"));
        Add(result, "delegatedFrom", Parameter(property, "DELEGATED-FROM"));
        Add(result, "sentBy", Parameter(property, "SENT-BY")?.SingleOrDefault());
        Add(result, "directory", Parameter(property, "DIR")?.SingleOrDefault());
        return result;
    }

    private static JsonNode TextProperty(CalendarContentProperty property) => new JsonObject
    {
        ["value"] = CalendarContentDocument.DecodeText(property.RawEncodedValue),
        ["parameters"] = Parameters(property)
    };

    private static JsonNode UriProperty(CalendarContentProperty property) => new JsonObject
    {
        ["uri"] = property.RawEncodedValue,
        ["parameters"] = Parameters(property)
    };

    private static JsonNode? NamedUri(CalendarContentProperty? property, string labelParameter)
    {
        if (property is null)
            return null;
        var result = new JsonObject
        {
            ["uri"] = property.RawEncodedValue,
            ["parameters"] = Parameters(property)
        };
        Add(result, "label", Parameter(property, labelParameter)?.SingleOrDefault());
        return result;
    }

    private static JsonNode NamedComponent(CalendarContentDocument document, CalendarContentComponent component)
    {
        var properties = Owned(document, component.Path);
        var uid = Property(properties, "UID")!;
        var result = new JsonObject
        {
            ["uid"] = CalendarContentDocument.DecodeText(uid.RawEncodedValue),
            ["parameters"] = Parameters(uid)
        };
        Add(result, "name", OptionalNode(Property(properties, "NAME"), TextProperty));
        Add(result, "description", OptionalNode(Property(properties, "DESCRIPTION"), TextProperty));
        Add(result, "geo", GeoProperty(Property(properties, "GEO")));
        var isLocation = component.Path[^1].Name.Equals("VLOCATION", StringComparison.OrdinalIgnoreCase);
        var componentTypes = Property(properties, isLocation ? "LOCATION-TYPE" : "RESOURCE-TYPE");
        Add(result, "componentTypes", isLocation
            ? TextListProperty(componentTypes)
            : OpenEnumProperty(componentTypes,
                ["PROJECTOR", "ROOM", "REMOTE-CONFERENCE-AUDIO", "REMOTE-CONFERENCE-VIDEO"]));
        if (isLocation)
            Add(result, "url", OptionalNode(Property(properties, "URL"), UriProperty));
        AddArray(result, "relatedTo", Properties(properties, "RELATED-TO").Select(Relation));
        AddArray(result, "concepts", Properties(properties, "CONCEPT").Select(UriProperty));
        AddArray(result, "links", Properties(properties, "LINK").Select(item => NamedUri(item, "LABEL")!));
        AddArray(result, "structuredDataUris", Properties(properties, "STRUCTURED-DATA").Select(UriProperty));
        return result;
    }

    private static JsonNode? OpenEnumProperty(
        CalendarContentProperty? property,
        IReadOnlyList<string> recognized) => property is null
        ? null
        : new JsonObject
        {
            ["value"] = OpenEnumNode(CalendarContentDocument.DecodeText(property.RawEncodedValue), recognized),
            ["parameters"] = Parameters(property)
        };

    private static JsonNode Relation(CalendarContentProperty property)
    {
        var result = new JsonObject
        {
            ["value"] = CalendarContentDocument.DecodeText(property.RawEncodedValue),
            ["parameters"] = Parameters(property)
        };
        Add(result, "relationType", EffectiveOpenEnum(
            property,
            "RELTYPE",
            "PARENT",
            ["PARENT", "CHILD", "SIBLING", "FINISHTOSTART", "FINISHTOFINISH", "STARTTOFINISH", "STARTTOSTART", "FIRST", "NEXT", "DEPENDS-ON", "REFID", "CONCEPT", "SNOOZE"]));
        return result;
    }

    private static JsonNode EffectiveOpenEnum(
        CalendarContentProperty property,
        string parameterName,
        string defaultValue,
        IReadOnlyList<string> recognized)
    {
        var explicitValue = Parameter(property, parameterName)?.SingleOrDefault();
        var result = new JsonObject
        {
            ["effectiveValue"] = OpenEnumNode(explicitValue ?? defaultValue, recognized)
        };
        if (explicitValue is not null)
            result["explicitValue"] = OpenEnumNode(explicitValue, recognized);
        return result;
    }

    private static JsonNode OpenEnumNode(string raw, IReadOnlyList<string> recognized) => new JsonObject
    {
        ["kind"] = recognized.Contains(raw, StringComparer.OrdinalIgnoreCase) ? raw.ToLowerInvariant() : "other",
        ["rawValue"] = raw
    };

    private static JsonNode RequestStatus(CalendarContentProperty property)
    {
        var parts = SplitEscaped(property.RawEncodedValue, ';').ToArray();
        var result = new JsonObject
        {
            ["code"] = parts[0],
            ["description"] = parts.Length > 1 ? CalendarContentDocument.DecodeText(parts[1]) : string.Empty,
            ["parameters"] = Parameters(property)
        };
        if (parts.Length > 2)
            result["exceptionData"] = CalendarContentDocument.DecodeText(string.Join(';', parts.Skip(2)));
        return result;
    }

    private static JsonNode? TemporalProperty(CalendarContentProperty? property) => property is null
        ? null
        : new JsonObject
        {
            ["value"] = Node(CalendarTemporalResult.FromValue(CalendarPatchValueSerializer.ParseTemporal(property))),
            ["parameters"] = Parameters(property)
        };

    private static JsonNode? GeoProperty(CalendarContentProperty? property)
    {
        if (property is null)
            return null;
        var geo = Geo([property], property.Name);
        return geo is null ? null : new JsonObject { ["value"] = Node(geo), ["parameters"] = Parameters(property) };
    }

    private static JsonNode? IntegerProperty(CalendarContentProperty? property) => property is null
        || !int.TryParse(property.RawEncodedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? null
            : new JsonObject { ["value"] = value, ["parameters"] = Parameters(property) };

    private static JsonNode? DurationProperty(CalendarContentProperty? property) => property is null
        ? null
        : new JsonObject { ["value"] = property.RawEncodedValue, ["parameters"] = Parameters(property) };

    private static JsonNode? TextListProperty(CalendarContentProperty? property) => property is null
        ? null
        : new JsonObject
        {
            ["value"] = Node(SplitEscaped(property.RawEncodedValue, ',').Select(CalendarContentDocument.DecodeText).ToArray()),
            ["parameters"] = Parameters(property)
        };

    private static IEnumerable<CalendarContentProperty> Properties(
        IEnumerable<CalendarContentProperty> properties,
        string name) => properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<CalendarContentComponent> ChildComponents(
        CalendarContentDocument document,
        CalendarContentComponent parent,
        string name) => document.Components.Where(component => component.Path.Count == parent.Path.Count + 1
        && component.Path.Take(parent.Path.Count).SequenceEqual(parent.Path)
        && component.Path[^1].Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string>? Parameter(CalendarContentProperty property, string name) => property.Parameters
        .FirstOrDefault(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Values;

    private static JsonArray Parameters(CalendarContentProperty property) => new(
        property.Parameters.Select(parameter => Node(new CalendarParameterResult(parameter.Name, parameter.Values))).ToArray());

    private static JsonNode? OptionalNode(
        CalendarContentProperty? property,
        Func<CalendarContentProperty, JsonNode> create) => property is null ? null : create(property);

    private static void Add(JsonObject target, string name, object? value)
    {
        if (value is null)
            return;
        target[name] = value is JsonNode node ? node : Node(value);
    }

    private static void AddArray(JsonObject target, string name, IEnumerable<JsonNode> values)
    {
        var items = values.ToArray();
        if (items.Length > 0)
            target[name] = new JsonArray(items);
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, ProjectionJson)!;

    private static IEnumerable<string> SplitEscaped(string value, char separator)
    {
        var start = 0;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!escaped && value[index] == separator)
            {
                yield return value[start..index];
                start = index + 1;
            }
            escaped = !escaped && value[index] == '\\';
        }
        yield return value[start..];
    }

    private sealed record CalendarEventFieldsResult(
        string? Summary,
        string? Description,
        CalendarTemporalResult? Start,
        CalendarTemporalResult? End,
        string? Duration,
        string? Location,
        CalendarGeoResult? Geo,
        CalendarOpenEnumResult? Status,
        CalendarOpenEnumResult? Transparency,
        CalendarOpenEnumResult? Classification,
        int? Priority,
        IReadOnlyList<string>? Categories,
        string? Url,
        JsonElement? RecurrenceSet,
        JsonElement? StructuredData);

    private sealed record CalendarTodoFieldsResult(
        string? Summary,
        string? Description,
        CalendarTemporalResult? Start,
        CalendarTemporalResult? Due,
        string? Duration,
        CalendarOpenEnumResult? Status,
        CalendarOpenEnumResult? Classification,
        int? Priority,
        int? PercentComplete,
        IReadOnlyList<string>? Categories,
        JsonElement? RecurrenceSet,
        JsonElement? StructuredData);

    private sealed record CalendarGeoResult(double Latitude, double Longitude);

    private sealed record CalendarRecurrenceRuleResult(string Text, string OriginalSlice);

    private sealed record CalendarOpenEnumResult(string Kind, string RawValue);

    private sealed record CalendarParameterResult(string Name, IReadOnlyList<string> Values);

    private sealed record CalendarTemporalResult(string Kind, string Value, string? TimeZoneId = null)
    {
        internal static CalendarTemporalResult FromValue(CalendarTemporalValue value) => new(
            value.Kind switch
            {
                CalendarTemporalKind.Date => "date",
                CalendarTemporalKind.FloatingDateTime => "floatingDateTime",
                CalendarTemporalKind.UtcDateTime => "utcDateTime",
                CalendarTemporalKind.ZonedDateTime => "zonedDateTime",
                _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, null)
            },
            value.Value,
            value.TimeZoneId);
    }
}
