namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Allowed destination modes for semantic creation.</summary>
public sealed record CalendarCreateDestination(
    CalendarEntityScopeMode Mode,
    CalendarReference? Calendar = null)
{
    public static CalendarCreateDestination Default { get; } = new(CalendarEntityScopeMode.Default);

    public static CalendarCreateDestination Selected(CalendarReference calendar) =>
        new(CalendarEntityScopeMode.Selected, calendar);
}

/// <summary>Typed fields for one complete Event master or recurrence override.</summary>
public sealed record CalendarEventCreateFields(
    string? Summary = null,
    string? Description = null,
    CalendarTemporalValue? Start = null,
    CalendarTemporalValue? End = null,
    string? Duration = null,
    string? Location = null,
    CalendarGeo? Geo = null,
    string? Status = null,
    string? Transparency = null,
    string? Classification = null,
    int? Priority = null,
    IReadOnlyList<string>? Categories = null,
    string? Url = null,
    CalendarStructuredData? StructuredData = null,
    CalendarEventRecurrenceSetCreate? RecurrenceSet = null);

/// <summary>Semantic creation request for one Event.</summary>
public sealed record CalendarEventCreateRequest(
    CalendarCreateDestination Destination,
    string? Uid,
    CalendarEventCreateFields Fields);

/// <summary>Typed fields for one complete To-do master or recurrence override.</summary>
public sealed record CalendarTodoCreateFields(
    string? Summary = null,
    string? Description = null,
    CalendarTemporalValue? Start = null,
    CalendarTemporalValue? Due = null,
    string? Duration = null,
    string? Status = null,
    int? Priority = null,
    IReadOnlyList<string>? Categories = null,
    CalendarStructuredData? StructuredData = null,
    CalendarTodoRecurrenceSetCreate? RecurrenceSet = null);

/// <summary>One typed recurrence date, including the RFC 5545 PERIOD form.</summary>
public sealed record CalendarRecurrenceDateCreate(
    CalendarTemporalValue? Value = null,
    CalendarRecurrencePeriodCreate? Period = null);

/// <summary>One positive recurrence period with either an explicit end or nominal duration.</summary>
public sealed record CalendarRecurrencePeriodCreate(
    CalendarTemporalValue Start,
    CalendarTemporalValue? End = null,
    string? Duration = null);

/// <summary>Range carried by one complete recurrence override.</summary>
public enum CalendarRecurrenceOverrideRange
{
    ThisAndPrior,
    ThisAndFuture
}

/// <summary>Explicit active or cancelled state for one complete recurrence override.</summary>
public enum CalendarRecurrenceOverrideStatus
{
    Active,
    Cancelled
}

/// <summary>One complete Event override. Kind and UID are inherited from the master.</summary>
public sealed record CalendarEventRecurrenceOverrideCreate(
    CalendarTemporalValue RecurrenceIdentity,
    CalendarRecurrenceOverrideStatus Status,
    CalendarEventCreateFields Fields,
    CalendarRecurrenceOverrideRange? Range = null);

/// <summary>One complete To-do override. Kind and UID are inherited from the master.</summary>
public sealed record CalendarTodoRecurrenceOverrideCreate(
    CalendarTemporalValue RecurrenceIdentity,
    CalendarRecurrenceOverrideStatus Status,
    CalendarTodoCreateFields Fields,
    CalendarRecurrenceOverrideRange? Range = null);

/// <summary>Typed recurrence input for Event creation.</summary>
public sealed record CalendarEventRecurrenceSetCreate(
    string? Rule = null,
    IReadOnlyList<CalendarRecurrenceDateCreate>? RecurrenceDates = null,
    IReadOnlyList<CalendarTemporalValue>? ExceptionDates = null,
    IReadOnlyList<CalendarEventRecurrenceOverrideCreate>? Overrides = null);

/// <summary>Typed recurrence input for To-do creation.</summary>
public sealed record CalendarTodoRecurrenceSetCreate(
    string? Rule = null,
    IReadOnlyList<CalendarRecurrenceDateCreate>? RecurrenceDates = null,
    IReadOnlyList<CalendarTemporalValue>? ExceptionDates = null,
    IReadOnlyList<CalendarTodoRecurrenceOverrideCreate>? Overrides = null);

/// <summary>Geographic coordinates stored by an iCalendar GEO property.</summary>
public sealed record CalendarGeo(double Latitude, double Longitude);

/// <summary>One URI-valued iCalendar property and its storage-only parameters.</summary>
public sealed record CalendarNamedUri(
    string Uri,
    string? Label,
    IReadOnlyList<CalendarParameter> Parameters);

/// <summary>One complete RFC 9073 VLOCATION or VRESOURCE component.</summary>
public sealed record CalendarNamedComponent(
    string Uid,
    CalendarTextValue? Name,
    IReadOnlyList<CalendarParameter> Parameters,
    CalendarTextValue? Description = null,
    CalendarGeoProperty? Geo = null,
    CalendarTextListProperty? ComponentTypes = null,
    CalendarUriValue? Url = null,
    IReadOnlyList<CalendarRelation>? RelatedTo = null,
    IReadOnlyList<CalendarUriValue>? Concepts = null,
    IReadOnlyList<CalendarNamedUri>? Links = null,
    IReadOnlyList<CalendarUriValue>? StructuredDataUris = null);

/// <summary>One URI-valued property without label semantics.</summary>
public sealed record CalendarUriValue(
    string Uri,
    IReadOnlyList<CalendarParameter> Parameters);

/// <summary>One text-valued iCalendar property and its storage-only parameters.</summary>
public sealed record CalendarTextValue(string Value, IReadOnlyList<CalendarParameter> Parameters)
{
    public static implicit operator CalendarTextValue(string value) => new(value, []);
}

/// <summary>One temporal iCalendar property and its storage-only parameters.</summary>
public sealed record CalendarTemporalProperty(
    CalendarTemporalValue Value,
    IReadOnlyList<CalendarParameter> Parameters)
{
    public static implicit operator CalendarTemporalProperty(CalendarTemporalValue value) => new(value, []);
}

/// <summary>One GEO property and its storage-only parameters.</summary>
public sealed record CalendarGeoProperty(CalendarGeo Value, IReadOnlyList<CalendarParameter> Parameters)
{
    public static implicit operator CalendarGeoProperty(CalendarGeo value) => new(value, []);
}

/// <summary>One integer iCalendar property and its storage-only parameters.</summary>
public sealed record CalendarIntegerProperty(int Value, IReadOnlyList<CalendarParameter> Parameters)
{
    public static implicit operator CalendarIntegerProperty(int value) => new(value, []);
}

/// <summary>One comma-separated TEXT-list property and its storage-only parameters.</summary>
public sealed record CalendarTextListProperty(
    IReadOnlyList<string> Value,
    IReadOnlyList<CalendarParameter> Parameters);

/// <summary>One RFC 5545 DURATION property and its storage-only parameters.</summary>
public sealed record CalendarDurationProperty(string Value, IReadOnlyList<CalendarParameter> Parameters)
{
    public static implicit operator CalendarDurationProperty(string value) => new(value, []);
}

/// <summary>One typed ATTENDEE value.</summary>
public sealed record CalendarAttendee(
    string Uri,
    IReadOnlyList<CalendarParameter> Parameters,
    string? CommonName = null,
    string? Role = null,
    string? ParticipationStatus = null,
    string? CalendarUserType = null,
    bool? Rsvp = null,
    IReadOnlyList<string>? DelegatedTo = null,
    IReadOnlyList<string>? DelegatedFrom = null,
    string? SentBy = null,
    string? Directory = null);

/// <summary>One independent RFC 9073 PARTICIPANT component.</summary>
public sealed record CalendarParticipant(
    CalendarTextValue Uid,
    CalendarTextValue ParticipantType,
    CalendarUriValue? CalendarAddress = null,
    CalendarTemporalProperty? Created = null,
    CalendarTextValue? Description = null,
    CalendarTemporalProperty? Timestamp = null,
    CalendarGeoProperty? Geo = null,
    CalendarTemporalProperty? LastModified = null,
    CalendarIntegerProperty? Priority = null,
    CalendarIntegerProperty? Sequence = null,
    CalendarTextValue? Status = null,
    CalendarTextValue? Summary = null,
    CalendarUriValue? Url = null,
    IReadOnlyList<CalendarNamedUri>? Attachments = null,
    CalendarTextListProperty? Categories = null,
    IReadOnlyList<CalendarTextValue>? Comments = null,
    IReadOnlyList<CalendarTextValue>? Contacts = null,
    IReadOnlyList<CalendarTextValue>? Locations = null,
    IReadOnlyList<CalendarRequestStatus>? RequestStatuses = null,
    IReadOnlyList<CalendarRelation>? RelatedTo = null,
    IReadOnlyList<CalendarTextValue>? Resources = null,
    IReadOnlyList<CalendarTextValue>? StyledDescriptions = null,
    IReadOnlyList<CalendarUriValue>? StructuredDataUris = null,
    IReadOnlyList<CalendarNamedComponent>? LocationUris = null,
    IReadOnlyList<CalendarNamedComponent>? ResourceUris = null);

/// <summary>One RELATED-TO relationship.</summary>
public sealed record CalendarRelation(
    string Value,
    string? RelationType = null,
    IReadOnlyList<CalendarParameter>? Parameters = null);

/// <summary>One REQUEST-STATUS value.</summary>
public sealed record CalendarRequestStatus(
    string Code,
    string Description,
    string? ExceptionData = null,
    IReadOnlyList<CalendarParameter>? Parameters = null);

/// <summary>One non-nested VALARM component.</summary>
public sealed record CalendarAlarm(
    CalendarTextValue Action,
    CalendarTextValue Trigger,
    CalendarTextValue? Description = null,
    CalendarIntegerProperty? Repeat = null,
    CalendarDurationProperty? Duration = null,
    CalendarTextValue? Summary = null,
    IReadOnlyList<CalendarAttendee>? Attendees = null,
    IReadOnlyList<CalendarNamedUri>? Attachments = null,
    CalendarTextValue? Uid = null,
    CalendarTemporalProperty? Acknowledged = null,
    CalendarTextValue? Proximity = null,
    IReadOnlyList<CalendarRelation>? RelatedTo = null,
    IReadOnlyList<CalendarNamedComponent>? ProximityLocations = null);

/// <summary>Complete storage-only structured data supported by create operations.</summary>
public sealed record CalendarStructuredData(
    CalendarNamedUri? Organizer = null,
    IReadOnlyList<CalendarAttendee>? Attendees = null,
    IReadOnlyList<CalendarParticipant>? Participants = null,
    IReadOnlyList<CalendarTextValue>? Contacts = null,
    IReadOnlyList<CalendarTextValue>? Resources = null,
    IReadOnlyList<CalendarRelation>? RelatedTo = null,
    IReadOnlyList<CalendarRequestStatus>? RequestStatuses = null,
    IReadOnlyList<CalendarAlarm>? Alarms = null,
    IReadOnlyList<CalendarNamedUri>? Attachments = null,
    IReadOnlyList<CalendarTextValue>? Comments = null,
    IReadOnlyList<CalendarTextValue>? StyledDescriptions = null,
    IReadOnlyList<CalendarNamedUri>? Images = null,
    IReadOnlyList<CalendarNamedUri>? Conferences = null,
    IReadOnlyList<CalendarNamedUri>? Links = null,
    IReadOnlyList<CalendarUriValue>? Concepts = null,
    IReadOnlyList<CalendarUriValue>? StructuredDataUris = null,
    IReadOnlyList<CalendarNamedComponent>? LocationUris = null,
    IReadOnlyList<CalendarNamedComponent>? ResourceUris = null);

/// <summary>Semantic creation request for one To-do.</summary>
public sealed record CalendarTodoCreateRequest(
    CalendarCreateDestination Destination,
    string? Uid,
    CalendarTodoCreateFields Fields);

/// <summary>Mutation state after semantic creation.</summary>
public enum CalendarMutationState
{
    NotAttempted,
    NotCommitted,
    Committed,
    Unknown
}

/// <summary>Closed semantic-create outcomes.</summary>
public enum CalendarEntityCreateCode
{
    Success,
    InvalidInput,
    InvalidCalendarData,
    NotFound,
    Ambiguous,
    OutsideScope,
    UnsupportedCapability,
    RecurrenceUnevaluable,
    OpaqueResource,
    ConcurrencyUnavailable,
    Conflict,
    LimitExhausted,
    PayloadTooLarge,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    FidelityFailure,
    CommittedButUnverified,
    CommittedButConcurrencyUnavailable,
    Indeterminate,
    UpstreamUnavailable,
    UpstreamProtocolError
}

/// <summary>Result of creating one Event or To-do Calendar Object Resource.</summary>
public sealed record CalendarEntityCreateResult(
    CalendarEntityCreateCode Code,
    CalendarMutationState MutationState,
    CalendarResourceSnapshot? Snapshot = null,
    IReadOnlyList<CalendarDescriptor>? AuthorizedCandidates = null,
    CalendarEntityCreateExecutionLimits? Limits = null)
{
    public static CalendarEntityCreateResult Success(CalendarResourceSnapshot snapshot) =>
        new(CalendarEntityCreateCode.Success, CalendarMutationState.Committed, snapshot);
}

/// <summary>Observed bounded-work evidence for semantic creation.</summary>
public sealed record CalendarEntityCreateExecutionLimits(
    int? ResourcesInspected = null,
    int? CalendarCount = null,
    int? ByteCount = null);

/// <summary>One complete conditional-create request at the CalDAV transport boundary.</summary>
public sealed record CalendarResourceCreateRequest(
    string CalendarHref,
    string ResourceHref,
    ReadOnlyMemory<byte> AuthoritativeUtf8);

/// <summary>Observable CalDAV create dispatch result.</summary>
public enum CalendarResourceCreateCode
{
    Dispatched,
    Conflict,
    PossiblyDispatched,
    InvalidInput,
    UnsupportedCapability,
    PayloadTooLarge,
    NotFound,
    UpstreamUnauthorized,
    UpstreamForbidden,
    UpstreamRateLimited,
    UpstreamUnavailable,
    UpstreamProtocolError
}

/// <summary>Low-level result that preserves whether a create may have reached CalDAV.</summary>
public sealed record CalendarResourceCreateResult(
    CalendarResourceCreateCode Code,
    string ResourceHref)
{
    public static CalendarResourceCreateResult Dispatched(string resourceHref) =>
        new(CalendarResourceCreateCode.Dispatched, resourceHref);
}
