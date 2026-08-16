using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarEntityCreateSerializer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] SerializeEvent(
        string uid,
        CalendarEventCreateFields fields,
        DateTimeOffset now)
    {
        CalendarEntityCreateValidator.ValidateEvent(uid, fields);
        var timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var content = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\n")
            .Append("VERSION:2.0\r\n")
            .Append("PRODID:-//dotnet-agents-caldav//EN\r\n")
            .Append("BEGIN:VEVENT\r\n")
            .Append("UID:").Append(EscapeText(uid)).Append("\r\n")
            .Append("DTSTAMP:").Append(timestamp).Append("\r\n")
            .Append("CREATED:").Append(timestamp).Append("\r\n")
            .Append("LAST-MODIFIED:").Append(timestamp).Append("\r\n");
        AppendText(content, "SUMMARY", fields.Summary);
        AppendText(content, "DESCRIPTION", fields.Description);
        AppendTemporal(content, "DTSTART", fields.Start);
        AppendTemporal(content, "DTEND", fields.End);
        AppendDuration(content, fields.Duration);
        AppendText(content, "LOCATION", fields.Location);
        if (fields.Geo is not null)
            content.Append("GEO:").Append(fields.Geo.Latitude.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(fields.Geo.Longitude.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        AppendToken(content, "STATUS", fields.Status);
        AppendToken(content, "TRANSP", fields.Transparency);
        AppendToken(content, "CLASS", fields.Classification);
        AppendPriority(content, fields.Priority);
        AppendCategories(content, fields.Categories);
        AppendUri(content, "URL", fields.Url);
        AppendStructuredData(content, fields.StructuredData);
        AppendCalendarComponents(content, fields.StructuredData);
        content.Append("END:VEVENT\r\n")
            .Append("END:VCALENDAR\r\n");
        return Utf8.GetBytes(content.ToString());
    }

    public static byte[] SerializeTodo(
        string uid,
        CalendarTodoCreateFields fields,
        DateTimeOffset now)
    {
        CalendarEntityCreateValidator.ValidateTodo(uid, fields);
        var timestamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var content = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\n")
            .Append("VERSION:2.0\r\n")
            .Append("PRODID:-//dotnet-agents-caldav//EN\r\n")
            .Append("BEGIN:VTODO\r\n")
            .Append("UID:").Append(EscapeText(uid)).Append("\r\n")
            .Append("DTSTAMP:").Append(timestamp).Append("\r\n")
            .Append("CREATED:").Append(timestamp).Append("\r\n")
            .Append("LAST-MODIFIED:").Append(timestamp).Append("\r\n");
        AppendText(content, "SUMMARY", fields.Summary);
        AppendText(content, "DESCRIPTION", fields.Description);
        AppendTemporal(content, "DTSTART", fields.Start);
        AppendTemporal(content, "DUE", fields.Due);
        AppendDuration(content, fields.Duration);
        AppendToken(content, "STATUS", fields.Status);
        AppendPriority(content, fields.Priority);
        AppendCategories(content, fields.Categories);
        AppendStructuredData(content, fields.StructuredData);
        AppendCalendarComponents(content, fields.StructuredData);
        content.Append("END:VTODO\r\n")
            .Append("END:VCALENDAR\r\n");
        return Utf8.GetBytes(content.ToString());
    }

    private static void AppendStructuredData(StringBuilder content, CalendarStructuredData? data)
    {
        if (data is null)
            return;
        if (data.Organizer is not null)
            AppendNamedUri(content, "ORGANIZER", data.Organizer, "CN");
        AppendAttendees(content, data.Attendees);
        AppendTextValues(content, "CONTACT", data.Contacts);
        AppendTextValues(content, "RESOURCES", data.Resources);
        AppendRelations(content, data.RelatedTo);
        AppendRequestStatuses(content, data.RequestStatuses);
        AppendNamedUris(content, "ATTACH", data.Attachments);
        AppendTextValues(content, "COMMENT", data.Comments);
        AppendTextValues(content, "STYLED-DESCRIPTION", data.StyledDescriptions);
        AppendNamedUris(content, "IMAGE", data.Images);
        AppendNamedUris(content, "CONFERENCE", data.Conferences);
        AppendNamedUris(content, "LINK", data.Links);
        AppendUriValues(content, "CONCEPT", data.Concepts, includeValueUri: false);
        AppendUriValues(content, "STRUCTURED-DATA", data.StructuredDataUris, includeValueUri: true);
        AppendAlarms(content, data.Alarms);
    }

    private static void AppendCalendarComponents(StringBuilder content, CalendarStructuredData? data)
    {
        AppendParticipantComponents(content, data?.Participants);
        AppendNamedComponents(content, "VLOCATION", data?.LocationUris);
        AppendNamedComponents(content, "VRESOURCE", data?.ResourceUris);
    }

    private static void AppendParticipantComponents(
        StringBuilder content,
        IReadOnlyList<CalendarParticipant>? participants)
    {
        foreach (var participant in participants ?? [])
        {
            content.Append("BEGIN:PARTICIPANT\r\n");
            AppendTokenValue(content, "PARTICIPANT-TYPE", participant.ParticipantType);
            AppendTextValue(content, "UID", participant.Uid);
            if (participant.CalendarAddress is not null)
                AppendUriValue(content, "CALENDAR-ADDRESS", participant.CalendarAddress, includeValueUri: false);
            AppendTemporalProperty(content, "CREATED", participant.Created);
            AppendTextValue(content, "DESCRIPTION", participant.Description);
            AppendTemporalProperty(content, "DTSTAMP", participant.Timestamp);
            if (participant.Geo is not null)
                AppendGeoProperty(content, participant.Geo);
            AppendTemporalProperty(content, "LAST-MODIFIED", participant.LastModified);
            AppendIntegerProperty(content, "PRIORITY", participant.Priority);
            AppendIntegerProperty(content, "SEQUENCE", participant.Sequence);
            AppendTokenValue(content, "STATUS", participant.Status);
            AppendTextValue(content, "SUMMARY", participant.Summary);
            if (participant.Url is not null)
                AppendUriValue(content, "URL", participant.Url, includeValueUri: false);
            AppendNamedUris(content, "ATTACH", participant.Attachments);
            AppendTextListProperty(content, "CATEGORIES", participant.Categories);
            AppendTextValues(content, "COMMENT", participant.Comments);
            AppendTextValues(content, "CONTACT", participant.Contacts);
            AppendTextValues(content, "LOCATION", participant.Locations);
            AppendRequestStatuses(content, participant.RequestStatuses);
            AppendRelations(content, participant.RelatedTo);
            AppendTextValues(content, "RESOURCES", participant.Resources);
            AppendTextValues(content, "STYLED-DESCRIPTION", participant.StyledDescriptions);
            AppendUriValues(content, "STRUCTURED-DATA", participant.StructuredDataUris, includeValueUri: true);
            AppendNamedComponents(content, "VLOCATION", participant.LocationUris);
            AppendNamedComponents(content, "VRESOURCE", participant.ResourceUris);
            content.Append("END:PARTICIPANT\r\n");
        }
    }

    private static void AppendNamedComponents(
        StringBuilder content,
        string componentName,
        IReadOnlyList<CalendarNamedUri>? values)
    {
        foreach (var value in values ?? [])
        {
            ValidateAbsoluteUri(value.Uri);
            content.Append("BEGIN:").Append(componentName).Append("\r\nUID");
            AppendParameters(content, value.Parameters);
            content.Append(':').Append(EscapeText(value.Uri)).Append("\r\n");
            AppendText(content, "NAME", value.Label);
            content.Append("END:").Append(componentName).Append("\r\n");
        }
    }

    private static void AppendAttendees(StringBuilder content, IReadOnlyList<CalendarAttendee>? participants)
    {
        foreach (var participant in participants ?? [])
        {
            ValidateAbsoluteUri(participant.Uri);
            content.Append("ATTENDEE");
            AppendParameter(content, "CN", participant.CommonName);
            AppendParameter(content, "ROLE", MapRole(participant.Role));
            AppendParameter(content, "PARTSTAT", participant.ParticipationStatus);
            AppendParameter(content, "CUTYPE", participant.CalendarUserType);
            AppendParameter(content, "RSVP", participant.Rsvp is null ? null : participant.Rsvp.Value ? "TRUE" : "FALSE");
            AppendUriParameter(content, "DELEGATED-TO", participant.DelegatedTo);
            AppendUriParameter(content, "DELEGATED-FROM", participant.DelegatedFrom);
            AppendUriParameter(content, "SENT-BY", participant.SentBy is null ? null : [participant.SentBy]);
            AppendUriParameter(content, "DIR", participant.Directory is null ? null : [participant.Directory]);
            AppendParameters(content, participant.Parameters);
            content.Append(':').Append(participant.Uri).Append("\r\n");
        }
    }

    private static void AppendNamedUris(
        StringBuilder content,
        string name,
        IReadOnlyList<CalendarNamedUri>? values)
    {
        foreach (var value in values ?? [])
            AppendNamedUri(content, name, value, "LABEL");
    }

    private static void AppendUriValues(
        StringBuilder content,
        string name,
        IReadOnlyList<CalendarUriValue>? values,
        bool includeValueUri)
    {
        foreach (var value in values ?? [])
            AppendUriValue(content, name, value, includeValueUri);
    }

    private static void AppendUriValue(
        StringBuilder content,
        string name,
        CalendarUriValue value,
        bool includeValueUri)
    {
        ValidateAbsoluteUri(value.Uri);
        content.Append(name);
        if (includeValueUri && !value.Parameters.Any(parameter =>
                parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)))
        {
            content.Append(";VALUE=URI");
        }
        AppendParameters(content, value.Parameters);
        content.Append(':').Append(value.Uri).Append("\r\n");
    }

    private static void AppendNamedUri(
        StringBuilder content,
        string name,
        CalendarNamedUri value,
        string labelParameter)
    {
        ValidateAbsoluteUri(value.Uri);
        content.Append(name);
        AppendParameter(content, labelParameter, value.Label);
        AppendParameters(content, value.Parameters);
        content.Append(':').Append(value.Uri).Append("\r\n");
    }

    private static void AppendTextValues(
        StringBuilder content,
        string name,
        IReadOnlyList<CalendarTextValue>? values)
    {
        foreach (var value in values ?? [])
        {
            content.Append(name);
            AppendParameters(content, value.Parameters);
            content.Append(':').Append(EscapeText(value.Value)).Append("\r\n");
        }
    }

    private static void AppendTextValue(StringBuilder content, string name, CalendarTextValue? value)
    {
        if (value is null)
            return;
        content.Append(name);
        AppendParameters(content, value.Parameters);
        content.Append(':').Append(EscapeText(value.Value)).Append("\r\n");
    }

    private static void AppendTokenValue(StringBuilder content, string name, CalendarTextValue? value)
    {
        if (value is null)
            return;
        if (!IsToken(value.Value))
            throw new ArgumentException("An iCalendar token is invalid.");
        content.Append(name);
        AppendParameters(content, value.Parameters);
        content.Append(':').Append(value.Value.ToUpperInvariant()).Append("\r\n");
    }

    private static void AppendTemporalProperty(
        StringBuilder content,
        string name,
        CalendarTemporalProperty? property)
    {
        if (property is not null)
            AppendTemporal(content, name, property.Value, property.Parameters);
    }

    private static void AppendGeoProperty(StringBuilder content, CalendarGeoProperty property)
    {
        content.Append("GEO");
        AppendParameters(content, property.Parameters);
        content.Append(':').Append(property.Value.Latitude.ToString(CultureInfo.InvariantCulture)).Append(';')
            .Append(property.Value.Longitude.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
    }

    private static void AppendIntegerProperty(
        StringBuilder content,
        string name,
        CalendarIntegerProperty? property)
    {
        if (property is null)
            return;
        content.Append(name);
        AppendParameters(content, property.Parameters);
        content.Append(':').Append(property.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
    }

    private static void AppendTextListProperty(
        StringBuilder content,
        string name,
        CalendarTextListProperty? property)
    {
        if (property is null)
            return;
        content.Append(name);
        AppendParameters(content, property.Parameters);
        content.Append(':').AppendJoin(',', property.Value.Select(EscapeText)).Append("\r\n");
    }

    private static void AppendDurationProperty(StringBuilder content, CalendarDurationProperty? property)
    {
        if (property is null)
            return;
        if (!CalendarDurationArithmetic.TryParse(property.Value, out var parsed) || !parsed.IsStrictlyPositive)
            throw new ArgumentException("A strictly positive duration is required.");
        content.Append("DURATION");
        AppendParameters(content, property.Parameters);
        content.Append(':').Append(property.Value).Append("\r\n");
    }

    private static void AppendRelations(StringBuilder content, IReadOnlyList<CalendarRelation>? relations)
    {
        foreach (var relation in relations ?? [])
        {
            if (string.IsNullOrEmpty(relation.Value))
                throw new ArgumentException("A relation value is required.");
            content.Append("RELATED-TO");
            AppendParameter(content, "RELTYPE", relation.RelationType);
            AppendParameters(content, relation.Parameters ?? []);
            content.Append(':').Append(EscapeText(relation.Value)).Append("\r\n");
        }
    }

    private static void AppendRequestStatuses(
        StringBuilder content,
        IReadOnlyList<CalendarRequestStatus>? statuses)
    {
        foreach (var status in statuses ?? [])
        {
            if (!IsRequestStatusCode(status.Code))
                throw new ArgumentException("A request status code must contain two numeric components.");
            content.Append("REQUEST-STATUS");
            AppendParameters(content, status.Parameters ?? []);
            content.Append(':').Append(status.Code).Append(';').Append(EscapeText(status.Description));
            if (status.ExceptionData is not null)
                content.Append(';').Append(EscapeText(status.ExceptionData));
            content.Append("\r\n");
        }
    }

    private static void AppendAlarms(StringBuilder content, IReadOnlyList<CalendarAlarm>? alarms)
    {
        foreach (var alarm in alarms ?? [])
        {
            var action = alarm.Action.Value.ToUpperInvariant();
            if (action is not ("DISPLAY" or "AUDIO" or "EMAIL") || string.IsNullOrWhiteSpace(alarm.Trigger.Value))
                throw new ArgumentException("The alarm action or trigger is invalid.");
            if (action == "DISPLAY" && alarm.Description is null)
                throw new ArgumentException("A display alarm requires a description.");
            if ((alarm.Repeat is null) != (alarm.Duration is null) || alarm.Repeat?.Value <= 0)
                throw new ArgumentException("Alarm repeat and duration must be supplied together.");
            content.Append("BEGIN:VALARM\r\nACTION");
            AppendParameters(content, alarm.Action.Parameters);
            content.Append(':').Append(action).Append("\r\nTRIGGER");
            AppendParameters(content, alarm.Trigger.Parameters);
            content.Append(':').Append(alarm.Trigger.Value).Append("\r\n");
            AppendTextValue(content, "SUMMARY", alarm.Summary);
            AppendTextValue(content, "DESCRIPTION", alarm.Description);
            AppendAttendees(content, alarm.Attendees);
            AppendNamedUris(content, "ATTACH", alarm.Attachments);
            AppendIntegerProperty(content, "REPEAT", alarm.Repeat);
            AppendDurationProperty(content, alarm.Duration);
            content.Append("END:VALARM\r\n");
        }
    }

    private static void AppendParameters(StringBuilder content, IReadOnlyList<CalendarParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var parameter in parameters)
        {
            if (!IsToken(parameter.Name) || parameter.Values is null || parameter.Values.Count == 0)
                throw new ArgumentException("An iCalendar parameter must have a token name and at least one value.");
            content.Append(';').Append(parameter.Name.ToUpperInvariant()).Append('=');
            for (var index = 0; index < parameter.Values.Count; index++)
            {
                if (index > 0)
                    content.Append(',');
                content.Append(EncodeParameterValue(parameter.Values[index]));
            }
        }
    }

    private static void AppendParameter(StringBuilder content, string name, string? value)
    {
        if (value is not null)
            content.Append(';').Append(name).Append('=').Append(EncodeParameterValue(value));
    }

    private static void AppendUriParameter(StringBuilder content, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
            return;
        foreach (var value in values)
            ValidateAbsoluteUri(value);
        content.Append(';').Append(name).Append('=')
            .AppendJoin(',', values.Select(value => EncodeParameterValue(value, forceQuote: true)));
    }

    private static string? MapRole(string? role) => role?.ToLowerInvariant() switch
    {
        null => null,
        "chair" => "CHAIR",
        "required" => "REQ-PARTICIPANT",
        "optional" => "OPT-PARTICIPANT",
        "non-participant" => "NON-PARTICIPANT",
        var extension when extension.StartsWith("x-", StringComparison.Ordinal) => extension.ToUpperInvariant(),
        _ => throw new ArgumentException("The participant role is invalid.")
    };

    private static string EncodeParameterValue(string value, bool forceQuote = false)
    {
        CalendarEntityCreateValidator.ValidateParameterValue(value);
        var encoded = value.Replace("^", "^^", StringComparison.Ordinal)
            .Replace("\r\n", "^n", StringComparison.Ordinal)
            .Replace("\n", "^n", StringComparison.Ordinal)
            .Replace("\"", "^'", StringComparison.Ordinal);
        return forceQuote || encoded.IndexOfAny([':', ';', ',']) >= 0 ? $"\"{encoded}\"" : encoded;
    }

    private static bool IsToken(string value) => value.Length > 0
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsRequestStatusCode(string value)
    {
        var separator = value.IndexOf('.');
        return separator > 0
            && separator < value.Length - 1
            && value.AsSpan(0, separator).IndexOfAnyExceptInRange('0', '9') < 0
            && value.AsSpan(separator + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static void ValidateAbsoluteUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("An absolute URI is required.");
    }

    private static void AppendToken(StringBuilder content, string name, string? value)
    {
        if (value is null)
            return;
        if (!IsToken(value))
            throw new ArgumentException("An iCalendar token is invalid.");
        content.Append(name).Append(':').Append(value.ToUpperInvariant()).Append("\r\n");
    }

    private static void AppendPriority(StringBuilder content, int? priority)
    {
        if (priority is null)
            return;
        if (priority is < 0 or > 9)
            throw new ArgumentException("Priority must be between zero and nine.");
        content.Append("PRIORITY:").Append(priority.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
    }

    private static void AppendCategories(StringBuilder content, IReadOnlyList<string>? categories)
    {
        if (categories is not null)
            content.Append("CATEGORIES:").AppendJoin(',', categories.Select(EscapeText)).Append("\r\n");
    }

    private static void AppendUri(StringBuilder content, string name, string? value)
    {
        if (value is null)
            return;
        ValidateAbsoluteUri(value);
        content.Append(name).Append(':').Append(value).Append("\r\n");
    }

    private static void AppendDuration(StringBuilder content, string? duration)
    {
        if (duration is null)
            return;
        if (!CalendarDurationArithmetic.TryParse(duration, out var parsed) || !parsed.IsStrictlyPositive)
            throw new ArgumentException("A strictly positive duration is required.");
        content.Append("DURATION:").Append(duration).Append("\r\n");
    }

    private static void AppendText(StringBuilder content, string name, string? value)
    {
        if (value is not null)
            content.Append(name).Append(':').Append(EscapeText(value)).Append("\r\n");
    }

    private static void AppendTemporal(
        StringBuilder content,
        string name,
        CalendarTemporalValue? value,
        IReadOnlyList<CalendarParameter>? parameters = null)
    {
        if (value is null)
            return;
        var encoded = value.Kind switch
        {
            CalendarTemporalKind.Date => value.Value.Replace("-", string.Empty, StringComparison.Ordinal),
            CalendarTemporalKind.UtcDateTime => CompactDateTime(value.Value[..^1]) + "Z",
            CalendarTemporalKind.FloatingDateTime => CompactDateTime(value.Value),
            CalendarTemporalKind.ZonedDateTime => CompactDateTime(value.Value),
            _ => throw new InvalidOperationException("Unsupported temporal value.")
        };
        content.Append(name);
        if (value.Kind == CalendarTemporalKind.Date && !HasParameter(parameters, "VALUE"))
            content.Append(";VALUE=DATE");
        else if (value.Kind == CalendarTemporalKind.ZonedDateTime)
            content.Append(";TZID=").Append(value.TimeZoneId);
        AppendParameters(content, parameters ?? []);
        content.Append(':').Append(encoded).Append("\r\n");
    }

    private static bool HasParameter(IReadOnlyList<CalendarParameter>? parameters, string name) =>
        parameters?.Any(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    private static string CompactDateTime(string value) => value
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(":", string.Empty, StringComparison.Ordinal);

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);
}
