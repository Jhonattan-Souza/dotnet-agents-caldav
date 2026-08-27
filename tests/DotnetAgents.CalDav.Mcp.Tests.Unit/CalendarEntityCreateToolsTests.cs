using System.Text;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarEntityCreateToolsTests
{
    [Theory]
    [InlineData("protocol", "upstream_protocol_error", "upstream", false)]
    [InlineData("xml", "upstream_protocol_error", "upstream", false)]
    [InlineData("unsupported", "unsupported_capability", "capabilityAndProjection", false)]
    [InlineData("io", "upstream_unavailable", "upstream", true)]
    [InlineData("cancel", "upstream_unavailable", "upstream", true)]
    public async Task CreateEventRawAsync_MapsExpectedSelectionExceptionsWithoutSensitiveDetails(
        string failure,
        string expectedCode,
        string expectedCategory,
        bool expectedRetryable)
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns<CalendarEntityCreateResult>(_ => throw failure switch
            {
                "protocol" => new CalendarDiscoveryProtocolException("secret protocol response"),
                "xml" => new XmlException("secret malformed response"),
                "unsupported" => new CalendarDiscoveryUnsupportedCapabilityException("secret unsupported response"),
                "io" => new IOException("secret transport response"),
                _ => new OperationCanceledException("secret upstream cancellation")
            });
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("category").GetString().ShouldBe(expectedCategory);
        structured.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        structured.GetProperty("retryable").GetBoolean().ShouldBe(expectedRetryable);
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Fact]
    public async Task CreateEventRawAsync_MapsFrozenInputAndReturnsRefetchedSnapshot()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEventCreateRequest? observed = null;
        service.CreateEventAsync(Arg.Do<CalendarEventCreateRequest>(request => observed = request), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(EventSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"selected","calendar":{"by":"name","name":"Events"}},"entity":{"kind":"event","uid":"event-1","fields":{"summary":"Plan","start":{"kind":"utcDateTime","value":"2026-08-17T13:00:00Z"},"structuredData":{"attendees":[{"uri":"urn:uuid:guest","parameters":[]}]}}}}
            """);

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        observed!.Destination.Calendar!.Name.ShouldBe("Events");
        observed.Fields.StructuredData!.Attendees!.Single().Uri.ShouldBe("urn:uuid:guest");
    }

    [Fact]
    public async Task CreateEventRawAsync_MapsEveryFrozenFirstClassAndStructuredFieldWithoutDroppingData()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEventCreateRequest? observed = null;
        service.CreateEventAsync(
                Arg.Do<CalendarEventCreateRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(EventSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {
              "destination":{"mode":"selected","calendar":{"by":"href","href":"https://cal.example/events/"}},
              "entity":{"kind":"event","uid":"event-1","fields":{
                "summary":"Plan","description":"Details",
                "start":{"kind":"zonedDateTime","value":"2026-08-17T13:00:00","timeZoneId":"Europe/Lisbon"},
                "end":{"kind":"zonedDateTime","value":"2026-08-17T14:00:00","timeZoneId":"Europe/Lisbon"},
                "location":"Room","geo":{"latitude":40.1,"longitude":-8.2},
                "status":"confirmed","transparency":"opaque","classification":"private","priority":5,
                "categories":["one","two"],"url":"https://example.test/event",
                "structuredData":{
                  "organizer":{"uri":"urn:uuid:owner","label":"Owner","parameters":[{"name":"X-ONE","values":["a","b"]}]},
                  "attendees":[{"uri":"urn:uuid:guest","commonName":"Guest","role":"required","partStat":"NEEDS-ACTION","cutype":"INDIVIDUAL","rsvp":true,"delegatedTo":["urn:uuid:delegate"],"delegatedFrom":["urn:uuid:source"],"sentBy":"urn:uuid:sender","directory":"https://example.test/directory","parameters":[{"name":"X-TWO","values":["c"]}]}],
                  "participants":[{"uid":{"value":"speaker-1","parameters":[]},"participantType":{"value":"speaker","parameters":[]},"calendarAddress":{"uri":"urn:uuid:speaker","parameters":[{"name":"X-PART","values":["one"]}]},"summary":{"value":"Speaker","parameters":[]},"structuredDataUris":[{"uri":"https://example.test/speaker.vcf","parameters":[{"name":"FMTTYPE","values":["text/vcard"]}]}]}],
                  "contacts":[{"value":"Desk","parameters":[]}],
                  "resources":[{"value":"Projector","parameters":[]}],
                  "relatedTo":[{"value":"parent","relationType":"PARENT","parameters":[]}],
                  "requestStatuses":[{"code":"2.0","description":"Success","exceptionData":"none","parameters":[]}],
                  "alarms":[{"action":{"value":"display","parameters":[]},"trigger":{"value":"-PT15M","parameters":[]},"description":{"value":"Reminder","parameters":[]},"repeat":{"value":2,"parameters":[]},"duration":{"value":"PT5M","parameters":[]}},{"action":{"value":"email","parameters":[]},"trigger":{"value":"-PT30M","parameters":[]},"description":{"value":"Body","parameters":[]},"summary":{"value":"Subject","parameters":[]},"attendees":[{"uri":"mailto:recipient@example.test","parameters":[]}],"attachments":[{"uri":"https://example.test/agenda","parameters":[]}]}],
                  "attachments":[{"uri":"https://example.test/a","label":"A","parameters":[]}],
                  "comments":[{"value":"Comment","parameters":[]}],
                  "styledDescriptions":[{"value":"<b>Styled</b>","parameters":[]}],
                  "images":[{"uri":"https://example.test/image","parameters":[]}],
                  "conferences":[{"uri":"https://example.test/meet","parameters":[]}],
                  "links":[{"uri":"https://example.test/link","parameters":[]}],
                  "concepts":[{"uri":"https://example.test/concepts/one","parameters":[]}],
                  "structuredDataUris":[{"uri":"https://example.test/event.json","parameters":[{"name":"SCHEMA","values":["https://schema.org/Event"]}]}],
                  "locationUris":[{"uid":"location-1","parameters":[]}],
                  "resourceUris":[{"uid":"resource-1","parameters":[]}]
                }
              }}
            }
            """);

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed!.Destination.Calendar!.Href.ShouldBe("https://cal.example/events/");
        observed.Fields.Geo.ShouldBe(new CalendarGeo(40.1, -8.2));
        observed.Fields.Categories.ShouldBe(["one", "two"]);
        var structured = observed.Fields.StructuredData!;
        structured.Organizer!.Parameters.Single().Values.ShouldBe(["a", "b"]);
        var attendee = structured.Attendees!.Single();
        attendee.Rsvp.ShouldBe(true);
        attendee.DelegatedTo.ShouldBe(["urn:uuid:delegate"]);
        attendee.DelegatedFrom.ShouldBe(["urn:uuid:source"]);
        var participant = structured.Participants!.Single();
        participant.Uid.Value.ShouldBe("speaker-1");
        participant.ParticipantType.Value.ShouldBe("speaker");
        participant.CalendarAddress!.Uri.ShouldBe("urn:uuid:speaker");
        participant.StructuredDataUris!.Single().Uri.ShouldBe("https://example.test/speaker.vcf");
        participant.Summary!.Value.ShouldBe("Speaker");
        structured.Contacts!.Single().Value.ShouldBe("Desk");
        structured.Resources!.Single().Value.ShouldBe("Projector");
        structured.RelatedTo!.Single().RelationType.ShouldBe("PARENT");
        structured.RequestStatuses!.Single().ExceptionData.ShouldBe("none");
        structured.Alarms![0].Repeat!.Value.ShouldBe(2);
        structured.Alarms[0].Trigger.Value.ShouldBe("-PT15M");
        structured.Alarms[1].Summary!.Value.ShouldBe("Subject");
        structured.Alarms[1].Attendees!.Single().Uri.ShouldBe("mailto:recipient@example.test");
        structured.Alarms[1].Attachments!.Single().Uri.ShouldBe("https://example.test/agenda");
        structured.Attachments!.Single().Label.ShouldBe("A");
        structured.Comments!.Single().Value.ShouldBe("Comment");
        structured.StyledDescriptions!.Single().Value.ShouldBe("<b>Styled</b>");
        structured.Images!.Single().Uri.ShouldBe("https://example.test/image");
        structured.Conferences!.Single().Uri.ShouldBe("https://example.test/meet");
        structured.Links!.Single().Uri.ShouldBe("https://example.test/link");
        structured.Concepts!.Single().Uri.ShouldBe("https://example.test/concepts/one");
        structured.StructuredDataUris!.Single().Uri.ShouldBe("https://example.test/event.json");
        structured.LocationUris!.Single().Uid.ShouldBe("location-1");
        structured.ResourceUris!.Single().Uid.ShouldBe("resource-1");
    }

    [Theory]
    [InlineData("event")]
    [InlineData("todo")]
    public async Task CreateRawAsync_AcceptsParameterizedParticipantTextUriAndAlarmTriggerShapes(string kind)
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEventCreateRequest? observedEvent = null;
        CalendarTodoCreateRequest? observedTodo = null;
        service.CreateEventAsync(
                Arg.Do<CalendarEventCreateRequest>(request => observedEvent = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(EventSnapshot()));
        service.CreateTodoAsync(
                Arg.Do<CalendarTodoCreateRequest>(request => observedTodo = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var temporal = kind == "event"
            ? ",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}"
            : string.Empty;
        var json = """
            {"destination":{"mode":"default"},"entity":{"kind":"__KIND__","fields":{
              "structuredData":{
                "participants":[{
                  "uid":{"value":"speaker-1","parameters":[]},
                  "participantType":{"value":"speaker","parameters":[]},
                  "description":{"value":"Biography","parameters":[{"name":"LANGUAGE","values":["pt-BR"]}]},
                  "summary":{"value":"Speaker","parameters":[{"name":"LANGUAGE","values":["en"]}]},
                  "url":{"uri":"https://example.test/speaker","parameters":[{"name":"X-LINK","values":["profile"]}]}
                }],
                "alarms":[{
                  "action":{"value":"display","parameters":[]},
                  "trigger":{"value":"-PT15M","parameters":[{"name":"RELATED","values":["END"]}]},
                  "description":{"value":"Reminder","parameters":[]}
                }]
              }__TEMPORAL__
            }}}
            """
            .Replace("__KIND__", kind, StringComparison.Ordinal)
            .Replace("__TEMPORAL__", temporal, StringComparison.Ordinal);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = kind == "event"
            ? await sut.CreateEventRawAsync(arguments, CancellationToken.None)
            : await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        if (kind == "event")
            await service.Received(1).CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>());
        else
            await service.Received(1).CreateTodoAsync(Arg.Any<CalendarTodoCreateRequest>(), Arg.Any<CancellationToken>());
        var structured = kind == "event"
            ? observedEvent!.Fields.StructuredData!
            : observedTodo!.Fields.StructuredData!;
        var participant = structured.Participants!.Single();
        participant.Description!.Parameters.Single().Values.ShouldBe(["pt-BR"]);
        participant.Summary!.Parameters.Single().Values.ShouldBe(["en"]);
        participant.Url!.Parameters.Single().Values.ShouldBe(["profile"]);
        structured.Alarms!.Single().Trigger.Parameters.Single().Values.ShouldBe(["END"]);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("todo")]
    public async Task CreateRawAsync_AcceptsEveryParameterizedParticipantRelationRequestStatusAndAlarmProperty(
        string kind)
    {
        var service = Substitute.For<ICalendarService>();
        CalendarEventCreateRequest? observedEvent = null;
        CalendarTodoCreateRequest? observedTodo = null;
        service.CreateEventAsync(
                Arg.Do<CalendarEventCreateRequest>(request => observedEvent = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(EventSnapshot()));
        service.CreateTodoAsync(
                Arg.Do<CalendarTodoCreateRequest>(request => observedTodo = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var temporal = kind == "event"
            ? ",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}"
            : string.Empty;
        var json = """
            {"destination":{"mode":"default"},"entity":{"kind":"__KIND__","fields":{
              "structuredData":{
                "participants":[{
                  "uid":{"value":"speaker-1","parameters":[{"name":"X-UID","values":["one"]}]},
                  "participantType":{"value":"speaker","parameters":[{"name":"X-TYPE","values":["one"]}]},
                  "created":{"value":{"kind":"utcDateTime","value":"2026-08-16T10:00:00Z"},"parameters":[{"name":"X-CREATED","values":["one"]}]},
                  "timestamp":{"value":{"kind":"utcDateTime","value":"2026-08-16T10:01:00Z"},"parameters":[{"name":"X-DTSTAMP","values":["one"]}]},
                  "geo":{"value":{"latitude":40.1,"longitude":-8.2},"parameters":[{"name":"X-GEO","values":["one"]}]},
                  "lastModified":{"value":{"kind":"utcDateTime","value":"2026-08-16T10:02:00Z"},"parameters":[{"name":"X-MODIFIED","values":["one"]}]},
                  "priority":{"value":5,"parameters":[{"name":"X-PRIORITY","values":["one"]}]},
                  "sequence":{"value":2,"parameters":[{"name":"X-SEQUENCE","values":["one"]}]},
                  "status":{"value":"confirmed","parameters":[{"name":"X-STATUS","values":["one"]}]},
                  "categories":{"value":["one","two"],"parameters":[{"name":"LANGUAGE","values":["en"]}]},
                  "relatedTo":[{"value":"parent","relationType":"PARENT","parameters":[{"name":"X-REL","values":["one"]}]}],
                  "requestStatuses":[{"code":"2.0","description":"Success","exceptionData":"none","parameters":[{"name":"LANGUAGE","values":["en"]}]}]
                }],
                "relatedTo":[{"value":"root","relationType":"PARENT","parameters":[{"name":"X-REL","values":["root"]}]}],
                "requestStatuses":[{"code":"2.0","description":"Success","parameters":[{"name":"LANGUAGE","values":["pt-BR"]}]}],
                "alarms":[{
                  "action":{"value":"email","parameters":[{"name":"X-ACTION","values":["one"]}]},
                  "trigger":{"value":"-PT15M","parameters":[{"name":"RELATED","values":["START"]}]},
                  "description":{"value":"Body","parameters":[{"name":"LANGUAGE","values":["en"]}]},
                  "repeat":{"value":2,"parameters":[{"name":"X-REPEAT","values":["one"]}]},
                  "duration":{"value":"PT5M","parameters":[{"name":"X-DURATION","values":["one"]}]},
                  "summary":{"value":"Subject","parameters":[{"name":"LANGUAGE","values":["en"]}]},
                  "attendees":[{"uri":"mailto:recipient@example.test","parameters":[]}],
                  "uid":{"value":"alarm-1","parameters":[]},
                  "acknowledged":{"value":{"kind":"utcDateTime","value":"2026-08-16T10:03:00Z"},"parameters":[]},
                  "proximity":{"value":"arrive","parameters":[]},
                  "relatedTo":[{"value":"parent-alarm","relationType":"PARENT","parameters":[]}],
                  "proximityLocations":[{"uid":"door-123","name":{"value":"Door","parameters":[]},"parameters":[],"description":{"value":"North entrance","parameters":[]},"geo":{"value":{"latitude":40.1,"longitude":-8.2},"parameters":[]},"componentTypes":{"value":["entrance","north"],"parameters":[]},"url":{"uri":"geo:40.1,-8.2","parameters":[]},"relatedTo":[],"concepts":[],"links":[],"structuredDataUris":[]}]
                }],
                "locationUris":[{"uid":"room-123","name":{"value":"Room","parameters":[{"name":"LANGUAGE","values":["en"]}]},"parameters":[],"description":{"value":"Conference room","parameters":[]},"geo":{"value":{"latitude":40.2,"longitude":-8.3},"parameters":[]},"componentTypes":{"value":["meeting-room","accessible"],"parameters":[]},"url":{"uri":"https://example.test/room","parameters":[]},"relatedTo":[],"concepts":[],"links":[],"structuredDataUris":[]}]
              }__TEMPORAL__
            }}}
            """
            .Replace("__KIND__", kind, StringComparison.Ordinal)
            .Replace("__TEMPORAL__", temporal, StringComparison.Ordinal);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = kind == "event"
            ? await sut.CreateEventRawAsync(arguments, CancellationToken.None)
            : await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        if (kind == "event")
            await service.Received(1).CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>());
        else
            await service.Received(1).CreateTodoAsync(Arg.Any<CalendarTodoCreateRequest>(), Arg.Any<CancellationToken>());
        var structured = kind == "event"
            ? observedEvent!.Fields.StructuredData!
            : observedTodo!.Fields.StructuredData!;
        var participant = structured.Participants!.Single();
        participant.Uid.Parameters.Single().Name.ShouldBe("X-UID");
        participant.ParticipantType.Parameters.Single().Name.ShouldBe("X-TYPE");
        participant.Created!.Parameters.Single().Name.ShouldBe("X-CREATED");
        participant.Timestamp!.Parameters.Single().Name.ShouldBe("X-DTSTAMP");
        participant.Geo!.Parameters.Single().Name.ShouldBe("X-GEO");
        participant.LastModified!.Parameters.Single().Name.ShouldBe("X-MODIFIED");
        participant.Priority!.Parameters.Single().Name.ShouldBe("X-PRIORITY");
        participant.Sequence!.Parameters.Single().Name.ShouldBe("X-SEQUENCE");
        participant.Status!.Parameters.Single().Name.ShouldBe("X-STATUS");
        participant.Categories!.Parameters.Single().Name.ShouldBe("LANGUAGE");
        participant.RelatedTo!.Single().Parameters!.Single().Name.ShouldBe("X-REL");
        participant.RequestStatuses!.Single().Parameters!.Single().Name.ShouldBe("LANGUAGE");
        structured.RelatedTo!.Single().Parameters!.Single().Values.ShouldBe(["root"]);
        structured.RequestStatuses!.Single().Parameters!.Single().Values.ShouldBe(["pt-BR"]);
        var alarm = structured.Alarms!.Single();
        alarm.Action.Parameters.Single().Name.ShouldBe("X-ACTION");
        alarm.Trigger.Parameters.Single().Name.ShouldBe("RELATED");
        alarm.Description!.Parameters.Single().Name.ShouldBe("LANGUAGE");
        alarm.Repeat!.Parameters.Single().Name.ShouldBe("X-REPEAT");
        alarm.Duration!.Parameters.Single().Name.ShouldBe("X-DURATION");
        alarm.Summary!.Parameters.Single().Name.ShouldBe("LANGUAGE");
        alarm.Uid!.Value.ShouldBe("alarm-1");
        alarm.Acknowledged!.Value.Kind.ShouldBe(CalendarTemporalKind.UtcDateTime);
        alarm.Acknowledged.Value.Value.ShouldBe("2026-08-16T10:03:00Z");
        alarm.Proximity!.Value.ShouldBe("arrive");
        alarm.RelatedTo!.Single().Value.ShouldBe("parent-alarm");
        alarm.ProximityLocations!.Single().Description!.Value.ShouldBe("North entrance");
        structured.LocationUris!.Single().Name!.Parameters.Single().Name.ShouldBe("LANGUAGE");
        structured.LocationUris!.Single().ComponentTypes!.Value.ShouldBe(["meeting-room", "accessible"]);
    }

    [Fact]
    public async Task CreateTodoRawAsync_MapsFrozenInputToTodoService()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarTodoCreateRequest? observed = null;
        service.CreateTodoAsync(Arg.Do<CalendarTodoCreateRequest>(request => observed = request), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{"summary":"Do it","due":{"kind":"date","value":"2026-08-18"}}}}
            """);

        var result = await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed!.Fields.Summary.ShouldBe("Do it");
        observed.Fields.Due!.Kind.ShouldBe(CalendarTemporalKind.Date);
    }

    [Fact]
    public async Task CreateTodoRawAsync_MapsRdateOnlyExclusionAndCompleteOverride()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarTodoCreateRequest? observed = null;
        service.CreateTodoAsync(Arg.Do<CalendarTodoCreateRequest>(request => observed = request), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"default"},"entity":{"kind":"todo","fields":{
              "summary":"Review","start":{"kind":"date","value":"2026-08-17"},
              "recurrenceSet":{"rdates":[{"kind":"date","value":"2026-08-24"}],
                "exdates":[{"kind":"date","value":"2026-08-31"}],
                "overrides":[{"recurrenceIdentity":{"value":{"kind":"date","value":"2026-08-24"}},
                  "status":"cancelled","fields":{"summary":"Skip review","start":{"kind":"date","value":"2026-08-24"}}}]}}}}
            """);

        var result = await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        var recurrence = observed!.Fields.RecurrenceSet.ShouldNotBeNull();
        recurrence.Rule.ShouldBeNull();
        recurrence.RecurrenceDates.ShouldHaveSingleItem().Value!.Value.ShouldBe("2026-08-24");
        recurrence.ExceptionDates.ShouldHaveSingleItem().Value.ShouldBe("2026-08-31");
        var recurrenceOverride = recurrence.Overrides.ShouldHaveSingleItem();
        recurrenceOverride.Status.ShouldBe(CalendarRecurrenceOverrideStatus.Cancelled);
        recurrenceOverride.Fields.Summary.ShouldBe("Skip review");
        recurrenceOverride.Fields.RecurrenceSet.ShouldBeNull();
    }

    [Fact]
    public async Task CreateTodoRawAsync_MapsAllTodoFieldsIncludingFloatingTemporalAndStructuredData()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarTodoCreateRequest? observed = null;
        service.CreateTodoAsync(
                Arg.Do<CalendarTodoCreateRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"selected","calendar":{"by":"name","name":"Todos"}},"entity":{"kind":"todo","uid":"todo-1","fields":{"summary":"Do it","description":"All fields","start":{"kind":"floatingDateTime","value":"2026-08-17T13:00:00"},"due":{"kind":"floatingDateTime","value":"2026-08-17T14:00:00"},"status":"needs-action","priority":4,"categories":["work"],"structuredData":{"organizer":{"uri":"urn:uuid:owner","parameters":[]},"alarms":[],"comments":[]}}}}
            """);

        var result = await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed!.Destination.Calendar!.Name.ShouldBe("Todos");
        observed.Uid.ShouldBe("todo-1");
        observed.Fields.Start!.Kind.ShouldBe(CalendarTemporalKind.FloatingDateTime);
        observed.Fields.Due!.Kind.ShouldBe(CalendarTemporalKind.FloatingDateTime);
        observed.Fields.Status.ShouldBe("needs-action");
        observed.Fields.Priority.ShouldBe(4);
        observed.Fields.Categories.ShouldBe(["work"]);
        observed.Fields.StructuredData!.Organizer!.Uri.ShouldBe("urn:uuid:owner");
        observed.Fields.StructuredData.Alarms.ShouldBeEmpty();
        observed.Fields.StructuredData.Comments.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateEventRawAsync_MapsDiscoveryLimitWithSafeLimitEvidence()
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarEntityCreateResult>>(_ => throw new CalendarDiscoveryLimitException(257));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        structured.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(257);
    }

    [Fact]
    public async Task CreateEventRawAsync_OnlyDefiniteRateLimitBeforeCommitIsRetryable()
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(
                CalendarEntityCreateCode.UpstreamRateLimited,
                CalendarMutationState.NotCommitted));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_rate_limited");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_committed");
    }

    [Fact]
    public async Task CreateEventRawAsync_DirectPutNotFoundKeepsExecutionPhaseAndNotCommittedState()
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(
                CalendarEntityCreateCode.NotFound,
                CalendarMutationState.NotCommitted));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("not_found");
        structured.GetProperty("phase").GetString().ShouldBe("execution");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_committed");
    }

    [Theory]
    [InlineData(CalendarEntityCreateCode.InvalidInput, "invalid_input", "input", "schemaLexicalDiscriminator")]
    [InlineData(CalendarEntityCreateCode.InvalidCalendarData, "invalid_calendar_data", "input", "completeResourceSemantics")]
    [InlineData(CalendarEntityCreateCode.NotFound, "not_found", "selection", "selectionDiscoveryCapability")]
    [InlineData(CalendarEntityCreateCode.Ambiguous, "ambiguous", "selection", "selectionDiscoveryCapability")]
    [InlineData(CalendarEntityCreateCode.OutsideScope, "outside_scope", "selection", "originScopeAuthorization")]
    [InlineData(CalendarEntityCreateCode.UnsupportedCapability, "unsupported_capability", "capabilityAndProjection", "selectionDiscoveryCapability")]
    [InlineData(CalendarEntityCreateCode.RecurrenceUnevaluable, "recurrence_unevaluable", "capabilityAndProjection", "completeResourceSemantics")]
    [InlineData(CalendarEntityCreateCode.OpaqueResource, "opaque_resource", "capabilityAndProjection", "targetRevision")]
    [InlineData(CalendarEntityCreateCode.ConcurrencyUnavailable, "concurrency_unavailable", "state", "targetRevision")]
    [InlineData(CalendarEntityCreateCode.Conflict, "conflict", "state", "execution")]
    [InlineData(CalendarEntityCreateCode.LimitExhausted, "limit_exhausted", "limitsAndAdmission", "execution")]
    [InlineData(CalendarEntityCreateCode.PayloadTooLarge, "payload_too_large", "limitsAndAdmission", "admissionAndPayload")]
    [InlineData(CalendarEntityCreateCode.UpstreamUnauthorized, "upstream_unauthorized", "upstream", "execution")]
    [InlineData(CalendarEntityCreateCode.UpstreamForbidden, "upstream_forbidden", "upstream", "execution")]
    [InlineData(CalendarEntityCreateCode.UpstreamRateLimited, "upstream_rate_limited", "upstream", "execution")]
    [InlineData(CalendarEntityCreateCode.UpstreamUnavailable, "upstream_unavailable", "upstream", "selectionDiscoveryCapability")]
    [InlineData(CalendarEntityCreateCode.UpstreamProtocolError, "upstream_protocol_error", "upstream", "selectionDiscoveryCapability")]
    [InlineData(CalendarEntityCreateCode.FidelityFailure, "fidelity_failure", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarEntityCreateCode.CommittedButUnverified, "committed_but_unverified", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarEntityCreateCode.CommittedButConcurrencyUnavailable, "committed_but_concurrency_unavailable", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarEntityCreateCode.Indeterminate, "indeterminate", "postWriteTruth", "postWriteVerificationOrReconciliation")]
    public async Task CreateEventRawAsync_MapsEveryClosedFailureOutcome(
        CalendarEntityCreateCode domainCode,
        string expectedCode,
        string expectedCategory,
        string expectedPhase)
    {
        var service = Substitute.For<ICalendarService>();
        var mutationState = domainCode is CalendarEntityCreateCode.FidelityFailure
            or CalendarEntityCreateCode.CommittedButUnverified
            or CalendarEntityCreateCode.CommittedButConcurrencyUnavailable
                ? CalendarMutationState.Committed
                : domainCode == CalendarEntityCreateCode.Indeterminate
                    ? CalendarMutationState.Unknown
                    : CalendarMutationState.NotAttempted;
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(domainCode, mutationState));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("category").GetString().ShouldBe(expectedCategory);
        structured.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        structured.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        structured.GetProperty("mutationState").GetString().ShouldBe(mutationState switch
        {
            CalendarMutationState.Committed => "committed",
            CalendarMutationState.Unknown => "unknown",
            _ => "not_attempted"
        });
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task CreateEventRawAsync_MapsExpectedPreDispatchExceptionsToRetryableUnavailable(string failure)
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarEntityCreateResult>>(_ => failure == "http"
                ? throw new HttpRequestException("safe test transport failure")
                : throw new TimeoutException("safe test timeout"));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
        structured.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, "committed_but_unverified", "committed")]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, "indeterminate", "unknown")]
    public async Task CreateEventRawAsync_PostWriteReadFailurePreservesMutationTruth(
        CalendarResourceCreateCode transportCode,
        string expectedCode,
        string expectedMutationState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/event-1.ics";
        var client = Substitute.For<ICalendarClient>();
        using var serviceHost = CalendarServiceTestHost.Create(
            client,
            options =>
            {
                options.BaseUrl = "https://cal.example";
                options.DefaultEventCalendarName = "Events";
            });
        var service = serviceHost.Service;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns<CalendarResourceRead>(_ => throw new HttpRequestException("safe verification failure"));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("phase").GetString().ShouldBe("postWriteVerificationOrReconciliation");
        structured.GetProperty("mutationState").GetString().ShouldBe(expectedMutationState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventRawAsync_MapsSixtySecondExecutionDeadlineWithoutSleeping()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var service = Substitute.For<ICalendarService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var cancellationToken = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<CalendarEntityCreateResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            entered.TrySetResult();
            return await completion.Task;
        });
        var sut = new CalendarEntityCreateTools(service, timeProvider);
        var pending = sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);
        await entered.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(60));
        var result = await pending;

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("phase").GetString().ShouldBe("execution");
        structured.GetProperty("mutationState").GetString().ShouldBe("unknown");
    }

    [Fact]
    public async Task CreateEventRawAsync_ReplacesOversizedCommittedResultWithBoundedCommittedError()
    {
        var service = Substitute.For<ICalendarService>();
        var oversized = OversizedEventSnapshot();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(oversized));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("payload_too_large");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        JsonSerializer.SerializeToUtf8Bytes(structured).Length.ShouldBeLessThan(64 * 1024);
    }

    [Fact]
    public async Task CreateEventRawAsync_ReplacesOversizedIndeterminateResultWithoutChangingMutationTruth()
    {
        var service = Substitute.For<ICalendarService>();
        var oversized = OversizedEventSnapshot();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(
                CalendarEntityCreateCode.Indeterminate,
                CalendarMutationState.Unknown,
                oversized));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("payload_too_large");
        structured.GetProperty("mutationState").GetString().ShouldBe("unknown");
        structured.TryGetProperty("currentSnapshot", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CreateEventRawAsync_MapsSafeCandidatesCurrentSnapshotAndLimitEvidence()
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(
                CalendarEntityCreateCode.FidelityFailure,
                CalendarMutationState.Committed,
                EventSnapshot(),
                [new CalendarDescriptor
                {
                    Href = "https://cal.example/events/",
                    DisplayName = "Events",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.NotAdvertised
                }],
                new CalendarEntityCreateExecutionLimits(ResourcesInspected: 5_001, ByteCount: 4_194_305)));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);

        var result = await sut.CreateEventRawAsync(ValidEventArguments(), CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("currentSnapshot").GetProperty("resourceRevision")
            .GetProperty("entityTag").GetString().ShouldBe("\"r1\"");
        structured.GetProperty("authorizedCandidates").GetArrayLength().ShouldBe(1);
        structured.GetProperty("limits").GetProperty("resourcesInspected").GetInt32().ShouldBe(5_001);
        structured.GetProperty("limits").GetProperty("byteCount").GetInt32().ShouldBe(4_194_305);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"destination\":null,\"entity\":{}}")]
    [InlineData("{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"fields\":{},\"unknown\":true}}")]
    public async Task CreateEventRawAsync_RejectsMissingNullUnknownAndRecurringInputBeforeService(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("event")]
    [InlineData("todo")]
    public async Task CreateRawAsync_RejectsLegacyOverrideKindShapeBeforeService(string kind)
    {
        const string calendarHref = "https://cal.example/entities/";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Entities",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        using var serviceHost = CalendarServiceTestHost.Create(
            client,
            options =>
            {
                options.BaseUrl = "https://cal.example";
                options.DefaultEventCalendarName = "Entities";
                options.DefaultTodoCalendarName = "Entities";
            });
        var service = serviceHost.Service;
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var eventStart = kind == "event"
            ? "\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"},"
            : string.Empty;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\""
            + kind
            + "\",\"fields\":{" + eventStart
            + "\"recurrenceSet\":{\"rrule\":\"FREQ=DAILY;COUNT=2\","
            + "\"rdates\":[{\"kind\":\"date\",\"value\":\"2026-08-18\"}],"
            + "\"exdates\":[{\"kind\":\"utcDateTime\",\"value\":\"2026-08-19T13:00:00Z\"}],"
            + "\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"}},"
            + "\"entityKind\":\"" + kind + "\",\"range\":\"this-and-future\",\"status\":\"active\","
            + "\"movedStart\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T14:00:00Z\"},"
            + "\"movedEnd\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T15:00:00Z\"}}]}}}}");

        var result = kind == "event"
            ? await sut.CreateEventRawAsync(arguments, CancellationToken.None)
            : await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("invalid_input");
        structured.GetProperty("phase").GetString().ShouldBe("schemaLexicalDiscriminator");
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventRawAsync_CreatesTypedRecurrenceAndReturnsVerifiedSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/recurring-event.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(resourceHref, "\"recurring-r1\"", dispatched!.AuthoritativeUtf8));
        using var serviceHost = CalendarServiceTestHost.Create(
            client,
            options =>
            {
                options.BaseUrl = "https://cal.example";
                options.DefaultEventCalendarName = "Events";
            });
        var service = serviceHost.Service;
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"uid\":\"recurring-event\","
            + "\"fields\":{\"summary\":\"Planning\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"},"
            + "\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T14:00:00Z\"},"
            + "\"recurrenceSet\":{\"rrule\":\"FREQ=DAILY;COUNT=3\","
            + "\"rdates\":[{\"kind\":\"utcDateTime\",\"value\":\"2026-08-20T13:00:00Z\"}],"
            + "\"exdates\":[{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"}]}}}}");

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        var content = Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span);
        content.ShouldContain("RRULE:FREQ=DAILY;COUNT=3\r\n");
        content.ShouldContain("RDATE:20260820T130000Z\r\n");
        content.ShouldContain("EXDATE:20260818T130000Z\r\n");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("PT2H")]
    [InlineData("P1W")]
    [InlineData("+P1D")]
    public async Task CreateEventRawAsync_RdatePeriodReturnsUnsupportedBeforeUidLookupOrPut(string duration)
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        using var serviceHost = CalendarServiceTestHost.Create(
            client,
            options =>
            {
                options.BaseUrl = "https://cal.example";
                options.DefaultEventCalendarName = "Events";
            });
        var service = serviceHost.Service;
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"uid\":\"period-event\","
            + "\"fields\":{\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"},"
            + "\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\","
            + "\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-20T13:00:00Z\"},"
            + "\"duration\":\"$DURATION$\"}]}}}}"
                .Replace("$DURATION$", duration, StringComparison.Ordinal));

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("event", "FREQ=WEEKLY;BYDAY=TU;COUNT=2")]
    [InlineData("todo", "FREQ=WEEKLY;BYDAY=TU;COUNT=2")]
    [InlineData("event", "FREQ=DAILY;COUNT=0")]
    [InlineData("todo", "FREQ=DAILY;COUNT=0")]
    [InlineData("event", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30;COUNT=1")]
    [InlineData("todo", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30;COUNT=1")]
    public async Task CreateRawAsync_UnevaluableRuleFailsBeforeDiscovery(
        string entityKind,
        string rule)
    {
        var client = Substitute.For<ICalendarClient>();
        using var serviceHost = CalendarServiceTestHost.Create(
            client,
            options =>
            {
                options.BaseUrl = "https://cal.example";
                options.DefaultEventCalendarName = "Events";
                options.DefaultTodoCalendarName = "Todos";
            });
        var service = serviceHost.Service;
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"default"},"entity":{"kind":"$KIND$","fields":{"start":{"kind":"utcDateTime","value":"2026-08-17T13:00:00Z"},"recurrenceSet":{"rrule":"$RULE$"}}}}
            """
            .Replace("$KIND$", entityKind, StringComparison.Ordinal)
            .Replace("$RULE$", rule, StringComparison.Ordinal));

        var result = entityKind == "event"
            ? await sut.CreateEventRawAsync(arguments, CancellationToken.None)
            : await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("recurrence_unevaluable");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventRawAsync_SerializesCompleteSameUidOverride()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/override-event.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(resourceHref, "\"override-r1\"", dispatched!.AuthoritativeUtf8));
        using var serviceHost = CalendarServiceTestHost.Create(client, options =>
        {
            options.BaseUrl = "https://cal.example";
            options.DefaultEventCalendarName = "Events";
        });
        var service = serviceHost.Service;
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"uid\":\"override-event\","
            + "\"fields\":{\"summary\":\"Master\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"},"
            + "\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T14:00:00Z\"},"
            + "\"recurrenceSet\":{\"overrides\":[{"
            + "\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"}},"
            + "\"status\":\"active\",\"fields\":{\"summary\":\"Moved\","
            + "\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T15:00:00Z\"},"
            + "\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T16:00:00Z\"}}}]}}}}");

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        var content = Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span);
        content.Split("BEGIN:VEVENT\r\n", StringSplitOptions.None).Length.ShouldBe(3);
        content.Split("UID:override-event\r\n", StringSplitOptions.None).Length.ShouldBe(3);
        content.ShouldContain("RECURRENCE-ID:20260818T130000Z\r\n");
        content.ShouldContain("SUMMARY:Moved\r\n");
        content.ShouldContain("DTSTART:20260818T150000Z\r\n");
    }

    [Theory]
    [MemberData(nameof(InvalidFrozenNestedInputs))]
    public async Task CreateEventRawAsync_RejectsMalformedFrozenNestedValuesBeforeService(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().CreateEventAsync(
            Arg.Any<CalendarEventCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("event", "DISPLAY")]
    [InlineData("event", "Display")]
    [InlineData("todo", "EMAIL")]
    public async Task CreateRawAsync_RejectsNonLowercaseAlarmActionBeforeService(string kind, string action)
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(EventSnapshot()));
        service.CreateTodoAsync(Arg.Any<CalendarTodoCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityCreateResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var start = kind == "event"
            ? ",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}"
            : string.Empty;
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\""
            + kind
            + "\",\"fields\":{\"structuredData\":{\"alarms\":[{\"action\":{\"value\":\""
            + action
            + "\",\"parameters\":[]},\"trigger\":{\"value\":\"-PT15M\",\"parameters\":[]},"
            + "\"description\":{\"value\":\"Reminder\",\"parameters\":[]}}]}"
            + start
            + "}}}");

        var result = kind == "event"
            ? await sut.CreateEventRawAsync(arguments, CancellationToken.None)
            : await sut.CreateTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().CreateEventAsync(
            Arg.Any<CalendarEventCreateRequest>(),
            Arg.Any<CancellationToken>());
        await service.DidNotReceive().CreateTodoAsync(
            Arg.Any<CalendarTodoCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CreateEventRawAsync_EnforcesExactArgumentByteBoundary(int extraByte, bool rejected)
    {
        var service = Substitute.For<ICalendarService>();
        service.CreateEventAsync(Arg.Any<CalendarEventCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityCreateResult(
                CalendarEntityCreateCode.InvalidCalendarData,
                CalendarMutationState.NotAttempted));
        var sut = new CalendarEntityCreateTools(service, TimeProvider.System);
        var targetSize = CalendarEntityCreateTools.MaximumArgumentBytes + extraByte;
        var prefix = "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"fields\":{\"summary\":\"";
        const string Suffix = "\"}}}";
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            prefix + new string('x', targetSize - Encoding.UTF8.GetByteCount(prefix + Suffix)) + Suffix);

        var result = await sut.CreateEventRawAsync(arguments, CancellationToken.None);

        JsonSerializer.SerializeToUtf8Bytes(arguments).Length.ShouldBe(targetSize);
        result.StructuredContent!.Value.GetProperty("code").GetString()
            .ShouldBe(rejected ? "payload_too_large" : "invalid_calendar_data");
        if (rejected)
        {
            await service.DidNotReceive().CreateEventAsync(
                Arg.Any<CalendarEventCreateRequest>(),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await service.Received(1).CreateEventAsync(
                Arg.Any<CalendarEventCreateRequest>(),
                Arg.Any<CancellationToken>());
        }
    }

    private static CalendarResourceSnapshot EventSnapshot() => Snapshot(CalendarResourceProjectionKind.Event, "event-1");

    private static CalendarResourceSnapshot OversizedEventSnapshot()
    {
        var snapshot = EventSnapshot();
        var value = new string('x', 3 * 1024 * 1024);
        return snapshot with
        {
            CalendarProperties =
            [
                new CalendarProperty(
                    [new CalendarComponentPathSegment("VCALENDAR", 0), new CalendarComponentPathSegment("VEVENT", 0)],
                    "X-LARGE",
                    [],
                    CalendarPropertyValueType.Unknown,
                    value,
                    $"X-LARGE:{value}\r\n")
            ]
        };
    }

    private static CalendarResourceSnapshot TodoSnapshot() => Snapshot(CalendarResourceProjectionKind.Todo, "todo-1");

    private static CalendarResourceSnapshot Snapshot(CalendarResourceProjectionKind kind, string uid)
    {
        var component = kind == CalendarResourceProjectionKind.Event ? "VEVENT" : "VTODO";
        var bytes = Encoding.UTF8.GetBytes($"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:{component}\r\nUID:{uid}\r\nDTSTAMP:20260816T120000Z\r\nEND:{component}\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/calendar/",
            $"https://cal.example/calendar/{uid}.ics",
            "\"r1\"",
            bytes,
            [],
            new CalendarResourceProjection(kind, uid, kind == CalendarResourceProjectionKind.Event ? "Plan" : "Do it"),
            []);
    }

    private static Dictionary<string, JsonElement> ValidEventArguments() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"start":{"kind":"utcDateTime","value":"2026-08-17T13:00:00Z"}}}}
            """)!;

    public static IEnumerable<object[]> InvalidFrozenNestedInputs()
    {
        static object[] EventFields(string fields) =>
        ["{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"fields\":" + fields + "}}"];

        yield return EventFields("{\"geo\":null}");
        yield return EventFields("{\"geo\":{\"latitude\":1}}");
        yield return EventFields("{\"geo\":{\"latitude\":\"1\",\"longitude\":2}}");
        yield return EventFields("{\"start\":{\"kind\":\"floatingDateTime\",\"value\":5}}");
        yield return EventFields("{\"start\":{\"kind\":\"zonedDateTime\",\"value\":\"2026-08-17T13:00:00\"}}");
        yield return EventFields("{\"structuredData\":null}");
        yield return EventFields("{\"structuredData\":{\"organizer\":{\"uri\":\"urn:uuid:owner\"}}}");
        yield return EventFields("{\"structuredData\":{\"attachments\":{}}}");
        yield return EventFields("{\"structuredData\":{\"attachments\":[{\"uri\":\"urn:uuid:a\",\"parameters\":[{\"name\":\"X-A\",\"values\":[\"ok\",1]}]}]}}");
        yield return EventFields("{\"structuredData\":{\"contacts\":[{\"parameters\":[]}]}}");
        yield return EventFields("{\"structuredData\":{\"relatedTo\":[{\"value\":null}]}}");
        yield return EventFields("{\"structuredData\":{\"requestStatuses\":[{\"code\":\"2.0\"}]}}");
        yield return EventFields("{\"structuredData\":{\"alarms\":[{\"action\":\"display\"}]}}");
        yield return EventFields("{\"structuredData\":{\"alarms\":[{\"action\":\"display\",\"trigger\":\"-PT1M\",\"repeat\":\"two\"}]}}");
        yield return EventFields("{\"structuredData\":{\"attendees\":[{\"uri\":\"urn:uuid:a\",\"rsvp\":\"true\",\"parameters\":[]}]}}");
        yield return EventFields("{\"structuredData\":{\"attendees\":[{\"uri\":\"urn:uuid:a\",\"delegatedTo\":{},\"parameters\":[]}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":{}}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[null]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T14:00:00Z\"},\"duration\":\"PT1H\"}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"date\",\"value\":\"2026-08-18\"},\"duration\":\"P1D\"}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"end\":{\"kind\":\"floatingDateTime\",\"value\":\"2026-08-18T14:00:00\"}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T12:00:00Z\"}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"duration\":\"-PT1H\"}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"rdates\":[{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"duration\":\"P1M\"}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":null},\"status\":\"active\",\"fields\":{}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"date\",\"value\":\"2026-08-18\"}},\"entityKind\":\"event\",\"status\":\"active\",\"fields\":{}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"date\",\"value\":\"2026-08-18\"}},\"range\":\"future\",\"status\":\"active\",\"fields\":{}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"date\",\"value\":\"2026-08-18\"}},\"range\":\"this-and-prior\",\"status\":\"active\",\"fields\":{}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"date\",\"value\":\"2026-08-18\"}},\"status\":\"deleted\",\"fields\":{}}]}}");
        yield return EventFields("{\"recurrenceSet\":{\"overrides\":[{\"recurrenceIdentity\":{\"value\":{\"kind\":\"date\",\"value\":\"2026-08-18\"}},\"status\":\"active\",\"fields\":{\"recurrenceSet\":{\"rrule\":\"FREQ=DAILY\"}}}]}}");
        yield return EventFields("{\"structuredData\":{\"structuredDataUris\":[{\"uri\":\"https://example.test/data\"}]}}");
        yield return EventFields("{\"structuredData\":{\"participants\":[{\"uid\":\"speaker-1\"}]}}");
        yield return EventFields("{\"structuredData\":{\"alarms\":[{\"action\":\"email\",\"trigger\":\"-PT1M\",\"attendees\":[{\"uri\":\"mailto:a@example.test\"}]}]}}");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
