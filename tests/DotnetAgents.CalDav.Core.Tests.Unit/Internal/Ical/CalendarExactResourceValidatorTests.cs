using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarExactResourceValidatorTests
{
    [Fact]
    public void TryValidate_RejectsPathologicalSupportingComponentDepth()
    {
        var content = new StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:bounded-depth\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n");
        for (var depth = 0; depth < 128; depth++)
            content.Append("BEGIN:X-NEST\r\n");
        for (var depth = 0; depth < 128; depth++)
            content.Append("END:X-NEST\r\n");
        content.Append("END:VCALENDAR\r\n");

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(content.ToString()),
            out _).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(InvalidResources))]
    public void TryValidate_RejectsIncompleteOrInconsistentCompleteResource(string content)
    {
        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_RejectsInvalidUtf8()
    {
        CalendarExactResourceValidator.TryValidate(new byte[] { 0xff, 0xfe }, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("VEVENT", CalendarEntityKind.Event)]
    [InlineData("VTODO", CalendarEntityKind.Todo)]
    public void TryValidate_AcceptsOneMasterAndConsistentOverridesWithSupportingData(
        string component,
        CalendarEntityKind expectedKind)
    {
        var content = Calendar(
            "BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n"
            + "BEGIN:X-SUPPORT\r\nX-VALUE:opaque\r\nEND:X-SUPPORT\r\n"
            + Entity(component, "u1", recurrence: null,
                (component == "VTODO" ? "DTSTART:20260818T120000Z\r\n" : string.Empty)
                + "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")
            + Entity(component, "u1", "20260818T120000Z"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();

        identity.ShouldBe(new CalendarExactResourceIdentity("u1", expectedKind));
    }

    [Fact]
    public void TryValidate_AcceptsStandardsValidOpaqueResource()
    {
        var content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nCALSCALE:X-CUSTOM\r\n"
            + Entity("VEVENT", "opaque", null)
            + "END:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();

        identity.ShouldBe(new CalendarExactResourceIdentity("opaque", CalendarEntityKind.Event));
        CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content)).Projection.Kind
            .ShouldBe(CalendarResourceProjectionKind.Opaque);
    }

    [Fact]
    public void TryValidate_AcceptsRegisteredGrammarParametersAndTemporalCollections()
    {
        var content = Calendar(
            "SOURCE;VALUE=URI:https://cal.example/source.ics\r\n"
            + "REFRESH-INTERVAL;VALUE=DURATION:PT1H\r\nCOLOR:blue\r\n"
            + "BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nTZURL:https://cal.example/zone.ics\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\n"
            + "RRULE:FREQ=YEARLY;BYMONTH=1;UNTIL=20271231T235959Z\r\nRDATE:20260201T000000\r\n"
            + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + Entity("VEVENT", "registered", null,
                "STATUS:X-FUTURE\r\nTRANSP:X-FUTURE\r\nCLASS:PUBLIC\r\nCOLOR:blue\r\n"
                + "ATTACH;FMTTYPE=application/ld+json:https://cal.example/data.json\r\n"
                + "ATTENDEE;RSVP=TRUE;CN=User;LANGUAGE=en-US:mailto:user@example.com\r\n"
                + "REQUEST-STATUS:2.0;Success\r\nRRULE:FREQ=MONTHLY;BYMONTHDAY=18\r\n"
                + "EXDATE:20260819T120000Z,20260820T120000Z\r\n"
                + "RDATE;VALUE=PERIOD:20260821T120000Z/PT1H,20260822T120000Z/20260822T130000Z"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();
        identity.EntityUid.ShouldBe("registered");
    }

    [Fact]
    public void TryValidate_AcceptsDateRecurrenceWithMatchingDateUntil()
    {
        var content = Calendar("BEGIN:VEVENT\r\nUID:date-until\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260818\r\nRRULE:FREQ=DAILY;UNTIL=20260820\r\nEND:VEVENT\r\n");

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRegisteredParametersOnPermittedOwners()
    {
        var content = Calendar(Entity("VEVENT", "parameters", null,
            "ORGANIZER;SENT-BY=\"mailto:assistant@example.com\";DIR=\"https://cal.example/directory/owner\";"
            + "CN=Owner;LANGUAGE=en:mailto:owner@example.com\r\n"
            + "ATTENDEE;RSVP=TRUE;CUTYPE=INDIVIDUAL;DELEGATED-FROM=\"mailto:manager@example.com\";"
            + "DIR=\"https://cal.example/directory/user\";MEMBER=\"mailto:team@example.com\";PARTSTAT=ACCEPTED;"
            + "ROLE=REQ-PARTICIPANT;CN=User;LANGUAGE=en-US:mailto:user@example.com\r\n"
            + "DESCRIPTION;ALTREP=\"https://cal.example/descriptions/1\";LANGUAGE=en:Description\r\n"
            + "RELATED-TO;RELTYPE=PARENT:parent-uid\r\n"
            + "ATTACH;ENCODING=BASE64;VALUE=BINARY;FMTTYPE=application/ld+json:SGVsbG8="));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRfc7986NoDefaultPropertiesAndParameters()
    {
        var content = Calendar("SOURCE;VALUE=URI:https://cal.example/source.ics\r\n"
            + "REFRESH-INTERVAL;VALUE=DURATION:PT1H\r\n"
            + Entity("VEVENT", "rfc7986", null,
                "IMAGE;VALUE=URI;DISPLAY=BADGE,THUMBNAIL;ALTREP=\"https://cal.example/image-info\";"
                + "FMTTYPE=image/png:https://cal.example/image.png\r\n"
                + "IMAGE;VALUE=BINARY;ENCODING=BASE64;FMTTYPE=image/png:aGVsbG8=\r\n"
                + "CONFERENCE;VALUE=URI;FEATURE=AUDIO,VIDEO;LABEL=Room;LANGUAGE=en:"
                + "https://meet.example.com/room"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("STYLED-DESCRIPTION;VALUE=TEXT;FMTTYPE=text/html:<b>Agenda</b>")]
    [InlineData("STYLED-DESCRIPTION;VALUE=URI;FMTTYPE=text/html:https://cal.example/agenda.html")]
    public void TryValidate_AcceptsStyledDescriptionWithRequiredMatchingValue(string styledDescription)
    {
        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "styled", null, styledDescription))),
            out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRfc9073PropertiesInTheirRegisteredComponents()
    {
        var extensions = "STRUCTURED-DATA;VALUE=TEXT;FMTTYPE=application/ld+json;"
            + "SCHEMA=\"https://schema.org/Event\":{\"name\":\"Agenda\"}\r\n"
            + "BEGIN:PARTICIPANT\r\nUID:participant-1\r\nPARTICIPANT-TYPE;ORDER=1:SPEAKER\r\n"
            + "ATTACH:https://cal.example/speaker.vcf\r\n"
            + "STYLED-DESCRIPTION;VALUE=TEXT;FMTTYPE=text/html:<b>Speaker</b>\r\n"
            + "STRUCTURED-DATA;VALUE=URI:https://cal.example/speaker.json\r\n"
            + "BEGIN:VLOCATION\r\nUID:location-1\r\nNAME:Room 1\r\nLOCATION-TYPE:Office,Meeting Room\r\n"
            + "STRUCTURED-DATA;VALUE=URI:https://cal.example/rooms/1.json\r\nEND:VLOCATION\r\n"
            + "BEGIN:VRESOURCE\r\nUID:resource-1\r\nRESOURCE-TYPE:PROJECTOR\r\n"
            + "STRUCTURED-DATA;VALUE=URI:https://cal.example/resources/1.json\r\nEND:VRESOURCE\r\n"
            + "END:PARTICIPANT\r\n"
            + "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\n"
            + "STYLED-DESCRIPTION;VALUE=TEXT;FMTTYPE=text/html:<b>Reminder</b>\r\nEND:VALARM";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "rfc9073", null, extensions))),
            out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("LINK;VALUE=URI;FMTTYPE=text/html;LINKREL=SOURCE;LABEL=Details;LANGUAGE=en:https://cal.example/details")]
    [InlineData("LINK;VALUE=UID;LINKREL=RELATED;FMTTYPE=text/plain:related-uid")]
    [InlineData("LINK;VALUE=XML-REFERENCE;LINKREL=RELATED;FMTTYPE=application/xml:https://cal.example/data.xml#xpointer(/event)")]
    public void TryValidate_AcceptsRfc9253LinkValueTypes(string link)
    {
        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "linked", null, link))),
            out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRfc9253RelationshipsInSupportingComponents()
    {
        var location = "BEGIN:VLOCATION\r\nUID:location-1\r\n"
            + "CONCEPT:https://schema.example/Office\r\nREFID:office-group\r\n"
            + "LINK;VALUE=URI;LINKREL=RELATED:https://cal.example/offices/1\r\n"
            + "RELATED-TO;VALUE=URI;RELTYPE=STARTTOFINISH;GAP=-PT5M:https://cal.example/tasks/1\r\n"
            + "END:VLOCATION";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "relationships", null, location))),
            out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRepeatedStyledDescriptionWithExactlyOneSource()
    {
        var styled = "STYLED-DESCRIPTION;VALUE=TEXT;ALTREP=\"https://cal.example/plain\";"
            + "FMTTYPE=text/plain:Agenda\r\n"
            + "STYLED-DESCRIPTION;VALUE=TEXT;FMTTYPE=text/html;DERIVED=TRUE:<b>Agenda</b>";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "styled-family", null, styled))),
            out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsSingleDerivedStyledDescription()
    {
        var styled = "STYLED-DESCRIPTION;VALUE=TEXT;FMTTYPE=text/html;DERIVED=TRUE:<b>Agenda</b>";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "single-derived", null, styled))),
            out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRootImageAndRegisteredEmailParameter()
    {
        var content = Calendar("IMAGE;VALUE=URI;FMTTYPE=image/png:https://cal.example/calendar.png\r\n"
            + Entity("VEVENT", "root-image", null,
                "ATTENDEE;EMAIL=user@example.com:mailto:user@example.com"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsRfc9074ProximityAlarmWithUidAndLocation()
    {
        var alarm = "BEGIN:VALARM\r\nUID:alarm-1\r\nACTION:DISPLAY\r\n"
            + "TRIGGER;VALUE=DATE-TIME:19760401T005545Z\r\nDESCRIPTION:Leave office\r\nPROXIMITY:DEPART\r\n"
            + "BEGIN:VLOCATION\r\nUID:office\r\nNAME:Office\r\n"
            + "URL:geo:40.443,-79.945,12345678901234567890123456789;crs=wgs84;"
            + "u=12345678901234567890123456789;label=office%201\r\n"
            + "END:VLOCATION\r\nEND:VALARM";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "proximity", null, alarm))),
            out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("geo:40")]
    [InlineData("geo:090,0")]
    [InlineData("geo:+40,-8")]
    [InlineData("geo:40,-8;u=-1")]
    [InlineData("geo:40,-8;u=1;u=2")]
    [InlineData("geo:40,-8;label=office;u=1")]
    [InlineData("geo:40,-8;label=%ZZ")]
    [InlineData("geo:90.00000000000000000000000000001,0")]
    public void TryValidate_RejectsInvalidRfc5870GeoUri(string geoUri)
    {
        var alarm = "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\n"
            + "DESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
            + $"BEGIN:VLOCATION\r\nUID:office\r\nURL:{geoUri}\r\nEND:VLOCATION\r\nEND:VALARM";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "invalid-geo", null, alarm))),
            out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_AcceptsCaseInsensitivePropertyAndComponentNames()
    {
        const string content = "begin:vcalendar\r\nversion:2.0\r\nprodid:-//Tests//EN\r\n"
            + "image;value=uri;fmttype=image/png:https://cal.example/calendar.png\r\n"
            + "begin:vevent\r\nuid:lowercase\r\ndtstamp:20260817T120000Z\r\n"
            + "dtstart:20260818T120000Z\r\nend:vevent\r\nend:vcalendar\r\n";

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsLowercaseUtcOffsetsInTimeZoneObservance()
    {
        const string content = "begin:vcalendar\r\nversion:2.0\r\nprodid:-//Tests//EN\r\n"
            + "begin:vtimezone\r\ntzid:Test/Zone\r\nbegin:standard\r\n"
            + "dtstart:20260101T000000\r\ntzoffsetfrom:+0100\r\ntzoffsetto:+0000\r\n"
            + "end:standard\r\nend:vtimezone\r\nbegin:vevent\r\nuid:lowercase-zone\r\n"
            + "dtstamp:20260817T120000Z\r\ndtstart;TZID=Test/Zone:20260818T120000\r\n"
            + "end:vevent\r\nend:vcalendar\r\n";

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsEscapedTextAndRegisteredTextLists()
    {
        var content = Calendar(Entity("VEVENT", "text-values", null,
            "SUMMARY:Quarterly\\; meeting\\, Q1\\\\Q2\\nSecond line\r\n"
            + "COMMENT:First\\NSecond\r\nCATEGORIES:One,Two\\,Three\r\n"
            + "RESOURCES:Room A,Projector\\; HDMI\r\nREQUEST-STATUS:2.0;Success\\\\;Extra"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SUMMARY:bad\\q")]
    [InlineData("SUMMARY:trailing\\")]
    [InlineData("SUMMARY:bad,comma")]
    [InlineData("SUMMARY:bad;semicolon")]
    [InlineData("CATEGORIES:valid-list,bad;item")]
    [InlineData("REQUEST-STATUS:2.0;Success;bad\\q")]
    [InlineData("SUMMARY:bad\u007fvalue")]
    [InlineData("STATUS:NOT VALID")]
    public void TryValidate_RejectsInvalidRegisteredTextLexicalValue(string property)
    {
        var content = Calendar(Entity("VEVENT", "invalid-text", null, property));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("REQUEST-STATUS:2.0;")]
    [InlineData("REQUEST-STATUS:2.0.1;Success")]
    [InlineData("REQUEST-STATUS:12.34;Extended status")]
    public void TryValidate_AcceptsRequestStatusWithValidCodeAndText(string property)
    {
        var content = Calendar(Entity("VEVENT", "valid-request-status", null, property));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_RejectsRequestStatusWithTooManyCodeComponents()
    {
        var content = Calendar(Entity("VEVENT", "invalid-request-status", null,
            "REQUEST-STATUS:2.0.1.2;Success"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("aliceblue")]
    [InlineData("LightGoldenRodYellow")]
    public void TryValidate_AcceptsCaseInsensitiveCss3ColorName(string color)
    {
        var content = Calendar(Entity("VEVENT", "valid-color", null, $"COLOR:{color}"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("not-a-css3-color")]
    [InlineData("123")]
    public void TryValidate_RejectsNonCss3ColorValue(string color)
    {
        var content = Calendar(Entity("VEVENT", "invalid-color", null, $"COLOR:{color}"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_AcceptsRfc9253RelationshipsInUnknownSupportingComponent()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nCALSCALE:X-CUSTOM\r\n"
            + "BEGIN:X-SUPPORT\r\nCONCEPT:https://schema.example/Support\r\n"
            + "LINK;VALUE=URI;LINKREL=RELATED;X-KEEP=one;X-KEEP=two:https://cal.example/support\r\n"
            + "END:X-SUPPORT\r\n"
            + "BEGIN:VEVENT\r\nUID:opaque-relationships\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
        CalendarResourceProjector.Project(Encoding.UTF8.GetBytes(content)).Projection.Kind
            .ShouldBe(CalendarResourceProjectionKind.Opaque);
    }

    [Fact]
    public void TryValidate_AcceptsRepeatedExtensionParametersAndQuotedSafeCharacters()
    {
        var content = Calendar(Entity("VEVENT", "extension-parameters", null,
            "SUMMARY;X-KEEP=one;X-KEEP=two;X-QUOTED=\"a:b;c,d\":Text"));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SUMMARY;X-P=\"a\"\"b\":Text")]
    [InlineData("SUMMARY;X-P=\"unclosed:Text")]
    [InlineData("SUMMARY;X-P=bad\u007fvalue:Text")]
    public void TryValidate_RejectsInvalidRawParameterValueSyntax(string property)
    {
        var content = Calendar(Entity("VEVENT", "invalid-raw-parameter", null, property));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_AcceptsFoldedPropertyNameAndParameterHeader()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:folded-header\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nSUMM\r\n ARY:Folded name\r\n"
            + "COMMENT;X-P=foo\r\n :Folded parameter\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_AcceptsFoldInsideUtf8SequenceAndPreservesIdentity()
    {
        var prefix = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:folded-utf8\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nSUMMARY:Caf");
        var suffix = Encoding.UTF8.GetBytes("\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        byte[] content = [.. prefix, 0xc3, (byte)'\r', (byte)'\n', (byte)' ', 0xa9, .. suffix];

        CalendarExactResourceValidator.TryValidate(content, out var identity).ShouldBeTrue();

        identity.EntityUid.ShouldBe("folded-utf8");
    }

    [Theory]
    [InlineData("X-KEEP:bad\u007fvalue")]
    [InlineData("X-KEEP:bad\u001fvalue")]
    public void TryValidate_RejectsControlInUnknownPropertyValue(string property)
    {
        var content = Calendar(Entity("VEVENT", "invalid-unknown-value", null, property));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_AcceptsDistinctRootLanguageVariants()
    {
        var content = Calendar("NAME:Default\r\nNAME;LANGUAGE=en:English\r\nNAME;LANGUAGE=pt-BR:Português\r\n"
            + "DESCRIPTION;LANGUAGE=en:English description\r\n"
            + "DESCRIPTION;LANGUAGE=pt-BR:Descrição\r\n"
            + Entity("VEVENT", "language-variants", null));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("NAME:One\r\nNAME:Two")]
    [InlineData("NAME;LANGUAGE=en:One\r\nNAME;LANGUAGE=EN:Two")]
    [InlineData("DESCRIPTION:One\r\nDESCRIPTION:Two")]
    public void TryValidate_RejectsRepeatedRootLanguageVariant(string properties)
    {
        var content = Calendar(properties + "\r\n" + Entity("VEVENT", "duplicate-language", null));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryReadIdentity_PreservesTolerantGroupedAndWhitespaceParsing()
    {
        const string content = "BEGIN: VCALENDAR\r\nBEGIN:VEVENT\r\nX.UID:grouped\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryReadIdentity(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();

        identity.EntityUid.ShouldBe("grouped");
    }

    [Fact]
    public void TryReadIdentity_PreservesTolerantMalformedParameterParsing()
    {
        const string content = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:tolerant-parameter\r\n"
            + "SUMMARY;X-P=\"a\"\"b\":Text\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryReadIdentity(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();

        identity.EntityUid.ShouldBe("tolerant-parameter");
    }

    [Theory]
    [InlineData("X-CUSTOM", null)]
    [InlineData("DISPLAY", "PROXIMITY:CONNECT\r\nDESCRIPTION:Connect reminder")]
    public void TryValidate_AcceptsExtensibleAlarmActionsAndNonLocationProximity(
        string action,
        string? additionalProperties)
    {
        var alarm = $"BEGIN:VALARM\r\nACTION:{action}\r\nTRIGGER:-PT5M\r\n"
            + (additionalProperties is null ? string.Empty : additionalProperties + "\r\n")
            + "END:VALARM";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "extensible-alarm", null, alarm))),
            out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("zh-Hant-TW")]
    [InlineData("sl-rozaj-biske-1994")]
    [InlineData("de-DE-u-co-phonebk")]
    [InlineData("x-private")]
    [InlineData("i-klingon")]
    public void TryValidate_AcceptsWellFormedRfc5646LanguageTags(string language)
    {
        var attendee = $"ATTENDEE;LANGUAGE={language}:mailto:user@example.com";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity("VEVENT", "language", null, attendee))),
            out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("VEVENT", "ACTION:EMAIL\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nSUMMARY:Event reminder\r\nATTENDEE:mailto:user@example.com")]
    [InlineData("VEVENT", "ACTION:AUDIO\r\nTRIGGER:-PT5M\r\nATTACH:https://cal.example/reminder.wav")]
    [InlineData("VEVENT", "ACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME:20260818T115500Z\r\nDESCRIPTION:Reminder")]
    [InlineData("VTODO", "DTSTART:20260818T120000Z\r\nDUE:20260818T130000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;RELATED=END:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")]
    [InlineData("VTODO", "DTSTART:20260818T120000Z\r\nDURATION:PT1H\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;RELATED=END:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")]
    public void TryValidate_AcceptsStandardsValidAlarmActionsAndAnchors(string component, string properties)
    {
        var alarm = properties.Contains("BEGIN:VALARM", StringComparison.Ordinal)
            ? properties
            : $"BEGIN:VALARM\r\n{properties}\r\nEND:VALARM";

        CalendarExactResourceValidator.TryValidate(
            Encoding.UTF8.GetBytes(Calendar(Entity(component, "alarm", null, alarm))),
            out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nBEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n")]
    [InlineData("BEGIN:VLOCATION\r\nUID:room-1\r\nEND:VLOCATION")]
    [InlineData("BEGIN:PARTICIPANT\r\nUID:participant-1\r\nPARTICIPANT-TYPE:INDIVIDUAL\r\nCALENDAR-ADDRESS:mailto:user@example.com\r\nBEGIN:VRESOURCE\r\nUID:resource-1\r\nEND:VRESOURCE\r\nEND:PARTICIPANT")]
    public void TryValidate_AcceptsValidSupportingComponentTopologies(string supportingComponent)
    {
        var content = supportingComponent.StartsWith("BEGIN:VTIMEZONE", StringComparison.Ordinal)
            ? Calendar(supportingComponent + Entity("VEVENT", "support", null))
            : Calendar(Entity("VEVENT", "support", null, supportingComponent));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("BEGIN:VLOCATION\r\nUID:room-1\r\nEND:VLOCATION")]
    [InlineData("BEGIN:VRESOURCE\r\nUID:projector-1\r\nEND:VRESOURCE")]
    public void TryValidate_AcceptsRfc9073SupportingComponentsDirectlyInTodo(string supportingComponent)
    {
        var content = Calendar(Entity("VTODO", "support", null, supportingComponent));

        CalendarExactResourceValidator.TryValidate(Encoding.UTF8.GetBytes(content), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData('n')]
    [InlineData('N')]
    public void TryReadIdentity_DecodesEscapedUidWithoutRequiringCompleteSemantics(char newlineEscape)
    {
        var content = Calendar(Entity("VEVENT", $"escaped\\,line\\{newlineEscape}next", null));

        CalendarExactResourceValidator.TryReadIdentity(Encoding.UTF8.GetBytes(content), out var identity).ShouldBeTrue();

        identity.EntityUid.ShouldBe("escaped,line\nnext");
        identity.EntityKind.ShouldBe(CalendarEntityKind.Event);
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityResources))]
    public void TryReadIdentity_RejectsAmbiguousOrIncompleteIdentity(string content)
    {
        CalendarExactResourceValidator.TryReadIdentity(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    [Fact]
    public void TryReadIdentity_RejectsInvalidUtf8()
    {
        CalendarExactResourceValidator.TryReadIdentity(new byte[] { 0xff }, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryReadIdentity_RejectsPathologicalSupportingComponentDepthBeforeParsing()
    {
        var nested = string.Concat(Enumerable.Repeat("BEGIN:X-NESTED\r\n", 65));
        var closed = string.Concat(Enumerable.Repeat("END:X-NESTED\r\n", 65));
        var content = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:deep-identity\r\nEND:VEVENT\r\n"
            + nested + closed + "END:VCALENDAR\r\n";

        CalendarExactResourceValidator.TryReadIdentity(Encoding.UTF8.GetBytes(content), out _).ShouldBeFalse();
    }

    public static TheoryData<string> InvalidIdentityResources => new()
    {
        string.Empty,
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:a\r\nEND:VTODO\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:b\r\nRECURRENCE-ID:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:a\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nUID:a\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:a\r\nRECURRENCE-ID:20260818T120000Z\r\n"
            + "RECURRENCE-ID:20260819T120000Z\r\nEND:VEVENT\r\n")
    };

    public static TheoryData<string> InvalidResources => new()
    {
        string.Empty,
        "BEGIN:VEVENT\r\nUID:u1\r\nEND:VEVENT\r\n",
        "BEGIN:VCALENDAR\r\nPRODID:-//Tests//EN\r\n" + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        "BEGIN:VCALENDAR\r\nVERSION:1.0\r\nPRODID:-//Tests//EN\r\n" + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nMETHOD:CANCEL\r\n"
            + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nCALSCALE:bad,value\r\n"
            + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nCALSCALE:GREGORIAN\r\n"
            + "CALSCALE:GREGORIAN\r\n" + Entity("VEVENT", "u1", null) + "END:VCALENDAR\r\n",
        Calendar(""),
        Calendar("BEGIN:VJOURNAL\r\nUID:u1\r\nEND:VJOURNAL\r\n"),
        Calendar(Entity("VEVENT", "u1", null) + Entity("VTODO", "u1", "20260818T120000Z")),
        Calendar(Entity("VEVENT", "u1", null)
            + "BEGIN:X-SUPPORT\r\nBEGIN:VTODO\r\nUID:nested\r\nEND:VTODO\r\nEND:X-SUPPORT\r\n"),
        Calendar("BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\n"
            + "TZOFFSETTO:+0000\r\nEND:STANDARD\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\n"
            + "END:VALARM\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:PARTICIPANT\r\nUID:p1\r\nPARTICIPANT-TYPE:INDIVIDUAL\r\n"
            + "END:PARTICIPANT\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nBEGIN:VLOCATION\r\nUID:room-1\r\n"
            + "END:VLOCATION\r\nEND:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VLOCATION\r\nUID:room-1\r\nBEGIN:VRESOURCE\r\nUID:projector-1\r\n"
            + "END:VRESOURCE\r\nEND:VLOCATION")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VRESOURCE\r\nUID:projector-1\r\nBEGIN:VLOCATION\r\nUID:room-1\r\n"
            + "END:VLOCATION\r\nEND:VRESOURCE")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:PARTICIPANT\r\nUID:p1\r\nPARTICIPANT-TYPE:INDIVIDUAL\r\n"
            + "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\n"
            + "END:VALARM\r\nEND:PARTICIPANT")),
        Calendar(Entity("VEVENT", "", null)),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", null) + Entity("VEVENT", "u2", "20260818T120000Z")),
        Calendar(Entity("VEVENT", "u1", null) + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\n"
            + "RECURRENCE-ID:20260818T120000Z\r\nRECURRENCE-ID:20260819T120000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTART:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", null)
            + Entity("VEVENT", "u1", "20260818T120000Z")
            + Entity("VEVENT", "u1", "20260818T120000Z")),
        Calendar(Entity("VEVENT", "u1", null) + Entity("VEVENT", "u1", "20260818", null, dateRecurrence: true)),
        Calendar(Entity("VEVENT", "u1", null)
            + "BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "RECURRENCE-ID:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:not-a-rule")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=DAILY\r\nRRULE:FREQ=WEEKLY")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=WEEKLY;BYMONTHDAY=1")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=DAILY;BYYEARDAY=1")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=MONTHLY;BYWEEKNO=1")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=MONTHLY;BYSETPOS=1")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=WEEKLY;BYDAY=1MO")),
        Calendar(Entity("VEVENT", "u1", null, "RRULE:FREQ=DAILY;COUNT=999999999999999999999")),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260818\r\nRRULE:FREQ=DAILY;UNTIL=20260820T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", null, "EXDATE:not-a-date")),
        Calendar("REFRESH-INTERVAL:not-a-duration\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("SOURCE:not a uri\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("SOURCE:https://cal.example/a\r\nSOURCE:https://cal.example/b\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("REFRESH-INTERVAL:PT1H\r\nREFRESH-INTERVAL:PT2H\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("COLOR:blue\r\nCOLOR:red\r\n" + Entity("VEVENT", "u1", null)),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nACKNOWLEDGED:not-a-date\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:bad,value\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null, "STATUS:bad,value")),
        Calendar(Entity("VEVENT", "u1", null, "TRANSP:bad,value")),
        Calendar(Entity("VEVENT", "u1", null, "REQUEST-STATUS:not-structured")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;RSVP=MAYBE:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "SUMMARY;SENT-BY=mailto:user@example.com:Invalid owner")),
        Calendar(Entity("VEVENT", "u1", null, "ATTACH;CUTYPE=INDIVIDUAL:https://cal.example/file")),
        Calendar(Entity("VEVENT", "u1", null, "SUMMARY;FMTTYPE=text/plain:Invalid owner")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;ENCODING=8BIT:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null,
            "ATTENDEE;CN=One;CN=Two:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "SUMMARY;VALUE=TEXT:Redundant")),
        Calendar(Entity("VEVENT", "u1", null,
            "ATTACH;ENCODING=BASE64:https://cal.example/file")),
        Calendar(Entity("VEVENT", "u1", null, "ATTACH;VALUE=BINARY:SGVsbG8=")),
        Calendar(Entity("VEVENT", "u1", null, "STYLED-DESCRIPTION;FMTTYPE=text/html:<b>Missing VALUE</b>")),
        Calendar(Entity("VEVENT", "u1", null, "STYLED-DESCRIPTION;VALUE=URI:not a uri")),
        Calendar(Entity("VEVENT", "u1", null, "STRUCTURED-DATA:Missing VALUE")),
        Calendar(Entity("VEVENT", "u1", null, "STRUCTURED-DATA;VALUE=TEXT;SCHEMA=\"https://schema.org/Event\":Missing FMTTYPE")),
        Calendar(Entity("VEVENT", "u1", null, "STRUCTURED-DATA;VALUE=TEXT;FMTTYPE=application/json:Missing SCHEMA")),
        Calendar(Entity("VEVENT", "u1", null,
            "STRUCTURED-DATA;VALUE=BINARY;ENCODING=BASE64;FMTTYPE=application/json:SGVsbG8=")),
        Calendar("SOURCE:https://cal.example/missing-value\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("REFRESH-INTERVAL:PT1H\r\n" + Entity("VEVENT", "u1", null)),
        Calendar(Entity("VEVENT", "u1", null, "IMAGE:https://cal.example/missing-value")),
        Calendar(Entity("VEVENT", "u1", null, "CONFERENCE:https://meet.example/missing-value")),
        Calendar(Entity("VEVENT", "u1", null, "IMAGE;VALUE=URI;FEATURE=VIDEO:https://cal.example/image")),
        Calendar(Entity("VEVENT", "u1", null, "CONFERENCE;VALUE=URI;DISPLAY=BADGE:https://meet.example/room")),
        Calendar(Entity("VEVENT", "u1", null, "IMAGE;VALUE=URI;LABEL=Image:https://cal.example/image")),
        Calendar(Entity("VEVENT", "u1", null,
            "IMAGE;VALUE=URI;FMTTYPE=application/json:https://cal.example/image")),
        Calendar(Entity("VEVENT", "u1", null,
            "SUMMARY;EMAIL=user@example.com:Invalid owner")),
        Calendar(Entity("VEVENT", "u1", null,
            "ATTENDEE;EMAIL=:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null,
            "ATTENDEE;EMAIL=one@example.com,two@example.com:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "SUMMARY;ORDER=1:Singleton")),
        Calendar(Entity("VEVENT", "u1", null, "LINK;VALUE=URI:https://cal.example/missing-linkrel")),
        Calendar(Entity("VEVENT", "u1", null, "LINK;VALUE=XML-REFERENCE:https://cal.example/no-fragment.xml")),
        Calendar(Entity("VEVENT", "u1", null, "RELATED-TO;VALUE=URI:not a uri")),
        Calendar(Entity("VEVENT", "u1", null, "RELATED-TO;GAP=not-a-duration:related")),
        Calendar(Entity("VEVENT", "u1", null, "RELATED-TO;VALUE=URI;RELTYPE=PARENT:https://cal.example/parent")),
        Calendar(Entity("VEVENT", "u1", null,
            "STYLED-DESCRIPTION;VALUE=TEXT;DERIVED=TRUE:One\r\n"
            + "STYLED-DESCRIPTION;VALUE=TEXT;DERIVED=TRUE:Two")),
        Calendar(Entity("VEVENT", "u1", null,
            "STYLED-DESCRIPTION;VALUE=TEXT:One\r\nSTYLED-DESCRIPTION;VALUE=TEXT:Two")),
        Calendar(Entity("VEVENT", "u1", null, "STYLED-DESCRIPTION;VALUE=TEXT;DERIVED=MAYBE:One")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:PARTICIPANT\r\nUID:p1\r\nPARTICIPANT-TYPE:SPEAKER\r\nDTSTART:20260818T120000Z\r\nEND:PARTICIPANT")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VLOCATION\r\nUID:l1\r\nATTACH:https://cal.example/invalid\r\nEND:VLOCATION")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VRESOURCE\r\nUID:r1\r\nLOCATION:Invalid\r\nEND:VRESOURCE")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nUID:a1\r\nUID:a2\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\n"
            + "DESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nUID:\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\n"
            + "DESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\n"
            + "BEGIN:VLOCATION\r\nUID:l1\r\nURL:geo:40,-8\r\nEND:VLOCATION\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
            + "BEGIN:VLOCATION\r\nUID:l1\r\nURL:geo:not-a-coordinate\r\nEND:VLOCATION\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
            + "BEGIN:VLOCATION\r\nUID:l1\r\nURL:geo:91,181\r\nEND:VLOCATION\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
            + "BEGIN:VLOCATION\r\nUID:l1\r\nURL:geo:40,-8;crs=other\r\nEND:VLOCATION\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=1:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=a:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=en-abcdefghi:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=en-a:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=en-u-ca-gregory-u-nu-latn:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "ATTENDEE;LANGUAGE=sl-rozaj-rozaj:mailto:user@example.com")),
        Calendar(Entity("VEVENT", "u1", null, "DURATION:-PT1H")),
        Calendar(Entity("VTODO", "u1", null, "DURATION:PT1H")),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260818\r\nDURATION:PT1H\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260818\r\nEXDATE:20260819T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", null, "GEO:91;0")),
        Calendar(Entity("VEVENT", "u1", null, "PRIORITY:10")),
        Calendar(Entity("VEVENT", "u1", null, "SEQUENCE:-1")),
        Calendar(Entity("VTODO", "u1", null, "PERCENT-COMPLETE:101")),
        Calendar(Entity("VEVENT", "u1", null, "ACTION:DISPLAY")),
        Calendar(Entity("VEVENT", "u1", null, "TZOFFSETFROM:+0100")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDURATION:PT5M\r\nREPEAT:-1\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDURATION:PT5M\r\nREPEAT:0\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME:20260818T115500\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME;RELATED=END:20260818T115500Z\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;RELATED=MIDDLE:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VTODO", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;RELATED=END:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;RANGE=THISANDFUTURE:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar(Entity("VEVENT", "u1", "20260818T120000Z").Replace(
            "RECURRENCE-ID:20260818T120000Z",
            "RECURRENCE-ID;RANGE=GARBAGE:20260818T120000Z",
            StringComparison.Ordinal)),
        Calendar(Entity("VEVENT", "u1", "20260818T120000Z").Replace(
            "RECURRENCE-ID:20260818T120000Z",
            "RECURRENCE-ID;RANGE=THISANDFUTURE;RANGE=THISANDFUTURE:20260818T120000Z",
            StringComparison.Ordinal)),
        Calendar(Entity("VEVENT", "u1", null, "DUE:20260819T120000Z")),
        Calendar(Entity("VTODO", "u1", null, "DTEND:20260819T120000Z")),
        Calendar("DTSTART:20260818T120000Z\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nSUMMARY:Wrong place\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;TZID=Missing/Zone:20260818T120000\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nDTEND;VALUE=DATE:20260819\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nDTEND:20260818T110000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VTODO\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nDUE:20260818T110000Z\r\nEND:VTODO\r\n"),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:u1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART;TZID=Test/Zone:20260818T120000Z\r\nEND:VEVENT\r\n"),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nTZURL:https://cal.example/zone-a\r\n"
            + "TZURL:https://cal.example/zone-b\r\nLAST-MODIFIED:20260817T120000Z\r\nLAST-MODIFIED:20260817T130000Z\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Same/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\nBEGIN:VTIMEZONE\r\nTZID:Same/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\n"
            + "LAST-MODIFIED:20260817T120000Z\r\nLAST-MODIFIED:20260817T130000Z\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\nEND:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+2400\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nRRULE:FREQ=DAILY\r\n"
            + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\n"
            + "RRULE:FREQ=YEARLY;UNTIL=20271231T235959\r\n"
            + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART;VALUE=DATE:20260101\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000Z\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART;TZID=Invalid/Zone:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nRDATE:20260201T000000Z\r\n"
            + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Mixed/Zone\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\n"
            + "TZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\n"
            + "TZOFFSETTO:+0100\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n"
            + Entity("VEVENT", "u1", null)),
        Calendar("BEGIN:VTIMEZONE\r\nTZID:Test/Zone\r\nTZURL:not a uri\r\n"
            + "BEGIN:STANDARD\r\nDTSTART:20260101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\n"
            + "END:VTIMEZONE\r\n" + Entity("VEVENT", "u1", null)),
        Calendar(Entity("VEVENT", "u1", null, "BEGIN:VALARM\r\nACTION:DISPLAY\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:AUDIO\r\nTRIGGER:-PT5M\r\nATTACH:https://cal.example/a.wav\r\n"
            + "ATTACH:https://cal.example/b.wav\r\nEND:VALARM")),
        Calendar(Entity("VTODO", "u1", null,
            "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\n"
            + "TRIGGER;RELATED=END:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDURATION:PT1M\r\nDESCRIPTION:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:EMAIL\r\nTRIGGER:-PT5M\r\nSUMMARY:Reminder\r\nATTENDEE:mailto:user@example.com\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:EMAIL\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nATTENDEE:mailto:user@example.com\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:EMAIL\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nSUMMARY:Reminder\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:AUDIO\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Not allowed\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nATTACH:https://cal.example/audio.wav\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nSUMMARY:Not allowed\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null,
            "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nATTENDEE:mailto:user@example.com\r\nEND:VALARM")),
        Calendar(Entity("VEVENT", "u1", null)).Replace("\r\n", "\n", StringComparison.Ordinal),
        Calendar(Entity("VEVENT", "u1", null)).TrimEnd('\r', '\n'),
        "\n" + Calendar(Entity("VEVENT", "u1", null)),
        Calendar(Entity("VEVENT", "u1", null)).Replace("UID:u1", "X.UID:u1", StringComparison.Ordinal),
        Calendar(Entity("VEVENT", "u1", null)).Replace("BEGIN:VCALENDAR", "BEGIN: VCALENDAR", StringComparison.Ordinal),
        Calendar(Entity("VEVENT", "u1", null)).Replace("PRODID:-//Tests//EN\r\n",
            "PRODID:-//Tests//EN\r\n\r\n", StringComparison.Ordinal),
        Calendar(Entity("VEVENT", "u1", null)).Replace("UID:u1", "uid:u1\r\nuid:u1", StringComparison.Ordinal),
        Calendar("source:not a uri\r\n" + Entity("VEVENT", "u1", null))
    };

    private static string Calendar(string components) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
        + components
        + "END:VCALENDAR\r\n";

    private static string Entity(
        string component,
        string uid,
        string? recurrence,
        string? extra = null,
        bool dateRecurrence = false) =>
        $"BEGIN:{component}\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + (component == "VEVENT" ? "DTSTART:20260818T120000Z\r\n" : string.Empty)
        + (recurrence is null
            ? string.Empty
            : $"RECURRENCE-ID{(dateRecurrence ? ";VALUE=DATE" : string.Empty)}:{recurrence}\r\n")
        + (extra is null ? string.Empty : extra + "\r\n")
        + $"END:{component}\r\n";
}
