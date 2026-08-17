using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarPatchOccurrenceSerializer
{
    private static readonly CalendarTemporalValue EventStart = new(
        CalendarTemporalKind.UtcDateTime,
        "2000-01-01T00:00:00Z");

    public static string Serialize(CalendarCollectionField field, object value, CalendarEntityKind kind)
    {
        var data = StructuredData(field, value);
        var bytes = kind == CalendarEntityKind.Event
            ? CalendarEntityCreateSerializer.SerializeEvent(
                "patch-occurrence",
                new CalendarEventCreateFields(Start: EventStart, StructuredData: data),
                DateTimeOffset.UnixEpoch)
            : CalendarEntityCreateSerializer.SerializeTodo(
                "patch-occurrence",
                new CalendarTodoCreateFields(StructuredData: data),
                DateTimeOffset.UnixEpoch);
        var document = CalendarContentDocument.Parse(bytes);
        var master = document.GetMasterComponent(kind);
        var componentName = ComponentName(field);
        if (componentName is not null)
            return document.GetDirectComponentOccurrences(master.Path, componentName).Single().OriginalSlice;
        return document.GetDirectPropertyOccurrences(master.Path, PropertyName(field)).Single().OriginalSlice;
    }

    public static IReadOnlyList<CalendarContentOccurrence> Current(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarCollectionField field)
    {
        var componentName = ComponentName(field);
        return componentName is null
            ? document.GetDirectPropertyOccurrences(master.Path, PropertyName(field))
            : document.GetDirectComponentOccurrences(master.Path, componentName);
    }

    private static CalendarStructuredData StructuredData(CalendarCollectionField field, object value) => field switch
    {
        CalendarCollectionField.Attendees => new(Attendees: [(CalendarAttendee)value]),
        CalendarCollectionField.Participants => new(Participants: [(CalendarParticipant)value]),
        CalendarCollectionField.Contacts => new(Contacts: [(CalendarTextValue)value]),
        CalendarCollectionField.Resources => new(Resources: [(CalendarTextValue)value]),
        CalendarCollectionField.RelatedTo => new(RelatedTo: [(CalendarRelation)value]),
        CalendarCollectionField.RequestStatuses => new(RequestStatuses: [(CalendarRequestStatus)value]),
        CalendarCollectionField.Alarms => new(Alarms: [(CalendarAlarm)value]),
        CalendarCollectionField.Attachments => new(Attachments: [(CalendarNamedUri)value]),
        CalendarCollectionField.Comments => new(Comments: [(CalendarTextValue)value]),
        CalendarCollectionField.StyledDescriptions => new(StyledDescriptions: [(CalendarTextValue)value]),
        CalendarCollectionField.Images => new(Images: [(CalendarNamedUri)value]),
        CalendarCollectionField.Conferences => new(Conferences: [(CalendarNamedUri)value]),
        CalendarCollectionField.Links => new(Links: [(CalendarNamedUri)value]),
        CalendarCollectionField.Concepts => new(Concepts: [(CalendarUriValue)value]),
        CalendarCollectionField.StructuredDataUris => new(StructuredDataUris: [(CalendarUriValue)value]),
        CalendarCollectionField.LocationUris => new(LocationUris: [(CalendarNamedUri)value]),
        CalendarCollectionField.ResourceUris => new(ResourceUris: [(CalendarNamedUri)value]),
        _ => throw new ArgumentException("Categories use the text-list editor.", nameof(field))
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
        _ => throw new ArgumentException("The collection field is component-valued.", nameof(field))
    };
}
