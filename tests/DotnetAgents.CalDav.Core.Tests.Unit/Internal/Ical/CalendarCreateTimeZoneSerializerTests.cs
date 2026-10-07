using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarCreateTimeZoneSerializerTests
{
    [Fact]
    public void SerializeEvent_EmitsDeterministicZoneThatResolvesPastAndFarFutureDst()
    {
        var fields = new CalendarEventCreateFields(
            Start: Zoned("1990-01-15T09:00:00"),
            End: Zoned("1990-01-15T10:00:00"),
            RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                Rule: "FREQ=YEARLY",
                RecurrenceDates:
                [
                    new CalendarRecurrenceDateCreate(Value: Zoned("2090-07-15T09:00:00"))
                ]));

        var pastBytes = CalendarEntityCreateSerializer.SerializeEvent(
            "deterministic-zone",
            fields,
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));
        var futureBytes = CalendarEntityCreateSerializer.SerializeEvent(
            "deterministic-zone",
            fields,
            DateTimeOffset.Parse("2099-12-31T23:59:59Z"));

        ExtractTimeZone(pastBytes).ShouldBe(ExtractTimeZone(futureBytes));
        pastBytes.Length.ShouldBeLessThan(4 * 1024 * 1024);
        AssertOccurrence(
            Evaluate(pastBytes, "1990-01-15T13:59:59Z", "1990-01-15T15:00:01Z"),
            "1990-01-15T14:00:00Z",
            "1990-01-15T15:00:00Z",
            "1990-01-15T10:00:00");
        AssertOccurrence(
            Evaluate(pastBytes, "2090-07-15T12:59:59Z", "2090-07-15T14:00:01Z"),
            "2090-07-15T13:00:00Z",
            "2090-07-15T14:00:00Z",
            "2090-07-15T10:00:00");
    }

    [Theory]
    [InlineData("event", "P1D", "2026-03-08T16:00:00Z", "2026-03-08T12:00:00")]
    [InlineData("event", "PT24H", "2026-03-08T17:00:00Z", "2026-03-08T13:00:00")]
    [InlineData("todo", "P1D", "2026-03-08T16:00:00Z", "2026-03-08T12:00:00")]
    [InlineData("todo", "PT24H", "2026-03-08T17:00:00Z", "2026-03-08T13:00:00")]
    public void SerializeEntity_DurationHorizonResolvesSpringDstEnd(
        string entityKind,
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var start = Zoned("2026-03-07T12:00:00");
        var bytes = entityKind == "event"
            ? CalendarEntityCreateSerializer.SerializeEvent(
                "duration-master",
                new CalendarEventCreateFields(
                    Start: start,
                    Duration: duration,
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(Rule: "FREQ=DAILY;COUNT=1")),
                DateTimeOffset.Parse("2000-01-01T00:00:00Z"))
            : CalendarEntityCreateSerializer.SerializeTodo(
                "duration-master",
                new CalendarTodoCreateFields(
                    Start: start,
                    Duration: duration,
                    RecurrenceSet: new CalendarTodoRecurrenceSetCreate(Rule: "FREQ=DAILY;COUNT=1")),
                DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        AssertOccurrence(
            Evaluate(bytes, "2026-03-07T16:59:59Z", "2026-03-08T17:00:01Z"),
            "2026-03-07T17:00:00Z",
            expectedEndUtc,
            expectedEndLocal);
    }

    [Theory]
    [InlineData("event", "P1D", "2026-11-01T17:00:00Z", "2026-11-01T12:00:00")]
    [InlineData("event", "PT24H", "2026-11-01T16:00:00Z", "2026-11-01T11:00:00")]
    [InlineData("todo", "P1D", "2026-11-01T17:00:00Z", "2026-11-01T12:00:00")]
    [InlineData("todo", "PT24H", "2026-11-01T16:00:00Z", "2026-11-01T11:00:00")]
    public void SerializeEntity_CompleteOverrideDurationHorizonResolvesFallDstEnd(
        string entityKind,
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var masterStart = Zoned("2026-10-30T12:00:00");
        var identity = Zoned("2026-10-31T12:00:00");
        var bytes = entityKind == "event"
            ? CalendarEntityCreateSerializer.SerializeEvent(
                "duration-override",
                new CalendarEventCreateFields(
                    Start: masterStart,
                    Duration: "PT1H",
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        Rule: "FREQ=DAILY;COUNT=2",
                        Overrides:
                        [
                            new CalendarEventRecurrenceOverrideCreate(
                                identity,
                                CalendarRecurrenceOverrideStatus.Active,
                                new CalendarEventCreateFields(Start: identity, Duration: duration))
                        ])),
                DateTimeOffset.Parse("2000-01-01T00:00:00Z"))
            : CalendarEntityCreateSerializer.SerializeTodo(
                "duration-override",
                new CalendarTodoCreateFields(
                    Start: masterStart,
                    Duration: "PT1H",
                    RecurrenceSet: new CalendarTodoRecurrenceSetCreate(
                        Rule: "FREQ=DAILY;COUNT=2",
                        Overrides:
                        [
                            new CalendarTodoRecurrenceOverrideCreate(
                                identity,
                                CalendarRecurrenceOverrideStatus.Active,
                                new CalendarTodoCreateFields(Start: identity, Duration: duration))
                        ])),
                DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        AssertOccurrence(
            Evaluate(bytes, "2026-10-31T15:59:59Z", "2026-11-01T17:00:01Z"),
            "2026-10-31T16:00:00Z",
            expectedEndUtc,
            expectedEndLocal);
    }

    [Fact]
    public void SerializeEvent_LastOccurrenceExplicitEndResolvesAcrossTheSpringTransition()
    {
        var bytes = CalendarEntityCreateSerializer.SerializeEvent(
            "explicit-end-horizon",
            new CalendarEventCreateFields(
                Start: Zoned("2026-03-01T01:30:00"),
                End: Zoned("2026-03-01T03:30:00"),
                RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                    Rule: "FREQ=WEEKLY;COUNT=2")),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        AssertOccurrence(
            Evaluate(bytes, "2026-03-08T06:29:59Z", "2026-03-08T08:30:01Z"),
            "2026-03-08T06:30:00Z",
            "2026-03-08T08:30:00Z",
            "2026-03-08T04:30:00");
    }

    [Fact]
    public void SerializeTodo_LastOccurrenceExplicitDueResolvesAcrossTheFallTransition()
    {
        var bytes = CalendarEntityCreateSerializer.SerializeTodo(
            "explicit-due-horizon",
            new CalendarTodoCreateFields(
                Start: Zoned("2026-10-25T00:30:00"),
                Due: Zoned("2026-10-25T03:30:00"),
                RecurrenceSet: new CalendarTodoRecurrenceSetCreate(
                    Rule: "FREQ=WEEKLY;COUNT=2")),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        AssertOccurrence(
            Evaluate(bytes, "2026-11-01T04:29:59Z", "2026-11-01T07:30:01Z"),
            "2026-11-01T04:30:00Z",
            "2026-11-01T07:30:00Z",
            "2026-11-01T02:30:00");
    }

    // Radicale reuses a stored VTIMEZONE for later resources that name the same TZID without one, so the
    // definition must stay correct beyond the values that produced it.
    [Theory]
    [InlineData("Europe/Paris", "2026-03-08T15:00:00", "20310701T100000", "2031-07-01T08:00:00Z")]
    [InlineData("America/New_York", "2026-01-10T09:00:00", "20280701T100000", "2028-07-01T14:00:00Z")]
    public void SerializeEvent_ZoneKeepsTheCurrentRuleBeyondItsValues(
        string timeZoneId,
        string createdLocal,
        string laterLocal,
        string expectedLaterUtc)
    {
        var start = new CalendarTemporalValue(CalendarTemporalKind.ZonedDateTime, createdLocal, timeZoneId);
        var bytes = CalendarEntityCreateSerializer.SerializeEvent(
            "rule-horizon",
            new CalendarEventCreateFields(Start: start, Duration: "PT1H"),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));
        var later = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + Encoding.UTF8.GetString(ExtractTimeZone(bytes))
            + $"BEGIN:VEVENT\r\nUID:later\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID={timeZoneId}:{laterLocal}\r\n"
            + "DURATION:PT1H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        var result = Evaluate(later, "2027-01-01T00:00:00Z", "2032-01-01T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedLaterUtc);
    }

    // Atlantic/South_Georgia has kept one offset since 1890; a value before that still needs its local mean time.
    [Fact]
    public void SerializeForLocalValues_KeepsTheOffsetBeforeAZoneSettledOnItsCurrentOne()
    {
        var definition = CalendarCreateTimeZoneSerializer.SerializeForLocalValues(
            "Atlantic/South_Georgia",
            [new DateTime(1800, 6, 1, 12, 0, 0)]);
        var bytes = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n" + definition
            + "BEGIN:VEVENT\r\nUID:early\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=Atlantic/South_Georgia:18000601T120000\r\n"
            + "DURATION:PT1H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        var result = Evaluate(bytes, "1800-06-01T00:00:00Z", "1800-06-02T00:00:00Z");

        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe("1800-06-01T14:26:08Z");
    }

    [Fact]
    public void SerializeForLocalValues_EveryTzdbZoneAgreesWithTzdbLongAfterItsValue()
    {
        var mismatches = new List<string>();
        foreach (var id in NodaTime.DateTimeZoneProviders.Tzdb.Ids)
        {
            var zone = NodaTime.DateTimeZoneProviders.Tzdb[id];
            var definition = CalendarCreateTimeZoneSerializer.SerializeForLocalValues(
                id,
                [new DateTime(2026, 3, 8, 15, 0, 0)]);
            foreach (var local in new[] { "20400115T120000", "20400715T120000", "20990115T120000", "20990715T120000" })
            {
                var bytes = Encoding.UTF8.GetBytes(
                    "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n" + definition
                    + $"BEGIN:VEVENT\r\nUID:zone\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID={id}:{local}\r\n"
                    + "DURATION:PT1H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
                var expected = zone.AtLeniently(NodaTime.Text.LocalDateTimePattern
                        .CreateWithInvariantCulture("yyyyMMdd'T'HHmmss").Parse(local).Value)
                    .ToInstant().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", null);
                var result = Evaluate(bytes, "2040-01-01T00:00:00Z", "2100-01-01T00:00:00Z");
                var actual = result.Code == CalendarOccurrenceEvaluationCode.Success
                    ? result.Items.Single().Timing.EvaluatedStartUtc!.Value
                    : result.Code.ToString();
                if (actual != expected)
                    mismatches.Add($"{id} {local}: {actual} != {expected}");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    private static CalendarOccurrenceEvaluation Evaluate(byte[] bytes, string from, string to)
    {
        var document = CalendarContentDocument.Parse(bytes);
        var projected = CalendarResourceProjector.Project(document);
        var snapshot = new CalendarResourceSnapshot(
            "https://cal.example/events/",
            "https://cal.example/events/deterministic-zone.ics",
            "\"r1\"",
            bytes,
            projected.Properties,
            projected.Projection,
            projected.Diagnostics);
        return CalendarOccurrenceEvaluator.Evaluate(
            snapshot,
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: snapshot.CalendarHref)),
                DateTimeOffset.Parse(from),
                DateTimeOffset.Parse(to)),
            document,
            CalendarResourceProjector.LoadTypedCalendar(document),
            CancellationToken.None);
    }

    private static void AssertOccurrence(
        CalendarOccurrenceEvaluation result,
        string expectedStartUtc,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.Count.ShouldBe(1);
        result.Items[0].Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedStartUtc);
        result.Items[0].Timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        result.Items[0].Timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    private static byte[] ExtractTimeZone(byte[] bytes)
    {
        var content = Encoding.UTF8.GetString(bytes);
        var start = content.IndexOf("BEGIN:VTIMEZONE\r\n", StringComparison.Ordinal);
        var finish = content.IndexOf("END:VTIMEZONE\r\n", start, StringComparison.Ordinal)
            + "END:VTIMEZONE\r\n".Length;
        return Encoding.UTF8.GetBytes(content[start..finish]);
    }

    private static CalendarTemporalValue Zoned(string value) => new(
        CalendarTemporalKind.ZonedDateTime,
        value,
        "America/New_York");
}
