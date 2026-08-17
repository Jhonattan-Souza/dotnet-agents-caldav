using System.Collections.Frozen;
using System.Globalization;
using DotnetAgents.CalDav.Core.Models;
using NodaTime;
using NodaTime.TimeZones;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarEntityCreateValidator
{
    private static readonly FrozenSet<string> EventStatuses =
        new[] { "TENTATIVE", "CONFIRMED", "CANCELLED" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> TodoStatuses =
        new[] { "NEEDS-ACTION", "COMPLETED", "IN-PROCESS", "CANCELLED" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> EventParticipationStatuses =
        new[] { "NEEDS-ACTION", "ACCEPTED", "DECLINED", "TENTATIVE", "DELEGATED" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> TodoParticipationStatuses =
        new[] { "NEEDS-ACTION", "ACCEPTED", "DECLINED", "TENTATIVE", "DELEGATED", "COMPLETED", "IN-PROCESS" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ParticipantTypes = new[]
    {
        "ACTIVE", "INACTIVE", "SPONSOR", "CONTACT", "BOOKING-CONTACT", "EMERGENCY-CONTACT",
        "PUBLICITY-CONTACT", "PLANNER-CONTACT", "PERFORMER", "SPEAKER"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> CalendarUserTypes =
        new[] { "INDIVIDUAL", "GROUP", "RESOURCE", "ROOM", "UNKNOWN" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> AttendeeRoles =
        new[] { "CHAIR", "REQUIRED", "OPTIONAL", "NON-PARTICIPANT" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> RelationTypes =
        new[] { "PARENT", "CHILD", "SIBLING" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> EventTransparencies =
        new[] { "OPAQUE", "TRANSPARENT" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> Classifications =
        new[] { "PUBLIC", "PRIVATE", "CONFIDENTIAL" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static void ValidateEvent(string uid, CalendarEventCreateFields fields)
    {
        ValidateSafeValue(uid, allowNewLines: false);
        RejectRecurring(fields.RecurrenceSetPresent, "Event");
        var start = ValidateTemporal(fields.Start ?? throw new ArgumentException("An Event start is required."));
        if (fields.End is not null && fields.Duration is not null)
            throw new ArgumentException("Event end and duration are mutually exclusive.");
        if (fields.End is not null)
            ValidateOrderedTemporal(start, ValidateTemporal(fields.End));
        ValidateDuration(fields.Duration, start.Kind == CalendarTemporalKind.Date);
        ValidateCommonFields(
            fields.Summary,
            fields.Description,
            fields.Priority,
            fields.Categories,
            fields.StructuredData,
            CalendarEntityKind.Event);
        ValidateSafeValue(fields.Location);
        ValidateGeo(fields.Geo);
        ValidateOpenEnum(fields.Status, EventStatuses);
        ValidateOpenEnum(fields.Transparency, EventTransparencies);
        ValidateOpenEnum(fields.Classification, Classifications);
        ValidateUri(fields.Url);
    }

    public static void ValidateTodo(string uid, CalendarTodoCreateFields fields)
    {
        ValidateSafeValue(uid, allowNewLines: false);
        RejectRecurring(fields.RecurrenceSetPresent, "To-do");
        if (fields.Due is not null && fields.Duration is not null)
            throw new ArgumentException("To-do due and duration are mutually exclusive.");
        if (fields.Duration is not null && fields.Start is null)
            throw new ArgumentException("A To-do duration requires a start.");
        var start = fields.Start is null ? null : ValidateTemporal(fields.Start);
        var due = fields.Due is null ? null : ValidateTemporal(fields.Due);
        if (start is not null && due is not null)
            ValidateOrderedTemporal(start, due);
        ValidateDuration(fields.Duration, start?.Kind == CalendarTemporalKind.Date);
        ValidateCommonFields(
            fields.Summary,
            fields.Description,
            fields.Priority,
            fields.Categories,
            fields.StructuredData,
            CalendarEntityKind.Todo);
        ValidateOpenEnum(fields.Status, TodoStatuses);
    }

    internal static void ValidatePatchScalars(CalendarEventPatch patch, CalendarEntityKind entityKind)
    {
        ValidateScalar(patch.Summary, value => ValidateSafeValue(value));
        ValidateScalar(patch.Description, value => ValidateSafeValue(value));
        ValidateScalar(patch.Start, value => ValidateTemporal(value));
        ValidateScalar(patch.Duration, value => ValidateDuration(value, dateOnly: false));
        ValidateScalar(patch.Priority, ValidatePriority);
        ValidateScalar(patch.Organizer, value => ValidateNamedUri(value, "CAL-ADDRESS", "CN"));
        if (entityKind == CalendarEntityKind.Event)
            ValidateEventPatchScalars(patch);
        else
            ValidateTodoPatchScalars(patch);
    }

    internal static void ValidatePatchFinalTemporal(
        CalendarEntityKind entityKind,
        CalendarTemporalValue? start,
        CalendarTemporalValue? endOrDue,
        string? duration)
    {
        if (entityKind == CalendarEntityKind.Event)
            ValidateEventPatchFinalTemporal(start, endOrDue, duration);
        else
            ValidateTodoPatchFinalTemporal(start, endOrDue, duration);
    }

    private static void ValidateEventPatchFinalTemporal(
        CalendarTemporalValue? start,
        CalendarTemporalValue? end,
        string? duration)
    {
        if (start is null)
            throw new ArgumentException("An Event start is required.");
        ValidateTemporalCombination(start, end, duration);
    }

    private static void ValidateTodoPatchFinalTemporal(
        CalendarTemporalValue? start,
        CalendarTemporalValue? due,
        string? duration)
    {
        if (duration is not null && start is null)
            throw new ArgumentException("A To-do duration requires a start.");
        ValidateTemporalCombination(start, due, duration);
    }

    private static void ValidateTemporalCombination(
        CalendarTemporalValue? start,
        CalendarTemporalValue? endOrDue,
        string? duration)
    {
        if (endOrDue is not null && duration is not null)
            throw new ArgumentException("End or due and duration are mutually exclusive.");
        if (start is not null && endOrDue is not null)
            ValidatePatchOrderedTemporal(start, endOrDue);
        ValidateDuration(duration, start?.Kind == CalendarTemporalKind.Date);
    }

    private static void ValidatePatchOrderedTemporal(CalendarTemporalValue start, CalendarTemporalValue endOrDue)
    {
        if (start.Kind != endOrDue.Kind
            || !string.Equals(start.TimeZoneId, endOrDue.TimeZoneId, StringComparison.Ordinal)
            || string.CompareOrdinal(endOrDue.Value, start.Value) <= 0)
        {
            throw new ArgumentException("The end or due must have the same temporal family and be later than the start.");
        }
    }

    private static void ValidateEventPatchScalars(CalendarEventPatch patch)
    {
        RejectAddressed(patch.Due);
        RejectAddressed(patch.PercentComplete);
        ValidateScalar(patch.End, value => ValidateTemporal(value));
        ValidateScalar(patch.Location, value => ValidateSafeValue(value));
        ValidateScalar(patch.Geo, value => ValidateGeo(value));
        ValidateScalar(patch.Status, value => ValidateOpenEnum(value, EventStatuses));
        ValidateScalar(patch.Transparency, value => ValidateOpenEnum(value, EventTransparencies));
        ValidateScalar(patch.Classification, value => ValidateOpenEnum(value, Classifications));
        ValidateScalar(patch.Url, value => ValidateUri(value));
    }

    private static void ValidateTodoPatchScalars(CalendarEventPatch patch)
    {
        RejectAddressed(patch.End);
        RejectAddressed(patch.Location);
        RejectAddressed(patch.Geo);
        RejectAddressed(patch.Transparency);
        RejectAddressed(patch.Classification);
        RejectAddressed(patch.Url);
        ValidateScalar(patch.Due, value => ValidateTemporal(value));
        ValidateScalar(patch.Status, ValidateTodoPatchStatus);
        ValidateScalar(patch.PercentComplete, ValidatePercentComplete);
    }

    private static void ValidateScalar<T>(CalendarScalarPatch<T>? patch, Action<T> validate)
    {
        if (patch?.Operation == CalendarScalarPatchOperation.Set)
            validate(patch.Value!);
    }

    private static void RejectAddressed<T>(CalendarScalarPatch<T>? patch)
    {
        if (patch is not null)
            throw new ArgumentException("The scalar does not belong to this Calendar Entity kind.");
    }

    private static void ValidatePriority(int value)
    {
        if (value is < 0 or > 9)
            throw new ArgumentException("Priority must be between zero and nine.");
    }

    private static void ValidatePercentComplete(int value)
    {
        if (value is < 0 or > 100)
            throw new ArgumentException("Percent complete must be between zero and one hundred.");
    }

    private static void ValidateTodoPatchStatus(string value)
    {
        ValidateOpenEnum(value, TodoStatuses);
        if (value.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("To-do completion is reserved for the coordinated completion operation.");
    }

    private static void RejectRecurring(bool recurrenceSetPresent, string entityKind)
    {
        if (recurrenceSetPresent)
            throw new ArgumentException($"Recurring {entityKind} creation is not supported by this operation.");
    }

    private static void ValidateCommonFields(
        string? summary,
        string? description,
        int? priority,
        IReadOnlyList<string>? categories,
        CalendarStructuredData? structuredData,
        CalendarEntityKind entityKind)
    {
        ValidateSafeValue(summary);
        ValidateSafeValue(description);
        if (priority is < 0 or > 9)
            throw new ArgumentException("Priority must be between zero and nine.");
        foreach (var category in categories ?? [])
            ValidateSafeValue(category);
        ValidateStructuredData(structuredData, entityKind);
    }

    private static void ValidateStructuredData(CalendarStructuredData? data, CalendarEntityKind entityKind)
    {
        if (data is null)
            return;
        ValidateNamedUri(data.Organizer, "CAL-ADDRESS", "CN");
        foreach (var attendee in data.Attendees ?? [])
            ValidateAttendee(attendee, entityKind);
        foreach (var participant in data.Participants ?? [])
            ValidateParticipant(participant, entityKind);
        ValidateTextValues(data.Contacts);
        ValidateTextValues(data.Resources);
        ValidateRelations(data.RelatedTo);
        ValidateRequestStatuses(data.RequestStatuses);
        ValidateAlarms(data.Alarms, entityKind);
        ValidateNamedUris(data.Attachments, "URI", "LABEL");
        ValidateTextValues(data.Comments);
        ValidateTextValues(data.StyledDescriptions);
        ValidateNamedUris(data.Images, "URI", "LABEL");
        ValidateNamedUris(data.Conferences, "URI", "LABEL");
        ValidateNamedUris(data.Links, "URI", "LABEL");
        ValidateUriValues(data.Concepts);
        ValidateUriValues(data.StructuredDataUris);
        ValidateNamedUris(data.LocationUris, "TEXT");
        ValidateNamedUris(data.ResourceUris, "TEXT");
    }

    private static void ValidateRelations(IReadOnlyList<CalendarRelation>? relations)
    {
        foreach (var relation in relations ?? [])
        {
            if (relation.Value.Length == 0)
                throw new ArgumentException("A relation value is required.");
            ValidateSafeValue(relation.Value);
            ValidateOpenEnum(relation.RelationType, RelationTypes);
            ValidateTypedParameters(relation.Parameters ?? [], "TEXT", "RELTYPE");
        }
    }

    private static void ValidateRequestStatuses(IReadOnlyList<CalendarRequestStatus>? statuses)
    {
        foreach (var status in statuses ?? [])
            ValidateRequestStatus(status);
    }

    private static void ValidateAlarms(IReadOnlyList<CalendarAlarm>? alarms, CalendarEntityKind entityKind)
    {
        foreach (var alarm in alarms ?? [])
            ValidateAlarm(alarm, entityKind);
    }

    private static void ValidateAttendee(CalendarAttendee participant, CalendarEntityKind entityKind)
    {
        ValidateUri(participant.Uri);
        ValidateSafeValue(participant.CommonName);
        ValidateOpenEnum(participant.Role, AttendeeRoles);
        ValidateOpenEnum(
            participant.ParticipationStatus,
            entityKind == CalendarEntityKind.Event ? EventParticipationStatuses : TodoParticipationStatuses);
        ValidateOpenEnum(participant.CalendarUserType, CalendarUserTypes);
        ValidateUris(participant.DelegatedTo);
        ValidateUris(participant.DelegatedFrom);
        ValidateUri(participant.SentBy);
        ValidateUri(participant.Directory);
        ValidateTypedParameters(
            participant.Parameters,
            "CAL-ADDRESS",
            "CN", "ROLE", "PARTSTAT", "CUTYPE", "RSVP", "DELEGATED-TO", "DELEGATED-FROM", "SENT-BY", "DIR");
    }

    private static void ValidateParticipant(CalendarParticipant participant, CalendarEntityKind entityKind)
    {
        ValidateRequiredTextProperty(participant.Uid, "A Participant UID is required.");
        ValidateRequiredOpenEnumProperty(
            participant.ParticipantType,
            ParticipantTypes,
            "A Participant type is required.");
        ValidateUriValue(participant.CalendarAddress);
        ValidateUtcTemporalProperty(participant.Created);
        ValidateTextValue(participant.Description);
        ValidateUtcTemporalProperty(participant.Timestamp);
        ValidateGeoProperty(participant.Geo);
        ValidateUtcTemporalProperty(participant.LastModified);
        ValidateIntegerProperty(participant.Priority, 0, 9);
        ValidateIntegerProperty(participant.Sequence, 0, int.MaxValue);
        ValidateOpenEnum(
            participant.Status?.Value,
            entityKind == CalendarEntityKind.Event ? EventStatuses : TodoStatuses);
        ValidateTextValue(participant.Status, "VALUE");
        ValidateTextValue(participant.Summary);
        ValidateUriValue(participant.Url);
        ValidateNamedUris(participant.Attachments, "URI", "LABEL");
        foreach (var category in participant.Categories?.Value ?? [])
            ValidateSafeValue(category);
        if (participant.Categories is not null)
            ValidateTypedParameters(participant.Categories.Parameters, "TEXT");
        ValidateTextValues(participant.Comments);
        ValidateTextValues(participant.Contacts);
        ValidateTextValues(participant.Locations);
        ValidateRequestStatuses(participant.RequestStatuses);
        ValidateRelations(participant.RelatedTo);
        ValidateTextValues(participant.Resources);
        ValidateTextValues(participant.StyledDescriptions);
        ValidateUriValues(participant.StructuredDataUris);
        ValidateNamedUris(participant.LocationUris, "TEXT");
        ValidateNamedUris(participant.ResourceUris, "TEXT");
    }

    private static void ValidateUtcTemporalProperty(CalendarTemporalProperty? property)
    {
        if (property is null)
            return;
        var value = property.Value;
        if (value.Kind != CalendarTemporalKind.UtcDateTime)
            throw new ArgumentException("Participant revision timestamps must be UTC DATE-TIME values.");
        ValidateTemporal(value);
        ValidateTypedParameters(property.Parameters, "DATE-TIME", "TZID");
    }

    private static void ValidateGeoProperty(CalendarGeoProperty? property)
    {
        if (property is null)
            return;
        ValidateGeo(property.Value);
        ValidateTypedParameters(property.Parameters, "FLOAT");
    }

    private static void ValidateIntegerProperty(CalendarIntegerProperty? property, int minimum, int maximum)
    {
        if (property is null)
            return;
        if (property.Value < minimum || property.Value > maximum)
            throw new ArgumentException("An integer structured property is outside its allowed range.");
        ValidateTypedParameters(property.Parameters, "INTEGER");
    }

    private static void ValidateAlarm(CalendarAlarm alarm, CalendarEntityKind entityKind)
    {
        var action = alarm.Action.Value.ToUpperInvariant();
        if (action is not ("DISPLAY" or "AUDIO" or "EMAIL"))
            throw new ArgumentException("The alarm action is invalid.");
        ValidateTextValue(alarm.Action, "VALUE");
        ValidateSafeValue(alarm.Trigger.Value, allowNewLines: false);
        ValidateParameters(alarm.Trigger.Parameters);
        if (string.IsNullOrWhiteSpace(alarm.Trigger.Value))
            throw new ArgumentException("An alarm trigger is required.");
        ValidateTextValue(alarm.Description, "VALUE");
        ValidateTextValue(alarm.Summary, "VALUE");
        foreach (var attendee in alarm.Attendees ?? [])
            ValidateAttendee(attendee, entityKind);
        ValidateNamedUris(alarm.Attachments, "URI", "LABEL");
        ValidateAlarmShape(action, alarm);
        ValidateAlarmRepeat(alarm);
        var relative = CalendarDurationArithmetic.TryParse(alarm.Trigger.Value, out _);
        var absolute = DateTimeOffset.TryParseExact(
                alarm.Trigger.Value,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _);
        if (!relative && !absolute)
        {
            throw new ArgumentException("The alarm trigger is invalid.");
        }
        ValidateAlarmTriggerParameters(alarm.Trigger.Parameters, relative);
    }

    private static void ValidateAlarmTriggerParameters(
        IReadOnlyList<CalendarParameter> parameters,
        bool relative)
    {
        var value = parameters.SingleOrDefault(parameter =>
            parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase));
        var related = parameters.SingleOrDefault(parameter =>
            parameter.Name.Equals("RELATED", StringComparison.OrdinalIgnoreCase));
        ValidateAlarmTriggerValueParameter(value, relative);
        if (!relative && value is null)
            throw new ArgumentException("An absolute alarm trigger requires VALUE=DATE-TIME.");
        if (related is not null && !IsValidRelatedParameter(related, relative))
        {
            throw new ArgumentException("The alarm trigger RELATED parameter is invalid for its value shape.");
        }
    }

    private static void ValidateAlarmRepeat(CalendarAlarm alarm)
    {
        if ((alarm.Repeat is null) != (alarm.Duration is null))
            throw new ArgumentException("Alarm repeat and duration must be supplied together.");
        ValidateIntegerProperty(alarm.Repeat, 1, int.MaxValue);
        if (alarm.Duration is null)
            return;
        ValidateDuration(alarm.Duration.Value, dateOnly: false);
        ValidateTypedParameters(alarm.Duration.Parameters, "DURATION");
    }

    private static void ValidateAlarmTriggerValueParameter(CalendarParameter? value, bool relative)
    {
        if (value is not null && !HasSingleValue(value, relative ? "DURATION" : "DATE-TIME"))
            throw new ArgumentException("The alarm trigger VALUE parameter does not match its value shape.");
    }

    private static bool IsValidRelatedParameter(CalendarParameter related, bool relative) => relative
        && related.Values.Count == 1
        && (related.Values[0].Equals("START", StringComparison.OrdinalIgnoreCase)
            || related.Values[0].Equals("END", StringComparison.OrdinalIgnoreCase));

    private static bool HasSingleValue(CalendarParameter parameter, string expected) =>
        parameter.Values.Count == 1
        && parameter.Values[0].Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void ValidateAlarmShape(string action, CalendarAlarm alarm)
    {
        switch (action)
        {
            case "DISPLAY":
                ValidateDisplayAlarm(alarm);
                break;
            case "AUDIO":
                ValidateAudioAlarm(alarm);
                break;
            case "EMAIL":
                ValidateEmailAlarm(alarm);
                break;
        }
    }

    private static void ValidateDisplayAlarm(CalendarAlarm alarm)
    {
        if (alarm.Description is null
            || alarm.Summary is not null
            || alarm.Attendees is { Count: > 0 }
            || alarm.Attachments is { Count: > 0 })
        {
            throw new ArgumentException("A display alarm requires only its description.");
        }
    }

    private static void ValidateAudioAlarm(CalendarAlarm alarm)
    {
        if (alarm.Description is not null
            || alarm.Summary is not null
            || alarm.Attendees is { Count: > 0 }
            || alarm.Attachments is { Count: > 1 })
        {
            throw new ArgumentException("An audio alarm allows at most one attachment.");
        }
    }

    private static void ValidateEmailAlarm(CalendarAlarm alarm)
    {
        if (alarm.Description is null || alarm.Summary is null || alarm.Attendees is not { Count: > 0 })
            throw new ArgumentException("An email alarm requires description, summary, and at least one attendee.");
    }

    private static void ValidateRequestStatus(CalendarRequestStatus status)
    {
        var separator = status.Code.IndexOf('.');
        if (separator <= 0
            || separator == status.Code.Length - 1
            || status.Code.AsSpan(0, separator).ContainsAnyExceptInRange('0', '9')
            || status.Code.AsSpan(separator + 1).ContainsAnyExceptInRange('0', '9'))
        {
            throw new ArgumentException("A request status code must contain two numeric components.");
        }
        ValidateSafeValue(status.Description);
        ValidateSafeValue(status.ExceptionData);
        ValidateTypedParameters(status.Parameters ?? [], "TEXT");
    }

    private static ValidatedTemporal ValidateTemporal(CalendarTemporalValue value)
    {
        return value.Kind switch
        {
            CalendarTemporalKind.Date => ValidateUnzonedTemporal(value, "yyyy-MM-dd", utcSuffix: false),
            CalendarTemporalKind.FloatingDateTime =>
                ValidateUnzonedTemporal(value, "yyyy-MM-dd'T'HH:mm:ss", utcSuffix: false),
            CalendarTemporalKind.UtcDateTime =>
                ValidateUnzonedTemporal(value, "yyyy-MM-dd'T'HH:mm:ss", utcSuffix: true),
            CalendarTemporalKind.ZonedDateTime => ValidateZonedTemporal(value),
            _ => throw new ArgumentException("The temporal kind is invalid.")
        };
    }

    private static ValidatedTemporal ValidateUnzonedTemporal(
        CalendarTemporalValue value,
        string format,
        bool utcSuffix)
    {
        if (value.TimeZoneId is not null
            || utcSuffix != value.Value.EndsWith('Z')
            || !DateTime.TryParseExact(
                utcSuffix ? value.Value[..^1] : value.Value,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new ArgumentException("The temporal value is invalid.");
        }
        return new ValidatedTemporal(
            value.Kind,
            null,
            Instant.FromDateTimeUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)));
    }

    private static ValidatedTemporal ValidateZonedTemporal(CalendarTemporalValue value)
    {
        ValidateSafeValue(value.TimeZoneId, allowNewLines: false);
        var zone = value.TimeZoneId is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(value.TimeZoneId);
        if (zone is null
            || !DateTime.TryParseExact(
                value.Value,
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new ArgumentException("The named-zone temporal value is invalid.");
        }
        return ResolveZonedTemporal(value, parsed, zone);
    }

    private static ValidatedTemporal ResolveZonedTemporal(
        CalendarTemporalValue value,
        DateTime parsed,
        DateTimeZone zone)
    {
        try
        {
            return new ValidatedTemporal(
                value.Kind,
                value.TimeZoneId,
                zone.AtStrictly(LocalDateTime.FromDateTime(parsed)).ToInstant());
        }
        catch (SkippedTimeException)
        {
            throw new ArgumentException("The named-zone local time does not exist.");
        }
        catch (AmbiguousTimeException)
        {
            throw new ArgumentException("The named-zone local time is ambiguous.");
        }
    }

    private static void ValidateOrderedTemporal(ValidatedTemporal start, ValidatedTemporal end)
    {
        if (start.Kind != end.Kind
            || !string.Equals(start.TimeZoneId, end.TimeZoneId, StringComparison.Ordinal)
            || end.Comparable <= start.Comparable)
        {
            throw new ArgumentException("The end or due must have the same temporal family and be later than the start.");
        }
    }

    private static void ValidateDuration(string? value, bool dateOnly)
    {
        if (value is null)
            return;
        if (!CalendarDurationArithmetic.TryParse(value, out var duration)
            || !duration.IsStrictlyPositive
            || dateOnly && duration.Accurate != TimeSpan.Zero)
        {
            throw new ArgumentException("The duration is invalid for this temporal family.");
        }
    }

    private static void ValidateGeo(CalendarGeo? geo)
    {
        if (geo is not null && (!double.IsFinite(geo.Latitude)
            || !double.IsFinite(geo.Longitude)
            || geo.Latitude is < -90 or > 90
            || geo.Longitude is < -180 or > 180))
        {
            throw new ArgumentException("The geographic coordinates are invalid.");
        }
    }

    private static void ValidateNamedUris(
        IReadOnlyList<CalendarNamedUri>? values,
        string expectedValueType,
        params string[] reservedParameterNames)
    {
        foreach (var value in values ?? [])
            ValidateNamedUri(value, expectedValueType, reservedParameterNames);
    }

    private static void ValidateNamedUri(
        CalendarNamedUri? value,
        string expectedValueType,
        params string[] reservedParameterNames)
    {
        if (value is null)
            return;
        ValidateUri(value.Uri);
        ValidateSafeValue(value.Label);
        ValidateTypedParameters(value.Parameters, expectedValueType, reservedParameterNames);
    }

    private static void ValidateUriValues(IReadOnlyList<CalendarUriValue>? values)
    {
        foreach (var value in values ?? [])
            ValidateUriValue(value);
    }

    private static void ValidateUriValue(CalendarUriValue? value)
    {
        if (value is null)
            return;
        ValidateUri(value.Uri);
        ValidateTypedParameters(value.Parameters, "URI");
    }

    private static void ValidateTextValues(IReadOnlyList<CalendarTextValue>? values)
    {
        foreach (var value in values ?? [])
            ValidateTextValue(value);
    }

    private static void ValidateTextValue(
        CalendarTextValue? value,
        params string[] reservedParameterNames)
    {
        if (value is null)
            return;
        ValidateSafeValue(value.Value);
        ValidateTypedParameters(value.Parameters, "TEXT", reservedParameterNames);
    }

    private static void ValidateRequiredTextProperty(CalendarTextValue value, string message)
    {
        ValidateTextValue(value, "VALUE");
        if (string.IsNullOrEmpty(value.Value))
            throw new ArgumentException(message);
    }

    private static void ValidateRequiredOpenEnumProperty(
        CalendarTextValue value,
        IReadOnlySet<string> recognized,
        string message)
    {
        ValidateRequiredTextProperty(value, message);
        ValidateOpenEnum(value.Value, recognized);
    }

    private static void ValidateParameters(
        IReadOnlyList<CalendarParameter> parameters,
        params string[] reservedParameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            ValidateToken(parameter.Name);
            if (!names.Add(parameter.Name)
                || reservedParameterNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("An iCalendar parameter name may occur only once and cannot duplicate a first-class field.");
            }
            if (parameter.Values is null || parameter.Values.Count == 0)
                throw new ArgumentException("A parameter requires at least one value.");
            foreach (var value in parameter.Values)
                ValidateSafeValue(value);
        }
    }

    private static void ValidateTypedParameters(
        IReadOnlyList<CalendarParameter> parameters,
        string expectedValueType,
        params string[] reservedParameterNames)
    {
        ValidateParameters(
            parameters,
            reservedParameterNames.Where(name => !name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)).ToArray());
        var value = parameters.SingleOrDefault(parameter =>
            parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase));
        if (value is not null && !HasSingleValue(value, expectedValueType))
            throw new ArgumentException("The explicit VALUE parameter is incompatible with the property value family.");
    }

    private static void ValidateUris(IReadOnlyList<string>? values)
    {
        foreach (var value in values ?? [])
            ValidateUri(value);
    }

    private static void ValidateUri(string? value)
    {
        if (value is null)
            return;
        ValidateSafeValue(value, allowNewLines: false);
        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("An absolute URI is required.");
    }

    private static void ValidateToken(string? value)
    {
        if (value is null)
            return;
        if (value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("An iCalendar token is invalid.");
    }

    private static void ValidateOpenEnum(string? value, IReadOnlySet<string> recognizedValues)
    {
        if (value is null)
            return;
        ValidateToken(value);
        if (!recognizedValues.Contains(value)
            && (!value.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || value.Length == 2))
        {
            throw new ArgumentException("An open enumeration value must be recognized or use the X- extension form.");
        }
    }

    internal static void ValidateParameterValue(string value) => ValidateSafeValue(value);

    private static void ValidateSafeValue(string? value, bool allowNewLines = true)
    {
        if (value is null)
            return;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\r')
            {
                if (!allowNewLines || index + 1 >= value.Length || value[index + 1] != '\n')
                    throw new ArgumentException("A value contains an unsafe carriage return.");
                index++;
                continue;
            }
            if (character is '\n' or '\t')
            {
                if (!allowNewLines)
                    throw new ArgumentException("A value contains an unsafe control character.");
                continue;
            }
            if (char.IsControl(character))
                throw new ArgumentException("A value contains an unsafe control character.");
        }
    }

    private sealed record ValidatedTemporal(
        CalendarTemporalKind Kind,
        string? TimeZoneId,
        Instant Comparable);
}
