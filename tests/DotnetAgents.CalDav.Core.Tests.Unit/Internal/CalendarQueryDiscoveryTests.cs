using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQueryDiscoveryTests
{
    [Fact]
    public void UnsupportedEntityKindCannotResolveADefaultCalendar()
    {
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([], []),
            CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous),
            CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope));

        var result = discovery.Default((CalendarEntityKind)999);

        result.Code.ShouldBe(CalendarSelectionCode.NotFound);
        result.Calendar.ShouldBeNull();
    }
}
