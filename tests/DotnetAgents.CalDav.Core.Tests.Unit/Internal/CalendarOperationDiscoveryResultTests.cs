using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarOperationDiscoveryResultTests
{
    [Fact]
    public void UnsupportedEntityKindCannotResolveADefaultCalendar()
    {
        var discovery = new CalendarOperationDiscoveryResult(
            new CalendarDiscoveryResult([], []),
            CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous),
            CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope));

        var result = discovery.Default((CalendarEntityKind)999);

        result.Code.ShouldBe(CalendarSelectionCode.NotFound);
        result.Calendar.ShouldBeNull();
    }
}
