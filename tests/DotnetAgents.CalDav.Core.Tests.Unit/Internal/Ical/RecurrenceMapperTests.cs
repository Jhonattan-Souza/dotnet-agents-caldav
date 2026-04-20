using Ical.Net;
using Ical.Net.DataTypes;
using DotnetAgents.CalDav.Core.Internal.Ical;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public class RecurrenceMapperTests
{
    [Fact]
    public void FromString_ValidDailyRule_ReturnsPattern()
    {
        // Act
        var result = RecurrenceMapper.FromString("FREQ=DAILY;COUNT=5");

        // Assert
        result.ShouldNotBeNull();
        result.Frequency.ShouldBe(FrequencyType.Daily);
        result.Count.ShouldBe(5);
    }

    [Fact]
    public void FromString_ValidWeeklyRule_ReturnsPattern()
    {
        // Act
        var result = RecurrenceMapper.FromString("FREQ=WEEKLY;INTERVAL=2");

        // Assert
        result.ShouldNotBeNull();
        result.Frequency.ShouldBe(FrequencyType.Weekly);
        result.Interval.ShouldBe(2);
    }

    [Fact]
    public void FromString_NullInput_ReturnsNull()
    {
        var result = RecurrenceMapper.FromString(null);
        result.ShouldBeNull();
    }

    [Fact]
    public void FromString_EmptyInput_ReturnsNull()
    {
        var result = RecurrenceMapper.FromString(string.Empty);
        result.ShouldBeNull();
    }

    [Fact]
    public void FromString_WhitespaceInput_ReturnsNull()
    {
        var result = RecurrenceMapper.FromString("   ");
        result.ShouldBeNull();
    }

    [Fact]
    public void FromString_InvalidInput_ReturnsNull()
    {
        var result = RecurrenceMapper.FromString("NOT_A_VALID_RRULE");
        result.ShouldBeNull();
    }

    [Fact]
    public void ToString_ValidPattern_ReturnsString()
    {
        // Arrange
        var pattern = new RecurrencePattern(FrequencyType.Daily, 1) { Count = 5 };

        // Act
        var result = RecurrenceMapper.ToString(pattern);

        // Assert
        result.ShouldNotBeNullOrEmpty();
        result!.ShouldContain("FREQ=DAILY");
        result.ShouldContain("COUNT=5");
    }

    [Fact]
    public void ToString_NullPattern_ReturnsNull()
    {
        var result = RecurrenceMapper.ToString(null);
        result.ShouldBeNull();
    }

    [Fact]
    public void RoundTrip_DailyWithCount_PreservesFrequencyAndCount()
    {
        // Arrange
        var original = "FREQ=DAILY;COUNT=5";

        // Act
        var pattern = RecurrenceMapper.FromString(original);
        pattern.ShouldNotBeNull();
        var result = RecurrenceMapper.ToString(pattern);

        // Assert
        result!.ShouldContain("FREQ=DAILY");
        result!.ShouldContain("COUNT=5");
    }

    [Fact]
    public void FromString_MonthlyRuleByMonthDay_ReturnsPattern()
    {
        // Act
        var result = RecurrenceMapper.FromString("FREQ=MONTHLY;BYMONTHDAY=15;COUNT=12");

        // Assert
        result.ShouldNotBeNull();
        result.Frequency.ShouldBe(FrequencyType.Monthly);
        result.Count.ShouldBe(12);
    }
}