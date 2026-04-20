using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit.Tools;

public class EnumParsingHelpersTests
{
    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("Waiting")]
    public void ParseTaskStatus_ThrowsOnInvalidValue(string value)
    {
        var exception = Should.Throw<ArgumentException>(() => EnumParsingHelpers.ParseTaskStatus(value));

        exception.Message.ShouldContain("Invalid status value");
        exception.Message.ShouldContain(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("urgent")]
    [InlineData("P1")]
    public void ParseTaskPriority_ThrowsOnInvalidValue(string value)
    {
        var exception = Should.Throw<ArgumentException>(() => EnumParsingHelpers.ParseTaskPriority(value));

        exception.Message.ShouldContain("Invalid priority value");
        exception.Message.ShouldContain(value);
    }
}
