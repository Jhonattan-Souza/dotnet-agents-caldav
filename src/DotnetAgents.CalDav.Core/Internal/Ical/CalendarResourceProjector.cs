using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net.DataTypes;
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
    private static readonly HashSet<string> ExtensionSingletonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "UID", "PARTICIPANT-TYPE", "CALENDAR-ADDRESS", "CREATED", "DESCRIPTION", "DTSTAMP", "GEO",
        "LAST-MODIFIED", "LOCATION-TYPE", "NAME", "PRIORITY", "RESOURCE-TYPE", "SEQUENCE", "STATUS", "SUMMARY", "URL"
    };
    private static readonly HashSet<string> TemporalSingletonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "DTSTART", "DTEND", "DUE", "RECURRENCE-ID", "DTSTAMP", "CREATED", "LAST-MODIFIED", "COMPLETED",
        "ACKNOWLEDGED"
    };
    private static readonly HashSet<string> UtcTemporalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "DTSTAMP", "CREATED", "LAST-MODIFIED", "COMPLETED", "ACKNOWLEDGED"
    };
    private static readonly HashSet<string> IntegerProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PERCENT-COMPLETE", "PRIORITY", "REPEAT", "SEQUENCE"
    };
    private static readonly HashSet<string> TokenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "CALSCALE", "CLASS", "COLOR", "PARTICIPANT-TYPE", "PROXIMITY", "RESOURCE-TYPE"
    };
    private static readonly HashSet<string> RootSingletonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERSION", "PRODID", "CALSCALE", "UID", "LAST-MODIFIED", "URL",
        "REFRESH-INTERVAL", "SOURCE", "COLOR"
    };
    private static readonly HashSet<string> AlarmSingletonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "TRIGGER", "DURATION", "REPEAT", "DESCRIPTION", "SUMMARY", "ACKNOWLEDGED", "PROXIMITY", "UID"
    };
    private static readonly HashSet<string> RootCalendarProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERSION", "PRODID", "CALSCALE", "METHOD", "NAME", "DESCRIPTION", "UID", "LAST-MODIFIED",
        "URL", "REFRESH-INTERVAL", "SOURCE", "COLOR", "IMAGE", "CATEGORIES"
    };
    private static readonly HashSet<string> TimeZoneProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONCEPT", "LAST-MODIFIED", "LINK", "REFID", "RELATED-TO", "TZID", "TZURL"
    };
    private static readonly HashSet<string> ObservanceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMMENT", "CONCEPT", "DTSTART", "LINK", "RDATE", "REFID", "RELATED-TO", "RRULE", "TZNAME",
        "TZOFFSETFROM", "TZOFFSETTO"
    };
    private static readonly HashSet<string> AlarmProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "TRIGGER", "DURATION", "REPEAT", "ATTACH", "DESCRIPTION", "SUMMARY", "ATTENDEE",
        "ACKNOWLEDGED", "CONCEPT", "LINK", "PROXIMITY", "REFID", "RELATED-TO", "STYLED-DESCRIPTION", "UID"
    };
    private static readonly HashSet<string> CommonAlarmProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "TRIGGER", "DURATION", "REPEAT", "ACKNOWLEDGED", "CONCEPT", "LINK", "PROXIMITY",
        "REFID", "RELATED-TO", "STYLED-DESCRIPTION", "UID"
    };
    private static readonly HashSet<string> AudioAlarmProperties = new(CommonAlarmProperties, StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH"
    };
    private static readonly HashSet<string> DisplayAlarmProperties = new(CommonAlarmProperties, StringComparer.OrdinalIgnoreCase)
    {
        "DESCRIPTION"
    };
    private static readonly HashSet<string> EmailAlarmProperties = new(CommonAlarmProperties, StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH", "DESCRIPTION", "SUMMARY", "ATTENDEE"
    };
    private static readonly HashSet<string> AlternateTextProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMMENT", "CONTACT", "DESCRIPTION", "IMAGE", "LOCATION", "RESOURCES", "STYLED-DESCRIPTION", "SUMMARY"
    };
    private static readonly HashSet<string> LanguageProperties = new(AlternateTextProperties, StringComparer.OrdinalIgnoreCase)
    {
        "ATTENDEE", "CATEGORIES", "CONFERENCE", "LINK", "NAME", "ORGANIZER", "REQUEST-STATUS",
        "STYLED-DESCRIPTION", "TZNAME"
    };
    private static readonly HashSet<string> BinaryValueProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH", "IMAGE", "STRUCTURED-DATA"
    };
    private static readonly HashSet<string> SingletonParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTREP", "CN", "CUTYPE", "DELEGATED-FROM", "DELEGATED-TO", "DERIVED", "DIR", "DISPLAY",
        "EMAIL", "ENCODING", "FBTYPE", "FEATURE", "FMTTYPE", "GAP", "LABEL", "LANGUAGE", "LINKREL",
        "MEMBER", "ORDER", "PARTSTAT", "RANGE", "RELATED", "RELTYPE", "ROLE", "RSVP", "SCHEMA",
        "SENT-BY", "TZID", "VALUE"
    };
    private static readonly HashSet<string> FormatTypeProperties = new(BinaryValueProperties, StringComparer.OrdinalIgnoreCase)
    {
        "CONFERENCE", "LINK", "STYLED-DESCRIPTION"
    };
    private static readonly HashSet<string> ParticipantProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH", "CALENDAR-ADDRESS", "CATEGORIES", "COMMENT", "CONCEPT", "CONTACT", "CREATED",
        "DESCRIPTION", "DTSTAMP", "GEO", "LAST-MODIFIED", "LINK", "LOCATION", "PARTICIPANT-TYPE",
        "PRIORITY", "REFID", "RELATED-TO", "REQUEST-STATUS", "RESOURCES", "SEQUENCE", "STATUS",
        "STRUCTURED-DATA", "STYLED-DESCRIPTION", "SUMMARY", "UID", "URL"
    };
    private static readonly HashSet<string> LocationComponentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONCEPT", "DESCRIPTION", "GEO", "LINK", "LOCATION-TYPE", "NAME", "REFID", "RELATED-TO",
        "STRUCTURED-DATA", "UID", "URL"
    };
    private static readonly HashSet<string> ResourceComponentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONCEPT", "DESCRIPTION", "GEO", "LINK", "NAME", "REFID", "RELATED-TO", "RESOURCE-TYPE",
        "STRUCTURED-DATA", "UID"
    };
    private static readonly HashSet<string> EntityExtensionOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "VEVENT", "VTODO"
    };
    private static readonly HashSet<string> StructuredDataOwners = new(EntityExtensionOwners, StringComparer.OrdinalIgnoreCase)
    {
        "PARTICIPANT", "VLOCATION", "VRESOURCE"
    };
    private static readonly HashSet<string> StyledDescriptionOwners = new(EntityExtensionOwners, StringComparer.OrdinalIgnoreCase)
    {
        "PARTICIPANT", "VALARM"
    };
    private static readonly HashSet<string> EventProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH", "ATTENDEE", "CATEGORIES", "CLASS", "COLOR", "COMMENT", "CONCEPT", "CONFERENCE",
        "CONTACT", "CREATED", "DESCRIPTION", "DTEND", "DTSTAMP", "DTSTART", "DURATION", "EXDATE", "GEO",
        "IMAGE", "LAST-MODIFIED", "LINK", "LOCATION", "ORGANIZER", "PRIORITY", "RDATE", "RECURRENCE-ID",
        "REFID", "RELATED-TO", "REQUEST-STATUS", "RESOURCES", "RRULE", "SEQUENCE", "STATUS",
        "STRUCTURED-DATA", "STYLED-DESCRIPTION", "SUMMARY", "TRANSP", "UID", "URL"
    };
    private static readonly HashSet<string> TodoProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ATTACH", "ATTENDEE", "CATEGORIES", "CLASS", "COLOR", "COMMENT", "COMPLETED", "CONCEPT",
        "CONFERENCE", "CONTACT", "CREATED", "DESCRIPTION", "DTSTAMP", "DTSTART", "DUE", "DURATION",
        "EXDATE", "GEO", "IMAGE", "LAST-MODIFIED", "LINK", "LOCATION", "ORGANIZER", "PERCENT-COMPLETE",
        "PRIORITY", "RDATE", "RECURRENCE-ID", "REFID", "RELATED-TO", "REQUEST-STATUS", "RESOURCES", "RRULE",
        "SEQUENCE", "STATUS", "STRUCTURED-DATA", "STYLED-DESCRIPTION", "SUMMARY", "UID", "URL"
    };

    public static CalendarProjectionResult Project(ReadOnlySpan<byte> authoritativeUtf8)
    {
        try
        {
            var document = CalendarContentDocument.Parse(authoritativeUtf8);
            return Project(document);
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

    internal static bool ContainsEntityUid(CalendarProjectionResult result, string uid) => result.Properties.Any(property =>
        property.Name.Equals("UID", StringComparison.OrdinalIgnoreCase)
        && property.ComponentPath.Count == 2
        && property.ComponentPath[1].Name is "VEVENT" or "VTODO"
        && string.Equals(DecodeText(property.RawEncodedValue), uid, StringComparison.Ordinal));

    internal static bool IsValidExactResource(CalendarContentDocument document)
    {
        if (!HasValidExactDocument(document))
            return false;

        var entities = GetEntityComponents(document);
        return ValidateEntityComponents(entities) is null
            && HasValidTemporalRelationships(entities, document);
    }

    private static bool HasValidExactDocument(CalendarContentDocument document) =>
        ValidateComponents(document.Components) is null
        && ValidateExtensionComponents(document.Components, document.Properties) is null
        && ValidateCalendarProperties(document.Properties) is null
        && ValidateFilteredProjectionExtensions(document.Properties) is null
        && HasValidExactPropertySemantics(document.Properties)
        && ValidateSupportingComponents(document.Components, document.Properties) is null;

    private static bool HasValidExactPropertySemantics(IReadOnlyList<CalendarContentProperty> properties) =>
        HasValidExactPropertyGrammar(properties) && HasValidExactPropertyShape(properties);

    private static bool HasValidExactPropertyGrammar(IReadOnlyList<CalendarContentProperty> properties) =>
        properties.All(HasValidRegisteredPropertyGrammar)
        && !properties.Any(HasInvalidRegisteredPlacement)
        && !properties.Any(HasInvalidRangeOrRelatedParameter)
        && !properties.Any(HasInvalidTimeZoneParameter)
        && !properties.Any(HasInvalidKnownParameter)
        && !properties.Any(HasInvalidRelatedToCombination)
        && !properties.Any(HasInvalidExactValueParameter);

    internal static bool HasValidRegisteredPropertyGrammar(CalendarContentProperty property) =>
        !HasInvalidRegisteredGrammar(property);

    private static bool HasValidExactPropertyShape(IReadOnlyList<CalendarContentProperty> properties) =>
        !HasRepeatedExactSingleton(properties)
        && HasValidStyledDescriptionCardinality(properties)
        && HasValidRootLanguageVariantCardinality(properties)
        && HasExactCalendarVersion(properties)
        && HasValidRootOptionalCardinality(properties)
        && HasValidCalendarScale(properties);

    internal static CalendarProjectionResult Project(CalendarContentDocument document)
    {
        var calendar = LoadTypedCalendar(document);
        return Project(document, calendar);
    }

    internal static CalendarProjectionResult Project(
        CalendarContentDocument document,
        IcalCalendar? typedCalendar)
    {
        var properties = document.Properties.Select(ToPublicProperty).ToArray();
        try
        {
            var diagnostic = ValidateComponents(document.Components);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);
            diagnostic = ValidateExtensionComponents(document.Components, document.Properties);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);
            diagnostic = ValidateCalendarProperties(document.Properties);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);
            if (document.Properties.Any(HasUnsupportedTypedProjectionValue))
                return Opaque(properties, "typed_projection_unsupported");
            if (HasUnsupportedCalendarScale(document.Properties))
                return Opaque(properties, "typed_projection_invalid");
            diagnostic = ValidateFilteredProjectionExtensions(document.Properties);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);

            var entities = GetEntityComponents(document);
            diagnostic = ValidateEntityComponents(entities);
            if (diagnostic is not null)
                return Opaque(properties, diagnostic);

            if (!IcalNetCorroborates(
                    document,
                    typedCalendar,
                    entities[0].Kind,
                    entities.Count))
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
            recurrenceProperty is not null && HasRecurrenceRange(recurrenceProperty),
            owned);
    }

    private static bool HasRecurrenceRange(CalendarContentProperty property) => property.Parameters
        .Any(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase));

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
        "PARTICIPANT" => path.Count == 3 && path[1].Name is "VEVENT" or "VTODO",
        "VLOCATION" => HasValidLocationTopology(path),
        "VRESOURCE" => path.Count == 3 && path[1].Name is "VEVENT" or "VTODO"
            || path.Count == 4 && path[2].Name == "PARTICIPANT",
        _ => true
    };

    private static bool HasValidLocationTopology(IReadOnlyList<CalendarComponentPathSegment> path) =>
        path.Count == 3 && path[1].Name is "VEVENT" or "VTODO"
        || path.Count == 4 && path[2].Name is "PARTICIPANT" or "VALARM";

    private static string? ValidateExtensionComponents(
        IReadOnlyList<CalendarContentComponent> components,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        foreach (var component in components.Where(component => component.Path[^1].Name is
                     "PARTICIPANT" or "VLOCATION" or "VRESOURCE"))
        {
            var owned = properties.Where(property => PathsEqual(property.ComponentPath, component.Path)).ToArray();
            if (!HasValidExtensionShape(component.Path[^1].Name, owned))
                return "extension_component_invalid";
        }
        return null;
    }

    private static bool HasValidExtensionShape(
        string componentName,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        if (CountProperties(properties, "UID") != 1 || HasRepeatedExtensionSingleton(properties))
            return false;
        return componentName != "PARTICIPANT"
            || CountProperties(properties, "PARTICIPANT-TYPE") == 1;
    }

    private static bool HasRepeatedExtensionSingleton(IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => ExtensionSingletonProperties.Contains(property.Name))
        .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        .Any(group => group.Count() > 1);

    private static int CountProperties(IReadOnlyList<CalendarContentProperty> properties, string name) =>
        properties.Count(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && property.RawEncodedValue.Length > 0);

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
            .GroupBy(entity => (entity.RecurrenceIdentity, entity.IsRange)).Any(group => group.Count() > 1))
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

    private static bool HasUnsupportedTypedProjectionValue(CalendarContentProperty property) =>
        property.Name.ToUpperInvariant() switch
        {
            "ATTACH" or "IMAGE" or "LINK" or "STRUCTURED-DATA" =>
                GetSingleParameterValue(property, "VALUE") is { } value
                && !value.Equals("URI", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool HasExactCalendarVersion(IReadOnlyList<CalendarContentProperty> properties) =>
        properties.Count(property => property.ComponentPath.Count == 1
            && property.Name.Equals("VERSION", StringComparison.OrdinalIgnoreCase)
            && property.RawEncodedValue == "2.0") == 1;

    private static bool HasValidRootOptionalCardinality(IReadOnlyList<CalendarContentProperty> properties) =>
        properties.All(property => property.ComponentPath.Count != 1
            || !property.Name.Equals("METHOD", StringComparison.OrdinalIgnoreCase))
        && properties.Count(property => property.ComponentPath.Count == 1
            && property.Name.Equals("CALSCALE", StringComparison.OrdinalIgnoreCase)) <= 1;

    private static bool HasUnsupportedCalendarScale(IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => property.ComponentPath.Count == 1
            && property.Name.Equals("CALSCALE", StringComparison.OrdinalIgnoreCase))
        .Any(property => !property.RawEncodedValue.Equals("GREGORIAN", StringComparison.OrdinalIgnoreCase));

    private static bool HasValidCalendarScale(IReadOnlyList<CalendarContentProperty> properties)
    {
        var values = properties.Where(property => property.ComponentPath.Count == 1
                && property.Name.Equals("CALSCALE", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.RawEncodedValue)
            .ToArray();
        return values.Length == 0 || values.Length == 1 && IsToken(values[0]);
    }

    private static string? ValidateSupportingComponents(
        IReadOnlyList<CalendarContentComponent> components,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        var timeZoneFailure = ValidateTimeZones(components, properties);
        return timeZoneFailure ?? ValidateAlarms(components, properties);
    }

    private static string? ValidateTimeZones(
        IReadOnlyList<CalendarContentComponent> components,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        var timeZones = components.Where(component => component.Path.Count == 2
            && component.Path[^1].Name == "VTIMEZONE").ToArray();
        var timeZoneIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var timeZone in timeZones)
        {
            var owned = OwnedProperties(timeZone, properties);
            if (!TryAddValidTimeZone(owned, timeZoneIds))
                return "timezone_invalid";
            var observances = components.Where(component => component.Path.Count == 3
                && component.Path[1].Occurrence == timeZone.Path[1].Occurrence
                && component.Path[^1].Name is "STANDARD" or "DAYLIGHT").ToArray();
            if (observances.Length == 0 || observances.Any(observance => !HasValidObservance(observance, properties)))
                return "timezone_observance_invalid";
        }

        return HasEveryReferencedTimeZone(properties, timeZoneIds)
            ? null
            : "timezone_reference_invalid";
    }

    private static bool TryAddValidTimeZone(
        IReadOnlyList<CalendarContentProperty> properties,
        ISet<string> timeZoneIds)
    {
        var timeZoneId = GetRequiredValue(properties, "TZID");
        return timeZoneId is not null
            && timeZoneIds.Add(timeZoneId)
            && CountProperties(properties, "TZURL") <= 1
            && CountProperties(properties, "LAST-MODIFIED") <= 1;
    }

    private static bool HasEveryReferencedTimeZone(
        IReadOnlyList<CalendarContentProperty> properties,
        IReadOnlySet<string> timeZoneIds) => properties
        .SelectMany(property => property.Parameters)
        .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
        .SelectMany(parameter => parameter.Values)
        .Distinct(StringComparer.Ordinal)
        .All(timeZoneIds.Contains);

    private static string? ValidateAlarms(
        IReadOnlyList<CalendarContentComponent> components,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        foreach (var alarm in components.Where(component => component.Path[^1].Name == "VALARM"))
        {
            var owned = OwnedProperties(alarm, properties);
            if (!HasValidAlarmShape(owned)
                || !HasValidAlarmAnchor(alarm, owned, properties)
                || !HasValidAlarmLocations(alarm, owned, components, properties))
                return "alarm_invalid";
        }

        return null;
    }

    private static bool HasValidAlarmShape(IReadOnlyList<CalendarContentProperty> properties)
    {
        var action = GetRequiredValue(properties, "ACTION");
        if (action is null || GetRequiredValue(properties, "TRIGGER") is null
            || !HasValidAlarmRepeatPair(properties) || !HasValidOptionalAlarmUid(properties))
            return false;
        return action.ToUpperInvariant() switch
        {
            "DISPLAY" => GetRequiredValue(properties, "DESCRIPTION") is not null
                && HasOnlyAllowedAlarmProperties(properties, DisplayAlarmProperties),
            "EMAIL" => GetRequiredValue(properties, "DESCRIPTION") is not null
                && GetRequiredValue(properties, "SUMMARY") is not null
                && CountProperties(properties, "ATTENDEE") > 0
                && HasOnlyAllowedAlarmProperties(properties, EmailAlarmProperties),
            "AUDIO" => CountProperties(properties, "ATTACH") <= 1
                && HasOnlyAllowedAlarmProperties(properties, AudioAlarmProperties),
            _ => true
        };
    }

    private static bool HasValidOptionalAlarmUid(IReadOnlyList<CalendarContentProperty> properties)
    {
        var occurrences = properties.Where(property => property.Name.Equals("UID", StringComparison.OrdinalIgnoreCase)).ToArray();
        return occurrences.Length == 0 || occurrences.Length == 1 && occurrences[0].RawEncodedValue.Length > 0;
    }

    private static bool HasValidAlarmLocations(
        CalendarContentComponent alarm,
        IReadOnlyList<CalendarContentProperty> alarmProperties,
        IReadOnlyList<CalendarContentComponent> components,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        var locations = components.Where(component => component.Path.Count == alarm.Path.Count + 1
            && component.Path.Take(alarm.Path.Count).SequenceEqual(alarm.Path)
            && component.Path[^1].Name == "VLOCATION").ToArray();
        var proximity = GetOptionalValue(alarmProperties, "PROXIMITY");
        if (locations.Length > 0 && proximity is null)
            return false;
        if (proximity is not null
            && (proximity.Equals("ARRIVE", StringComparison.OrdinalIgnoreCase)
                || proximity.Equals("DEPART", StringComparison.OrdinalIgnoreCase))
            && locations.Length == 0)
            return false;
        return locations.All(location => HasValidProximityLocation(OwnedProperties(location, properties)));
    }

    private static bool HasValidProximityLocation(IReadOnlyList<CalendarContentProperty> properties)
    {
        var url = GetRequiredValue(properties, "URL");
        return url is not null && IsValidGeoUri(url);
    }

    private static bool IsValidGeoUri(string value)
    {
        if (!value.StartsWith("geo:", StringComparison.OrdinalIgnoreCase))
            return false;
        var sections = value[4..].Split(';', StringSplitOptions.None);
        return HasValidGeoCoordinates(sections[0]) && HasValidGeoParameters(sections[1..]);
    }

    private static bool HasValidGeoCoordinates(string value)
    {
        var coordinates = value.Split(',', StringSplitOptions.None);
        if (coordinates.Length is < 2 or > 3
            || !TryReadGeoNumber(coordinates[0], true, 2, out var latitude)
            || !TryReadGeoNumber(coordinates[1], true, 3, out var longitude))
            return false;
        if (!IsWithinGeoLimit(latitude, 90) || !IsWithinGeoLimit(longitude, 180))
            return false;
        return coordinates.Length == 2 || TryReadGeoNumber(coordinates[2], true, int.MaxValue, out _);
    }

    private static bool HasValidGeoParameters(IReadOnlyList<string> parameters)
    {
        var parsed = parameters.Select(ParseGeoParameter).ToArray();
        if (parsed.Any(parameter => parameter is null))
            return false;
        var values = parsed.Select(parameter => parameter!.Value).ToArray();
        return HasValidGeoParameterOrder(values)
            && HasValidGeoCoordinateSystem(values)
            && HasValidGeoUncertainty(values)
            && values.Where(parameter => parameter.Name is not ("CRS" or "U"))
                .All(parameter => IsValidGeoParameterValue(parameter.Value));
    }

    private static (string Name, string? Value)? ParseGeoParameter(string parameter)
    {
        var separator = parameter.IndexOf('=');
        var name = separator < 0 ? parameter : parameter[..separator];
        var value = separator < 0 ? null : parameter[(separator + 1)..];
        return IsValidGeoParameterName(name) && (value is null || value.Length > 0)
            ? (name.ToUpperInvariant(), value)
            : null;
    }

    private static bool HasValidGeoCoordinateSystem(IReadOnlyList<(string Name, string? Value)> parameters)
    {
        var coordinateSystems = parameters.Where(parameter => parameter.Name == "CRS").ToArray();
        return coordinateSystems.Length == 0 || coordinateSystems.Length == 1
            && coordinateSystems[0].Value?.Equals("wgs84", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasValidGeoUncertainty(IReadOnlyList<(string Name, string? Value)> parameters)
    {
        var uncertainties = parameters.Where(parameter => parameter.Name == "U").ToArray();
        return uncertainties.Length == 0 || uncertainties.Length == 1
            && uncertainties[0].Value is { } value
            && TryReadGeoNumber(value, false, int.MaxValue, out _);
    }

    private static bool HasValidGeoParameterOrder(IReadOnlyList<(string Name, string? Value)> parameters)
    {
        var knownPrefixLength = parameters.TakeWhile(parameter => parameter.Name is "CRS" or "U").Count();
        if (parameters.Skip(knownPrefixLength).Any(parameter => parameter.Name is "CRS" or "U"))
            return false;
        var coordinateSystemIndex = Enumerable.Range(0, parameters.Count)
            .FirstOrDefault(index => parameters[index].Name == "CRS", -1);
        var uncertaintyIndex = Enumerable.Range(0, parameters.Count)
            .FirstOrDefault(index => parameters[index].Name == "U", -1);
        return coordinateSystemIndex < 0 || coordinateSystemIndex == 0
            && (uncertaintyIndex < 0 || uncertaintyIndex > coordinateSystemIndex);
    }

    private static bool TryReadGeoNumber(
        string value,
        bool allowNegative,
        int maximumIntegerDigits,
        out GeoNumber number)
    {
        number = default;
        var negative = value.StartsWith('-');
        if (negative && !allowNegative)
            return false;
        var start = negative ? 1 : 0;
        var separator = value.IndexOf('.');
        var integerEnd = separator < 0 ? value.Length : separator;
        if (!HasValidGeoDigits(value[start..integerEnd], maximumIntegerDigits))
            return false;
        var fraction = separator < 0 ? string.Empty : value[(separator + 1)..];
        if (separator >= 0 && !HasValidGeoDigits(fraction, int.MaxValue))
            return false;
        number = new GeoNumber(value[start..integerEnd], fraction);
        return true;
    }

    private static bool HasValidGeoDigits(string value, int maximumLength) => value.Length > 0
        && value.Length <= maximumLength
        && value.All(char.IsAsciiDigit);

    private static bool IsWithinGeoLimit(GeoNumber number, int limit)
    {
        var integral = int.Parse(number.Integer, NumberStyles.None, CultureInfo.InvariantCulture);
        return integral < limit || integral == limit && number.Fraction.All(character => character == '0');
    }

    private readonly record struct GeoNumber(string Integer, string Fraction);

    private static bool IsValidGeoParameterName(string value) => value.Length > 0
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidGeoParameterValue(string? value)
    {
        if (value is null)
            return true;
        for (var index = 0; index < value.Length; index++)
        {
            if (IsValidGeoParameterCharacter(value[index]))
                continue;
            if (value[index] != '%' || index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }
        return value.Length > 0;
    }

    private static bool IsValidGeoParameterCharacter(char value) => char.IsAsciiLetterOrDigit(value)
        || value is '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')'
            or '[' or ']' or ':' or '&' or '+' or '$';

    private static bool HasOnlyAllowedAlarmProperties(
        IReadOnlyList<CalendarContentProperty> properties,
        IReadOnlySet<string> allowed) => properties.All(property =>
            !CalendarContentDocument.IsRegisteredPropertyName(property.Name) || allowed.Contains(property.Name));

    private static bool HasValidAlarmRepeatPair(IReadOnlyList<CalendarContentProperty> properties)
    {
        var durationCount = CountProperties(properties, "DURATION");
        var repeatCount = CountProperties(properties, "REPEAT");
        return durationCount == repeatCount && durationCount <= 1;
    }

    private static bool HasValidAlarmAnchor(
        CalendarContentComponent alarm,
        IReadOnlyList<CalendarContentProperty> alarmProperties,
        IReadOnlyList<CalendarContentProperty> allProperties)
    {
        var trigger = GetProperty(alarmProperties, "TRIGGER")!;
        if (trigger.ValueType == CalendarPropertyValueType.DateTime)
            return true;
        var related = trigger.Parameters
            .Where(parameter => parameter.Name.Equals("RELATED", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault() ?? "START";
        var parentPath = alarm.Path.Take(2).ToArray();
        var parent = allProperties.Where(property => PathsEqual(property.ComponentPath, parentPath)).ToArray();
        if (related.Equals("START", StringComparison.OrdinalIgnoreCase))
            return GetProperty(parent, "DTSTART") is not null;
        var endName = parentPath[^1].Name == "VEVENT" ? "DTEND" : "DUE";
        return GetProperty(parent, endName) is not null
            || GetProperty(parent, "DTSTART") is not null && GetProperty(parent, "DURATION") is not null;
    }

    private static bool HasValidObservance(
        CalendarContentComponent observance,
        IReadOnlyList<CalendarContentProperty> properties)
    {
        var owned = OwnedProperties(observance, properties);
        return GetRequiredValue(owned, "DTSTART") is not null
            && GetRequiredValue(owned, "TZOFFSETFROM") is not null
            && GetRequiredValue(owned, "TZOFFSETTO") is not null
            && CountProperties(owned, "RRULE") <= 1
            && HasLocalObservanceDateTimes(owned)
            && HasValidRecurrenceUntil(owned, isObservance: true);
    }

    private static bool HasLocalObservanceDateTimes(IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => property.Name.ToUpperInvariant() is "DTSTART" or "RDATE")
        .All(property => property.ValueType == CalendarPropertyValueType.DateTime
            && !property.RawEncodedValue.Split(',', StringSplitOptions.None).Any(value => value.EndsWith('Z'))
            && !property.Parameters.Any(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase)));

    private static CalendarContentProperty[] OwnedProperties(
        CalendarContentComponent component,
        IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => PathsEqual(property.ComponentPath, component.Path))
        .ToArray();

    private static string? ValidateFilteredProjectionExtensions(
        IReadOnlyList<CalendarContentProperty> properties)
    {
        foreach (var property in properties.Where(property =>
                     CalendarContentDocument.IsProjectionExtensionUnsupportedByIcalNet(property.Name)))
        {
            if (!HasAllowedExtensionPropertyPath(property))
                return "extension_property_topology_invalid";
            if (HasRepeatedParameterName(property) || HasInvalidFilteredExtensionValue(property))
                return "extension_property_value_invalid";
        }

        return null;
    }

    private static bool HasAllowedExtensionPropertyPath(CalendarContentProperty property)
    {
        var component = property.ComponentPath[^1].Name;
        if (property.Name.ToUpperInvariant() is "CONCEPT" or "LINK")
            return property.ComponentPath.Count > 1;
        if (!HasDirectSupportedOwnerPath(property.ComponentPath, component))
            return false;
        return property.Name.ToUpperInvariant() switch
        {
            "CALENDAR-ADDRESS" or "PARTICIPANT-TYPE" => component == "PARTICIPANT",
            "IMAGE" => component == "VCALENDAR" || EntityExtensionOwners.Contains(component),
            "STRUCTURED-DATA" => StructuredDataOwners.Contains(component),
            "STYLED-DESCRIPTION" => StyledDescriptionOwners.Contains(component),
            _ => EntityExtensionOwners.Contains(component)
        };
    }

    private static bool HasDirectSupportedOwnerPath(
        IReadOnlyList<CalendarComponentPathSegment> path,
        string component) => component switch
        {
            "VCALENDAR" => path.Count == 1,
            "VEVENT" or "VTODO" => path.Count == 2,
            "VTIMEZONE" => path.Count == 2,
            "DAYLIGHT" or "PARTICIPANT" or "STANDARD" or "VALARM" => path.Count == 3,
            "VLOCATION" or "VRESOURCE" => path.Count is 3 or 4,
            _ => false
        };

    private static bool HasRepeatedParameterName(CalendarContentProperty property) => property.Parameters
        .Where(parameter => SingletonParameterNames.Contains(parameter.Name))
        .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
        .Any(group => group.Count() > 1);

    private static bool HasInvalidFilteredExtensionValue(CalendarContentProperty property) => property.Name.ToUpperInvariant() switch
    {
        "PARTICIPANT-TYPE" => !IsToken(property.RawEncodedValue),
        "IMAGE" or "STRUCTURED-DATA" when property.ValueType == CalendarPropertyValueType.Binary =>
            !IsValidEncodedBinary(property),
        _ => false
    };

    private static bool IsValidEncodedBinary(CalendarContentProperty property) =>
        IsBase64(property.RawEncodedValue)
        && GetSingleParameterValue(property, "ENCODING")?.Equals("BASE64", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetSingleParameterValue(CalendarContentProperty property, string name)
    {
        var values = property.Parameters
            .Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    internal static bool IsToken(string value) => value.Length > 0
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');

    private static bool HasInvalidRegisteredValueType(CalendarContentProperty property)
    {
        if (property.Name.ToUpperInvariant() is "TZOFFSETFROM" or "TZOFFSETTO")
            return !CalendarContentDocument.IsValidRegisteredUnknownValue(property);
        if (!CalendarContentDocument.IsRegisteredPropertyName(property.Name))
            return false;
        if (property.ValueType == CalendarPropertyValueType.Unknown
            && CalendarContentDocument.HasNoDefaultValueType(property.Name)
            && CountParameterValues(property, "VALUE") == 0)
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

    private static bool HasInvalidExactValueParameter(CalendarContentProperty property)
    {
        var overrides = property.Parameters
            .Where(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .ToArray();
        return property.Name.ToUpperInvariant() switch
        {
            "STYLED-DESCRIPTION" => !HasSingleAllowedValueParameter(property, overrides, "TEXT", "URI"),
            "STRUCTURED-DATA" => HasInvalidStructuredDataValue(property, overrides),
            "LINK" => HasInvalidLinkValueParameter(property, overrides),
            "RELATED-TO" => HasInvalidRelatedToValueParameter(property, overrides),
            "REFRESH-INTERVAL" => !HasSingleAllowedValueParameter(property, overrides, "DURATION"),
            "SOURCE" or "CONFERENCE" => !HasSingleAllowedValueParameter(property, overrides, "URI"),
            "IMAGE" => !HasSingleAllowedValueParameter(property, overrides, "URI", "BINARY"),
            _ => HasInvalidOptionalValueParameter(property, overrides)
        };
    }

    private static bool HasInvalidStructuredDataValue(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides) => !HasSingleAllowedValueParameter(property, overrides, "TEXT", "BINARY", "URI")
            || !HasRequiredStructuredDataParameters(property, overrides);

    private static bool HasInvalidLinkValueParameter(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides) => !HasSingleAllowedValueParameter(property, overrides, "URI", "UID", "XML-REFERENCE")
            || CountParameterValues(property, "LINKREL") != 1;

    private static bool HasInvalidRelatedToValueParameter(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides) => overrides.Count > 0
            && !HasSingleAllowedValueParameter(property, overrides, "URI", "UID", "TEXT");

    private static bool HasInvalidOptionalValueParameter(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides) => overrides.Count > 0
            && (!AllowsExplicitValueParameter(property.Name)
                || overrides.Count != 1
                || !AllowsValueType(property.Name, property.ValueType));

    private static bool HasSingleAllowedValueParameter(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides,
        params string[] allowed) => overrides.Count == 1
        && allowed.Contains(overrides[0], StringComparer.OrdinalIgnoreCase)
        && AllowsValueType(property.Name, property.ValueType);

    private static bool HasRequiredStructuredDataParameters(
        CalendarContentProperty property,
        IReadOnlyList<string> overrides)
    {
        if (overrides.Count != 1 || overrides[0].Equals("URI", StringComparison.OrdinalIgnoreCase))
            return true;
        return CountParameterValues(property, "FMTTYPE") == 1
            && CountParameterValues(property, "SCHEMA") == 1;
    }

    private static bool AllowsExplicitValueParameter(string propertyName) => propertyName.ToUpperInvariant() is
        "ATTACH" or "CONFERENCE" or "IMAGE" or "SOURCE" or "REFRESH-INTERVAL" or "DTSTART" or "DTEND"
        or "DUE" or "EXDATE" or "RECURRENCE-ID" or "RELATED-TO" or "RDATE" or "TRIGGER" or "LINK"
        or "STRUCTURED-DATA" or "STYLED-DESCRIPTION";

    private static bool AllowsValueType(string propertyName, CalendarPropertyValueType valueType) => propertyName.ToUpperInvariant() switch
    {
        "ATTACH" or "IMAGE" => valueType is CalendarPropertyValueType.Uri or CalendarPropertyValueType.Binary,
        "DTSTART" or "DTEND" or "DUE" or "EXDATE" or "RECURRENCE-ID" =>
            valueType is CalendarPropertyValueType.Date or CalendarPropertyValueType.DateTime,
        "RDATE" => valueType is CalendarPropertyValueType.Date or CalendarPropertyValueType.DateTime or CalendarPropertyValueType.Period,
        "TRIGGER" => valueType is CalendarPropertyValueType.Duration or CalendarPropertyValueType.DateTime,
        "CONFERENCE" or "SOURCE" => valueType == CalendarPropertyValueType.Uri,
        "REFRESH-INTERVAL" => valueType == CalendarPropertyValueType.Duration,
        "LINK" => valueType is CalendarPropertyValueType.Uri or CalendarPropertyValueType.Uid
            or CalendarPropertyValueType.XmlReference,
        "RELATED-TO" => valueType is CalendarPropertyValueType.Text or CalendarPropertyValueType.Uri
            or CalendarPropertyValueType.Uid,
        "STRUCTURED-DATA" => valueType is CalendarPropertyValueType.Text or CalendarPropertyValueType.Uri or CalendarPropertyValueType.Binary,
        "STYLED-DESCRIPTION" => valueType is CalendarPropertyValueType.Text or CalendarPropertyValueType.Uri,
        _ => valueType == CalendarContentDocument.GetDefaultValueType(propertyName)
    };

    private static bool HasInvalidKnownValue(CalendarContentProperty property) => property.Name.ToUpperInvariant() switch
    {
        "URL" or "TZURL" or "SOURCE" or "ATTENDEE" or "ORGANIZER" or "CALENDAR-ADDRESS"
            or "CONCEPT" or "CONFERENCE" =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "LINK" => !HasValidLinkValue(property),
        "RELATED-TO" => !HasValidRelatedToValue(property),
        "IMAGE" when property.ValueType == CalendarPropertyValueType.Uri =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "IMAGE" when property.ValueType == CalendarPropertyValueType.Binary => !IsBase64(property.RawEncodedValue),
        "IMAGE" when property.ValueType == CalendarPropertyValueType.Unknown =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "STRUCTURED-DATA" when property.ValueType == CalendarPropertyValueType.Uri =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "STRUCTURED-DATA" when property.ValueType == CalendarPropertyValueType.Binary => !IsBase64(property.RawEncodedValue),
        "STYLED-DESCRIPTION" when property.ValueType == CalendarPropertyValueType.Uri =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "ATTACH" when property.ValueType == CalendarPropertyValueType.Uri =>
            !Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
        "ATTACH" when property.ValueType == CalendarPropertyValueType.Binary => !IsValidEncodedBinary(property),
        _ => false
    };

    private static bool HasValidLinkValue(CalendarContentProperty property) =>
        GetSingleParameterValue(property, "VALUE")?.ToUpperInvariant() switch
        {
            "UID" => property.RawEncodedValue.Length > 0,
            "URI" => Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
            "XML-REFERENCE" => Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out var uri)
                && uri.Fragment.Length > 1,
            _ => Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _)
        };

    private static bool HasValidRelatedToValue(CalendarContentProperty property)
    {
        var explicitValue = GetSingleParameterValue(property, "VALUE")?.ToUpperInvariant();
        return explicitValue switch
        {
            "URI" => Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out _),
            "TEXT" or "UID" or null => property.RawEncodedValue.Length > 0,
            _ => false
        };
    }

    private static bool HasInvalidRelatedToCombination(CalendarContentProperty property)
    {
        if (!property.Name.Equals("RELATED-TO", StringComparison.OrdinalIgnoreCase))
            return false;
        var explicitValue = GetSingleParameterValue(property, "VALUE")?.ToUpperInvariant();
        var relationship = GetSingleParameterValue(property, "RELTYPE")?.ToUpperInvariant() ?? "PARENT";
        return relationship is "PARENT" or "CHILD" or "SIBLING"
            && explicitValue is not (null or "UID");
    }

    private static bool HasInvalidRegisteredGrammar(CalendarContentProperty property)
    {
        if (HasInvalidRegisteredTextGrammar(property))
            return true;
        if (TemporalSingletonProperties.Contains(property.Name))
            return !HasValidTemporalGrammar(property);
        if (property.ValueType == CalendarPropertyValueType.Recur)
            return !HasValidRecurrenceRule(property);
        if (property.Name.ToUpperInvariant() is "DURATION" or "REFRESH-INTERVAL")
            return !HasValidEntityDuration(property);
        if (property.Name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
            return !HasValidTrigger(property);
        if (property.Name.ToUpperInvariant() is "RDATE" or "EXDATE")
            return !HasValidTemporalCollection(property);
        if (IntegerProperties.Contains(property.Name))
            return !HasValidInteger(property.Name, property.RawEncodedValue);
        return HasInvalidScalarGrammar(property);
    }

    private static bool HasInvalidScalarGrammar(CalendarContentProperty property)
    {
        if (property.Name.Equals("COLOR", StringComparison.OrdinalIgnoreCase))
            return !Css3ColorNameValidator.IsValid(property.RawEncodedValue);
        if (TokenProperties.Contains(property.Name))
            return !IsToken(property.RawEncodedValue);
        if (property.Name.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            return !IsToken(property.RawEncodedValue);
        if (property.Name.Equals("TRANSP", StringComparison.OrdinalIgnoreCase))
            return !IsToken(property.RawEncodedValue);
        return property.Name.Equals("GEO", StringComparison.OrdinalIgnoreCase) && !HasValidGeo(property.RawEncodedValue);
    }

    private static bool HasInvalidRegisteredTextGrammar(CalendarContentProperty property)
    {
        if (!CalendarContentDocument.IsRegisteredPropertyName(property.Name)
            || property.ValueType != CalendarPropertyValueType.Text)
            return false;
        if (property.Name.Equals("REQUEST-STATUS", StringComparison.OrdinalIgnoreCase))
            return !HasValidRequestStatus(property.RawEncodedValue);
        return !HasValidTextValue(property.RawEncodedValue,
            property.Name.ToUpperInvariant() is "CATEGORIES" or "LOCATION-TYPE" or "RESOURCES");
    }

    private static bool HasValidInteger(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;
        return name.ToUpperInvariant() switch
        {
            "PRIORITY" => parsed is >= 0 and <= 9,
            "PERCENT-COMPLETE" => parsed is >= 0 and <= 100,
            "REPEAT" => parsed > 0,
            "SEQUENCE" => parsed >= 0,
            _ => false
        };
    }

    private static bool HasInvalidRegisteredPlacement(CalendarContentProperty property)
    {
        if (!CalendarContentDocument.IsRegisteredPropertyName(property.Name))
            return false;
        return property.ComponentPath[^1].Name switch
        {
            "VCALENDAR" => !RootCalendarProperties.Contains(property.Name),
            "VTIMEZONE" => !TimeZoneProperties.Contains(property.Name),
            "STANDARD" or "DAYLIGHT" => !ObservanceProperties.Contains(property.Name),
            "VALARM" => !AlarmProperties.Contains(property.Name),
            "VEVENT" => !EventProperties.Contains(property.Name),
            "VTODO" => !TodoProperties.Contains(property.Name),
            "PARTICIPANT" => !ParticipantProperties.Contains(property.Name),
            "VLOCATION" => !LocationComponentProperties.Contains(property.Name),
            "VRESOURCE" => !ResourceComponentProperties.Contains(property.Name),
            _ => false
        };
    }

    private static bool HasInvalidRangeOrRelatedParameter(CalendarContentProperty property) =>
        HasInvalidRangeParameter(property) || HasInvalidRelatedParameter(property);

    private static bool HasInvalidRangeParameter(CalendarContentProperty property)
    {
        var ranges = property.Parameters
            .Where(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return ranges.Length > 0
            && (!property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase)
            || ranges.Length != 1
            || ranges[0].Values.Count != 1
            || !ranges[0].Values[0].Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasInvalidRelatedParameter(CalendarContentProperty property)
    {
        var related = property.Parameters
            .Where(parameter => parameter.Name.Equals("RELATED", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (related.Length == 0)
            return false;
        return !property.Name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)
            || property.ValueType != CalendarPropertyValueType.Duration
            || related.Length != 1
            || related[0].Values.Count != 1
            || !related[0].Values[0].Equals("START", StringComparison.OrdinalIgnoreCase)
            && !related[0].Values[0].Equals("END", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInvalidTimeZoneParameter(CalendarContentProperty property)
    {
        var parameters = property.Parameters
            .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (parameters.Length == 0)
            return false;
        return parameters.Length != 1
            || parameters[0].Values.Count != 1
            || property.ValueType is not (CalendarPropertyValueType.DateTime or CalendarPropertyValueType.Period)
            || property.RawEncodedValue.Split([',', '/'], StringSplitOptions.None).Any(value => value.EndsWith('Z'));
    }

    private static bool HasRepeatedExactSingleton(IReadOnlyList<CalendarContentProperty> properties) => properties
        .GroupBy(property => string.Join('/', property.ComponentPath.Select(segment =>
                $"{segment.Name}:{segment.Occurrence}")) + "|" + property.Name,
            StringComparer.OrdinalIgnoreCase)
        .Any(group => group.Count() > 1 && IsExactSingleton(group.First()));

    private static bool HasValidStyledDescriptionCardinality(
        IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => property.Name.Equals("STYLED-DESCRIPTION", StringComparison.OrdinalIgnoreCase))
        .GroupBy(property => string.Join('/', property.ComponentPath.Select(segment =>
                $"{segment.Name}:{segment.Occurrence}")), StringComparer.OrdinalIgnoreCase)
        .All(group => group.Count() == 1 || group.Count(property => !IsDerived(property)) == 1);

    private static bool HasValidRootLanguageVariantCardinality(
        IReadOnlyList<CalendarContentProperty> properties) => properties
        .Where(property => property.ComponentPath.Count == 1
            && property.Name.ToUpperInvariant() is "NAME" or "DESCRIPTION")
        .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        .All(group => group.Select(property => GetSingleParameterValue(property, "LANGUAGE") ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == group.Count());

    private static bool IsDerived(CalendarContentProperty property) =>
        GetSingleParameterValue(property, "DERIVED")?.Equals("TRUE", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsExactSingleton(CalendarContentProperty property) => property.ComponentPath[^1].Name switch
    {
        "VCALENDAR" => RootSingletonProperties.Contains(property.Name),
        "VEVENT" or "VTODO" => SingletonProperties.Contains(property.Name)
            || property.Name.ToUpperInvariant() is "RRULE" or "COLOR",
        "VTIMEZONE" => property.Name.ToUpperInvariant() is "TZID" or "LAST-MODIFIED" or "TZURL",
        "STANDARD" or "DAYLIGHT" => property.Name.ToUpperInvariant() is "DTSTART" or "TZOFFSETFROM" or "TZOFFSETTO" or "RRULE",
        "VALARM" => AlarmSingletonProperties.Contains(property.Name),
        _ => false
    };

    private static bool HasInvalidKnownParameter(CalendarContentProperty property) => HasRepeatedParameterName(property)
        || property.Parameters.Any(parameter => !IsKnownParameterApplicable(property, parameter.Name)
            || HasInvalidKnownParameterValue(property, parameter));

    private static bool HasInvalidKnownParameterValue(
        CalendarContentProperty property,
        CalendarParameter parameter)
    {
        var extensionFailure = HasInvalidExtensionParameterValue(parameter);
        if (extensionFailure is not null)
            return extensionFailure.Value;
        return parameter.Name.ToUpperInvariant() switch
        {
            "RSVP" => !HasSingleValue(parameter, "TRUE", "FALSE"),
            "ALTREP" or "DIR" or "SENT-BY" => !HasOneAbsoluteUri(parameter),
            "DELEGATED-FROM" or "DELEGATED-TO" or "MEMBER" =>
                parameter.Values.Count == 0 || parameter.Values.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out _)),
            "CUTYPE" or "FBTYPE" or "PARTSTAT" or "RELTYPE" or "ROLE" =>
                parameter.Values.Count != 1 || !IsToken(parameter.Values[0]),
            "ENCODING" => property.ValueType != CalendarPropertyValueType.Binary
                || !HasSingleValue(parameter, "BASE64"),
            "CN" => parameter.Values.Count != 1 || string.IsNullOrWhiteSpace(parameter.Values[0]),
            "EMAIL" => parameter.Values.Count != 1 || string.IsNullOrWhiteSpace(parameter.Values[0]),
            "LANGUAGE" => parameter.Values.Count != 1 || !HasValidLanguageTag(parameter.Values[0]),
            "FMTTYPE" => HasInvalidFormatType(property, parameter),
            "SCHEMA" => !HasOneAbsoluteUri(parameter),
            "LINKREL" => parameter.Values.Count != 1 || !IsTokenOrAbsoluteUri(parameter.Values[0]),
            _ => false
        };
    }

    private static bool? HasInvalidExtensionParameterValue(CalendarParameter parameter) =>
        parameter.Name.ToUpperInvariant() switch
        {
            "DISPLAY" or "FEATURE" => parameter.Values.Count == 0 || parameter.Values.Any(value => !IsToken(value)),
            "LABEL" => parameter.Values.Count != 1 || string.IsNullOrWhiteSpace(parameter.Values[0]),
            "DERIVED" => !HasSingleValue(parameter, "TRUE", "FALSE"),
            "ORDER" => HasInvalidOrder(parameter),
            "GAP" => parameter.Values.Count != 1 || !HasValidGap(parameter.Values[0]),
            _ => null
        };

    private static bool HasInvalidOrder(CalendarParameter parameter) => parameter.Values.Count != 1
        || !int.TryParse(parameter.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var order)
        || order < 1;

    internal static bool IsKnownParameterApplicable(CalendarContentProperty property, string parameterName) =>
        parameterName.ToUpperInvariant() switch
        {
            "RSVP" or "CUTYPE" or "DELEGATED-FROM" or "DELEGATED-TO" or "MEMBER" or "PARTSTAT"
                or "ROLE" => property.Name.Equals("ATTENDEE", StringComparison.OrdinalIgnoreCase),
            "ALTREP" => AlternateTextProperties.Contains(property.Name),
            "SENT-BY" or "CN" or "DIR" or "EMAIL" => property.Name.Equals("ATTENDEE", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("ORGANIZER", StringComparison.OrdinalIgnoreCase),
            "FBTYPE" => property.Name.Equals("FREEBUSY", StringComparison.OrdinalIgnoreCase),
            "RELTYPE" => property.Name.Equals("RELATED-TO", StringComparison.OrdinalIgnoreCase),
            "ENCODING" => BinaryValueProperties.Contains(property.Name),
            "LANGUAGE" => LanguageProperties.Contains(property.Name),
            "FMTTYPE" => FormatTypeProperties.Contains(property.Name),
            "SCHEMA" => property.Name.Equals("STRUCTURED-DATA", StringComparison.OrdinalIgnoreCase),
            "LINKREL" => property.Name.Equals("LINK", StringComparison.OrdinalIgnoreCase),
            "DISPLAY" => property.Name.Equals("IMAGE", StringComparison.OrdinalIgnoreCase),
            "FEATURE" => property.Name.Equals("CONFERENCE", StringComparison.OrdinalIgnoreCase),
            "LABEL" => property.Name.ToUpperInvariant() is "CONFERENCE" or "LINK",
            "GAP" => property.Name.Equals("RELATED-TO", StringComparison.OrdinalIgnoreCase),
            "ORDER" => IsOrderApplicable(property),
            _ => true
        };

    private static bool IsOrderApplicable(CalendarContentProperty property)
    {
        if (property.Name.Equals("PARTICIPANT-TYPE", StringComparison.OrdinalIgnoreCase))
            return true;
        var owner = property.ComponentPath[^1].Name;
        return owner is not ("PARTICIPANT" or "VLOCATION" or "VRESOURCE")
            ? !IsExactSingleton(property)
            : !ExtensionSingletonProperties.Contains(property.Name);
    }

    private static bool HasValidGap(string value) => CalendarDurationArithmetic.TryParse(value, out _);

    private static bool IsTokenOrAbsoluteUri(string value) => IsToken(value)
        || Uri.TryCreate(value, UriKind.Absolute, out _);

    private static bool HasOneAbsoluteUri(CalendarParameter parameter) => parameter.Values.Count == 1
        && Uri.TryCreate(parameter.Values[0], UriKind.Absolute, out _);

    private static bool HasSingleValue(CalendarParameter parameter, params string[] allowed) =>
        parameter.Values.Count == 1
        && allowed.Contains(parameter.Values[0], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GrandfatheredLanguageTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "en-GB-oed", "i-ami", "i-bnn", "i-default", "i-enochian", "i-hak", "i-klingon", "i-lux",
        "i-mingo", "i-navajo", "i-pwn", "i-tao", "i-tay", "i-tsu", "sgn-BE-FR", "sgn-BE-NL",
        "sgn-CH-DE", "art-lojban", "cel-gaulish", "no-bok", "no-nyn", "zh-guoyu", "zh-hakka",
        "zh-min", "zh-min-nan", "zh-xiang"
    };

    internal static bool HasValidLanguageTag(string value)
    {
        if (GrandfatheredLanguageTags.Contains(value))
            return true;
        var subtags = value.Split('-', StringSplitOptions.None);
        if (subtags.Any(subtag => !IsLanguageSubtag(subtag, 1, 8)))
            return false;
        return subtags[0].Equals("x", StringComparison.OrdinalIgnoreCase)
            ? HasPrivateUse(subtags, 1)
            : HasValidNormalLanguageTag(subtags);
    }

    private static bool HasValidNormalLanguageTag(IReadOnlyList<string> subtags)
    {
        if (!IsPrimaryLanguage(subtags[0]))
            return false;
        var index = 1;
        index = SkipExtendedLanguages(subtags, index, subtags[0].Length <= 3 ? 3 : 0);
        if (index < subtags.Count && IsAsciiLetters(subtags[index], 4, 4))
            index++;
        if (index < subtags.Count && IsRegion(subtags[index]))
            index++;
        return HasValidLanguageSuffix(subtags, index);
    }

    private static int SkipExtendedLanguages(IReadOnlyList<string> subtags, int index, int remaining)
    {
        while (remaining > 0 && index < subtags.Count && IsAsciiLetters(subtags[index], 3, 3))
        {
            index++;
            remaining--;
        }
        return index;
    }

    private static bool HasValidLanguageSuffix(IReadOnlyList<string> subtags, int index)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Count && IsVariant(subtags[index]))
        {
            if (!variants.Add(subtags[index++]))
                return false;
        }
        return HasValidExtensionsAndPrivateUse(subtags, index);
    }

    private static bool HasValidExtensionsAndPrivateUse(IReadOnlyList<string> subtags, int index)
    {
        var singletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Count && IsExtensionSingleton(subtags[index]))
        {
            if (!singletons.Add(subtags[index++]) || !SkipExtensionValues(subtags, ref index))
                return false;
        }
        return index == subtags.Count
            || subtags[index].Equals("x", StringComparison.OrdinalIgnoreCase) && HasPrivateUse(subtags, index + 1);
    }

    private static bool SkipExtensionValues(IReadOnlyList<string> subtags, ref int index)
    {
        var start = index;
        while (index < subtags.Count && IsLanguageSubtag(subtags[index], 2, 8))
            index++;
        return index > start;
    }

    private static bool HasPrivateUse(IReadOnlyList<string> subtags, int index) => index < subtags.Count
        && subtags.Skip(index).All(subtag => IsLanguageSubtag(subtag, 1, 8));

    private static bool IsPrimaryLanguage(string value) => IsAsciiLetters(value, 2, 3)
        || IsAsciiLetters(value, 4, 4)
        || IsAsciiLetters(value, 5, 8);

    private static bool IsRegion(string value) => IsAsciiLetters(value, 2, 2)
        || value.Length == 3 && value.All(char.IsAsciiDigit);

    private static bool IsVariant(string value) => IsLanguageSubtag(value, 5, 8)
        || value.Length == 4 && char.IsAsciiDigit(value[0])
            && value.Skip(1).All(char.IsAsciiLetterOrDigit);

    private static bool IsExtensionSingleton(string value) => value.Length == 1
        && char.IsAsciiLetterOrDigit(value[0])
        && !value.Equals("x", StringComparison.OrdinalIgnoreCase);

    private static bool IsAsciiLetters(string value, int minimum, int maximum) => value.Length >= minimum
        && value.Length <= maximum
        && value.All(char.IsAsciiLetter);

    private static bool IsLanguageSubtag(string value, int minimum, int maximum) => value.Length >= minimum
        && value.Length <= maximum
        && value.All(char.IsAsciiLetterOrDigit);

    private static bool HasValidMediaType(string value)
    {
        var parts = value.Split('/', StringSplitOptions.None);
        return parts.Length == 2 && parts.All(HasValidMediaTypeName);
    }

    private static bool HasInvalidFormatType(CalendarContentProperty property, CalendarParameter parameter) =>
        parameter.Values.Count != 1
        || !HasValidMediaType(parameter.Values[0])
        || property.Name.Equals("IMAGE", StringComparison.OrdinalIgnoreCase)
        && !parameter.Values[0].StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidMediaTypeName(string value) => value.Length is >= 1 and <= 127
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+');

    private static bool HasValidRequestStatus(string value)
    {
        var parts = SplitUnescaped(value, ';');
        if (parts.Count is < 2 or > 3
            || !HasValidTextValue(parts[1], allowCommaSeparator: false)
            || parts.Count == 3 && !HasValidTextValue(parts[2], allowCommaSeparator: false))
            return false;
        var code = parts[0].Split('.', StringSplitOptions.None);
        return code.Length is 2 or 3
            && code.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static IReadOnlyList<string> SplitUnescaped(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != separator || IsEscaped(value, index))
                continue;
            parts.Add(value[start..index]);
            start = index + 1;
        }
        parts.Add(value[start..]);
        return parts;
    }

    private static bool IsEscaped(string value, int index)
    {
        var backslashes = 0;
        for (var previous = index - 1; previous >= 0 && value[previous] == '\\'; previous--)
            backslashes++;
        return backslashes % 2 == 1;
    }

    private static bool HasValidTextValue(string value, bool allowCommaSeparator)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                if (++index >= value.Length || value[index] is not ('\\' or ';' or ',' or 'N' or 'n'))
                    return false;
                continue;
            }
            if (!IsValidUnescapedTextCharacter(value[index], allowCommaSeparator))
                return false;
        }
        return true;
    }

    private static bool IsValidUnescapedTextCharacter(char character, bool allowCommaSeparator) =>
        character is '\t' or ' '
        || character >= '!' && character <= '~'
        && character != '\\'
        && character != ';'
        && (character != ',' || allowCommaSeparator)
        || character >= '\u0080';

    private static bool HasValidTemporalGrammar(CalendarContentProperty property)
    {
        try
        {
            _ = CalendarPatchValueSerializer.ParseTemporal(property);
            return !UtcTemporalProperties.Contains(property.Name)
                || property.ValueType == CalendarPropertyValueType.DateTime
                && property.RawEncodedValue.EndsWith('Z');
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasValidRecurrenceRule(CalendarContentProperty property)
    {
        try
        {
            _ = new RecurrencePattern(property.RawEncodedValue);
            var parts = ParseRecurrenceRuleParts(property.RawEncodedValue);
            return parts is not null && HasValidRecurrenceRuleCombinations(property, parts);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string>? ParseRecurrenceRuleParts(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in value.Split(';', StringSplitOptions.None))
        {
            var pair = segment.Split('=', 2, StringSplitOptions.None);
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0 || !result.TryAdd(pair[0], pair[1]))
                return null;
        }
        return result;
    }

    private static bool HasValidRecurrenceRuleCombinations(
        CalendarContentProperty property,
        IReadOnlyDictionary<string, string> parts)
    {
        if (!parts.TryGetValue("FREQ", out var frequency))
            return false;
        if (property.ComponentPath[^1].Name is "STANDARD" or "DAYLIGHT"
            && !frequency.Equals("YEARLY", StringComparison.OrdinalIgnoreCase))
            return false;
        return HasValidByMonthDayRule(frequency, parts)
            && HasValidByYearDayRule(frequency, parts)
            && HasValidByWeekNumberRule(frequency, parts)
            && HasValidBySetPositionRule(parts)
            && HasValidByDayRule(frequency, parts);
    }

    private static bool HasValidByMonthDayRule(string frequency, IReadOnlyDictionary<string, string> parts) =>
        !parts.ContainsKey("BYMONTHDAY") || !frequency.Equals("WEEKLY", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidByYearDayRule(string frequency, IReadOnlyDictionary<string, string> parts) =>
        !parts.ContainsKey("BYYEARDAY") || frequency.ToUpperInvariant() is not ("DAILY" or "WEEKLY" or "MONTHLY");

    private static bool HasValidByWeekNumberRule(string frequency, IReadOnlyDictionary<string, string> parts) =>
        !parts.ContainsKey("BYWEEKNO") || frequency.Equals("YEARLY", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidBySetPositionRule(IReadOnlyDictionary<string, string> parts) =>
        !parts.ContainsKey("BYSETPOS") || parts.Keys.Any(key => key.StartsWith("BY", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("BYSETPOS", StringComparison.OrdinalIgnoreCase));

    private static bool HasValidByDayRule(string frequency, IReadOnlyDictionary<string, string> parts)
    {
        if (!parts.TryGetValue("BYDAY", out var byDay)
            || !byDay.Split(',', StringSplitOptions.None).Any(value => value.Length > 2))
            return true;
        var normalizedFrequency = frequency.ToUpperInvariant();
        return normalizedFrequency is "MONTHLY" or "YEARLY"
            && !(normalizedFrequency == "YEARLY" && parts.ContainsKey("BYWEEKNO"));
    }

    private static bool HasValidTemporalCollection(CalendarContentProperty property)
    {
        var values = property.RawEncodedValue.Split(',', StringSplitOptions.None);
        if (values.Length == 0 || values.Any(string.IsNullOrEmpty))
            return false;
        return property.ValueType == CalendarPropertyValueType.Period
            ? property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase) && values.All(value => HasValidPeriod(property, value))
            : values.All(value => HasValidTemporalToken(property, value));
    }

    private static bool HasValidPeriod(CalendarContentProperty property, string value)
    {
        var parts = value.Split('/', StringSplitOptions.None);
        if (parts.Length != 2 || !HasValidTemporalToken(property with
            {
                ValueType = CalendarPropertyValueType.DateTime,
                RawEncodedValue = parts[0]
            }, parts[0]))
        {
            return false;
        }
        if (CalendarDurationArithmetic.TryParse(parts[1], out var duration))
            return duration.IsStrictlyPositive;
        return HasValidTemporalToken(property with
            {
                ValueType = CalendarPropertyValueType.DateTime,
                RawEncodedValue = parts[1]
            }, parts[1])
            && parts[0].EndsWith('Z') == parts[1].EndsWith('Z')
            && string.CompareOrdinal(parts[1], parts[0]) > 0;
    }

    private static bool HasValidTemporalToken(CalendarContentProperty property, string value)
    {
        try
        {
            _ = CalendarPatchValueSerializer.ParseTemporal(property with { RawEncodedValue = value });
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasValidTemporalRelationships(
        IReadOnlyList<EntityComponent> entities,
        CalendarContentDocument document)
    {
        var resolver = new CalendarTemporalResolver(
            document.Properties.Select(ToPublicProperty).ToArray(),
            document.Replay());
        return entities.All(entity => HasValidTemporalRelationship(entity, resolver)
            && HasValidDurationRelationship(entity)
            && HasMatchingRecurrenceTemporalFamily(entity)
            && HasRequiredExactEventStart(entity)
            && HasValidRecurrenceUntil(entity.Properties, isObservance: false));
    }

    private static bool HasRequiredExactEventStart(EntityComponent entity) =>
        entity.Kind != CalendarResourceProjectionKind.Event || GetRequiredValue(entity.Properties, "DTSTART") is not null;

    private static bool HasValidRecurrenceUntil(
        IReadOnlyList<CalendarContentProperty> properties,
        bool isObservance)
    {
        var recurrence = GetProperty(properties, "RRULE");
        if (recurrence is null)
            return true;
        var parts = ParseRecurrenceRuleParts(recurrence.RawEncodedValue);
        if (parts is null || !parts.TryGetValue("UNTIL", out var until))
            return true;
        var start = GetProperty(properties, "DTSTART");
        if (start is null)
            return false;
        if (start.ValueType == CalendarPropertyValueType.Date)
            return !isObservance && IsDateToken(until);
        var requiresUtc = isObservance
            || start.RawEncodedValue.EndsWith('Z')
            || start.Parameters.Any(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase));
        return requiresUtc ? IsUtcDateTimeToken(until) : IsLocalDateTimeToken(until);
    }

    private static bool IsDateToken(string value) => value.Length == 8 && value.All(char.IsAsciiDigit);

    private static bool IsUtcDateTimeToken(string value) => value.Length == 16
        && value[^1] == 'Z'
        && IsLocalDateTimeToken(value[..^1]);

    private static bool IsLocalDateTimeToken(string value) => value.Length == 15
        && value[8] == 'T'
        && value.Where((_, index) => index != 8).All(char.IsAsciiDigit);

    private static bool HasValidTemporalRelationship(EntityComponent entity, CalendarTemporalResolver resolver)
    {
        var start = GetProperty(entity.Properties, "DTSTART");
        var end = GetProperty(entity.Properties,
            entity.Kind == CalendarResourceProjectionKind.Event ? "DTEND" : "DUE");
        if (start is null || end is null)
            return true;
        if (start.ValueType != end.ValueType)
            return false;
        if (start.ValueType == CalendarPropertyValueType.Date
            || GetTemporalFamily(start) == "floating-date-time" && GetTemporalFamily(end) == "floating-date-time")
        {
            return string.CompareOrdinal(end.RawEncodedValue, start.RawEncodedValue) > 0;
        }
        var resolvedStart = resolver.Resolve(ToPublicProperty(start)).Value;
        var resolvedEnd = resolver.Resolve(ToPublicProperty(end)).Value;
        return resolvedStart is not null && resolvedEnd > resolvedStart;
    }

    private static bool HasValidDurationRelationship(EntityComponent entity)
    {
        var durationProperty = GetProperty(entity.Properties, "DURATION");
        if (durationProperty is null)
            return true;
        var start = GetProperty(entity.Properties, "DTSTART");
        if (start is null)
            return false;
        return start.ValueType != CalendarPropertyValueType.Date
            || CalendarDurationArithmetic.TryParse(durationProperty.RawEncodedValue, out var duration)
            && duration.Accurate == TimeSpan.Zero;
    }

    private static bool HasMatchingRecurrenceTemporalFamily(EntityComponent entity)
    {
        var start = GetProperty(entity.Properties, "DTSTART");
        if (start is null)
            return !entity.Properties.Any(property => property.Name.ToUpperInvariant() is "RDATE" or "EXDATE");
        var family = GetTemporalFamily(start);
        return entity.Properties.Where(property => property.Name.ToUpperInvariant() is "RDATE" or "EXDATE")
            .All(property => property.ValueType == CalendarPropertyValueType.Period
                ? start.ValueType == CalendarPropertyValueType.DateTime
                : GetTemporalFamily(property) == family);
    }

    private static bool HasValidEntityDuration(CalendarContentProperty property) =>
        CalendarDurationArithmetic.TryParse(property.RawEncodedValue, out var duration)
        && duration.IsStrictlyPositive;

    private static bool HasValidTrigger(CalendarContentProperty property) => property.ValueType switch
    {
        CalendarPropertyValueType.Duration => CalendarDurationArithmetic.TryParse(property.RawEncodedValue, out _),
        CalendarPropertyValueType.DateTime => HasValidTemporalGrammar(property)
            && property.RawEncodedValue.EndsWith('Z'),
        _ => false
    };

    private static bool HasValidGeo(string value)
    {
        var parts = value.Split(';');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            && latitude is >= -90 and <= 90
            && longitude is >= -180 and <= 180;
    }

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
        CalendarContentDocument source,
        IcalCalendar? calendar,
        CalendarResourceProjectionKind kind,
        int entityCount)
    {
        try
        {
            if (calendar is null)
                return false;
            var entityCountsMatch = kind == CalendarResourceProjectionKind.Event
                ? calendar.Events.Count == entityCount && calendar.Todos.Count == 0
                : calendar.Todos.Count == entityCount && calendar.Events.Count == 0;
            if (!entityCountsMatch)
                return false;
            var roundTrip = new CalendarSerializer().SerializeToString(calendar);
            return roundTrip is not null && HasAllRegisteredOccurrences(
                source.ProjectionValidationProperties(),
                CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(roundTrip)));
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.Runtime.Serialization.SerializationException)
        {
            return false;
        }
    }

    internal static IcalCalendar? LoadTypedCalendar(CalendarContentDocument document)
    {
        try
        {
            return IcalCalendar.Load(new UTF8Encoding(false, true).GetString(
                document.ReplayForProjectionValidation()));
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.Runtime.Serialization.SerializationException)
        {
            return null;
        }
    }

    private static bool HasAllRegisteredOccurrences(
        IReadOnlyList<CalendarContentProperty> source,
        CalendarContentDocument roundTrip)
    {
        var sourceCounts = GetRegisteredOccurrenceCounts(source);
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

    internal static CalendarProjectionResult InvalidCalendarData() => Opaque([], "invalid_calendar_data");

    private sealed record EntityComponent(
        CalendarResourceProjectionKind Kind,
        string? Uid,
        string? RecurrenceIdentity,
        bool IsRange,
        IReadOnlyList<CalendarContentProperty> Properties);
}
