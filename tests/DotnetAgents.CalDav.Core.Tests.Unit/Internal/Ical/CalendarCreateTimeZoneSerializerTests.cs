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
    public void SerializeEvent_ExplicitEndHorizonIncludesLastOccurrenceSpringTransition()
    {
        var bytes = CalendarEntityCreateSerializer.SerializeEvent(
            "explicit-end-horizon",
            new CalendarEventCreateFields(
                Start: Zoned("2026-03-01T01:30:00"),
                End: Zoned("2026-03-01T03:30:00"),
                RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                    Rule: "FREQ=WEEKLY;COUNT=2")),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        Encoding.UTF8.GetString(ExtractTimeZone(bytes)).ShouldContain("20260308T020000");
        AssertOccurrence(
            Evaluate(bytes, "2026-03-08T06:29:59Z", "2026-03-08T08:30:01Z"),
            "2026-03-08T06:30:00Z",
            "2026-03-08T08:30:00Z",
            "2026-03-08T04:30:00");
    }

    [Fact]
    public void SerializeTodo_ExplicitDueHorizonIncludesLastOccurrenceFallTransition()
    {
        var bytes = CalendarEntityCreateSerializer.SerializeTodo(
            "explicit-due-horizon",
            new CalendarTodoCreateFields(
                Start: Zoned("2026-10-25T00:30:00"),
                Due: Zoned("2026-10-25T03:30:00"),
                RecurrenceSet: new CalendarTodoRecurrenceSetCreate(
                    Rule: "FREQ=WEEKLY;COUNT=2")),
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"));

        Encoding.UTF8.GetString(ExtractTimeZone(bytes)).ShouldContain("20261101T020000");
        AssertOccurrence(
            Evaluate(bytes, "2026-11-01T04:29:59Z", "2026-11-01T07:30:01Z"),
            "2026-11-01T04:30:00Z",
            "2026-11-01T07:30:00Z",
            "2026-11-01T02:30:00");
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
