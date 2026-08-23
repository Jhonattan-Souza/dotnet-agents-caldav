using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarOccurrenceEvaluatorTests
{
    [Fact]
    public void Evaluate_MovedRangeMatchesEffectiveWindowAndRetainsOriginalIdentity()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260814T090000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
            + "DTSTART:20260817T130000Z\r\nDURATION:PT2H\r\n",
            "2026-08-17T13:30:00Z",
            "2026-08-17T13:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-17T09:00:00Z");
        occurrence.Timing.SourceStart.Value.ShouldBe("2026-08-17T09:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T13:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-17T15:00:00Z");
    }

    [Fact]
    public void Evaluate_CancelledRangeOmitsUntilLaterRangeWhileExactOverrideWins()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260814T090000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260815T090000Z\r\n"
            + "DTSTART:20260815T090000Z\r\nSTATUS:CANCELLED\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T090000Z\r\nDTSTART:20260816T200000Z\r\nDURATION:PT1H\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
            + "DTSTART:20260817T130000Z\r\nDURATION:PT1H\r\n",
            "2026-08-14T00:00:00Z",
            "2026-08-19T00:00:00Z");

        result.Items.Select(item => (item.RecurrenceIdentity.Value, item.Timing.EffectiveStart.Value)).ShouldBe([
            ("2026-08-14T09:00:00Z", "2026-08-14T09:00:00Z"),
            ("2026-08-16T09:00:00Z", "2026-08-16T20:00:00Z"),
            ("2026-08-17T09:00:00Z", "2026-08-17T13:00:00Z"),
            ("2026-08-18T09:00:00Z", "2026-08-18T13:00:00Z")
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_DetachedIndividualWinsSameIdentityRangeInEitherComponentOrder(
        bool individualFirst)
    {
        const string range = "BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260820T100000Z\r\n"
            + "DTSTART:20260820T120000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n";
        const string individual = "BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART:20260820T180000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n";
        var overrides = individualFirst ? individual + range : range + individual;

        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\n"
            + overrides[..^"END:VEVENT\r\n".Length],
            "2026-08-20T00:00:00Z",
            "2026-08-21T00:00:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-20T18:00:00Z");
    }

    [Fact]
    public void Evaluate_DetachedEventOverridesAreEnumeratedWithCancellationAndExdatePrecedence()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260823T100000Z\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\nDTSTART:20260821T150000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260823T100000Z\r\nDTSTART:20260824T150000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Event,
            "2026-08-21T00:00:00Z",
            "2026-08-25T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.SourceStart.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-21T15:00:00Z");
    }

    [Fact]
    public void Evaluate_DetachedTodoOverridesWithoutDtstartRemainObservable()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260814T100000Z\r\nDUE:20260814T110000Z\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260823T100000Z\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\nDUE:20260821T150000Z\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260823T100000Z\r\nDUE:20260824T150000Z\r\nEND:VTODO\r\n"
            + "END:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-21T00:00:00Z",
            "2026-08-25T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-21T15:00:00Z");
        occurrence.Timing.EffectiveEnd.ShouldBeNull();
    }

    [Fact]
    public void Evaluate_DueOnlyRecurringTodoIsTypedRecurrenceUnevaluable()
    {
        var bytes = TodoWithTemporalLines(
            "due-only",
            "DUE:20260815T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-15T00:00:00Z",
            "2026-08-20T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_OldResourceLocalSeriesStartsNearBoundedWindow()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Zurich:20000101T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY\r\n",
            "2026-08-16T08:30:00Z",
            "2026-08-16T08:45:00Z",
            calendarLines: ResourceLocalFixedZone());

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00");
    }

    [Fact]
    public void Evaluate_OldTodoRangeMovedByDueIsIncludedInBoundedSearch()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + ResourceLocalFixedZone()
            + "BEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;TZID=Private/Zurich:20000101T100000\r\nRRULE:FREQ=DAILY\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=Private/Zurich;RANGE=THISANDFUTURE:20260801T100000\r\n"
            + "DUE;TZID=Private/Zurich:20260816T100000\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-17T07:59:00Z",
            "2026-08-17T08:01:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-02T10:00:00");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T10:00:00");
    }

    [Fact]
    public void Evaluate_DateEventIndividualOverrideDefaultsToOneDay()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260814\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE:20260820\r\nDTSTART;VALUE=DATE:20260822\r\n",
            "2026-08-22T12:00:00Z",
            "2026-08-22T13:00:00Z",
            "UTC");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20");
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-23"));
    }

    [Fact]
    public void Evaluate_DateEventRangeOverrideDefaultsEachOccurrenceToOneDay()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260820\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE;RANGE=THISANDFUTURE:20260820\r\n"
            + "DTSTART;VALUE=DATE:20260822\r\n",
            "2026-08-23T12:00:00Z",
            "2026-08-23T13:00:00Z",
            "UTC");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-21");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-23");
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-24"));
    }

    [Fact]
    public void Evaluate_DateEventOverrideDefaultsToNextLocalDayAcrossDst()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260301\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE:20260308\r\nDTSTART;VALUE=DATE:20260308\r\n",
            "2026-03-08T00:00:00Z",
            "2026-03-10T00:00:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T05:00:00Z");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T04:00:00Z");
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public void Evaluate_IanaMasterDistinguishesNominalDayFromAccurateHours(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public void Evaluate_IanaOverrideDistinguishesNominalDayFromAccurateHours(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260301T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York:20260307T100000\r\n"
            + $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Fact]
    public void Evaluate_DetachedOverrideRetainsAccurateMasterSourceSpanAcrossDst()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York:20260307T100000\r\n"
            + "DTSTART;TZID=America/New_York:20260310T100000\r\nDURATION:PT1H\r\n",
            "2026-03-10T13:59:00Z",
            "2026-03-10T14:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-10T11:00:00");
    }

    [Theory]
    [InlineData("P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("PT24H", "2026-03-29T09:00:00Z", "2026-03-29T11:00:00")]
    public void Evaluate_ResourceLocalMasterUsesTheSameDurationArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;TZID=Private/Zurich:20260328T100000\r\nDURATION:{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z",
            calendarLines: ResourceLocalDstZone());

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("PT24H", "2026-03-29T09:00:00Z", "2026-03-29T11:00:00")]
    public void Evaluate_RdatePeriodUsesNominalThenAccurateDurationArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Zurich:20260301T100000\r\nDURATION:PT1H\r\n"
            + $"RDATE;TZID=Private/Zurich;VALUE=PERIOD:20260328T100000/{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z",
            calendarLines: ResourceLocalDstZone());

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.SourceEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("+P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("+P1W", "2026-04-04T08:00:00Z", "2026-04-04T10:00:00")]
    public void Evaluate_RdatePeriodAcceptsPositiveDurationAndPreservesLexicalValue(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Europe/Zurich:20260301T100000\r\nDURATION:PT1H\r\n"
            + $"RDATE;TZID=Europe/Zurich;VALUE=PERIOD:20260328T100000/{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceDuration.ShouldBe(duration);
        timing.EffectiveDuration.ShouldBe(duration);
        timing.SourceEnd!.Value.ShouldBe(expectedEndLocal);
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
    }

    [Theory]
    [InlineData("DTSTART;TZID=Europe/Zurich:20260328T100000\r\nDURATION:P2147483647D\r\nRRULE:FREQ=DAILY;COUNT=1\r\n")]
    [InlineData("DTSTART;TZID=Europe/Zurich:20260301T100000\r\nDURATION:PT1H\r\nRDATE;TZID=Europe/Zurich;VALUE=PERIOD:20260328T100000/P2147483647D\r\n")]
    public void Evaluate_ExtremeDurationIsTypedRecurrenceUnevaluableWithoutPartialItems(
        string temporalLines)
    {
        var result = EvaluateSingleOccurrenceEvent(
            temporalLines,
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DURATION:PT0S")]
    [InlineData("DURATION:-PT1H")]
    public void Evaluate_EventDurationMustBeStrictlyPositive(string durationLine)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART:20260816T100000Z\r\n{durationLine}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PT0S")]
    [InlineData("-PT1H")]
    public void Evaluate_TodoDurationMustBeStrictlyPositive(string duration)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART:20260816T100000Z\r\nDURATION:{duration}\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P0D")]
    [InlineData("-P1D")]
    public void Evaluate_RdatePeriodDurationMustBeStrictlyPositive(string duration)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + $"RDATE;VALUE=PERIOD:20260816T100000Z/{duration}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260816T100000Z/20260816T100000Z")]
    [InlineData("20260816T100000Z/20260816T095959Z")]
    [InlineData("20260816T100000Z/not-a-date-time")]
    [InlineData("20260816T100000Z/P1Q")]
    public void Evaluate_RdatePeriodMustHaveAValidPositiveSpan(string period)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + $"RDATE;VALUE=PERIOD:{period}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_RdatePeriodUnknownZoneRemainsTemporalUnresolved()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + "RDATE;TZID=Private/Unknown;VALUE=PERIOD:20260816T100000/20260816T110000\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_PeriodRecurrenceDateInTimeZoneObservanceIsTypedUnevaluable()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Zone:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T07:59:00Z",
            "2026-08-16T08:01:00Z",
            calendarLines: "BEGIN:VTIMEZONE\r\nTZID:Private/Zone\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
                + "RDATE;VALUE=PERIOD:20261025T030000/20261025T040000\r\n"
                + "END:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_LocalDateTimeObservanceRecurrenceDateRemainsEffective()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Custom/Shift:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T06:29:00Z",
            "2026-08-16T06:31:00Z",
            calendarLines: "BEGIN:VTIMEZONE\r\nTZID:Custom/Shift\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\n"
                + "BEGIN:DAYLIGHT\r\nDTSTART:20260816T020000\r\nRDATE:20260816T020000\r\n"
                + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0330\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T06:30:00Z");
    }

    [Theory]
    [InlineData("VEVENT", "DTEND:20260816T110000Z")]
    [InlineData("VTODO", "DUE:20260816T110000Z")]
    public void Evaluate_DurationAndExplicitEndAreMutuallyExclusive(
        string component,
        string endLine)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + $"BEGIN:{component}\r\nUID:exclusive\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART:20260816T100000Z\r\n{endLine}\r\nDURATION:PT1H\r\n"
            + $"END:{component}\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            component == "VEVENT" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("VEVENT")]
    [InlineData("VTODO")]
    public void Evaluate_DurationRequiresDtstart(string component)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + $"BEGIN:{component}\r\nUID:missing-start\r\nDTSTAMP:20260815T120000Z\r\nDURATION:PT1H\r\n"
            + $"END:{component}\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            component == "VEVENT" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public void Evaluate_OverrideDurationAndEndAreMutuallyExclusive(string rangeParameter)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID{rangeParameter}:20260816T100000Z\r\n"
            + "DTSTART:20260816T100000Z\r\nDTEND:20260816T110000Z\r\nDURATION:PT1H\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PT24H")]
    [InlineData("P1DT1H")]
    public void Evaluate_DateEventDurationMustUseOnlyDaysOrWeeks(string duration)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P1D")]
    [InlineData("P1W")]
    public void Evaluate_DateEventAcceptsDayOrWeekDuration(string duration)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public void Evaluate_DateTodoDurationMustUseOnlyDaysOrWeeks()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:date-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260816\r\nDURATION:PT24H\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P1D")]
    [InlineData("P1W")]
    public void Evaluate_DateTodoAcceptsDayOrWeekDuration(string duration)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:date-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public void Evaluate_DateOverrideDurationMustUseOnlyDaysOrWeeks(string rangeParameter)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260815\r\nDURATION:P1D\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID;VALUE=DATE{rangeParameter}:20260816\r\n"
            + "DTSTART;VALUE=DATE:20260816\r\nDURATION:P1DT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public void Evaluate_OverrideDurationMustBeStrictlyPositive(string rangeParameter)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID{rangeParameter}:20260816T100000Z\r\n"
            + "DTSTART:20260816T100000Z\r\nDURATION:-PT1H\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260307", "20260308", "2026-03-08T05:00:00Z", "2026-03-09", "2026-03-09T01:00:00", "2026-03-09T05:00:00Z")]
    [InlineData("20261031", "20261101", "2026-11-01T04:00:00Z", "2026-11-02", "2026-11-01T23:00:00", "2026-11-02T04:00:00Z")]
    public void Evaluate_RecurringDateExactDurationKeepsSourceDateAndTruthfulEffectiveInstant(
        string masterStart,
        string masterEnd,
        string secondStartUtc,
        string expectedSourceEnd,
        string expectedEffectiveEnd,
        string expectedEndUtc)
    {
        var secondStart = DateTimeOffset.Parse(secondStartUtc);
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;VALUE=DATE:{masterStart}\r\nDTEND;VALUE=DATE:{masterEnd}\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n",
            secondStart.ToString("O"),
            secondStart.AddMinutes(1).ToString("O"),
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, expectedSourceEnd));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            expectedEffectiveEnd,
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
    }

    [Fact]
    public void Evaluate_RecurringTodoDateExactDurationUsesTruthfulEffectiveInstant()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260307\r\nDUE;VALUE=DATE:20260308\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-03-08T05:00:00Z",
            "2026-03-08T05:01:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-03-09"));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-09T01:00:00",
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T05:00:00Z");
    }

    [Fact]
    public void Evaluate_RangeDateExactDurationKeepsSourceDateAndTruthfulEffectiveInstant()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260307\r\nDTEND;VALUE=DATE:20260308\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE;RANGE=THISANDFUTURE:20260307\r\n"
            + "DTSTART;VALUE=DATE:20260307\r\nDTEND;VALUE=DATE:20260308\r\n",
            "2026-03-08T05:00:00Z",
            "2026-03-08T05:01:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-03-09"));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-09T01:00:00",
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T05:00:00Z");
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public void Evaluate_RangeDurationUsesPerInstanceNominalThenAccurateArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260301T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York;RANGE=THISANDFUTURE:20260307T100000\r\n"
            + $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Fact]
    public void Evaluate_RecurringExplicitEndPropagatesExactDurationAcrossDst()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public void Evaluate_RangeExplicitEndPropagatesExactDurationAfterAnchor()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260306T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York;RANGE=THISANDFUTURE:20260306T100000\r\n"
            + "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\n",
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public void Evaluate_RecurringTodoDuePropagatesExactDurationAcrossDst()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DUE;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = EvaluateSingleOccurrence(
            bytes,
            CalendarEntityKind.Todo,
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public void Evaluate_WeekDurationRemainsNominalAcrossDst()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Europe/Zurich:20260328T100000\r\nDURATION:P1W\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-04-04T10:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-04-04T08:00:00Z");
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1W1D")]
    [InlineData("P1WT1H")]
    [InlineData("P1H")]
    [InlineData("PT1H30S")]
    [InlineData("P1D1H")]
    [InlineData("P-1D")]
    public void CalendarDurationParser_RejectsNonRfcGrammar(string rawDuration)
    {
        CalendarDurationArithmetic.TryParse(rawDuration, out _).ShouldBeFalse();
    }

    [Fact]
    public void CalendarDurationParser_RecognizesNominalWeek()
    {
        CalendarDurationArithmetic.TryParse("P1W", out var duration).ShouldBeTrue();
        duration.NominalDays.ShouldBe(7);
        duration.Accurate.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("+P1D", 1, 0)]
    [InlineData("-P2D", -2, 0)]
    [InlineData("P0D", 0, 0)]
    [InlineData("-PT0S", 0, 0)]
    [InlineData("PT1H", 0, 3600)]
    [InlineData("PT2M3S", 0, 123)]
    [InlineData("PT3S", 0, 3)]
    [InlineData("P1DT2H3M4S", 1, 7384)]
    public void CalendarDurationParser_PreservesNominalAndAccurateComponents(
        string rawDuration,
        int expectedNominalDays,
        int expectedAccurateSeconds)
    {
        CalendarDurationArithmetic.TryParse(rawDuration, out var duration).ShouldBeTrue();
        duration.NominalDays.ShouldBe(expectedNominalDays);
        duration.Accurate.ShouldBe(TimeSpan.FromSeconds(expectedAccurateSeconds));
    }

    [Fact]
    public void Evaluate_RecurringDateSearchIncludesTwentyFiveHourFallBackSpan()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;COUNT=1\r\n",
            "2026-11-02T04:30:00Z",
            "2026-11-02T04:45:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-11-01T04:00:00Z");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-11-02T05:00:00Z");
    }

    [Fact]
    public void Evaluate_MultipleRrulesAreTypedRecurrenceUnevaluableWithNoPartialItems()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n",
            "2026-08-15T00:00:00Z",
            "2026-08-20T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public void Evaluate_ExplicitIanaGapUsesPriorOffsetAndOverlapUsesFirstOccurrence(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;TZID=Europe/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"));

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedUtc);
    }

    [Fact]
    public void Evaluate_IanaRruleSkipsGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART;TZID=Europe/Zurich:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z");
        var afterGap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z");

        gap.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Fact]
    public void Evaluate_ExplicitFloatingGapUsesPriorEvaluationZoneOffset()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260329T023000\r\nDURATION:PT30M\r\n",
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            "Europe/Zurich");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-29T01:30:00Z");
    }

    [Fact]
    public void Evaluate_FloatingRruleSkipsEvaluationZoneGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            "Europe/Zurich");
        var afterGap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z",
            "Europe/Zurich");

        gap.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Theory]
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public void Evaluate_ExplicitResourceLocalGapAndOverlapFollowRfc5545(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART;TZID=Private/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"),
            calendarLines: ResourceLocalDstZone());

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedUtc);
    }

    [Fact]
    public void Evaluate_ResourceLocalRruleSkipsGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART;TZID=Private/Zurich:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            calendarLines: ResourceLocalDstZone());
        var afterGap = EvaluateSingleOccurrenceEvent(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z",
            calendarLines: ResourceLocalDstZone());

        gap.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Theory]
    [InlineData("Europe/Zurich", null, false)]
    [InlineData("Private/Zurich", null, true)]
    [InlineData(null, "Europe/Zurich", false)]
    public void Evaluate_RruleOverlapUsesFirstOccurrenceExactlyOnce(
        string? timeZoneId,
        string? evaluationTimeZone,
        bool resourceLocal)
    {
        var parameter = timeZoneId is null ? string.Empty : $";TZID={timeZoneId}";
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART{parameter}:20261024T023000\r\nDURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-10-25T00:29:00Z",
            "2026-10-25T00:31:00Z",
            evaluationTimeZone,
            resourceLocal ? ResourceLocalDstZone() : string.Empty);

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-10-25T00:30:00Z");
    }

    [Fact]
    public void Evaluate_IanaDailyRecurrencePreservesWallTimeAcrossDst()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n",
            "2026-03-08T14:30:00Z",
            "2026-03-08T14:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-08T10:00:00",
            "America/New_York"));
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T14:00:00Z");
    }

    [Fact]
    public void Evaluate_LeapRuleFindsNextLeapDayWithoutServerExpansion()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20240229T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=YEARLY;COUNT=3\r\n",
            "2028-02-29T10:30:00Z",
            "2028-02-29T10:45:00Z");

        result.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2028-02-29T10:00:00Z");
    }

    [Fact]
    public void Evaluate_UnknownNamedZoneIsTypedTemporalUnresolved()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Office:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ExdateSuppressedUnknownZoneOverrideDoesNotRequireResolution()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260820T100000Z\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nDURATION:PT1H\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_CancelledUnknownZoneOverrideDoesNotRequireResolution()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nSTATUS:CANCELLED\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ActiveUnknownZoneOverrideRemainsTypedTemporalUnresolved()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nDURATION:PT1H\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ConflictingResourceLocalZonesAreTypedTemporalUnresolved()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Private/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n";
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Office:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            calendarLines: zone + zone);

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_ResourceLocalRecurringZoneIsEvaluatedWithoutIanaFallback()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Private/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n";
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;TZID=Private/Office:20260815T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-08-16T08:30:00Z",
            "2026-08-16T08:45:00Z",
            calendarLines: zone);

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.TimeZoneId.ShouldBe("Private/Office");
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T08:00:00Z");
    }

    [Fact]
    public void Evaluate_RdatePeriodSuppliesOccurrenceSpecificSpan()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/20260816T130000Z\r\n",
            "2026-08-16T12:30:00Z",
            "2026-08-16T12:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00Z");
        occurrence.Timing.SourceEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
    }

    [Fact]
    public void Evaluate_RdatePeriodDeduplicatesAndOverrideUsesOccurrenceSpecificSourceSpan()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/PT3H\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T100000Z\r\nDTSTART:20260816T150000Z\r\nDURATION:PT2H\r\n",
            "2026-08-16T15:30:00Z",
            "2026-08-16T15:45:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00Z");
        occurrence.Timing.SourceEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
        occurrence.Timing.SourceDuration.ShouldBe("PT3H");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-16T15:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-16T17:00:00Z");
        occurrence.Timing.EffectiveDuration.ShouldBe("PT2H");
    }

    [Fact]
    public void Evaluate_ExdateSuppressesDuplicateRdatePeriodAndItsOverride()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/PT3H\r\nEXDATE:20260816T100000Z\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T100000Z\r\nDTSTART:20260816T150000Z\r\nDURATION:PT2H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public void Evaluate_DateOnlyEventDefaultsToOneLocalDay()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260816\r\n",
            "2026-08-17T03:30:00Z",
            "2026-08-17T03:45:00Z",
            "America/New_York");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-16"));
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-17"));
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T04:00:00Z");
        occurrence.Timing.EvaluatedEndUtc!.Value.ShouldBe("2026-08-17T04:00:00Z");
    }

    [Fact]
    public void Evaluate_DateOnlyExdateListResolvesEachIdentity()
    {
        var result = EvaluateSingleOccurrenceEvent(
            "DTSTART;VALUE=DATE:20260815\r\nRRULE:FREQ=DAILY;COUNT=4\r\n"
            + "EXDATE;VALUE=DATE:20260816,20260817\r\n",
            "2026-08-15T00:00:00Z",
            "2026-08-19T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.Select(item => item.RecurrenceIdentity.Value).ShouldBe(["2026-08-15", "2026-08-18"]);
    }

    [Fact]
    public void Evaluate_PerEntityBoundaryIncludesUniqueRdatePeriods()
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(0, 2001).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));

        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2026-08-15T12:00:00Z",
            "2026-08-15T13:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.LimitExhausted);
        result.Items.ShouldBeEmpty();
        result.ObservedOccurrenceCount.ShouldBe(2001);
    }

    [Theory]
    [InlineData(2000, "Success", null)]
    [InlineData(2001, "LimitExhausted", 2001)]
    public void Evaluate_PeriodBoundaryCountsUniqueDerivedWorkOutsideWindow(
        int periodCount,
        string expectedCode,
        int? expectedObservedCount)
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(0, periodCount).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));

        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(Enum.Parse<CalendarOccurrenceEvaluationCode>(expectedCode));
        result.Items.ShouldBeEmpty();
        if (expectedObservedCount is not null)
            result.ObservedOccurrenceCount.ShouldBe(expectedObservedCount.Value);
    }

    [Fact]
    public void Evaluate_DuplicatePeriodIdentityDoesNotInflateDerivedWork()
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var unique = Enumerable.Range(0, 2000)
            .Select(index => $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S")
            .ToArray();
        var periods = string.Join(',', unique.Append(unique[^1]));

        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2000, "Success", null)]
    [InlineData(2001, "LimitExhausted", 2001)]
    public void Evaluate_DetachedOverrideBoundaryCountsUniqueDerivedWorkOutsideWindow(
        int overrideCount,
        string expectedCode,
        int? expectedObservedCount)
    {
        var result = EvaluateSingleOccurrence(
            EventWithDetachedOverrides(overrideCount),
            CalendarEntityKind.Event,
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(Enum.Parse<CalendarOccurrenceEvaluationCode>(expectedCode));
        result.Items.ShouldBeEmpty();
        if (expectedObservedCount is not null)
            result.ObservedOccurrenceCount.ShouldBe(expectedObservedCount.Value);
    }

    [Theory]
    [InlineData(500, "Success", 1500, null)]
    [InlineData(501, "LimitExhausted", 0, 2001)]
    public void Evaluate_PerEntityBoundaryUnionsRrulePeriodAndDetachedIdentities(
        int detachedCount,
        string expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        var result = EvaluateSingleOccurrence(
            EventWithCompositeDerivedWork(detachedCount),
            CalendarEntityKind.Event,
            "2026-08-15T12:00:00Z",
            "2026-08-15T13:00:00Z");

        result.Code.ShouldBe(Enum.Parse<CalendarOccurrenceEvaluationCode>(expectedCode));
        result.Items.Count.ShouldBe(expectedItems);
        if (expectedObservedCount is not null)
            result.ObservedOccurrenceCount.ShouldBe(expectedObservedCount.Value);
    }

    [Theory]
    [InlineData("20531230T100000Z", "Success")]
    [InlineData("20531231T100000Z", "Success")]
    [InlineData("20540101T100000Z", "LimitExhausted")]
    public void Evaluate_EnforcesExactUnmatchedIncrementBoundaryWithoutPartialOrInventedOccurrenceCount(
        string until,
        string expectedCode)
    {
        var result = EvaluateSingleOccurrenceEvent(
            $"DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;BYMONTH=2;BYMONTHDAY=30;UNTIL={until}\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T11:00:00Z");

        result.Code.ShouldBe(Enum.Parse<CalendarOccurrenceEvaluationCode>(expectedCode));
        result.Items.ShouldBeEmpty();
        result.ObservedOccurrenceCount.ShouldBe(0);
    }

    private static byte[] Event(string uid, string start, string summary) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:{start}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] EventWithEnd(string uid, string start, string end) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:{start}\r\nDTEND:{end}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] Todo(string uid, string summary) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] TodoWithTemporalLines(string uid, string temporalLines) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] TodoWithTiming(string uid, string? start, string due)
    {
        var startLine = start is null ? string.Empty : $"DTSTART:{start}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{startLine}DUE:{due}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
    }

    private static byte[] EventWithRawStart(string uid, string parameter, string start) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART{parameter}:{start}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] RecurringEvent(string uid, string? exceptionDate)
    {
        var exceptionLine = exceptionDate is null ? string.Empty : $"EXDATE:{exceptionDate}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=4\r\n{exceptionLine}END:VEVENT\r\nEND:VCALENDAR\r\n");
    }

    private static byte[] OverrideEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T100000Z\r\nDURATION:PT1H\r\n"
        + "RRULE:FREQ=DAILY;COUNT=4\r\nEXDATE:20260815T100000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260815T100000Z\r\n"
        + "DTSTART:20260815T120000Z\r\nDTEND:20260815T130000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260816T100000Z\r\n"
        + "DTSTART:20260816T100000Z\r\nDTEND:20260816T110000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260817T100000Z\r\n"
        + "DTSTART:20260817T200000Z\r\nDTEND:20260817T210000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] RangeOverrideEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T090000Z\r\nDURATION:PT1H\r\n"
        + "RRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260815T090000Z\r\n"
        + "DTSTART:20260815T110000Z\r\nDTEND:20260815T120000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
        + "DTSTART:20260817T130000Z\r\nDTEND:20260817T150000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260818T090000Z\r\n"
        + "DTSTART:20260818T160000Z\r\nDTEND:20260818T170000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] SameEffectiveStartRecurrence(string uid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T100000Z\r\n"
        + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260815T100000Z\r\n"
        + "DTSTART:20260820T100000Z\r\nDURATION:PT1M\r\nEND:VEVENT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260816T100000Z\r\n"
        + "DTSTART:20260820T100000Z\r\nDURATION:PT1M\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] ResourceLocalZoneEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VTIMEZONE\r\nTZID:America/New_York\r\nBEGIN:STANDARD\r\n"
        + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
        + "END:STANDARD\r\nEND:VTIMEZONE\r\n"
        + "BEGIN:VEVENT\r\nUID:local-zone\r\nDTSTAMP:20260815T120000Z\r\n"
        + "DTSTART;TZID=America/New_York:20260816T100000\r\nDURATION:PT30M\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static string ResourceLocalDstZone() =>
        "BEGIN:VTIMEZONE\r\nTZID:Private/Zurich\r\n"
        + "BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\n"
        + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\n"
        + "BEGIN:STANDARD\r\nDTSTART:20261025T030000\r\n"
        + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n"
        + "END:VTIMEZONE\r\n";

    private static string ResourceLocalFixedZone() =>
        "BEGIN:VTIMEZONE\r\nTZID:Private/Zurich\r\nBEGIN:STANDARD\r\n"
        + "DTSTART:19900101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
        + "END:STANDARD\r\nEND:VTIMEZONE\r\n";

    private static byte[] RecurringTodo(string uid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] RecurringEventWithRule(string uid, string recurrenceLines) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T120000Z\r\nDURATION:PT1S\r\n{recurrenceLines}END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] EventWithDetachedOverrides(int overrideCount)
    {
        var content = new System.Text.StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:detached-limit\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20200101T000000Z\r\nDURATION:PT1S\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20200101T000000Z\r\nEND:VEVENT\r\n");
        var identity = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var index = 0; index < overrideCount; index++)
        {
            var value = identity.AddSeconds(index);
            content.Append("BEGIN:VEVENT\r\nUID:detached-limit\r\nDTSTAMP:20260815T120000Z\r\n")
                .Append($"RECURRENCE-ID:{value:yyyyMMdd'T'HHmmss'Z'}\r\n")
                .Append($"DTSTART:{value.AddYears(4):yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT1S\r\nEND:VEVENT\r\n");
        }
        content.Append("END:VCALENDAR\r\n");
        return System.Text.Encoding.UTF8.GetBytes(content.ToString());
    }

    private static byte[] EventWithCompositeDerivedWork(int detachedCount)
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(2000, 500).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));
        var content = new System.Text.StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:composite-limit\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260815T120000Z\r\nDURATION:PT1S\r\nRRULE:FREQ=SECONDLY;COUNT=1000\r\n"
            + $"RDATE;VALUE=PERIOD:{periods}\r\nEND:VEVENT\r\n");
        for (var index = 0; index < detachedCount; index++)
        {
            var identity = start.AddSeconds(4000 + index);
            content.Append("BEGIN:VEVENT\r\nUID:composite-limit\r\nDTSTAMP:20260815T120000Z\r\n")
                .Append($"RECURRENCE-ID:{identity:yyyyMMdd'T'HHmmss'Z'}\r\n")
                .Append($"DTSTART:{identity.AddYears(1):yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT1S\r\nEND:VEVENT\r\n");
        }
        content.Append("END:VCALENDAR\r\n");
        return System.Text.Encoding.UTF8.GetBytes(content.ToString());
    }

    private static byte[] Mixed(string eventUid, string todoUid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{eventUid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:{todoUid}\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");


    private static CalendarOccurrenceEvaluation EvaluateSingleOccurrenceEvent(
        string temporalLines,
        string from,
        string to,
        string? evaluationTimeZone = null,
        string calendarLines = "")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n" + calendarLines
            + $"BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n");
        return Evaluate(
            bytes,
            CalendarEntityKind.Event,
            from,
            to,
            evaluationTimeZone);
    }

    private static CalendarOccurrenceEvaluation EvaluateSingleOccurrence(
        byte[] authoritativeBytes,
        CalendarEntityKind kind,
        string from,
        string to,
        string? evaluationTimeZone = null) => Evaluate(
            authoritativeBytes,
            kind,
            from,
            to,
            evaluationTimeZone);

    private static CalendarOccurrenceEvaluation Evaluate(
        byte[] bytes,
        CalendarEntityKind kind,
        string from,
        string to,
        string? evaluationTimeZone)
    {
        var calendarHref = kind == CalendarEntityKind.Event
            ? "https://cal.example/events/"
            : "https://cal.example/todos/";
        var document = CalendarContentDocument.Parse(bytes);
        var projected = CalendarResourceProjector.Project(document);
        var snapshot = new CalendarResourceSnapshot(
            calendarHref,
            $"{calendarHref}semantic-corpus.ics",
            "\"r1\"",
            bytes,
            projected.Properties,
            projected.Projection,
            projected.Diagnostics);
        return CalendarOccurrenceEvaluator.Evaluate(
            snapshot,
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse(from),
                DateTimeOffset.Parse(to),
                evaluationTimeZone),
            document,
            CalendarResourceProjector.LoadTypedCalendar(document),
            CancellationToken.None);
    }

}
