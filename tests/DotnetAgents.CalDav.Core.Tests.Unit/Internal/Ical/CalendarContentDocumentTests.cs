using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarContentDocumentTests
{
    [Fact]
    public void Parse_PreservesFoldedSlicesUnknownValuesAndRepeatedParameterOccurrences()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nDESCRIPTION:Ol\u00e1 \ud83c\udf0d and a long value\r\n continued exactly\r\n\r\nX-LINK;X-LABEL=One,one;X-LABEL=TWO;VALUE=URI:https://example.test/a,b;c\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var bytes = Encoding.UTF8.GetBytes(content);

        var document = CalendarContentDocument.Parse(bytes);
        var property = document.Properties.Single(item => item.Name == "X-LINK");

        document.Replay().ShouldBe(bytes);
        property.ComponentPath.Select(item => (item.Name, item.Occurrence)).ShouldBe(
            [("VCALENDAR", 0), ("VEVENT", 0)]);
        property.Parameters.Select(item => (item.Name, string.Join('|', item.Values))).ShouldBe(
            [("X-LABEL", "One|one"), ("X-LABEL", "TWO"), ("VALUE", "URI")]);
        property.ValueType.ShouldBe(CalendarPropertyValueType.Uri);
        property.RawEncodedValue.ShouldBe("https://example.test/a,b;c");
        property.OriginalSlice.ShouldBe("X-LINK;X-LABEL=One,one;X-LABEL=TWO;VALUE=URI:https://example.test/a,b;c\r\n");
        document.Properties.Single(item => item.Name == "DESCRIPTION").OriginalSlice.ShouldContain("\r\n continued exactly\r\n");
    }

    [Fact]
    public void ReplayForOccurrenceEvaluation_PreservesEveryObservanceRecurrenceDate()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTIMEZONE\r\nTZID:Private/Zone\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20261025T030000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\n"
            + "RDATE:20271031T030000\r\nRDATE;VALUE=DATE-TIME:20281029T030000\r\n"
            + "RDATE;VALUE=PERIOD:20291028T030000/20291028T040000\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;TZID=Private/Zone:20260816T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        document.ReplayForOccurrenceEvaluation().ShouldBe(Encoding.UTF8.GetBytes(content));
        document.ReplayForTypedValidation().ShouldBe(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public void TypedReplayRetainsEntityPeriodWhileOccurrenceReplayDelegatesItToTheLocalEvaluator()
    {
        const string periodLine = "RDATE;VALUE=PERIOD:20260816T100000Z/PT1H\r\n";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260816T090000Z\r\n" + periodLine
            + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        Encoding.UTF8.GetString(document.ReplayForTypedValidation()).ShouldContain(periodLine);
        Encoding.UTF8.GetString(document.ReplayForOccurrenceEvaluation()).ShouldNotContain(periodLine);
        Encoding.UTF8.GetString(document.ReplayForProjectionValidation()).ShouldNotContain(periodLine);
    }

    [Fact]
    public void Parse_DecodesRfc6868ParameterEscapesInOnePass()
    {
        const string content = "BEGIN:VCALENDAR\r\nX-PROP;X-P=^^n,^n,^',^^:value\r\nEND:VCALENDAR\r\n";

        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        document.Properties.Single().Parameters.Single().Values.ShouldBe(["^n", "\n", "\"", "^"]);
    }

    [Theory]
    [InlineData(CalendarPropertyValueType.Uri, "ATTACH,ATTENDEE,CALENDAR-ADDRESS,CONCEPT,CONFERENCE,IMAGE,LINK,ORGANIZER,SOURCE,TZURL,URL")]
    [InlineData(CalendarPropertyValueType.DateTime, "ACKNOWLEDGED,COMPLETED,CREATED,DTEND,DTSTAMP,DTSTART,DUE,EXDATE,LAST-MODIFIED,RDATE,RECURRENCE-ID")]
    [InlineData(CalendarPropertyValueType.Duration, "DURATION,REFRESH-INTERVAL,TRIGGER")]
    [InlineData(CalendarPropertyValueType.Integer, "PERCENT-COMPLETE,PRIORITY,REPEAT,SEQUENCE")]
    [InlineData(CalendarPropertyValueType.Float, "GEO")]
    [InlineData(CalendarPropertyValueType.Period, "FREEBUSY")]
    [InlineData(CalendarPropertyValueType.Recur, "EXRULE,RRULE")]
    [InlineData(CalendarPropertyValueType.Text, "ACTION,CALSCALE,CATEGORIES,CLASS,COLOR,COMMENT,CONTACT,DESCRIPTION,LOCATION,METHOD,NAME,PRODID,PROXIMITY,REFID,RELATED-TO,REQUEST-STATUS,RESOURCES,RESOURCE-TYPE,STATUS,STRUCTURED-DATA,SUMMARY,TRANSP,TZID,TZNAME,UID,VERSION")]
    public void GetDefaultValueType_ExhaustivelyClassifiesRegisteredFrozenValues(
        CalendarPropertyValueType expected,
        string propertyNames)
    {
        foreach (var propertyName in propertyNames.Split(','))
        {
            CalendarContentDocument.IsRegisteredPropertyName(propertyName).ShouldBeTrue();
            CalendarContentDocument.GetDefaultValueType(propertyName).ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData("TEXT", CalendarPropertyValueType.Text)]
    [InlineData("URI", CalendarPropertyValueType.Uri)]
    [InlineData("CAL-ADDRESS", CalendarPropertyValueType.Uri)]
    [InlineData("DATE", CalendarPropertyValueType.Date)]
    [InlineData("DATE-TIME", CalendarPropertyValueType.DateTime)]
    [InlineData("DURATION", CalendarPropertyValueType.Duration)]
    [InlineData("INTEGER", CalendarPropertyValueType.Integer)]
    [InlineData("FLOAT", CalendarPropertyValueType.Float)]
    [InlineData("PERIOD", CalendarPropertyValueType.Period)]
    [InlineData("RECUR", CalendarPropertyValueType.Recur)]
    [InlineData("BINARY", CalendarPropertyValueType.Binary)]
    [InlineData("X-UNKNOWN", CalendarPropertyValueType.Unknown)]
    public void Parse_ClassifiesEveryFrozenExplicitValueType(
        string explicitValue,
        CalendarPropertyValueType expected)
    {
        var content = $"BEGIN:VCALENDAR\r\nX-PROP;VALUE={explicitValue}:value\r\nEND:VCALENDAR\r\n";

        var property = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content)).Properties.Single();

        property.ValueType.ShouldBe(expected);
    }

    [Theory]
    [InlineData("TZOFFSETFROM")]
    [InlineData("TZOFFSETTO")]
    public void GetDefaultValueType_KeepsUtcOffsetsRegisteredButFrozenUnknown(string propertyName)
    {
        CalendarContentDocument.IsRegisteredPropertyName(propertyName).ShouldBeTrue();
        CalendarContentDocument.GetDefaultValueType(propertyName).ShouldBe(CalendarPropertyValueType.Unknown);
    }

    [Theory]
    [InlineData("BEGIN:VCALENDAR\rEND:VCALENDAR\r\n")]
    [InlineData(" SUMMARY:value\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nMALFORMED\r\nEND:VCALENDAR\r\n")]
    [InlineData("X-PROP:value\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\n")]
    [InlineData("BEGIN:X_BAD\r\nEND:X_BAD\r\n")]
    [InlineData("END:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VEVENT\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nX-PROP;PARAM:value\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nX-PROP;BAD_PARAM=value:value\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nA.B.C:value\r\nEND:VCALENDAR\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nBAD_NAME:value\r\nEND:VCALENDAR\r\n")]
    public void Parse_RejectsMalformedContentLineStructure(string content)
    {
        Should.Throw<FormatException>(() => CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content)));
    }

    [Theory]
    [InlineData("new\rvalue")]
    [InlineData("new\nvalue")]
    public void ReplaceSinglePropertyValue_RejectsPhysicalLineBreaks(string replacement)
    {
        const string content = "BEGIN:VCALENDAR\r\nSUMMARY:old\r\nEND:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        Should.Throw<ArgumentException>(() =>
            document.ReplaceSinglePropertyValue(document.Components.Single().Path, "SUMMARY", replacement));
    }

    [Theory]
    [InlineData("DESCRIPTION:other\r\n")]
    [InlineData("SUMMARY:one\r\nSUMMARY:two\r\n")]
    public void ReplaceSinglePropertyValue_RequiresExactlyOneAddressedOccurrence(string properties)
    {
        var content = $"BEGIN:VCALENDAR\r\n{properties}END:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        Should.Throw<InvalidOperationException>(() =>
            document.ReplaceSinglePropertyValue(document.Components.Single().Path, "SUMMARY", "new"));
    }

    [Fact]
    public void ReplaceSinglePropertyValue_PreservesLfFoldedHeaderPrefix()
    {
        const string content = "BEGIN:VCALENDAR\nSUMMARY;X-LABEL=first\n continued:old\nEND:VCALENDAR\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

        var edited = document.ReplaceSinglePropertyValue(document.Components.Single().Path, "SUMMARY", "new");

        Encoding.UTF8.GetString(edited).ShouldBe("BEGIN:VCALENDAR\nSUMMARY;X-LABEL=first\n continued:new\nEND:VCALENDAR\n");
    }

    [Fact]
    public void Project_TreatsUnsupportedRootEntityComponentAsOpaqueEvenWhenItIsEmpty()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:vevent\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:vevent\r\nBEGIN:VJOURNAL\r\nEND:VJOURNAL\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["unsupported_entity_component"]);
    }

    [Fact]
    public void Project_TreatsAnEmptyAdditionalEntityAsOpaque()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_uid_invalid"]);
    }

    [Fact]
    public void Project_TreatsRepeatedSingletonPropertyAsOpaque()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:one\r\nSUMMARY:two\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["singleton_property_repeated"]);
    }

    [Fact]
    public void Project_DecodesTextEscapesInOnePass()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:literal\\\\n and newline\\n\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Projection.Summary.ShouldBe("literal\\n and newline\n");
    }

    [Fact]
    public void ReplaceSinglePropertyValue_ChangesOnlyTheAddressedSlice()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nX-ROOT;X-P=one,two:keep\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY;X-LABEL=\"quoted:\r\n parameter\":old and\r\n folded\r\nATTACH;VALUE=URI:https://example.test/inert\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:keep nested\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));
        var path = document.Components.Single(component => component.Path.Count == 2).Path;

        var edited = document.ReplaceSinglePropertyValue(path, "SUMMARY", "new\\, exact");

        Encoding.UTF8.GetString(edited).ShouldBe(content.Replace("parameter\":old and\r\n folded\r\n", "parameter\":new\\, exact\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplaceSinglePropertyValue_PreservesNamedNestedAndUnknownCorpusByteForByte()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:America/New_York\r\nBEGIN:STANDARD\r\nDTSTART:19701101T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU\r\nTZOFFSETFROM:-0400\r\nTZOFFSETTO:-0500\r\nTZNAME:EST\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nBEGIN:X-SUPPORT\r\nX-META:kept\r\nEND:X-SUPPORT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=America/New_York:20260816T090000\r\nSUMMARY:old\r\nX-REPEAT:first\r\nX-REPEAT:second\r\nITEM1.X-UNKNOWN;X-P=one;X-P=two,three:keep exactly\r\nBEGIN:X-NESTED\r\nX-VALUE:untouched\r\nEND:X-NESTED\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));
        var eventPath = document.Components.Single(component => component.Path.Count == 2 && component.Path[1].Name == "VEVENT").Path;

        var projection = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));
        projection.Projection.Kind.ShouldBe(
            CalendarResourceProjectionKind.Event,
            string.Join(',', projection.Diagnostics.Select(diagnostic => diagnostic.Code)));

        var edited = document.ReplaceSinglePropertyValue(eventPath, "SUMMARY", "new");

        Encoding.UTF8.GetString(edited).ShouldBe(content.Replace("SUMMARY:old\r\n", "SUMMARY:new\r\n", StringComparison.Ordinal));
        document.Properties.Single(property => property.Name == "X-UNKNOWN").Parameters.Count.ShouldBe(2);
        document.Properties.Count(property => property.Name == "X-REPEAT").ShouldBe(2);
        document.Components.ShouldContain(component => component.Path.Select(part => part.Name).SequenceEqual(new[] { "VCALENDAR", "VTIMEZONE", "STANDARD" }));
        document.Components.ShouldContain(component => component.Path.Select(part => part.Name).SequenceEqual(new[] { "VCALENDAR", "VEVENT", "X-NESTED" }));
    }

    [Fact]
    public void Project_PermitsUnknownRootSupportingComponentAlongsideEvent()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:X-SUPPORT\r\nX-META:kept\r\nEND:X-SUPPORT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:projectable\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Properties.Single(property => property.Name == "X-META").RawEncodedValue.ShouldBe("kept");
    }

    [Fact]
    public void Project_RejectsEntityComponentNestedUnderUnknownComponent()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:X-SUPPORT\r\nBEGIN:VTODO\r\nUID:u2\r\nEND:VTODO\r\nEND:X-SUPPORT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["known_component_topology_invalid"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BEGIN:X-ROOT\r\nEND:X-ROOT\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\nBEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")]
    public void Project_RejectsInvalidCalendarRootCardinality(string content)
    {
        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["calendar_component_cardinality"]);
    }

    [Fact]
    public void Project_RejectsMixedDirectEntityKinds()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["mixed_entity_kinds"]);
    }

    [Fact]
    public void Project_RejectsEventWithoutStart()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_start_invalid"]);
    }

    [Fact]
    public void Project_PermitsTodoWithoutStart()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

        CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content)).Projection.Kind
            .ShouldBe(CalendarResourceProjectionKind.Todo);
    }

    [Fact]
    public void Project_RejectsTwoMasterEntities()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260817T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_master_cardinality"]);
    }

    [Theory]
    [InlineData("u2", "RRULE:FREQ=DAILY;COUNT=2", "recurrence_override_uid_mismatch")]
    public void Project_RejectsInvalidRecurrenceOverrideSet(string overrideUid, string recurrence, string diagnostic)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\n{recurrence}\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:{overrideUid}\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260817T090000Z\r\nDTSTART:20260817T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe([diagnostic]);
    }

    [Theory]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VCALENDAR")]
    [InlineData("BEGIN:VTIMEZONE\r\nTZID:Nested\r\nEND:VTIMEZONE")]
    [InlineData("BEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0000\r\nTZOFFSETTO:+0000\r\nEND:STANDARD")]
    [InlineData("BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nEND:VALARM")]
    public void Project_RejectsKnownComponentInIllegalTopology(string illegalComponent)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:X-SUPPORT\r\n{illegalComponent}\r\nEND:X-SUPPORT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["known_component_topology_invalid"]);
    }

    [Fact]
    public void Project_RejectsInvalidValueTypeOnRegisteredProperty()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nRRULE;VALUE=X-BOGUS:FREQ=DAILY\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Properties.Single(property => property.Name == "RRULE").RawEncodedValue.ShouldBe("FREQ=DAILY");
        result.Diagnostics.Select(item => item.Code).ShouldBe(["registered_property_value_invalid"]);
    }

    [Theory]
    [InlineData("RRULE;VALUE=TEXT:FREQ=DAILY")]
    [InlineData("URL;VALUE=TEXT:https://example.test/event")]
    [InlineData("DTSTAMP;VALUE=DATE:20260815")]
    public void Project_RejectsRecognizedButIncompatibleValueOverrides(string property)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\n{property}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["registered_property_value_invalid"]);
    }

    [Fact]
    public void Project_PermitsLegitimateDateAlternate()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;VALUE=DATE:20260816\r\nDTEND;VALUE=DATE:20260817\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Properties.Single(property => property.Name == "DTSTART").ValueType.ShouldBe(CalendarPropertyValueType.Date);
    }

    [Theory]
    [InlineData("BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:alarm\r\nTRIGGER:NOT-A-DURATION\r\nEND:VALARM")]
    [InlineData("URL:not a uri")]
    [InlineData("ATTACH;ENCODING=BASE64;VALUE=BINARY:not-base64!")]
    [InlineData("ATTENDEE:not a uri")]
    public void Project_RejectsRegisteredPropertyDroppedByIcalNet(string probe)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\n{probe}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["typed_projection_invalid"]);
    }

    [Fact]
    public void Project_RejectsMissingRequiredCalendarProperty()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["calendar_required_property_invalid"]);
    }

    [Fact]
    public void Project_RejectsEntityWithoutDtstamp()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_dtstamp_invalid"]);
    }

    [Theory]
    [InlineData("VEVENT", "DTEND:20260816T100000Z")]
    [InlineData("VTODO", "DUE:20260816T100000Z")]
    public void Project_RejectsMutuallyExclusiveEndAndDuration(string component, string endProperty)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:{component}\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\n{endProperty}\r\nDURATION:PT1H\r\nEND:{component}\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_temporal_cardinality"]);
    }

    [Theory]
    [InlineData("VEVENT", "CREATED:20260815T100000Z")]
    [InlineData("VEVENT", "LAST-MODIFIED:20260815T110000Z")]
    [InlineData("VEVENT", "GEO:37.386013;-122.082932")]
    [InlineData("VEVENT", "ORGANIZER:mailto:organizer@example.test")]
    [InlineData("VTODO", "PERCENT-COMPLETE:50")]
    [InlineData("VEVENT", "SEQUENCE:1")]
    public void Project_RejectsRepeatedRegisteredEntitySingletons(string component, string property)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:{component}\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\n{property}\r\n{property}\r\nEND:{component}\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["singleton_property_repeated"]);
    }

    [Theory]
    [InlineData("+0000")]
    [InlineData("-0400")]
    [InlineData("+053045")]
    [InlineData("-053045")]
    public void Project_PreservesValidUtcOffsetsAsUnknownFrozenValues(string offset)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Custom\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:{offset}\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=Custom:20260816T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Properties.Single(property => property.Name == "TZOFFSETFROM").ValueType.ShouldBe(CalendarPropertyValueType.Unknown);
    }

    [Theory]
    [InlineData("-0000")]
    [InlineData("-000000")]
    [InlineData("+2400")]
    [InlineData("+0061")]
    [InlineData("+010061")]
    public void Project_RejectsInvalidUtcOffsets(string offset)
    {
        var content = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Custom\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:{offset}\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=Custom:20260816T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["registered_property_value_invalid"]);
    }

    [Fact]
    public void Project_RejectsUtcOffsetDeclaredAsFrozenTextValue()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Custom\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM;VALUE=TEXT:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=Custom:20260816T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["registered_property_value_invalid"]);
    }

    [Fact]
    public void Project_RejectsSemanticallyDuplicateRecurrenceIdentities()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTIMEZONE\r\nTZID:America/New_York\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=America/New_York:20260816T090000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;TZID=America/New_York:20260817T090000\r\nDTSTART;TZID=America/New_York:20260817T100000\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;TZID=\"America/New_York\":20260817T090000\r\nDTSTART;TZID=America/New_York:20260817T110000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["recurrence_identity_duplicate"]);
    }

    [Fact]
    public void Project_RejectsRecurrenceIdentityWithDifferentTemporalFamily()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;VALUE=DATE:20260816\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260817T090000Z\r\nDTSTART:20260817T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["recurrence_identity_family_mismatch"]);
    }

    [Fact]
    public void Project_PreservesLosslessPropertiesWhenTypedParsingFails()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nRRULE:not-a-rule\r\nX-KEEP:still readable\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Properties.Single(property => property.Name == "X-KEEP").RawEncodedValue.ShouldBe("still readable");
        result.Diagnostics.Select(item => item.Code).ShouldBe(["typed_projection_invalid"]);
    }

    [Fact]
    public void Project_RejectsAmbiguousTemporalParametersWithoutThrowing()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=A;TZID=B:20260816T090000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        var result = CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content));

        result.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["property_parameter_cardinality"]);
    }
}
