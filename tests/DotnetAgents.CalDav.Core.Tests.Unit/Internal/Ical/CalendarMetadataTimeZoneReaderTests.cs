using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarMetadataTimeZoneReaderTests
{
    private const string Zone = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Custom/Zone\r\nBEGIN:STANDARD\r\nDTSTART:19700101T000000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Read_extracts_one_identifier_from_bounded_folded_timezone_text()
    {
        var input = Zone.Replace("Custom/Zone", "Cus\r\n tom/Zo\r\n\tne", StringComparison.Ordinal);

        CalendarMetadataTimeZoneReader.Read(input, CancellationToken.None).ShouldBe(["Custom/Zone"]);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1200)]
    public void Read_rejects_deep_embedded_components_before_constructing_calendar_paths(int depth)
    {
        var input = string.Concat(Enumerable.Repeat("BEGIN:VCALENDAR\r\n", depth))
            + string.Concat(Enumerable.Repeat("END:VCALENDAR\r\n", depth));

        var error = Should.Throw<CalendarProtocolException>(() => CalendarMetadataTimeZoneReader.Read(input, CancellationToken.None));

        error.Code.ShouldBe("limit_exhausted");
    }

    [Fact]
    public void Read_rejects_heavily_folded_long_property_before_general_calendar_parser()
    {
        var folded = string.Concat(Enumerable.Repeat("x\r\n ", 17000));
        var input = Zone.Replace("TZID:Custom/Zone", "TZID:" + folded, StringComparison.Ordinal);

        var error = Should.Throw<CalendarProtocolException>(() => CalendarMetadataTimeZoneReader.Read(input, CancellationToken.None));

        error.Code.ShouldBe("limit_exhausted");
    }

    [Theory]
    [InlineData("characters")]
    [InlineData("lines")]
    public void Read_bounds_work_across_characters_and_content_lines(string dimension)
    {
        var input = dimension == "characters" ? new string('x', 262145)
            : Zone.Replace("TZID:Custom/Zone", "TZID:Custom/Zone\r\n" + string.Concat(Enumerable.Repeat("COMMENT:small\r\n", 4100)), StringComparison.Ordinal);

        Should.Throw<CalendarProtocolException>(() => CalendarMetadataTimeZoneReader.Read(input, CancellationToken.None))
            .Code.ShouldBe("limit_exhausted");
    }

    [Theory]
    [InlineData("missing_id")]
    [InlineData("duplicate_id")]
    [InlineData("event")]
    [InlineData("unknown_observance")]
    public void Read_rejects_timezone_structure_that_cannot_supply_one_identifier(string invalid)
    {
        var input = invalid switch
        {
            "missing_id" => Zone.Replace("TZID:Custom/Zone\r\n", string.Empty, StringComparison.Ordinal),
            "duplicate_id" => Zone.Replace("TZID:Custom/Zone", "TZID:Custom/Zone\r\nTZID:Other", StringComparison.Ordinal),
            "event" => Zone.Replace("VTIMEZONE", "VEVENT", StringComparison.Ordinal),
            _ => Zone.Replace("STANDARD", "EVENT", StringComparison.Ordinal)
        };

        Should.Throw<CalendarProtocolException>(() => CalendarMetadataTimeZoneReader.Read(input, CancellationToken.None))
            .Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public void Read_observes_cancellation_before_embedded_calendar_parsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Should.Throw<OperationCanceledException>(() => CalendarMetadataTimeZoneReader.Read(Zone, cancellation.Token));
    }
}
