using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using NodaTime;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarCollectionPropertyValuesTests
{
    [Theory]
    [InlineData("#FF2968", "#FF2968")]
    [InlineData("#aAbBcC", "#aAbBcC")]
    [InlineData("#FF2968FF", "#FF2968")]
    [InlineData("#0000FF80", "#0000FF")]
    [InlineData("\n  #102030 \t", "#102030")]
    [InlineData("#12345", null)]
    [InlineData("#1234567", null)]
    [InlineData("#123456789", null)]
    [InlineData("102030", null)]
    [InlineData("#GG0000", null)]
    [InlineData("red", null)]
    [InlineData("#102030\n#405060", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Color_reads_six_or_eight_digit_values_as_rgb(string? stored, string? expected)
    {
        CalendarCollectionPropertyValues.ReadColor(stored).ShouldBe(expected);
    }

    [Theory]
    [InlineData("#FF2968", true)]
    [InlineData("#abcdef", true)]
    [InlineData("#FF2968FF", false)]
    [InlineData("#FF296", false)]
    [InlineData(" #FF2968", false)]
    [InlineData("#FF2968\n", false)]
    [InlineData(null, false)]
    public void Color_writes_only_rgb(string? value, bool expected)
    {
        CalendarCollectionPropertyValues.IsWritableColor(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 12 ", 12)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483648", null)]
    [InlineData("-1", null)]
    [InlineData("+1", null)]
    [InlineData("1.5", null)]
    [InlineData("first", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Order_reads_only_nonnegative_32_bit_integers(string? stored, int? expected)
    {
        CalendarCollectionPropertyValues.ReadOrder(stored).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(-1, false)]
    [InlineData(null, false)]
    public void Order_writes_only_nonnegative_values(int? value, bool expected)
    {
        CalendarCollectionPropertyValues.IsWritableOrder(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Europe/Berlin", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("Etc/GMT+5", true)]
    [InlineData("UTC", true)]
    [InlineData("US/Eastern", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData(" Europe/Berlin", false)]
    [InlineData("Europe/Berlin\n", false)]
    [InlineData("Europe\\Berlin", false)]
    [InlineData("+0100", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Time_zone_requires_a_known_iana_identifier(string? value, bool expected)
    {
        CalendarCollectionPropertyValues.IsTimeZoneId(value).ShouldBe(expected);
    }

    [Fact]
    public void Time_zone_rejects_overlong_identifiers_before_lookup()
    {
        CalendarCollectionPropertyValues.IsTimeZoneId("A" + new string('b', 255)).ShouldBeFalse();
    }

    [Fact]
    public void Every_tzdb_zone_serializes_as_one_bounded_vtimezone_that_reads_back_its_identifier()
    {
        foreach (var id in DateTimeZoneProviders.Tzdb.Ids)
        {
            var calendar = CalendarCollectionPropertyValues.SerializeTimeZone(id);

            calendar.ShouldStartWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//EN\r\nBEGIN:VTIMEZONE\r\n");
            calendar.ShouldEndWith("END:VTIMEZONE\r\nEND:VCALENDAR\r\n");
            CalendarMetadataTimeZoneReader.Read(calendar, TestContext.Current.CancellationToken).ShouldBe([id], id);
        }
    }

    [Fact]
    public void Collection_time_zone_carries_observances_across_its_fixed_window()
    {
        var calendar = CalendarCollectionPropertyValues.SerializeTimeZone("America/New_York");

        calendar.ShouldContain("TZID:America/New_York\r\n");
        calendar.ShouldContain("BEGIN:DAYLIGHT\r\n");
        calendar.ShouldContain("BEGIN:STANDARD\r\n");
        calendar.ShouldContain("DTSTART:19700101T120000\r\n");
        calendar.ShouldContain("RDATE:20990308T020000\r\n");
        calendar.ShouldNotContain("RDATE:2100");
        calendar.ShouldBe(CalendarCollectionPropertyValues.SerializeTimeZone("America/New_York"));
    }
}
