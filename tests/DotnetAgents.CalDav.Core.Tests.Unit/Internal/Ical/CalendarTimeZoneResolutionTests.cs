using System.Text;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarTimeZoneResolutionTests
{
    private const string CalendarHref = "https://cal.example/events/";
    private const string EmbeddedEasternZone = "BEGIN:VTIMEZONE\r\nTZID:Eastern Standard Time\r\n"
        + "BEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n"
        + "END:VTIMEZONE\r\n";

    [Theory]
    [InlineData("America/New_York", "America/New_York", false)]
    [InlineData("US/Eastern", "US/Eastern", false)]
    [InlineData("Eastern Standard Time", "America/New_York", true)]
    [InlineData("W. Europe Standard Time", "Europe/Berlin", true)]
    [InlineData("Private/Unknown", null, false)]
    [InlineData("eastern standard time", null, false)]
    public void Identifiers_ResolveIanaDirectlyAndWindowsThroughTheMapping(
        string timeZoneId,
        string? expectedIana,
        bool expectedWindows)
    {
        CalendarTimeZoneIdentifiers.ToIanaIdentifier(timeZoneId).ShouldBe(expectedIana);
        CalendarTimeZoneIdentifiers.IsMappedWindowsIdentifier(timeZoneId).ShouldBe(expectedWindows);
        CalendarTimeZoneIdentifiers.FindZone(timeZoneId)?.Id.ShouldBe(expectedIana);
    }

    [Theory]
    [InlineData("America/New_York", new string[0])]
    [InlineData("Eastern Standard Time", new[] { "timezone_reference_resolved_externally" })]
    [InlineData("Private/Unknown", new[] { "timezone_reference_unresolved" })]
    public void Project_KeepsResourcesWithoutEmbeddedVtimezoneSemanticAndDescribesTheirResolution(
        string timeZoneId,
        string[] expectedCodes)
    {
        var result = CalendarResourceProjector.Project(Resource(
            $"DTSTART;TZID={timeZoneId}:20260310T100000\r\nDURATION:PT1H\r\n"));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Diagnostics.Select(item => item.Code).ShouldBe(expectedCodes);
    }

    [Fact]
    public void Project_ReportsEachExternalResolutionKindOnceWithItsSeverity()
    {
        var result = CalendarResourceProjector.Project(Resource(
            "DTSTART;TZID=Eastern Standard Time:20260310T100000\r\n"
            + "DTEND;TZID=Private/Unknown:20260310T110000\r\n"
            + "RDATE;TZID=Eastern Standard Time:20260311T100000\r\n"));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Diagnostics.Select(item => (item.Code, item.Severity)).ShouldBe([
            ("timezone_reference_resolved_externally", CalendarResourceDiagnosticSeverity.Info),
            ("timezone_reference_unresolved", CalendarResourceDiagnosticSeverity.Warning)
        ]);
    }

    [Fact]
    public void Project_EmbeddedVtimezoneForAWindowsIdentifierNeedsNoExternalResolution()
    {
        var result = CalendarResourceProjector.Project(Resource(
            "DTSTART;TZID=Eastern Standard Time:20260310T100000\r\nDURATION:PT1H\r\n",
            EmbeddedEasternZone));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Eastern Standard Time")]
    public void Evaluate_ResolvesBareIanaAndWindowsIdentifiersToTheSameInstantsAcrossDst(string timeZoneId)
    {
        var result = Evaluate(Resource(
            $"DTSTART;TZID={timeZoneId}:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"));

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.Select(item => item.Timing.EvaluatedStartUtc!.Value).ShouldBe([
            "2026-03-07T15:00:00Z",
            "2026-03-08T14:00:00Z",
            "2026-03-09T14:00:00Z"
        ]);
        result.Items.Select(item => item.Timing.EffectiveStart.TimeZoneId).Distinct().ShouldBe([timeZoneId]);
    }

    [Fact]
    public void Evaluate_EmbeddedVtimezoneWinsOverTheWindowsMapping()
    {
        var result = Evaluate(Resource(
            "DTSTART;TZID=Eastern Standard Time:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            EmbeddedEasternZone));

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.Success);
        result.Items.Select(item => item.Timing.EvaluatedStartUtc!.Value).ShouldBe([
            "2026-03-07T09:00:00Z",
            "2026-03-08T09:00:00Z"
        ]);
    }

    [Fact]
    public void Evaluate_UnresolvableIdentifierWithoutVtimezoneStaysTemporalUnresolved()
    {
        var result = Evaluate(Resource("DTSTART;TZID=Private/Unknown:20260307T100000\r\nDURATION:PT1H\r\n"));

        result.Code.ShouldBe(CalendarOccurrenceEvaluationCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("America/New_York", true)]
    [InlineData("Eastern Standard Time", true)]
    [InlineData("Private/Unknown", false)]
    public void ExactValidation_AcceptsOnlyExternallyResolvableReferencesWithoutVtimezone(
        string timeZoneId,
        bool expected)
    {
        var content = Resource($"DTSTART;TZID={timeZoneId}:20260310T100000\r\nDURATION:PT1H\r\n");

        CalendarExactResourceValidator.TryValidate(content, out _).ShouldBe(expected);
    }

    [Theory]
    [InlineData(CalendarTemporalKind.ZonedDateTime, "Eastern Standard Time", "America/New_York")]
    [InlineData(CalendarTemporalKind.ZonedDateTime, "Europe/Paris", "Europe/Paris")]
    [InlineData(CalendarTemporalKind.ZonedDateTime, "Private/Unknown", "Private/Unknown")]
    [InlineData(CalendarTemporalKind.ZonedDateTime, null, null)]
    [InlineData(CalendarTemporalKind.FloatingDateTime, null, null)]
    public void Normalize_MapsOnlyWindowsIdentifiersOfNamedZoneValues(
        CalendarTemporalKind kind,
        string? timeZoneId,
        string? expected)
    {
        var value = new CalendarTemporalValue(kind, "2026-03-10T10:00:00", timeZoneId);

        CalendarAuthoringTimeZones.Normalize(value).TimeZoneId.ShouldBe(expected);
        CalendarAuthoringTimeZones.Normalize((CalendarTemporalValue?)null).ShouldBeNull();
    }

    [Fact]
    public void Normalize_MapsEveryAuthoredValueOfARecurrenceSetPatch()
    {
        var patch = new CalendarEventPatch(
            Start: new(CalendarScalarPatchOperation.Set, Zoned("2026-03-07T10:00:00")),
            End: new(CalendarScalarPatchOperation.Clear),
            RecurrenceSet: new CalendarRecurrenceSetPatch(
                CalendarScalarPatchOperation.Set,
                new CalendarRecurrenceSetPatchValue(
                    Rule: "FREQ=DAILY;COUNT=3",
                    RecurrenceDates: [Zoned("2026-03-11T10:00:00")],
                    ExceptionDates: [Zoned("2026-03-09T10:00:00")],
                    Overrides:
                    [
                        new CalendarRecurrenceOverridePatchValue(
                            Zoned("2026-03-08T10:00:00"),
                            CalendarEntityKind.Event,
                            CalendarRecurrenceOverrideStatus.Active,
                            MovedStart: Zoned("2026-03-08T12:00:00"))
                    ]),
                []),
            RecurrenceSetAddressed: true);

        var normalized = CalendarAuthoringTimeZones.Normalize(patch);

        normalized.Start!.Value!.TimeZoneId.ShouldBe("America/New_York");
        normalized.End!.Operation.ShouldBe(CalendarScalarPatchOperation.Clear);
        normalized.Due.ShouldBeNull();
        var value = normalized.RecurrenceSet!.Value!;
        value.RecurrenceDates!.Single().TimeZoneId.ShouldBe("America/New_York");
        value.ExceptionDates!.Single().TimeZoneId.ShouldBe("America/New_York");
        var recurrenceOverride = value.Overrides!.Single();
        recurrenceOverride.RecurrenceIdentity.TimeZoneId.ShouldBe("America/New_York");
        recurrenceOverride.MovedStart!.TimeZoneId.ShouldBe("America/New_York");
        recurrenceOverride.MovedEnd.ShouldBeNull();
    }

    [Fact]
    public void Normalize_LeavesClearedAndSparseRecurrenceSetPatchesUnchanged()
    {
        var cleared = new CalendarEventPatch(
            RecurrenceSet: new CalendarRecurrenceSetPatch(CalendarScalarPatchOperation.Clear, null, []),
            RecurrenceSetAddressed: true);
        var sparse = new CalendarEventPatch(
            RecurrenceSet: new CalendarRecurrenceSetPatch(
                CalendarScalarPatchOperation.Set,
                new CalendarRecurrenceSetPatchValue(Rule: "FREQ=DAILY"),
                []),
            RecurrenceSetAddressed: true);

        CalendarAuthoringTimeZones.Normalize(cleared).ShouldBe(cleared);
        var normalizedSparse = CalendarAuthoringTimeZones.Normalize(sparse).RecurrenceSet!.Value!;
        normalizedSparse.RecurrenceDates.ShouldBeNull();
        normalizedSparse.ExceptionDates.ShouldBeNull();
        normalizedSparse.Overrides.ShouldBeNull();
        CalendarAuthoringTimeZones.Normalize(new CalendarEventPatch()).RecurrenceSet.ShouldBeNull();
    }

    [Fact]
    public void Normalize_MapsRecurrenceDatePeriodsInCreateRequests()
    {
        var eventRequest = new CalendarEventCreateRequest(
            CalendarCreateDestination.Default,
            null,
            new CalendarEventCreateFields(
                Start: Zoned("2026-03-07T10:00:00"),
                RecurrenceSet: new CalendarEventRecurrenceSetCreate(RecurrenceDates:
                [
                    new CalendarRecurrenceDateCreate(Period: new CalendarRecurrencePeriodCreate(
                        Zoned("2026-03-11T10:00:00"),
                        Zoned("2026-03-11T11:00:00"))),
                    new CalendarRecurrenceDateCreate(Period: new CalendarRecurrencePeriodCreate(
                        Zoned("2026-03-12T10:00:00"),
                        Duration: "PT1H"))
                ])));
        var todoRequest = new CalendarTodoCreateRequest(
            CalendarCreateDestination.Default,
            null,
            new CalendarTodoCreateFields(
                Start: Zoned("2026-03-07T10:00:00"),
                RecurrenceSet: new CalendarTodoRecurrenceSetCreate(
                    Rule: "FREQ=DAILY;COUNT=2",
                    ExceptionDates: [Zoned("2026-03-08T10:00:00")],
                    Overrides:
                    [
                        new CalendarTodoRecurrenceOverrideCreate(
                            Zoned("2026-03-08T10:00:00"),
                            CalendarRecurrenceOverrideStatus.Cancelled,
                            new CalendarTodoCreateFields(Due: Zoned("2026-03-08T12:00:00")))
                    ])));

        var periods = CalendarAuthoringTimeZones.Normalize(eventRequest).Fields.RecurrenceSet!.RecurrenceDates!;
        var todo = CalendarAuthoringTimeZones.Normalize(todoRequest).Fields;

        periods.Select(item => item.Period!.Start.TimeZoneId).ShouldBe(["America/New_York", "America/New_York"]);
        periods[0].Period!.End!.TimeZoneId.ShouldBe("America/New_York");
        periods[1].Period!.End.ShouldBeNull();
        periods.ShouldAllBe(item => item.Value == null);
        todo.Start!.TimeZoneId.ShouldBe("America/New_York");
        todo.RecurrenceSet!.RecurrenceDates.ShouldBeNull();
        todo.RecurrenceSet.ExceptionDates!.Single().TimeZoneId.ShouldBe("America/New_York");
        var recurrenceOverride = todo.RecurrenceSet.Overrides!.Single();
        recurrenceOverride.RecurrenceIdentity.TimeZoneId.ShouldBe("America/New_York");
        recurrenceOverride.Fields.Due!.TimeZoneId.ShouldBe("America/New_York");
        CalendarAuthoringTimeZones.Normalize(todoRequest with { Fields = new CalendarTodoCreateFields() })
            .Fields.RecurrenceSet.ShouldBeNull();
    }

    [Fact]
    public void AddIntroduced_DefinesOnlyNewIanaReferencesAcrossStoredValues()
    {
        var original = Resource("DTSTART;TZID=Eastern Standard Time:20260310T100000\r\nDURATION:PT1H\r\n");
        var edited = Resource(
            "DTSTART;TZID=Eastern Standard Time:20260310T100000\r\nDURATION:PT1H\r\n"
            + "RDATE;TZID=Europe/Paris;VALUE=PERIOD:20260320T100000/PT1H,20260330T100000/20260330T110000\r\n"
            + "EXDATE;TZID=Custom/Zone:20260311T100000\r\n");

        var content = Encoding.UTF8.GetString(CalendarPatchTimeZoneDefinitions.AddIntroduced(original, edited));

        content.Split("BEGIN:VTIMEZONE", StringSplitOptions.None).Length.ShouldBe(2);
        content.ShouldContain("PRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Europe/Paris\r\n");
        content.ShouldContain("BEGIN:STANDARD\r\nDTSTART:20260320T100000\r\n");
        content.ShouldContain("BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\n");
        content.ShouldContain("END:VTIMEZONE\r\nBEGIN:VEVENT\r\n");
    }

    [Fact]
    public void AddIntroduced_LeavesDefinedAndPreviouslyReferencedZonesUnchanged()
    {
        var original = Resource("DTSTART;TZID=America/New_York:20260310T100000\r\n");
        var defined = Resource("DTSTART;TZID=Eastern Standard Time:20260310T100000\r\n", EmbeddedEasternZone);

        CalendarPatchTimeZoneDefinitions.AddIntroduced(original, original).ShouldBe(original);
        CalendarPatchTimeZoneDefinitions.AddIntroduced(original, defined).ShouldBe(defined);
    }

    [Fact]
    public void AddIntroduced_MatchesLineFeedOnlyResources()
    {
        var original = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Example//EN\nBEGIN:VTODO\nUID:lf\n"
            + "DTSTAMP:20260815T120000Z\nDUE:20260810T100000Z\nEND:VTODO\nEND:VCALENDAR\n");
        var edited = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Example//EN\nBEGIN:VTODO\nUID:lf\n"
            + "DTSTAMP:20260815T120000Z\nDUE;TZID=Asia/Tokyo:20260810T100000\nEND:VTODO\nEND:VCALENDAR\n");

        var content = Encoding.UTF8.GetString(CalendarPatchTimeZoneDefinitions.AddIntroduced(original, edited));

        content.ShouldContain("PRODID:-//Example//EN\nBEGIN:VTIMEZONE\nTZID:Asia/Tokyo\nBEGIN:STANDARD\n");
        content.ShouldNotContain("\r");
        CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content)).Diagnostics.ShouldBeEmpty();
    }

    private static CalendarTemporalValue Zoned(string value) =>
        new(CalendarTemporalKind.ZonedDateTime, value, "Eastern Standard Time");

    private static byte[] Resource(string temporalLines, string supportingComponents = "") => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n" + supportingComponents
        + $"BEGIN:VEVENT\r\nUID:zone\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static CalendarOccurrenceEvaluation Evaluate(byte[] bytes)
    {
        var document = CalendarContentDocument.Parse(bytes);
        var projected = CalendarResourceProjector.Project(document);
        var snapshot = new CalendarResourceSnapshot(
            CalendarHref,
            $"{CalendarHref}zone.ics",
            "\"r1\"",
            bytes,
            projected.Properties,
            projected.Projection,
            projected.Diagnostics);
        return CalendarOccurrenceEvaluator.Evaluate(
            snapshot,
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: CalendarHref)),
                DateTimeOffset.Parse("2026-03-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-03-20T00:00:00Z"),
                "UTC"),
            document,
            CalendarResourceProjector.LoadTypedCalendar(document),
            CancellationToken.None);
    }
}
