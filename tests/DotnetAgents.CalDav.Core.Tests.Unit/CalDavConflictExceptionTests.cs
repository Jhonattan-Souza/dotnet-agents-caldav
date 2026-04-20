using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit;

public class CalDavConflictExceptionTests
{
    [Fact]
    public void Constructor_WithHrefAndEtag_SetsProperties()
    {
        // Act
        var ex = new CalDavConflictException("/calendars/user/task.ics", "etag-123");

        // Assert
        ex.Href.ShouldBe("/calendars/user/task.ics");
        ex.CurrentEtag.ShouldBe("etag-123");
        ex.Message.ShouldContain("/calendars/user/task.ics");
        ex.Message.ShouldContain("Precondition Failed");
    }

    [Fact]
    public void Constructor_WithNullEtag_SetsProperties()
    {
        // Act
        var ex = new CalDavConflictException("/calendars/user/task.ics", null);

        // Assert
        ex.Href.ShouldBe("/calendars/user/task.ics");
        ex.CurrentEtag.ShouldBeNull();
    }

    [Fact]
    public void Constructor_WithCustomMessage_SetsMessage()
    {
        // Act
        var ex = new CalDavConflictException("/calendars/user/task.ics", "etag-123", "Custom error message");

        // Assert
        ex.Message.ShouldBe("Custom error message");
        ex.Href.ShouldBe("/calendars/user/task.ics");
        ex.CurrentEtag.ShouldBe("etag-123");
    }

    [Fact]
    public void Constructor_WithInnerException_PreservesInner()
    {
        // Arrange
        var inner = new InvalidOperationException("inner");

        // Act
        var ex = new CalDavConflictException("/href", "etag", "outer", inner);

        // Assert
        ex.InnerException.ShouldBeSameAs(inner);
        ex.Href.ShouldBe("/href");
        ex.CurrentEtag.ShouldBe("etag");
    }
}
