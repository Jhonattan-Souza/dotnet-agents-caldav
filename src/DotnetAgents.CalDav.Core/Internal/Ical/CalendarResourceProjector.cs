using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarProjectionResult(
    CalendarResourceProjection Projection,
    IReadOnlyList<CalendarProperty> Properties,
    IReadOnlyList<CalendarResourceDiagnostic> Diagnostics);

internal static class CalendarResourceProjector
{
    private static readonly HashSet<string> SingletonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "UID", "RECURRENCE-ID", "SUMMARY", "DESCRIPTION", "DTSTART", "DTEND", "DUE", "DURATION",
        "LOCATION", "STATUS", "TRANSP", "CLASS", "PRIORITY", "URL", "COMPLETED", "DTSTAMP",
        "CREATED", "LAST-MODIFIED", "GEO", "ORGANIZER", "PERCENT-COMPLETE", "SEQUENCE"
    };

    public static CalendarProjectionResult Project(ReadOnlySpan<byte> authoritativeUtf8)
    {
        try
        {
            var document = CalendarContentDocument.Parse(authoritativeUtf8);
            return ProjectDocument(document);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return Opaque([], "invalid_calendar_data");
        }
    }

    public static CalendarResourceRead AttachSnapshot(string calendarHref, CalendarResourceRead read)
    {
        var projection = Project(read.AuthoritativeUtf8.Span);
        var snapshot = new CalendarResourceSnapshot(
            calendarHref,
            read.ResourceHref!,
            read.EntityTag!,
            read.AuthoritativeUtf8,
            projection.Properties,
            projection.Projection,
            projection.Diagnostics);
        return read with { Snapshot = snapshot };
    }

    private static CalendarProjectionResult ProjectDocument(CalendarContentDocument document)
    {
        var properties = document.Properties.Select(ToPublicProperty).ToArray();
        try
        {
            var diagnostic = ValidateComponents(document.Components);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);
            diagnostic = ValidateCalendarProperties(document.Properties);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);

            var entities = GetEntityComponents(document);
            diagnostic = ValidateEntityComponents(entities);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);

            if (!IcalNetCorroborates(document.ReplayForTypedValidation(), entities[0].Kind, entities.Count))
                return Opaque(properties, "typed_projection_invalid");

            var master = entities.Single(entity => entity.RecurrenceIdentity is null);
            var projection = new CalendarResourceProjection(
                master.Kind,
                master.Uid,
                DecodeText(GetOptionalValue(master.Properties, "SUMMARY")));
            return new CalendarProjectionResult(projection, properties, []);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return Opaque(properties, "typed_projection_invalid");
        }
    }

    private static IReadOnlyList<EntityComponent> GetEntityComponents(CalendarContentDocument document) => document.Components
        .Where(component => IsEntityComponent(component.Path[^1].Name))
        .Select(component => CreateEntity(component, document.Properties))
        .ToArray();

    private static EntityComponent CreateEntity(
        CalendarContentComponent component,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        var owned = properties.Where(property => PathsEqual(property.ComponentPath, component.Path)).ToArray();
        var recurrenceProperty = GetProperty(owned, "RECURRENCE-ID");
        return new EntityComponent(
            component.Path[^1].Name.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)
                ? CalendarResourceProjectionKind.Event
                : CalendarResourceProjectionKind.Todo,
            DecodeText(GetRequiredValue(owned, "UID")),
            recurrenceProperty is null ? null : GetTemporalIdentity(recurrenceProperty),
            owned);
    }

    private static string? ValidateComponents(IReadOnlyList<CalendarContentComponent> components)
    {
        var roots = components.Where(component => component.Path.Count == 1).ToArray();
        if (roots.Length != 1 || !roots[0].Path[0].Name.Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase))
            return "calendar_component_cardinality";

        if (components.Any(component => !HasValidKnownTopology(component.Path)))
            return "known_component_topology_invalid";

        var unsupported = components.Any(component => component.Path[^1].Name is "VJOURNAL" or "VFREEBUSY");
        return unsupported ? "unsupported_entity_component" : null;
    }

    private static bool HasValidKnownTopology(IReadOnlyList<CalendarComponentPathSegment> path) => path[^1].Name switch
    {
        "VCALENDAR" => path.Count == 1,
        "VEVENT" or "VTODO" or "VTIMEZONE" => path.Count == 2 && path[0].Name == "VCALENDAR",
        "STANDARD" or "DAYLIGHT" => path.Count == 3 && path[1].Name == "VTIMEZONE",
        "VALARM" => path.Count == 3 && path[1].Name is "VEVENT" or "VTODO",
        _ => true
    };

    private static bool IsEntityComponent(string name) =>
        name.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("VTODO", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateEntityComponents(IReadOnlyList<EntityComponent> entities)
    {
        var basicError = ValidateBasicEntityShape(entities);
        if (basicError is not null)
            return basicError;

        var masters = entities.Where(entity => entity.RecurrenceIdentity is null).ToArray();
        if (masters.Length != 1)
            return "entity_master_cardinality";
        if (entities.Any(entity => !string.Equals(entity.Uid, masters[0].Uid, StringComparison.Ordinal)))
            return "recurrence_override_uid_mismatch";
        if (entities.Where(entity => entity.RecurrenceIdentity is not null)
            .GroupBy(entity => entity.RecurrenceIdentity, StringComparer.Ordinal).Any(group => group.Count() > 1))
            return "recurrence_identity_duplicate";

        var recurrenceError = ValidateRecurrenceFamily(entities, masters[0]);
        if (recurrenceError is not null)
            return recurrenceError;
        return null;
    }

    private static string? ValidateBasicEntityShape(IReadOnlyList<EntityComponent> entities)
    {
        if (entities.Count == 0)
            return "entity_master_missing";
        if (entities.Select(entity => entity.Kind).Distinct().Count() != 1)
            return "mixed_entity_kinds";
        if (entities.Any(entity => entity.Uid is null))
            return "entity_uid_invalid";
        if (entities.Any(entity => GetRequiredValue(entity.Properties, "DTSTAMP") is null))
            return "entity_dtstamp_invalid";
        if (entities.Any(entity => HasAmbiguousValueParameters(entity.Properties)))
            return "property_parameter_cardinality";
        if (entities.Any(HasMutuallyExclusiveEndProperties))
            return "entity_temporal_cardinality";
        return entities.Any(entity => HasRepeatedSingleton(entity.Properties))
            ? "singleton_property_repeated"
            : null;
    }

    private static string? ValidateRecurrenceFamily(
        IReadOnlyList<EntityComponent> entities,
        EntityComponent master)
    {
        var start = GetProperty(master.Properties, "DTSTART");
        if (start is null)
            return master.Kind == CalendarResourceProjectionKind.Event ? "entity_start_invalid" : null;

        var startFamily = GetTemporalFamily(start);
        return entities.Where(entity => entity.RecurrenceIdentity is not null)
            .Select(entity => GetProperty(entity.Properties, "RECURRENCE-ID"))
            .Any(property => property is null || GetTemporalFamily(property) != startFamily)
                ? "recurrence_identity_family_mismatch"
                : null;
    }

    private static string? ValidateCalendarProperties(IReadOnlyList<CalendarContentProperty> properties)
    {
        if (properties.Any(HasInvalidRegisteredValueType))
            return "registered_property_value_invalid";
        if (properties.Any(HasInvalidKnownValue))
            return "typed_projection_invalid";

        var rootProperties = properties.Where(property => property.ComponentPath.Count == 1).ToArray();
        return GetRequiredValue(rootProperties, "VERSION") is null
            || GetRequiredValue(rootProperties, "PRODID") is null
                ? "calendar_required_property_invalid"
                : null;
    }

    private static bool HasInvalidRegisteredValueType(CalendarContentProperty property)
    {
        if (property.Name is "TZOFFSETFROM" or "TZOFFSETTO")
            return !CalendarContentDocument.IsValidRegisteredUnknownValue(property);
        if (!CalendarContentDocument.IsRegisteredPropertyName(property.Name))
            return false;
        return property.ValueType == CalendarPropertyValueType.Unknown
            || HasIncompatibleValueOverride(property);
    }

    private static bool HasIncompatibleValueOverride(CalendarContentProperty property)
    {
        var overrides = property.Parameters
            .Where(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .ToArray();
        if (overrides.Length == 0)
            return false;
        if (overrides.Length != 1)
            return true;
        return !AllowsValueType(property.Name, property.ValueType);
    }

    private static bool AllowsValueType(string propertyName, CalendarPropertyValueType valueType) => propertyName switch
    {
        "ATTACH" or "IMAGE" => valueType is CalendarPropertyValueType.Uri or CalendarPropertyValueType.Binary,
        "DTSTART" or "DTEND" or "DUE" or "EXDATE" or "RECURRENCE-ID" =>
            valueType is CalendarPropertyValueType.Date or CalendarPropertyValueType.DateTime,
        "RDATE" => valueType is CalendarPropertyValueType.Date or CalendarPropertyValueType.DateTime or CalendarPropertyValueType.Period,
        "TRIGGER" => valueType is CalendarPropertyValueType.Duration or CalendarPropertyValueType.DateTime,
        "STRUCTURED-DATA" => valueType is CalendarPropertyValueType.Text or CalendarPropertyValueType.Uri or CalendarPropertyValueType.Binary,
        _ => valueType == CalendarContentDocument.GetDefaultValueType(propertyName)
    };

    private static bool HasInvalidKnownValue(CalendarContentProperty property) => property.Name switch
    {
        "URL" or "ATTENDEE" or "ORGANIZER" => !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "ATTACH" when property.ValueType == CalendarPropertyValueType.Uri =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "ATTACH" when property.ValueType == CalendarPropertyValueType.Binary => !IsBase64(property.RawEncodedValue),
        _ => false
    };

    private static bool IsBase64(string value)
    {
        var decoded = new byte[(value.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(value, decoded, out _);
    }

    private static bool HasMutuallyExclusiveEndProperties(EntityComponent entity)
    {
        var hasDuration = GetProperty(entity.Properties, "DURATION") is not null;
        if (!hasDuration)
            return false;
        var endName = entity.Kind == CalendarResourceProjectionKind.Event ? "DTEND" : "DUE";
        return GetProperty(entity.Properties, endName) is not null;
    }

    private static bool HasRepeatedSingleton(IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => SingletonProperties.Contains(property.Name))
        .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        .Any(group => group.Count() > 1);

    private static bool HasAmbiguousValueParameters(IReadOnlyList<CalendarContentProperty> properties) => properties.Any(property =>
        CountParameterValues(property, "VALUE") > 1 || CountParameterValues(property, "TZID") > 1);

    private static int CountParameterValues(CalendarContentProperty property, string name) => property.Parameters
        .Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        .Sum(parameter => parameter.Values.Count);

    private static bool IcalNetCorroborates(
        ReadOnlySpan<byte> authoritativeUtf8,
        CalendarResourceProjectionKind kind,
        int entityCount)
    {
        try
        {
            var calendar = IcalCalendar.Load(new UTF8Encoding(false, true).GetString(authoritativeUtf8));
            if (calendar is null)
                return false;
            var entityCountsMatch = kind == CalendarResourceProjectionKind.Event
                ? calendar.Events.Count == entityCount && calendar.Todos.Count == 0
                : calendar.Todos.Count == entityCount && calendar.Events.Count == 0;
            if (!entityCountsMatch)
                return false;
            var roundTrip = new CalendarSerializer().SerializeToString(calendar);
            return roundTrip is not null && HasAllRegisteredOccurrences(
                CalendarContentDocument.Parse(authoritativeUtf8),
                CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(roundTrip)));
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.Runtime.Serialization.SerializationException)
        {
            return false;
        }
    }

    private static bool HasAllRegisteredOccurrences(
        CalendarContentDocument source,
        CalendarContentDocument roundTrip)
    {
        var sourceCounts = GetRegisteredOccurrenceCounts(source.Properties);
        var roundTripCounts = GetRegisteredOccurrenceCounts(roundTrip.Properties);
        return sourceCounts.All(sourceCount => roundTripCounts.GetValueOrDefault(sourceCount.Key) >= sourceCount.Value);
    }

    private static IReadOnlyDictionary<string, int> GetRegisteredOccurrenceCounts(
        IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => CalendarContentDocument.IsRegisteredPropertyName(property.Name))
        .GroupBy(property => $"{string.Join('/', property.ComponentPath.Select(segment => segment.Name))}|{property.Name}", StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static CalendarContentProperty? GetProperty(
        IEnumerable<CalendarContentProperty> properties,
        string name) => properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? GetRequiredValue(
        IEnumerable<CalendarContentProperty> properties,
        string name)
    {
        var values = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.RawEncodedValue)
            .ToArray();
        return values.Length == 1 && values[0].Length > 0 ? values[0] : null;
    }

    private static string? GetOptionalValue(IEnumerable<CalendarContentProperty> properties, string name) =>
        GetProperty(properties, name)?.RawEncodedValue;

    private static string GetTemporalIdentity(CalendarContentProperty property)
    {
        var family = GetTemporalFamily(property);
        if (family.StartsWith("utc", StringComparison.Ordinal))
        {
            var parsed = DateTimeOffset.ParseExact(
                property.RawEncodedValue,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return $"{family}|{parsed:O}";
        }

        return $"{family}|{property.RawEncodedValue}";
    }

    private static string GetTemporalFamily(CalendarContentProperty property)
    {
        if (property.ValueType == CalendarPropertyValueType.Date)
            return "date";
        if (property.RawEncodedValue.EndsWith('Z'))
            return "utc-date-time";
        var timeZone = property.Parameters
            .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault();
        return timeZone is null ? "floating-date-time" : $"zoned-date-time:{timeZone}";
    }

    private static string? DecodeText(string? value)
    {
        if (value is null)
            return null;
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                decoded.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            decoded.Append(escaped is 'n' or 'N' ? '\n' : escaped);
        }

        return decoded.ToString();
    }

    private static bool PathsEqual(
        IReadOnlyList<CalendarComponentPathSegment> left,
        IReadOnlyList<CalendarComponentPathSegment> right) => left.Count == right.Count
        && left.Zip(right).All(pair => pair.First.Occurrence == pair.Second.Occurrence
            && pair.First.Name.Equals(pair.Second.Name, StringComparison.OrdinalIgnoreCase));

    private static CalendarProperty ToPublicProperty(CalendarContentProperty property) => new(
        property.ComponentPath,
        property.Name,
        property.Parameters,
        property.ValueType,
        property.RawEncodedValue,
        property.OriginalSlice);

    private static CalendarProjectionResult Opaque(
        IReadOnlyList<CalendarProperty> properties,
        string code) => new(
        new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
        properties,
        [new CalendarResourceDiagnostic(code, "The Calendar Object Resource is readable but cannot be projected safely.", CalendarResourceDiagnosticSeverity.Error)]);

    private sealed record EntityComponent(
        CalendarResourceProjectionKind Kind,
        string? Uid,
        string? RecurrenceIdentity,
        IReadOnlyList<CalendarContentProperty> Properties);
}
