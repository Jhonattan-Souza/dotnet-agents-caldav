using DotnetAgents.CalDav.Core.Internal.Ical;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarEntityViolationsTests
{
    [Fact]
    public void FieldValueInvalid_surfaces_only_messages_the_validator_authored()
    {
        CalendarEntityViolations.FieldValueInvalid("priority", new CalendarAuthoredArgumentException("Priority must be between zero and nine."))
            .Violation.Message.ShouldBe("Priority must be between zero and nine.");
        CalendarEntityViolations.FieldValueInvalid("url", new ArgumentException("private-value from a library"))
            .Violation.Message.ShouldBe("This value is invalid for its field.");
    }
}
