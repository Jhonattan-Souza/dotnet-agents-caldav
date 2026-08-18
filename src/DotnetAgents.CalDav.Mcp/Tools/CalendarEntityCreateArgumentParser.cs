using System.Globalization;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class CalendarEntityCreateArgumentParser
{
    private static readonly HashSet<string> PatchTextFields = new(StringComparer.Ordinal)
    {
        "summary", "description", "duration", "location", "status", "transparency", "classification", "url"
    };
    private static readonly HashSet<string> PatchTemporalFields = new(StringComparer.Ordinal)
    {
        "start", "end", "due", "completed"
    };
    private static readonly string[] RootProperties = ["destination", "entity"];
    private static readonly string[] EventEntityProperties = ["kind", "uid", "fields"];
    private static readonly string[] TodoEntityProperties = ["kind", "uid", "fields"];
    private static readonly string[] EventFieldProperties =
    [
        "summary", "description", "start", "end", "duration", "location", "geo", "status", "transparency",
        "classification", "priority", "categories", "url", "recurrenceSet", "structuredData"
    ];
    private static readonly string[] TodoFieldProperties =
    ["summary", "description", "start", "due", "duration", "status", "priority", "categories", "recurrenceSet", "structuredData"];
    private static readonly string[] EventOverrideFieldProperties =
        EventFieldProperties.Where(name => name != "recurrenceSet").ToArray();
    private static readonly string[] TodoOverrideFieldProperties =
        TodoFieldProperties.Where(name => name != "recurrenceSet").ToArray();
    private static readonly string[] StructuredProperties =
    [
        "organizer", "attendees", "participants", "contacts", "resources", "relatedTo", "requestStatuses", "alarms",
        "attachments", "comments", "styledDescriptions", "images", "conferences", "links", "concepts",
        "structuredDataUris", "locationUris", "resourceUris"
    ];

    internal static bool TryParseStructuredCollectionItem(
        string field,
        JsonElement value,
        out object item)
    {
        item = null!;
        return field switch
        {
            "attendees" => TryBox<CalendarAttendee>(value, TryParseAttendee, out item),
            "participants" => TryBox<CalendarParticipant>(value, TryParseParticipant, out item),
            "contacts" or "resources" or "comments" or "styledDescriptions" =>
                TryBox<CalendarTextValue>(value, TryParseTextValue, out item),
            "relatedTo" => TryBox<CalendarRelation>(value, TryParseRelation, out item),
            "requestStatuses" => TryBox<CalendarRequestStatus>(value, TryParseRequestStatus, out item),
            "alarms" => TryBox<CalendarAlarm>(value, TryParseAlarm, out item),
            "attachments" or "images" or "conferences" or "links" =>
                TryBox<CalendarNamedUri>(value, TryParseNamedUri, out item),
            "locationUris" or "resourceUris" =>
                TryBox<CalendarNamedComponent>(value, TryParseNamedComponent, out item),
            "concepts" or "structuredDataUris" => TryBox<CalendarUriValue>(value, TryParseUriValue, out item),
            _ => false
        };
    }

    internal static bool TryParsePatchScalarValue(string field, JsonElement value, out object parsed)
    {
        parsed = null!;
        if (PatchTextFields.Contains(field))
        {
            if (value.ValueKind != JsonValueKind.String)
                return false;
            parsed = value.GetString()!;
            return true;
        }
        if (field is "priority" or "percentComplete")
        {
            if (!value.TryGetInt32(out var integer))
                return false;
            parsed = integer;
            return true;
        }
        return TryParseComplexPatchScalarValue(field, value, out parsed);
    }

    private static bool TryParseComplexPatchScalarValue(string field, JsonElement value, out object parsed)
    {
        parsed = null!;
        if (PatchTemporalFields.Contains(field))
        {
            if (!TryParseTemporal(value, out var temporal) || temporal is null)
                return false;
            parsed = temporal;
            return true;
        }
        if (field == "geo")
        {
            if (!TryParseGeo(value, out var geo) || geo is null)
                return false;
            parsed = geo;
            return true;
        }
        return field == "organizer" && TryBox<CalendarNamedUri>(value, TryParseNamedUri, out parsed);
    }

    private static bool TryBox<T>(JsonElement value, TryParseValue<T> parser, out object item)
    {
        item = null!;
        if (!parser(value, out var parsed))
            return false;
        item = parsed!;
        return true;
    }

    private delegate bool TryParseValue<T>(JsonElement value, out T parsed);

    public static bool TryParseEvent(
        IDictionary<string, JsonElement>? arguments,
        out CalendarEventCreateRequest request)
    {
        request = null!;
        if (!TryGetRoot(arguments, out var destinationElement, out var entity)
            || !HasShape(entity, EventEntityProperties, ["kind", "fields"])
            || !TryRequiredString(entity, "kind", out var kind)
            || kind != "event"
            || !TryOptionalString(entity, "uid", out var uid)
            || !entity.TryGetProperty("fields", out var fields)
            || !TryParseEventFields(fields, out var parsedFields)
            || !TryParseDestination(destinationElement, out var destination))
        {
            return false;
        }
        request = new CalendarEventCreateRequest(destination, uid, parsedFields);
        return true;
    }

    public static bool TryParseTodo(
        IDictionary<string, JsonElement>? arguments,
        out CalendarTodoCreateRequest request)
    {
        request = null!;
        if (!TryGetRoot(arguments, out var destinationElement, out var entity)
            || !HasShape(entity, TodoEntityProperties, ["kind", "fields"])
            || !TryRequiredString(entity, "kind", out var kind)
            || kind != "todo"
            || !TryOptionalString(entity, "uid", out var uid)
            || !entity.TryGetProperty("fields", out var fields)
            || !TryParseTodoFields(fields, out var parsedFields)
            || !TryParseDestination(destinationElement, out var destination))
        {
            return false;
        }
        request = new CalendarTodoCreateRequest(destination, uid, parsedFields);
        return true;
    }

    private static bool TryGetRoot(
        IDictionary<string, JsonElement>? arguments,
        out JsonElement destination,
        out JsonElement entity)
    {
        destination = default;
        entity = default;
        return arguments is not null
            && arguments.Count == RootProperties.Length
            && RootProperties.All(arguments.ContainsKey)
            && arguments.TryGetValue("destination", out destination)
            && arguments.TryGetValue("entity", out entity);
    }

    private static bool TryParseDestination(JsonElement value, out CalendarCreateDestination destination)
    {
        destination = null!;
        if (value.ValueKind != JsonValueKind.Object || !TryRequiredString(value, "mode", out var mode))
            return false;
        if (mode == "default" && HasShape(value, ["mode"], ["mode"]))
        {
            destination = CalendarCreateDestination.Default;
            return true;
        }
        if (mode != "selected"
            || !HasShape(value, ["mode", "calendar"], ["mode", "calendar"])
            || !value.TryGetProperty("calendar", out var calendar)
            || !TryParseCalendarReference(calendar, out var reference))
        {
            return false;
        }
        destination = CalendarCreateDestination.Selected(reference);
        return true;
    }

    private static bool TryParseCalendarReference(JsonElement value, out CalendarReference reference)
    {
        reference = null!;
        if (value.ValueKind != JsonValueKind.Object || !TryRequiredString(value, "by", out var by))
            return false;
        if (by == "name"
            && HasShape(value, ["by", "name"], ["by", "name"])
            && TryRequiredString(value, "name", out var name))
        {
            reference = new CalendarReference(Name: name);
            return true;
        }
        if (by == "href"
            && HasShape(value, ["by", "href"], ["by", "href"])
            && TryRequiredString(value, "href", out var href))
        {
            reference = new CalendarReference(Href: href);
            return true;
        }
        return false;
    }

    private static bool TryParseEventFields(JsonElement value, out CalendarEventCreateFields fields) =>
        TryParseEventFields(value, allowRecurrence: true, out fields);

    private static bool TryParseEventFields(
        JsonElement value,
        bool allowRecurrence,
        out CalendarEventCreateFields fields)
    {
        fields = null!;
        var valid = HasShape(value, allowRecurrence ? EventFieldProperties : EventOverrideFieldProperties, []);
        valid &= TryOptionalString(value, "summary", out var summary);
        valid &= TryOptionalString(value, "description", out var description);
        valid &= TryOptionalTemporal(value, "start", out var start);
        valid &= TryOptionalTemporal(value, "end", out var end);
        valid &= TryOptionalString(value, "duration", out var duration);
        valid &= TryOptionalString(value, "location", out var location);
        valid &= TryOptionalGeo(value, "geo", out var geo);
        valid &= TryOptionalString(value, "status", out var status);
        valid &= TryOptionalString(value, "transparency", out var transparency);
        valid &= TryOptionalString(value, "classification", out var classification);
        valid &= TryOptionalInteger(value, "priority", out var priority);
        valid &= TryOptionalStringArray(value, "categories", out var categories);
        valid &= TryOptionalString(value, "url", out var url);
        CalendarEventRecurrenceSetCreate? recurrenceSet = null;
        valid &= allowRecurrence
            ? TryOptionalEventRecurrenceSet(value, out recurrenceSet)
            : !value.TryGetProperty("recurrenceSet", out _);
        valid &= TryOptionalStructuredData(value, out var structuredData);
        if (!valid)
            return false;
        fields = new CalendarEventCreateFields(
            summary,
            description,
            start,
            end,
            duration,
            location,
            geo,
            status,
            transparency,
            classification,
            priority,
            categories,
            url,
            structuredData,
            recurrenceSet);
        return true;
    }

    private static bool TryParseTodoFields(JsonElement value, out CalendarTodoCreateFields fields) =>
        TryParseTodoFields(value, allowRecurrence: true, out fields);

    private static bool TryParseTodoFields(
        JsonElement value,
        bool allowRecurrence,
        out CalendarTodoCreateFields fields)
    {
        fields = null!;
        var valid = HasShape(value, allowRecurrence ? TodoFieldProperties : TodoOverrideFieldProperties, []);
        valid &= TryOptionalString(value, "summary", out var summary);
        valid &= TryOptionalString(value, "description", out var description);
        valid &= TryOptionalTemporal(value, "start", out var start);
        valid &= TryOptionalTemporal(value, "due", out var due);
        valid &= TryOptionalString(value, "duration", out var duration);
        valid &= TryOptionalString(value, "status", out var status);
        valid &= TryOptionalInteger(value, "priority", out var priority);
        valid &= TryOptionalStringArray(value, "categories", out var categories);
        CalendarTodoRecurrenceSetCreate? recurrenceSet = null;
        valid &= allowRecurrence
            ? TryOptionalTodoRecurrenceSet(value, out recurrenceSet)
            : !value.TryGetProperty("recurrenceSet", out _);
        valid &= TryOptionalStructuredData(value, out var structuredData);
        if (!valid)
            return false;
        fields = new CalendarTodoCreateFields(
            summary,
            description,
            start,
            due,
            duration,
            status,
            priority,
            categories,
            structuredData,
            recurrenceSet);
        return true;
    }

    private static bool TryOptionalEventRecurrenceSet(
        JsonElement owner,
        out CalendarEventRecurrenceSetCreate? parsed)
    {
        parsed = null;
        if (!owner.TryGetProperty("recurrenceSet", out var recurrenceSet))
            return true;
        if (!HasShape(recurrenceSet, ["rrule", "rdates", "exdates", "overrides"], []))
            return false;
        var valid = TryOptionalString(recurrenceSet, "rrule", out var rule);
        valid &= TryOptionalArray<CalendarRecurrenceDateCreate>(
            recurrenceSet, "rdates", TryParseRecurrenceDate, out var recurrenceDates);
        valid &= TryOptionalArray<CalendarTemporalValue>(
            recurrenceSet, "exdates", TryParseRequiredTemporal, out var exceptionDates);
        valid &= TryOptionalArray<CalendarEventRecurrenceOverrideCreate>(
            recurrenceSet, "overrides", TryParseEventRecurrenceOverride, out var overrides);
        if (valid)
            parsed = new CalendarEventRecurrenceSetCreate(rule, recurrenceDates, exceptionDates, overrides);
        return valid;
    }

    private static bool TryOptionalTodoRecurrenceSet(
        JsonElement owner,
        out CalendarTodoRecurrenceSetCreate? parsed)
    {
        parsed = null;
        if (!owner.TryGetProperty("recurrenceSet", out var recurrenceSet))
            return true;
        if (!HasShape(recurrenceSet, ["rrule", "rdates", "exdates", "overrides"], []))
            return false;
        var valid = TryOptionalString(recurrenceSet, "rrule", out var rule);
        valid &= TryOptionalArray<CalendarRecurrenceDateCreate>(
            recurrenceSet, "rdates", TryParseRecurrenceDate, out var recurrenceDates);
        valid &= TryOptionalArray<CalendarTemporalValue>(
            recurrenceSet, "exdates", TryParseRequiredTemporal, out var exceptionDates);
        valid &= TryOptionalArray<CalendarTodoRecurrenceOverrideCreate>(
            recurrenceSet, "overrides", TryParseTodoRecurrenceOverride, out var overrides);
        if (valid)
            parsed = new CalendarTodoRecurrenceSetCreate(rule, recurrenceDates, exceptionDates, overrides);
        return valid;
    }

    private static bool TryParseRecurrenceDate(JsonElement value, out CalendarRecurrenceDateCreate parsed)
    {
        parsed = null!;
        if (!TryRequiredString(value, "kind", out var kind))
            return false;
        if (kind != "period")
        {
            if (!TryParseTemporal(value, out var temporal) || temporal is null)
                return false;
            parsed = new CalendarRecurrenceDateCreate(Value: temporal);
            return true;
        }
        return TryParseRecurrencePeriod(value, out parsed);
    }

    private static bool TryParseRecurrencePeriod(JsonElement value, out CalendarRecurrenceDateCreate parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["kind", "start", "end", "duration"], ["kind", "start"])
            || !value.TryGetProperty("start", out var startElement)
            || !TryParseTemporal(startElement, out var start)
            || start is null
            || !TryOptionalTemporal(value, "end", out var end)
            || !TryOptionalString(value, "duration", out var duration)
            || (end is null) == (duration is null))
        {
            return false;
        }
        if (!IsValidRecurrencePeriod(start, end, duration))
            return false;
        parsed = new CalendarRecurrenceDateCreate(Period: new CalendarRecurrencePeriodCreate(start, end, duration));
        return true;
    }

    private static bool IsValidRecurrencePeriod(
        CalendarTemporalValue start,
        CalendarTemporalValue? end,
        string? duration)
    {
        if (start.Kind == CalendarTemporalKind.Date || !TryParseTemporalValue(start, out var parsedStart))
            return false;
        if (end is not null)
        {
            return end.Kind == start.Kind
                && string.Equals(end.TimeZoneId, start.TimeZoneId, StringComparison.Ordinal)
                && TryParseTemporalValue(end, out var parsedEnd)
                && parsedEnd > parsedStart;
        }
        return IsPositiveDateTimeDuration(duration!);
    }

    private static bool TryParseTemporalValue(CalendarTemporalValue value, out DateTime parsed)
    {
        var utc = value.Kind == CalendarTemporalKind.UtcDateTime;
        var raw = utc && value.Value.EndsWith('Z') ? value.Value[..^1] : value.Value;
        return DateTime.TryParseExact(
            raw,
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);
    }

    private static bool IsPositiveDateTimeDuration(string duration)
        => CalendarDurationArithmetic.TryParse(duration, out var parsed)
            && parsed.IsStrictlyPositive;

    private static bool TryParseEventRecurrenceOverride(
        JsonElement value,
        out CalendarEventRecurrenceOverrideCreate parsed)
    {
        parsed = null!;
        if (!TryParseRecurrenceOverrideHeader(value, out var identity, out var status, out var range)
            || !value.TryGetProperty("fields", out var fieldsElement)
            || !TryParseEventFields(fieldsElement, allowRecurrence: false, out var fields))
        {
            return false;
        }
        parsed = new CalendarEventRecurrenceOverrideCreate(identity, status, fields, range);
        return true;
    }

    private static bool TryParseTodoRecurrenceOverride(
        JsonElement value,
        out CalendarTodoRecurrenceOverrideCreate parsed)
    {
        parsed = null!;
        if (!TryParseRecurrenceOverrideHeader(value, out var identity, out var status, out var range)
            || !value.TryGetProperty("fields", out var fieldsElement)
            || !TryParseTodoFields(fieldsElement, allowRecurrence: false, out var fields))
        {
            return false;
        }
        parsed = new CalendarTodoRecurrenceOverrideCreate(identity, status, fields, range);
        return true;
    }

    private static bool TryParseRecurrenceOverrideHeader(
        JsonElement value,
        out CalendarTemporalValue identity,
        out CalendarRecurrenceOverrideStatus status,
        out CalendarRecurrenceOverrideRange? range)
    {
        identity = null!;
        status = default;
        range = null;
        if (!HasShape(value, ["recurrenceIdentity", "range", "status", "fields"], ["recurrenceIdentity", "status", "fields"])
            || !TryParseRecurrenceIdentity(value, out var parsedIdentity)
            || !TryParseOverrideStatus(value, out status)
            || !TryParseOverrideRange(value, out range))
        {
            return false;
        }
        identity = parsedIdentity;
        return true;
    }

    private static bool TryParseRecurrenceIdentity(JsonElement value, out CalendarTemporalValue identity)
    {
        identity = null!;
        return value.TryGetProperty("recurrenceIdentity", out var recurrenceIdentity)
            && HasShape(recurrenceIdentity, ["value"], ["value"])
            && recurrenceIdentity.TryGetProperty("value", out var identityValue)
            && TryParseRequiredTemporal(identityValue, out identity);
    }

    private static bool TryParseOverrideStatus(JsonElement value, out CalendarRecurrenceOverrideStatus status)
    {
        status = default;
        if (!TryRequiredString(value, "status", out var rawStatus))
            return false;
        status = rawStatus switch
        {
            "active" => CalendarRecurrenceOverrideStatus.Active,
            "cancelled" => CalendarRecurrenceOverrideStatus.Cancelled,
            _ => (CalendarRecurrenceOverrideStatus)(-1)
        };
        return (int)status >= 0;
    }

    private static bool TryParseOverrideRange(JsonElement value, out CalendarRecurrenceOverrideRange? range)
    {
        range = null;
        if (!TryOptionalString(value, "range", out var rawRange))
            return false;
        range = rawRange switch
        {
            "this-and-future" => CalendarRecurrenceOverrideRange.ThisAndFuture,
            null => null,
            _ => (CalendarRecurrenceOverrideRange)(-1)
        };
        return range is null || (int)range.Value >= 0;
    }

    private static bool TryParseRequiredTemporal(JsonElement value, out CalendarTemporalValue parsed)
    {
        parsed = null!;
        if (!TryParseTemporal(value, out var temporal) || temporal is null)
            return false;
        parsed = temporal;
        return true;
    }

    private static bool TryRequiredEnum(JsonElement owner, string name, IReadOnlyList<string> allowed) =>
        TryRequiredString(owner, name, out var value) && allowed.Contains(value);

    private static bool TryOptionalEnum(JsonElement owner, string name, IReadOnlyList<string> allowed) =>
        TryOptionalString(owner, name, out var value) && (value is null || allowed.Contains(value));

    private static bool TryOptionalTemporal(
        JsonElement owner,
        string name,
        out CalendarTemporalValue? temporal)
    {
        temporal = null;
        return !owner.TryGetProperty(name, out var value) || TryParseTemporal(value, out temporal);
    }

    private static bool TryParseTemporal(JsonElement value, out CalendarTemporalValue? temporal)
    {
        temporal = null;
        if (value.ValueKind != JsonValueKind.Object
            || !TryRequiredString(value, "kind", out var kind)
            || !TryRequiredString(value, "value", out var raw))
        {
            return false;
        }
        var parsedKind = kind switch
        {
            "date" => CalendarTemporalKind.Date,
            "floatingDateTime" => CalendarTemporalKind.FloatingDateTime,
            "utcDateTime" => CalendarTemporalKind.UtcDateTime,
            "zonedDateTime" => CalendarTemporalKind.ZonedDateTime,
            _ => (CalendarTemporalKind?)null
        };
        var zoned = parsedKind == CalendarTemporalKind.ZonedDateTime;
        if (parsedKind is null
            || !HasShape(value, zoned ? ["kind", "value", "timeZoneId"] : ["kind", "value"],
                zoned ? ["kind", "value", "timeZoneId"] : ["kind", "value"])
            || zoned && !TryRequiredString(value, "timeZoneId", out _))
        {
            return false;
        }
        _ = TryOptionalString(value, "timeZoneId", out var timeZoneId);
        temporal = new CalendarTemporalValue(parsedKind.Value, raw, timeZoneId);
        return true;
    }

    private static bool TryOptionalGeo(JsonElement owner, string name, out CalendarGeo? geo)
    {
        geo = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!HasShape(value, ["latitude", "longitude"], ["latitude", "longitude"])
            || !value.TryGetProperty("latitude", out var latitude)
            || !value.TryGetProperty("longitude", out var longitude)
            || latitude.ValueKind != JsonValueKind.Number
            || longitude.ValueKind != JsonValueKind.Number
            || !latitude.TryGetDouble(out var parsedLatitude)
            || !longitude.TryGetDouble(out var parsedLongitude))
        {
            return false;
        }
        geo = new CalendarGeo(parsedLatitude, parsedLongitude);
        return true;
    }

    private static bool TryOptionalStructuredData(JsonElement owner, out CalendarStructuredData? data)
    {
        data = null;
        if (!owner.TryGetProperty("structuredData", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var valid = HasShape(value, StructuredProperties, []);
        valid &= TryOptionalNamedUri(value, "organizer", out var organizer);
        valid &= TryOptionalArray<CalendarAttendee>(value, "attendees", TryParseAttendee, out var attendees);
        valid &= TryOptionalArray<CalendarParticipant>(value, "participants", TryParseParticipant, out var participants);
        valid &= TryOptionalArray<CalendarTextValue>(value, "contacts", TryParseTextValue, out var contacts);
        valid &= TryOptionalArray<CalendarTextValue>(value, "resources", TryParseTextValue, out var resources);
        valid &= TryOptionalArray<CalendarRelation>(value, "relatedTo", TryParseRelation, out var relatedTo);
        valid &= TryOptionalArray<CalendarRequestStatus>(value, "requestStatuses", TryParseRequestStatus, out var requestStatuses);
        valid &= TryOptionalArray<CalendarAlarm>(value, "alarms", TryParseAlarm, out var alarms);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "attachments", TryParseNamedUri, out var attachments);
        valid &= TryOptionalArray<CalendarTextValue>(value, "comments", TryParseTextValue, out var comments);
        valid &= TryOptionalArray<CalendarTextValue>(value, "styledDescriptions", TryParseTextValue, out var styledDescriptions);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "images", TryParseNamedUri, out var images);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "conferences", TryParseNamedUri, out var conferences);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "links", TryParseNamedUri, out var links);
        valid &= TryOptionalArray<CalendarUriValue>(value, "concepts", TryParseUriValue, out var concepts);
        valid &= TryOptionalArray<CalendarUriValue>(value, "structuredDataUris", TryParseUriValue, out var structuredDataUris);
        valid &= TryOptionalArray<CalendarNamedComponent>(value, "locationUris", TryParseNamedComponent, out var locationUris);
        valid &= TryOptionalArray<CalendarNamedComponent>(value, "resourceUris", TryParseNamedComponent, out var resourceUris);
        if (!valid)
            return false;
        data = new CalendarStructuredData(
            Organizer: organizer,
            Attendees: attendees,
            Participants: participants,
            Contacts: contacts,
            Resources: resources,
            RelatedTo: relatedTo,
            RequestStatuses: requestStatuses,
            Alarms: alarms,
            Attachments: attachments,
            Comments: comments,
            StyledDescriptions: styledDescriptions,
            Images: images,
            Conferences: conferences,
            Links: links,
            Concepts: concepts,
            StructuredDataUris: structuredDataUris,
            LocationUris: locationUris,
            ResourceUris: resourceUris);
        return true;
    }

    private static bool TryOptionalNamedUri(JsonElement owner, string name, out CalendarNamedUri? value)
    {
        value = null;
        return !owner.TryGetProperty(name, out var element) || TryParseNamedUri(element, out value);
    }

    private static bool TryParseNamedUri(JsonElement value, out CalendarNamedUri parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["uri", "label", "parameters"], ["uri", "parameters"])
            || !TryRequiredString(value, "uri", out var uri)
            || !TryOptionalString(value, "label", out var label)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        parsed = new CalendarNamedUri(uri, label, parameters);
        return true;
    }

    private static bool TryParseNamedComponent(JsonElement value, out CalendarNamedComponent parsed)
    {
        parsed = null!;
        string[] allowed =
        ["uid", "name", "parameters", "description", "geo", "componentTypes", "url", "relatedTo", "concepts", "links", "structuredDataUris"];
        var valid = HasShape(value, allowed, ["uid", "parameters"]);
        valid &= TryRequiredString(value, "uid", out var uid);
        valid &= TryOptionalTextValue(value, "name", out var name);
        valid &= TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters);
        valid &= TryOptionalTextValue(value, "description", out var description);
        valid &= TryOptionalGeoProperty(value, "geo", out var geo);
        valid &= TryOptionalTextListProperty(value, "componentTypes", out var componentTypes);
        valid &= TryOptionalUriValue(value, "url", out var url);
        valid &= TryOptionalArray<CalendarRelation>(value, "relatedTo", TryParseRelation, out var relatedTo);
        valid &= TryOptionalArray<CalendarUriValue>(value, "concepts", TryParseUriValue, out var concepts);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "links", TryParseNamedUri, out var links);
        valid &= TryOptionalArray<CalendarUriValue>(value, "structuredDataUris", TryParseUriValue, out var structuredDataUris);
        if (!valid)
            return false;
        parsed = new CalendarNamedComponent(
            uid, name, parameters, description, geo, componentTypes, url,
            relatedTo, concepts, links, structuredDataUris);
        return true;
    }

    private static bool TryParseUriValue(JsonElement value, out CalendarUriValue parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["uri", "parameters"], ["uri", "parameters"])
            || !TryRequiredString(value, "uri", out var uri)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        parsed = new CalendarUriValue(uri, parameters);
        return true;
    }

    private static bool TryParseAttendee(JsonElement value, out CalendarAttendee parsed)
    {
        parsed = null!;
        string[] allowed =
        ["uri", "commonName", "role", "partStat", "cutype", "rsvp", "delegatedTo", "delegatedFrom", "sentBy", "directory", "parameters"];
        var valid = HasShape(value, allowed, ["uri", "parameters"]);
        valid &= TryRequiredString(value, "uri", out var uri);
        valid &= TryOptionalString(value, "commonName", out var commonName);
        valid &= TryOptionalString(value, "role", out var role);
        valid &= TryOptionalString(value, "partStat", out var partStat);
        valid &= TryOptionalString(value, "cutype", out var cutype);
        valid &= TryOptionalBoolean(value, "rsvp", out var rsvp);
        valid &= TryOptionalStringArray(value, "delegatedTo", out var delegatedTo);
        valid &= TryOptionalStringArray(value, "delegatedFrom", out var delegatedFrom);
        valid &= TryOptionalString(value, "sentBy", out var sentBy);
        valid &= TryOptionalString(value, "directory", out var directory);
        valid &= TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters);
        if (!valid)
            return false;
        parsed = new CalendarAttendee(
            uri, parameters, commonName, role, partStat, cutype, rsvp, delegatedTo, delegatedFrom, sentBy, directory);
        return true;
    }

    private static bool TryParseParticipant(JsonElement value, out CalendarParticipant parsed)
    {
        parsed = null!;
        string[] allowed =
        [
            "uid", "participantType", "calendarAddress", "created", "description", "timestamp", "geo",
            "lastModified", "priority", "sequence", "status", "summary", "url", "attachments", "categories",
            "comments", "contacts", "locations", "requestStatuses", "relatedTo", "resources",
            "styledDescriptions", "structuredDataUris", "locationUris", "resourceUris"
        ];
        var valid = HasShape(value, allowed, ["uid", "participantType"]);
        valid &= TryRequiredTextValue(value, "uid", out var uid);
        valid &= TryRequiredTextValue(value, "participantType", out var participantType);
        valid &= TryOptionalUriValue(value, "calendarAddress", out var calendarAddress);
        valid &= TryOptionalTemporalProperty(value, "created", out var created);
        valid &= TryOptionalTextValue(value, "description", out var description);
        valid &= TryOptionalTemporalProperty(value, "timestamp", out var timestamp);
        valid &= TryOptionalGeoProperty(value, "geo", out var geo);
        valid &= TryOptionalTemporalProperty(value, "lastModified", out var lastModified);
        valid &= TryOptionalIntegerProperty(value, "priority", out var priority);
        valid &= TryOptionalIntegerProperty(value, "sequence", out var sequence);
        valid &= TryOptionalTextValue(value, "status", out var status);
        valid &= TryOptionalTextValue(value, "summary", out var summary);
        valid &= TryOptionalUriValue(value, "url", out var url);
        valid &= TryOptionalArray<CalendarNamedUri>(value, "attachments", TryParseNamedUri, out var attachments);
        valid &= TryOptionalTextListProperty(value, "categories", out var categories);
        valid &= TryOptionalArray<CalendarTextValue>(value, "comments", TryParseTextValue, out var comments);
        valid &= TryOptionalArray<CalendarTextValue>(value, "contacts", TryParseTextValue, out var contacts);
        valid &= TryOptionalArray<CalendarTextValue>(value, "locations", TryParseTextValue, out var locations);
        valid &= TryOptionalArray<CalendarRequestStatus>(value, "requestStatuses", TryParseRequestStatus, out var requestStatuses);
        valid &= TryOptionalArray<CalendarRelation>(value, "relatedTo", TryParseRelation, out var relatedTo);
        valid &= TryOptionalArray<CalendarTextValue>(value, "resources", TryParseTextValue, out var resources);
        valid &= TryOptionalArray<CalendarTextValue>(value, "styledDescriptions", TryParseTextValue, out var styledDescriptions);
        valid &= TryOptionalArray<CalendarUriValue>(value, "structuredDataUris", TryParseUriValue, out var structuredDataUris);
        valid &= TryOptionalArray<CalendarNamedComponent>(value, "locationUris", TryParseNamedComponent, out var locationUris);
        valid &= TryOptionalArray<CalendarNamedComponent>(value, "resourceUris", TryParseNamedComponent, out var resourceUris);
        if (!valid)
            return false;
        parsed = new CalendarParticipant(
            uid, participantType, calendarAddress, created, description, timestamp, geo, lastModified, priority,
            sequence, status, summary, url, attachments, categories, comments, contacts, locations,
            requestStatuses, relatedTo, resources, styledDescriptions, structuredDataUris, locationUris, resourceUris);
        return true;
    }

    private static bool TryOptionalUriValue(JsonElement owner, string name, out CalendarUriValue? value)
    {
        value = null;
        return !owner.TryGetProperty(name, out var element) || TryParseUriValue(element, out value);
    }

    private static bool TryOptionalTextValue(JsonElement owner, string name, out CalendarTextValue? value)
    {
        value = null;
        return !owner.TryGetProperty(name, out var element) || TryParseTextValue(element, out value);
    }

    private static bool TryRequiredTextValue(JsonElement owner, string name, out CalendarTextValue value)
    {
        value = null!;
        return owner.TryGetProperty(name, out var element) && TryParseTextValue(element, out value);
    }

    private static bool TryParseTextValue(JsonElement value, out CalendarTextValue parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["value", "parameters"], ["value", "parameters"])
            || !TryRequiredString(value, "value", out var text)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        parsed = new CalendarTextValue(text, parameters);
        return true;
    }

    private static bool TryOptionalTemporalProperty(
        JsonElement owner,
        string name,
        out CalendarTemporalProperty? property)
    {
        property = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!TryParseProperty(value, TryParseTemporal, out CalendarTemporalValue? temporal, out var parameters)
            || temporal is null)
        {
            return false;
        }
        property = new CalendarTemporalProperty(temporal, parameters);
        return true;
    }

    private static bool TryOptionalGeoProperty(JsonElement owner, string name, out CalendarGeoProperty? property)
    {
        property = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!TryParseProperty(value, TryParseGeo, out CalendarGeo? geo, out var parameters) || geo is null)
            return false;
        property = new CalendarGeoProperty(geo, parameters);
        return true;
    }

    private static bool TryOptionalIntegerProperty(
        JsonElement owner,
        string name,
        out CalendarIntegerProperty? property)
    {
        property = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!HasShape(value, ["value", "parameters"], ["value", "parameters"])
            || !TryRequiredInteger(value, "value", out var integer)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        property = new CalendarIntegerProperty(integer, parameters);
        return true;
    }

    private static bool TryOptionalTextListProperty(
        JsonElement owner,
        string name,
        out CalendarTextListProperty? property)
    {
        property = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!HasShape(value, ["value", "parameters"], ["value", "parameters"])
            || !TryRequiredStringArray(value, "value", out var items)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        property = new CalendarTextListProperty(items, parameters);
        return true;
    }

    private static bool TryOptionalDurationProperty(
        JsonElement owner,
        string name,
        out CalendarDurationProperty? property)
    {
        property = null;
        if (!owner.TryGetProperty(name, out var value))
            return true;
        if (!HasShape(value, ["value", "parameters"], ["value", "parameters"])
            || !TryRequiredString(value, "value", out var duration)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        property = new CalendarDurationProperty(duration, parameters);
        return true;
    }

    private static bool TryParseProperty<T>(
        JsonElement property,
        TryParse<T> parseValue,
        out T parsed,
        out IReadOnlyList<CalendarParameter> parameters)
    {
        parsed = default!;
        parameters = [];
        return HasShape(property, ["value", "parameters"], ["value", "parameters"])
            && property.TryGetProperty("value", out var value)
            && parseValue(value, out parsed)
            && TryRequiredArray<CalendarParameter>(property, "parameters", TryParseParameter, out parameters);
    }

    private static bool TryParseGeo(JsonElement value, out CalendarGeo? geo)
    {
        geo = null;
        if (!HasShape(value, ["latitude", "longitude"], ["latitude", "longitude"])
            || !value.TryGetProperty("latitude", out var latitude)
            || !value.TryGetProperty("longitude", out var longitude)
            || latitude.ValueKind != JsonValueKind.Number
            || longitude.ValueKind != JsonValueKind.Number
            || !latitude.TryGetDouble(out var parsedLatitude)
            || !longitude.TryGetDouble(out var parsedLongitude))
        {
            return false;
        }
        geo = new CalendarGeo(parsedLatitude, parsedLongitude);
        return true;
    }

    private static bool TryParseRelation(JsonElement value, out CalendarRelation parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["value", "relationType", "parameters"], ["value", "parameters"])
            || !TryRequiredString(value, "value", out var related)
            || !TryOptionalString(value, "relationType", out var relationType)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        parsed = new CalendarRelation(related, relationType, parameters);
        return true;
    }

    private static bool TryParseRequestStatus(JsonElement value, out CalendarRequestStatus parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["code", "description", "exceptionData", "parameters"], ["code", "description", "parameters"])
            || !TryRequiredString(value, "code", out var code)
            || !TryRequiredString(value, "description", out var description)
            || !TryOptionalString(value, "exceptionData", out var exceptionData)
            || !TryRequiredArray<CalendarParameter>(value, "parameters", TryParseParameter, out var parameters))
        {
            return false;
        }
        parsed = new CalendarRequestStatus(code, description, exceptionData, parameters);
        return true;
    }

    private static bool TryParseAlarm(JsonElement value, out CalendarAlarm parsed)
    {
        parsed = null!;
        if (!HasShape(
                value,
                ["action", "trigger", "description", "repeat", "duration", "summary", "attendees", "attachments",
                    "uid", "acknowledged", "proximity", "relatedTo", "proximityLocations"],
                ["action", "trigger"])
            || !TryParseAlarmCore(value, out var core)
            || !TryParseAlarmExtensions(value, out var extensions))
        {
            return false;
        }
        parsed = new CalendarAlarm(
            core.Action, core.Trigger, core.Description, core.Repeat, core.Duration, core.Summary,
            core.Attendees, core.Attachments, extensions.Uid, extensions.Acknowledged,
            extensions.Proximity, extensions.RelatedTo, extensions.ProximityLocations);
        return true;
    }

    private static bool TryParseAlarmCore(JsonElement value, out CalendarAlarmCore parsed)
    {
        parsed = null!;
        if (!TryRequiredAlarmAction(value, out var action)
            || !TryRequiredTextValue(value, "trigger", out var trigger)
            || !TryOptionalTextValue(value, "description", out var description)
            || !TryOptionalIntegerProperty(value, "repeat", out var repeat)
            || !TryOptionalDurationProperty(value, "duration", out var duration)
            || !TryOptionalTextValue(value, "summary", out var summary)
            || !TryOptionalArray<CalendarAttendee>(value, "attendees", TryParseAttendee, out var attendees)
            || !TryOptionalArray<CalendarNamedUri>(value, "attachments", TryParseNamedUri, out var attachments))
        {
            return false;
        }

        parsed = new(action, trigger, description, repeat, duration, summary, attendees, attachments);
        return true;
    }

    private static bool TryParseAlarmExtensions(JsonElement value, out CalendarAlarmExtensions parsed)
    {
        parsed = null!;
        if (!TryOptionalTextValue(value, "uid", out var uid)
            || !TryOptionalTemporalProperty(value, "acknowledged", out var acknowledged)
            || !TryOptionalTextValue(value, "proximity", out var proximity)
            || !TryOptionalArray<CalendarRelation>(value, "relatedTo", TryParseRelation, out var relatedTo)
            || !TryOptionalArray<CalendarNamedComponent>(value, "proximityLocations", TryParseNamedComponent, out var proximityLocations))
        {
            return false;
        }

        parsed = new(uid, acknowledged, proximity, relatedTo, proximityLocations);
        return true;
    }

    private sealed record CalendarAlarmCore(
        CalendarTextValue Action,
        CalendarTextValue Trigger,
        CalendarTextValue? Description,
        CalendarIntegerProperty? Repeat,
        CalendarDurationProperty? Duration,
        CalendarTextValue? Summary,
        IReadOnlyList<CalendarAttendee>? Attendees,
        IReadOnlyList<CalendarNamedUri>? Attachments);

    private sealed record CalendarAlarmExtensions(
        CalendarTextValue? Uid,
        CalendarTemporalProperty? Acknowledged,
        CalendarTextValue? Proximity,
        IReadOnlyList<CalendarRelation>? RelatedTo,
        IReadOnlyList<CalendarNamedComponent>? ProximityLocations);

    private static bool TryRequiredAlarmAction(JsonElement owner, out CalendarTextValue action) =>
        TryRequiredTextValue(owner, "action", out action)
        && action.Value is "display" or "audio" or "email";

    private static bool TryParseParameter(JsonElement value, out CalendarParameter parsed)
    {
        parsed = null!;
        if (!HasShape(value, ["name", "values"], ["name", "values"])
            || !TryRequiredString(value, "name", out var name)
            || !TryRequiredStringArray(value, "values", out var values))
        {
            return false;
        }
        parsed = new CalendarParameter(name, values);
        return true;
    }

    private static bool TryRequiredString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        return owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()!) is not null;
    }

    private static bool TryOptionalString(JsonElement owner, string name, out string? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return true;
    }

    private static bool TryOptionalInteger(JsonElement owner, string name, out int? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var parsed))
            return false;
        value = parsed;
        return true;
    }

    private static bool TryRequiredInteger(JsonElement owner, string name, out int value)
    {
        value = default;
        return owner.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryOptionalBoolean(JsonElement owner, string name, out bool? value)
    {
        value = null;
        if (!owner.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = property.GetBoolean();
        return true;
    }

    private static bool TryOptionalStringArray(JsonElement owner, string name, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!owner.TryGetProperty(name, out var property))
            return true;
        if (!TryParseStringArray(property, out var parsed))
            return false;
        values = parsed;
        return true;
    }

    private static bool TryRequiredStringArray(JsonElement owner, string name, out IReadOnlyList<string> values)
    {
        values = [];
        return owner.TryGetProperty(name, out var property) && TryParseStringArray(property, out values);
    }

    private static bool TryParseStringArray(JsonElement value, out IReadOnlyList<string> values)
    {
        values = [];
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            return false;
        values = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return true;
    }

    private static bool TryOptionalArray<T>(
        JsonElement owner,
        string name,
        TryParse<T> parser,
        out IReadOnlyList<T>? values)
    {
        values = null;
        if (!owner.TryGetProperty(name, out var property))
            return true;
        if (!TryParseArray(property, parser, out var parsed))
            return false;
        values = parsed;
        return true;
    }

    private static bool TryRequiredArray<T>(
        JsonElement owner,
        string name,
        TryParse<T> parser,
        out IReadOnlyList<T> values)
    {
        values = [];
        return owner.TryGetProperty(name, out var property) && TryParseArray(property, parser, out values);
    }

    private static bool TryParseArray<T>(JsonElement value, TryParse<T> parser, out IReadOnlyList<T> values)
    {
        values = [];
        if (value.ValueKind != JsonValueKind.Array)
            return false;
        var parsed = new List<T>();
        foreach (var item in value.EnumerateArray())
        {
            if (!parser(item, out var result))
                return false;
            parsed.Add(result);
        }
        values = parsed;
        return true;
    }

    private static bool HasShape(JsonElement value, IReadOnlyList<string> allowed, IReadOnlyList<string> required)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.All(allowed.Contains)
            && required.All(names.Contains);
    }

    private delegate bool TryParse<T>(JsonElement value, out T parsed);
}
