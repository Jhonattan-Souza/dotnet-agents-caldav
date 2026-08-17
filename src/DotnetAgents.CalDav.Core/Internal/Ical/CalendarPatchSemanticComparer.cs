using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarPatchSemanticComparer
{
    private static readonly IReadOnlySet<string> ParticipantProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PARTICIPANT-TYPE", "UID", "CALENDAR-ADDRESS", "CREATED", "DESCRIPTION", "DTSTAMP", "GEO",
        "LAST-MODIFIED", "PRIORITY", "SEQUENCE", "STATUS", "SUMMARY", "URL", "ATTACH", "CATEGORIES",
        "COMMENT", "CONTACT", "LOCATION", "REQUEST-STATUS", "RELATED-TO", "RESOURCES", "STYLED-DESCRIPTION",
        "STRUCTURED-DATA", "NAME"
    };
    private static readonly IReadOnlySet<string> AlarmProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "TRIGGER", "SUMMARY", "DESCRIPTION", "ATTENDEE", "ATTACH", "REPEAT", "DURATION"
    };
    private static readonly IReadOnlySet<string> NamedComponentProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UID", "NAME"
    };

    public static bool Matches(
        CalendarCollectionField field,
        CalendarContentOccurrence current,
        object requestedValue,
        CalendarEntityKind kind)
    {
        var requested = CalendarPatchOccurrenceSerializer.Serialize(field, requestedValue, kind);
        return ComponentName(field) is { } componentName
            ? ComponentMatches(current.OriginalSlice, requested, componentName, Properties(field), kind)
            : PropertyMatches(current.OriginalSlice, requested, PropertyName(field), kind);
    }

    public static bool ValuesEquivalent(
        CalendarCollectionField field,
        object left,
        object right,
        CalendarEntityKind kind)
    {
        var serialized = CalendarPatchOccurrenceSerializer.Serialize(field, left, kind);
        return Matches(field, new CalendarContentOccurrence(0, serialized.Length, serialized), right, kind);
    }

    public static bool IsDerived(
        CalendarCollectionField field,
        CalendarContentOccurrence occurrence,
        CalendarEntityKind kind)
    {
        var document = ParseOccurrence(occurrence.OriginalSlice, kind);
        var master = document.GetMasterComponent(kind);
        var componentName = ComponentName(field);
        var properties = componentName is null
            ? document.Properties.Where(property => property.ComponentPath.SequenceEqual(master.Path)
                && property.Name.Equals(PropertyName(field), StringComparison.OrdinalIgnoreCase))
            : document.Properties.Where(property => property.ComponentPath.Take(master.Path.Count).SequenceEqual(master.Path)
                && property.ComponentPath.Count > master.Path.Count
                && property.ComponentPath[master.Path.Count].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
        return properties.Any(IsDerivedProperty);
    }

    private static bool IsDerivedProperty(CalendarContentProperty property) => property.Parameters.Any(parameter =>
        parameter.Name.Equals("DERIVED", StringComparison.OrdinalIgnoreCase)
        && parameter.Values.Any(value => value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)));

    private static bool PropertyMatches(
        string current,
        string requested,
        string propertyName,
        CalendarEntityKind kind)
    {
        var currentDocument = ParseOccurrence(current, kind);
        var requestedDocument = ParseOccurrence(requested, kind);
        var currentMaster = currentDocument.GetMasterComponent(kind);
        var requestedMaster = requestedDocument.GetMasterComponent(kind);
        var currentProperty = currentDocument.Properties.Single(property =>
            property.ComponentPath.SequenceEqual(currentMaster.Path)
            && property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        var requestedProperty = requestedDocument.Properties.Single(property =>
            property.ComponentPath.SequenceEqual(requestedMaster.Path)
            && property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        return CalendarEntityCreateFidelity.ArePropertiesEquivalent(currentProperty, requestedProperty);
    }

    private static bool ComponentMatches(
        string current,
        string requested,
        string componentName,
        IReadOnlySet<string> propertyNames,
        CalendarEntityKind kind)
    {
        var currentDocument = ParseOccurrence(current, kind);
        var requestedDocument = ParseOccurrence(requested, kind);
        var currentRoot = FindComponent(currentDocument, componentName, kind);
        var requestedRoot = FindComponent(requestedDocument, componentName, kind);
        return CalendarEntityCreateFidelity.CanonicalizeSelectedProperties(currentDocument, currentRoot.Path, propertyNames)
            .SequenceEqual(
                CalendarEntityCreateFidelity.CanonicalizeSelectedProperties(
                    requestedDocument,
                    requestedRoot.Path,
                    propertyNames),
                StringComparer.Ordinal);
    }

    private static CalendarContentDocument ParseOccurrence(string occurrence, CalendarEntityKind kind)
    {
        var component = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var requiredStart = kind == CalendarEntityKind.Event ? "DTSTART:20000101T000000Z\r\n" : string.Empty;
        var content = "BEGIN:VCALENDAR\r\nBEGIN:" + component + "\r\nUID:patch-occurrence\r\n"
            + requiredStart + occurrence + "END:" + component + "\r\nEND:VCALENDAR\r\n";
        return CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));
    }

    private static CalendarContentComponent FindComponent(
        CalendarContentDocument document,
        string componentName,
        CalendarEntityKind kind)
    {
        var master = document.GetMasterComponent(kind);
        return document.Components.Single(component => component.Path.Count == master.Path.Count + 1
            && component.Path.Take(master.Path.Count).SequenceEqual(master.Path)
            && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<string> Properties(CalendarCollectionField field) => field switch
    {
        CalendarCollectionField.Participants => ParticipantProperties,
        CalendarCollectionField.Alarms => AlarmProperties,
        CalendarCollectionField.LocationUris or CalendarCollectionField.ResourceUris => NamedComponentProperties,
        _ => throw new ArgumentException("The collection is not component-valued.", nameof(field))
    };

    private static string? ComponentName(CalendarCollectionField field) => field switch
    {
        CalendarCollectionField.Participants => "PARTICIPANT",
        CalendarCollectionField.Alarms => "VALARM",
        CalendarCollectionField.LocationUris => "VLOCATION",
        CalendarCollectionField.ResourceUris => "VRESOURCE",
        _ => null
    };

    private static string PropertyName(CalendarCollectionField field) => field switch
    {
        CalendarCollectionField.Attendees => "ATTENDEE",
        CalendarCollectionField.Contacts => "CONTACT",
        CalendarCollectionField.Resources => "RESOURCES",
        CalendarCollectionField.RelatedTo => "RELATED-TO",
        CalendarCollectionField.RequestStatuses => "REQUEST-STATUS",
        CalendarCollectionField.Attachments => "ATTACH",
        CalendarCollectionField.Comments => "COMMENT",
        CalendarCollectionField.StyledDescriptions => "STYLED-DESCRIPTION",
        CalendarCollectionField.Images => "IMAGE",
        CalendarCollectionField.Conferences => "CONFERENCE",
        CalendarCollectionField.Links => "LINK",
        CalendarCollectionField.Concepts => "CONCEPT",
        CalendarCollectionField.StructuredDataUris => "STRUCTURED-DATA",
        _ => throw new ArgumentException("The collection is not property-valued.", nameof(field))
    };
}
